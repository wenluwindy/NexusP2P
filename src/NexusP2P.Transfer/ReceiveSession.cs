using System.Buffers;
using NexusP2P.Core.Crypto;
using NexusP2P.Core.Manifest;
using NexusP2P.Transfer.Protocol;
using NexusP2P.Transfer.Storage;
using NexusP2P.Transport.Abstractions;

namespace NexusP2P.Transfer;

/// <summary>一次接收的结果。</summary>
public sealed record ReceiveResult(
    TransferManifest Manifest,
    IReadOnlyList<string> LandedFiles)
{
    /// <summary>
    /// 会话开始时本地已经有多少个分片是齐的（续传的起点）。
    ///
    /// <para>断线重连后这个值大于 0，就证明<b>确实是接着传的</b>，
    /// 而不是把整个文件又拉了一遍。界面上要显示出来 ——
    /// 20 GB 传到一半断了，用户最想知道的就是这件事。</para>
    /// </summary>
    public int ResumedPieces { get; init; }

    /// <summary>续传起点对应的字节数。</summary>
    public long ResumedBytes { get; init; }
}

/// <summary>
/// 接收端状态机。
///
/// <para>流程：收清单 → <b>校验路径安全</b> → 查本地 <c>.part</c> 决定已有进度
/// → 回发位图 → 逐分片解密校验后落盘 → 全齐后整体根校验并落到最终路径 → 通知对端。</para>
///
/// <para><b>清单是不可信输入</b>。任一路径非法就整体拒绝并报错，不做部分接受 ——
/// 部分接受既让用户困惑，也给攻击者留了「混一条恶意路径进去」的空间。</para>
/// </summary>
public sealed class ReceiveSession(TransferSecret secret, string destinationRoot)
{
    private readonly string _destinationRoot = !string.IsNullOrWhiteSpace(destinationRoot)
        ? destinationRoot
        : throw new ArgumentException("目标目录不能为空。", nameof(destinationRoot));

    /// <summary>
    /// 允许连续拒收多少个分片后放弃。防止「对端一直发垃圾」变成无限循环。
    /// </summary>
    public int MaxConsecutiveRejections { get; init; } = 16;

    /// <summary>最多来回几轮。必须与发送端的设置一致或更宽松。</summary>
    public int MaxRounds { get; init; } = 8;

    public async Task<ReceiveResult> RunAsync(
        ProtocolConnection connection,
        IProgress<TransferProgress>? progress = null,
        IProgress<RescanProgress>? rescanProgress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // 与发送端同理：失败必须关通道，否则对端永远等下去
        try
        {
            return await RunCoreAsync(connection, progress, rescanProgress, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await SendSession.TryCloseAsync(connection, ex).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<ReceiveResult> RunCoreAsync(
        ProtocolConnection connection,
        IProgress<TransferProgress>? progress,
        IProgress<RescanProgress>? rescanProgress,
        CancellationToken cancellationToken)
    {
        var manifest = await ReceiveManifestAsync(connection, cancellationToken).ConfigureAwait(false);

        PieceStore store;
        try
        {
            store = await PieceStore
                .OpenAsync(_destinationRoot, manifest, rescanProgress, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (InsufficientDiskSpaceException ex)
        {
            // 在开始接收之前就失败 —— 传了五十分钟才发现磁盘满是最难受的结局
            await connection
                .SendErrorAndCloseAsync(TransferErrorCode.InsufficientDiskSpace, ex.Message)
                .ConfigureAwait(false);
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await connection
                .SendErrorAndCloseAsync(TransferErrorCode.DestinationNotWritable,
                    $"目标目录不可写：{ex.Message}")
                .ConfigureAwait(false);
            throw;
        }

        // 收第一条位图之前先记下来 —— 之后它会被填满，就问不出起点了
        var resumedPieces = store.Bitfield.SetCount;
        var resumedBytes = (long)resumedPieces * manifest.Parameters.PieceSize;

        await using (store)
        {
            // 轮次循环：发位图 → 收分片直到对端宣告本轮结束 → 若还缺就再发一次位图。
            // 被拒收的分片因此会在下一轮被重传，而不是把两边都卡住。
            for (var round = 1; round <= MaxRounds; round++)
            {
                await connection
                    .SendAsync(MessageType.Bitfield, store.Bitfield.Serialize(), cancellationToken)
                    .ConfigureAwait(false);

                if (store.Bitfield.IsComplete)
                {
                    break;
                }

                var before = store.Bitfield.SetCount;

                await ReceivePiecesAsync(connection, store, manifest, progress, cancellationToken)
                    .ConfigureAwait(false);

                if (store.Bitfield.SetCount == before)
                {
                    // 一整轮下来一个分片都没补上。再来一轮也是一样的结果。
                    var reason = $"第 {round} 轮没有收到任何通过校验的分片，放弃。";
                    await connection
                        .SendErrorAndCloseAsync(TransferErrorCode.PieceVerificationFailed, reason)
                        .ConfigureAwait(false);
                    throw new TransferFailedException(
                        TransferErrorCode.PieceVerificationFailed, reason);
                }

                if (round == MaxRounds && !store.Bitfield.IsComplete)
                {
                    var reason = $"来回 {MaxRounds} 轮仍未收齐，放弃。";
                    await connection
                        .SendErrorAndCloseAsync(TransferErrorCode.PieceVerificationFailed, reason)
                        .ConfigureAwait(false);
                    throw new TransferFailedException(
                        TransferErrorCode.PieceVerificationFailed, reason);
                }
            }

            var landed = await store.FinalizeAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            await connection
                .SendAsync(MessageType.Complete, ReadOnlyMemory<byte>.Empty, cancellationToken)
                .ConfigureAwait(false);
            await connection.Channel.WaitForDrainAsync(0, cancellationToken).ConfigureAwait(false);

            return new ReceiveResult(manifest, landed)
            {
                ResumedPieces = resumedPieces,
                ResumedBytes = Math.Min(resumedBytes, manifest.TotalLength),
            };
        }
    }

    private async Task<TransferManifest> ReceiveManifestAsync(
        ProtocolConnection connection, CancellationToken cancellationToken)
    {
        var message = await connection.ReceiveAsync(cancellationToken).ConfigureAwait(false);

        if (message.Type == MessageType.Error)
        {
            var error = ErrorPayload.Parse(message.Payload.Span);
            throw new TransferFailedException(error.Code, $"对端报错：{error.Message}");
        }

        if (message.Type != MessageType.Manifest)
        {
            var reason = $"期望首条消息是 Manifest，实际收到 {message.Type}。";
            await connection
                .SendErrorAndCloseAsync(TransferErrorCode.ProtocolViolation, reason)
                .ConfigureAwait(false);
            throw new TransferFailedException(TransferErrorCode.ProtocolViolation, reason);
        }

        var manifestKey = KeyDerivation.DeriveManifestKey(secret);

        byte[] plaintext;
        try
        {
            plaintext = BlobCipher.Open(manifestKey, message.Payload.Span);
        }
        catch (BlobAuthenticationException ex)
        {
            // 最常见的原因是文件码里的密钥不对 —— 用户可能少复制了 # 后面那一段
            var reason = $"清单解密失败，很可能是文件码不匹配：{ex.Message}";
            await connection
                .SendErrorAndCloseAsync(TransferErrorCode.InvalidManifest, reason)
                .ConfigureAwait(false);
            throw new TransferFailedException(TransferErrorCode.InvalidManifest, reason);
        }

        try
        {
            // Deserialize 内部会校验每条路径的安全性，任一条非法就整体拒绝
            return TransferManifest.Deserialize(plaintext);
        }
        catch (Exception ex) when (ex is InvalidManifestException or UnsafePathException)
        {
            var reason = $"清单不合法：{ex.Message}";
            await connection
                .SendErrorAndCloseAsync(TransferErrorCode.InvalidManifest, reason)
                .ConfigureAwait(false);
            throw new TransferFailedException(TransferErrorCode.InvalidManifest, reason);
        }
    }

    private async Task ReceivePiecesAsync(
        ProtocolConnection connection,
        PieceStore store,
        TransferManifest manifest,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var cipher = new PieceCipher(secret, manifest.Hash);
        var plaintextBuffer = ArrayPool<byte>.Shared.Rent(manifest.Parameters.PieceSize);
        var consecutiveRejections = 0;

        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var message = await connection.ReceiveAsync(cancellationToken).ConfigureAwait(false);

                switch (message.Type)
                {
                    case MessageType.Piece:
                        break;

                    case MessageType.PushComplete:
                        // 本轮结束。缺的部分交给外层循环重发位图去要。
                        return;

                    case MessageType.Error:
                        var error = ErrorPayload.Parse(message.Payload.Span);
                        throw new TransferFailedException(error.Code, $"对端报错：{error.Message}");

                    default:
                        var reason = $"接收分片期间收到意外的 {message.Type} 消息。";
                        await connection
                            .SendErrorAndCloseAsync(TransferErrorCode.ProtocolViolation, reason)
                            .ConfigureAwait(false);
                        throw new TransferFailedException(TransferErrorCode.ProtocolViolation, reason);
                }

                var accepted = await TryAcceptPieceAsync(
                        connection, store, cipher, plaintextBuffer, message, cancellationToken)
                    .ConfigureAwait(false);

                if (accepted)
                {
                    consecutiveRejections = 0;

                    progress?.Report(new TransferProgress(
                        store.CompletedBytes,
                        manifest.TotalLength,
                        store.Bitfield.SetCount,
                        store.Bitfield.Count));

                    // 收齐了就不必等 PushComplete —— 对端也知道该发 Complete 了。
                    // 这让常见路径少一次往返。
                    if (store.Bitfield.IsComplete)
                    {
                        return;
                    }
                }
                else if (++consecutiveRejections >= MaxConsecutiveRejections)
                {
                    // 对端持续发不通过校验的数据。继续下去只会无限循环。
                    var reason = $"连续 {consecutiveRejections} 个分片校验失败，放弃。";
                    await connection
                        .SendErrorAndCloseAsync(TransferErrorCode.PieceVerificationFailed, reason)
                        .ConfigureAwait(false);
                    throw new TransferFailedException(
                        TransferErrorCode.PieceVerificationFailed, reason);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(plaintextBuffer);
        }
    }

    /// <summary>
    /// 解密并落盘一个分片。返回是否被接受。
    ///
    /// <para>解密失败或校验失败<b>不是致命错误</b> —— 单个分片坏掉是可以重传的。
    /// 但要计数，避免对端一直发垃圾把我们卡在循环里。</para>
    /// </summary>
    private static async Task<bool> TryAcceptPieceAsync(
        ProtocolConnection connection,
        PieceStore store,
        PieceCipher cipher,
        byte[] plaintextBuffer,
        AssembledMessage message,
        CancellationToken cancellationToken)
    {
        PiecePayload payload;
        try
        {
            payload = PiecePayload.Parse(message.Payload.Span);
        }
        catch (ProtocolException ex)
        {
            await connection
                .SendErrorAndCloseAsync(TransferErrorCode.ProtocolViolation, ex.Message)
                .ConfigureAwait(false);
            throw new TransferFailedException(TransferErrorCode.ProtocolViolation, ex.Message);
        }

        int globalIndex;
        try
        {
            globalIndex = store.Locator.GlobalIndex(payload.FileIndex, payload.PieceIndex);
        }
        catch (ArgumentOutOfRangeException)
        {
            // 位置越界是协议违规而不是数据损坏 —— 正常的对端不会算错位置
            var reason = $"分片位置越界：文件 {payload.FileIndex}，分片 {payload.PieceIndex}。";
            await connection
                .SendErrorAndCloseAsync(TransferErrorCode.ProtocolViolation, reason)
                .ConfigureAwait(false);
            throw new TransferFailedException(TransferErrorCode.ProtocolViolation, reason);
        }

        // 已经有的分片直接忽略。重连后对端可能重发几个，这不是错误。
        if (store.Bitfield[globalIndex])
        {
            return true;
        }

        var location = store.Locator.Locate(globalIndex);
        var expectedCiphertextLength = PieceCipher.GetCiphertextLength(location.Length);
        if (payload.Ciphertext.Length != expectedCiphertextLength)
        {
            return false;
        }

        try
        {
            cipher.Decrypt(
                payload.FileIndex,
                payload.PieceIndex,
                payload.Ciphertext.Span,
                plaintextBuffer.AsSpan(0, location.Length));
        }
        catch (PieceAuthenticationException)
        {
            return false;
        }

        try
        {
            await store
                .WritePieceAsync(globalIndex, plaintextBuffer.AsMemory(0, location.Length), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (PieceRejectedException)
        {
            return false;
        }

        return true;
    }
}
