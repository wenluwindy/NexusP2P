using System.Buffers;
using NexusP2P.Core.Crypto;
using NexusP2P.Core.Manifest;
using NexusP2P.Transfer.Protocol;
using NexusP2P.Transfer.Storage;
using NexusP2P.Transport.Abstractions;

namespace NexusP2P.Transfer;

/// <summary>传输进度快照。</summary>
public readonly record struct TransferProgress(
    long CompletedBytes,
    long TotalBytes,
    int CompletedPieces,
    int TotalPieces)
{
    public double Fraction => TotalBytes == 0 ? 1.0 : (double)CompletedBytes / TotalBytes;
}

/// <summary>
/// 发送端状态机。
///
/// <para>流程：发清单 → 等对端位图 → <b>只发对端缺的分片</b> → 等完成通知。
/// 「只发缺的」就是断点续传的发送侧 —— 不需要任何额外机制，
/// 位图本身就表达了「从哪继续」。</para>
///
/// <para>背压交给 <see cref="ProtocolConnection"/> 按缓冲水位处理，
/// 这里不做忙等。</para>
/// </summary>
public sealed class SendSession
{
    private readonly TransferManifest _manifest;
    private readonly IPieceSource _source;
    private readonly TransferSecret _secret;
    private readonly PieceLocator _locator;

    public SendSession(TransferManifest manifest, IPieceSource source, TransferSecret secret)
    {
        _manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _secret = secret;
        _locator = new PieceLocator(manifest);
    }

    /// <summary>
    /// 已投递的分片数。
    ///
    /// <para><b>这是「入队」的计数而不是「对端收到」的计数</b> ——
    /// <see cref="IDataChannel.Send"/> 是同步入队，几百 KB 可以一瞬间全部塞进
    /// 缓冲区。想知道对端实际收到多少，要看接收端的位图。</para>
    /// </summary>
    public int PiecesSent { get; private set; }

    /// <summary>
    /// 最多来回几轮。每轮要么有进展要么直接报错，所以这只是防御性上限。
    /// </summary>
    public int MaxRounds { get; init; } = 8;

    /// <summary>
    /// 跑完一次发送。正常结束表示对端已确认全部收齐并校验通过。
    /// </summary>
    /// <exception cref="TransferFailedException">对端报错，或协议被违反。</exception>
    public async Task RunAsync(
        ProtocolConnection connection,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // 失败时必须关掉通道，否则对端会一直等下去 ——
        // 症状是「传输卡死且两边都不报错」，是最难查的一类故障。
        // 放在这里而不是指望每个调用点自觉，因为后者必然会被漏掉。
        try
        {
            await RunCoreAsync(connection, progress, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await TryCloseAsync(connection, ex).ConfigureAwait(false);
            throw;
        }
    }

    internal static async Task TryCloseAsync(ProtocolConnection connection, Exception cause)
    {
        try
        {
            await connection.Channel.CloseAsync($"{cause.GetType().Name}: {cause.Message}")
                .ConfigureAwait(false);
        }
        catch (Exception closeFailure) when (closeFailure is InvalidOperationException or IOException)
        {
            // 通道可能已经关了，没别的办法可想
        }
    }

    private async Task RunCoreAsync(
        ProtocolConnection connection,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        // 1. 清单。用 manifestKey 密封 —— 文件名本身就是隐私。
        var manifestKey = KeyDerivation.DeriveManifestKey(_secret);
        var sealedManifest = BlobCipher.Seal(manifestKey, _manifest.Serialize());
        await connection.SendAsync(MessageType.Manifest, sealedManifest, cancellationToken)
            .ConfigureAwait(false);

        // 2~3. 轮次循环：收位图 → 推送它缺的 → 宣告本轮结束 → 再收位图。
        // 每轮结束后接收方会重发位图，所以被拒收的分片下一轮会被重传。
        using var cipher = new PieceCipher(_secret, _manifest.Hash);
        var plaintextBuffer = ArrayPool<byte>.Shared.Rent(_manifest.Parameters.PieceSize);
        var ciphertextBuffer = ArrayPool<byte>.Shared.Rent(
            PieceCipher.GetCiphertextLength(_manifest.Parameters.PieceSize));

        try
        {
            for (var round = 1; round <= MaxRounds; round++)
            {
                var message = await connection.ReceiveAsync(cancellationToken).ConfigureAwait(false);

                switch (message.Type)
                {
                    case MessageType.Complete:
                        // 对端已收齐并通过整体根校验
                        return;

                    case MessageType.Error:
                        var error = ErrorPayload.Parse(message.Payload.Span);
                        throw new TransferFailedException(error.Code, $"对端报错：{error.Message}");

                    case MessageType.Bitfield:
                        break;

                    default:
                        throw new TransferFailedException(
                            TransferErrorCode.ProtocolViolation,
                            $"期望收到 Bitfield 或 Complete，实际收到 {message.Type}。");
                }

                var remoteBitfield = ParseBitfield(message);

                // 对端已经齐了，等它做完整体校验后发 Complete
                if (remoteBitfield.IsComplete)
                {
                    continue;
                }

                await PushMissingAsync(
                        connection, cipher, remoteBitfield,
                        plaintextBuffer, ciphertextBuffer, progress, cancellationToken)
                    .ConfigureAwait(false);

                await connection
                    .SendAsync(MessageType.PushComplete, ReadOnlyMemory<byte>.Empty, cancellationToken)
                    .ConfigureAwait(false);
            }

            throw new TransferFailedException(
                TransferErrorCode.PieceVerificationFailed,
                $"来回 {MaxRounds} 轮仍未收齐，放弃。对端可能一直无法通过校验。");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(plaintextBuffer);
            ArrayPool<byte>.Shared.Return(ciphertextBuffer);
        }
    }

    private async Task PushMissingAsync(
        ProtocolConnection connection,
        PieceCipher cipher,
        PieceBitfield remoteBitfield,
        byte[] plaintextBuffer,
        byte[] ciphertextBuffer,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        var alreadyDone = remoteBitfield.SetCount;
        var sentThisRound = 0;
        long sentBytes = 0;

        foreach (var globalIndex in remoteBitfield.MissingIndices())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (connection.Channel.State != DataChannelState.Open)
            {
                throw new DataChannelClosedException("发送过程中通道关闭。");
            }

            var location = _locator.Locate(globalIndex);

            var read = await _source
                .ReadPieceAsync(location.FileIndex, location.LocalPieceIndex,
                    plaintextBuffer.AsMemory(0, location.Length), cancellationToken)
                .ConfigureAwait(false);

            if (read != location.Length)
            {
                throw new TransferFailedException(
                    TransferErrorCode.Unknown,
                    $"读取本地文件时第 {globalIndex} 个分片只读到 {read} 字节，期望 {location.Length} 字节。" +
                    "文件可能在传输期间被改动了。");
            }

            var ciphertextLength = PieceCipher.GetCiphertextLength(location.Length);
            cipher.Encrypt(
                location.FileIndex,
                location.LocalPieceIndex,
                plaintextBuffer.AsSpan(0, location.Length),
                ciphertextBuffer.AsSpan(0, ciphertextLength));

            var payload = new PiecePayload(
                location.FileIndex,
                location.LocalPieceIndex,
                ciphertextBuffer.AsMemory(0, ciphertextLength)).Serialize();

            await connection.SendAsync(MessageType.Piece, payload, cancellationToken)
                .ConfigureAwait(false);

            PiecesSent++;
            sentThisRound++;
            sentBytes += location.Length;

            progress?.Report(new TransferProgress(
                sentBytes,
                _manifest.TotalLength,
                alreadyDone + sentThisRound,
                _locator.TotalPieces));
        }
    }

    private PieceBitfield ParseBitfield(AssembledMessage message)
    {
        try
        {
            return PieceBitfield.Deserialize(message.Payload.Span, _locator.TotalPieces);
        }
        catch (ArgumentException ex)
        {
            throw new TransferFailedException(
                TransferErrorCode.ProtocolViolation, $"对端的位图不合法：{ex.Message}");
        }
    }

}

/// <summary>传输失败。<see cref="Code"/> 与协议里的错误码一致，便于两端对照。</summary>
public sealed class TransferFailedException(TransferErrorCode code, string message)
    : Exception(message)
{
    public TransferErrorCode Code { get; } = code;
}
