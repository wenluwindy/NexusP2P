using System.Buffers;
using NexusP2P.Core.Crypto;
using NexusP2P.Core.Manifest;
using NexusP2P.Transfer.Storage;

namespace NexusP2P.Transfer;

/// <summary>
/// 发送端分片密文的提供者。
///
/// <para>V1 的 <see cref="SendSession"/> 自己「读盘 → 加密」；一对多（V2）时
/// 把这一步抽出来共享：nonce 由位置派生、密钥对整次传输唯一（AD-13 的前提），
/// 所以<b>同一分片对所有接收方的密文逐字节相同</b> —— 加密一次、发 N 次。</para>
/// </summary>
public interface ICipherPieceProvider
{
    /// <summary>
    /// 取一个分片的密文（含认证标签）。返回的内存段只保证在下一次调用前有效 ——
    /// 调用方要么立刻序列化发出，要么自己复制。
    /// </summary>
    ValueTask<ReadOnlyMemory<byte>> GetCiphertextAsync(
        PieceLocation location, CancellationToken cancellationToken = default);
}

/// <summary>
/// 密文 LRU 缓存（AD-13）：分片第一次被任何链路需要时读盘 + 加密进缓存，
/// 其他链路命中后直接发。几个人几乎同时开始收（口头念码的真实场景）时
/// 命中率接近 100%，磁盘读与 AES 开销从 ×N 降回 ×1。
///
/// <para><b>正确性不依赖缓存。</b>容量为 0 时全部旁路（读盘 + 加密后直接返回），
/// 端到端行为只是变慢。接收方进度发散时命中率下降、最坏退化为各读各的 ——
/// 这是可接受的退化，不为最坏情况设计。</para>
///
/// <para><b>single-flight</b>：同一分片被两条链路同时请求时只读盘加密一次，
/// 后到的等第一个的结果。</para>
///
/// <para>线程安全。多条链路并发调用 <see cref="GetCiphertextAsync"/> 是常态。</para>
/// </summary>
public sealed class CipherPieceCache : ICipherPieceProvider, IDisposable
{
    /// <summary>默认缓存容量：64 MiB（64 个 1 MiB 分片）。</summary>
    public const long DefaultCapacityBytes = 64 * 1024 * 1024;

    private readonly IPieceSource _source;
    private readonly PieceCipher _cipher;
    private readonly int _pieceSize;
    private readonly long _capacityBytes;

    private readonly Lock _gate = new();

    /// <summary>串行化 AesGcm 调用 —— 其实例方法不保证线程安全。</summary>
    private readonly Lock _encryptGate = new();

    /// <summary>全局分片下标 → 缓存节点。节点同时挂在 LRU 链表上。</summary>
    private readonly Dictionary<int, LinkedListNode<CacheEntry>> _entries = [];

    /// <summary>头部最新，尾部最旧。</summary>
    private readonly LinkedList<CacheEntry> _lru = new();

    private long _usedBytes;
    private long _hits;
    private long _encryptions;
    private bool _disposed;

    private sealed record CacheEntry(int GlobalIndex, Task<byte[]> Ciphertext, int Length);

    /// <param name="capacityBytes">0 表示完全旁路（只保留 single-flight 之外的直通路径）。</param>
    public CipherPieceCache(
        TransferManifest manifest,
        IPieceSource source,
        TransferSecret secret,
        long capacityBytes = DefaultCapacityBytes)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegative(capacityBytes);

        _source = source;
        _cipher = new PieceCipher(secret, manifest.Hash);
        _pieceSize = manifest.Parameters.PieceSize;
        _capacityBytes = capacityBytes;
    }

    /// <summary>命中次数（诊断与测试用）。</summary>
    public long Hits => Interlocked.Read(ref _hits);

    /// <summary>实际执行「读盘 + 加密」的次数（诊断与测试用）。</summary>
    public long Encryptions => Interlocked.Read(ref _encryptions);

    /// <summary>当前缓存占用的字节数。</summary>
    public long UsedBytes
    {
        get
        {
            lock (_gate)
            {
                return _usedBytes;
            }
        }
    }

    public async ValueTask<ReadOnlyMemory<byte>> GetCiphertextAsync(
        PieceLocation location, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var length = PieceCipher.GetCiphertextLength(location.Length);

        // 容量装不下单个分片时旁路，不进缓存（含容量 0 的退化路径）
        if (length > _capacityBytes)
        {
            return await ProduceAsync(location, cancellationToken).ConfigureAwait(false);
        }

        Task<byte[]> pending;
        lock (_gate)
        {
            if (_entries.TryGetValue(location.GlobalIndex, out var node))
            {
                // LRU：命中就挪到头部
                _lru.Remove(node);
                _lru.AddFirst(node);
                Interlocked.Increment(ref _hits);
                pending = node.Value.Ciphertext;
            }
            else
            {
                // single-flight：占位的是 Task 而不是结果，后到的等它
                var produce = ProduceForCacheAsync(location, cancellationToken);
                var entry = new CacheEntry(location.GlobalIndex, produce, length);
                var fresh = _lru.AddFirst(entry);
                _entries.Add(location.GlobalIndex, fresh);
                _usedBytes += length;
                EvictLocked(exempt: location.GlobalIndex);
                pending = produce;
            }
        }

        try
        {
            return await pending.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (RemoveFaulted(location.GlobalIndex, pending))
        {
            throw;   // 过滤器已做清理，永远返回 false 之外的路径不会到这里
        }
    }

    /// <summary>生产失败（读盘 IO 错误、取消）时把占位挪出缓存，别让失败被缓存住。</summary>
    private bool RemoveFaulted(int globalIndex, Task<byte[]> pending)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(globalIndex, out var node)
                && ReferenceEquals(node.Value.Ciphertext, pending))
            {
                _lru.Remove(node);
                _entries.Remove(globalIndex);
                _usedBytes -= node.Value.Length;
            }
        }

        return false;   // 不吞异常，只做清理
    }

    /// <summary>调用方必须已持有 <see cref="_gate"/>。从尾部逐出直到容量达标。</summary>
    private void EvictLocked(int exempt)
    {
        var node = _lru.Last;
        while (_usedBytes > _capacityBytes && node is not null)
        {
            var previous = node.Previous;

            if (node.Value.GlobalIndex != exempt)
            {
                _lru.Remove(node);
                _entries.Remove(node.Value.GlobalIndex);
                _usedBytes -= node.Value.Length;
            }

            node = previous;
        }
    }

    /// <summary>
    /// 进缓存的生产路径。<b>刻意不带取消</b> —— 结果是共享的，
    /// 第一条链路取消不该把其他正在等同一分片的链路一起拖死。
    /// 等待侧的取消由 <c>WaitAsync</c> 处理。
    /// </summary>
    private async Task<byte[]> ProduceForCacheAsync(PieceLocation location, CancellationToken trigger)
    {
        // 只在「还没开始」时尊重触发方的取消；开始后跑完
        trigger.ThrowIfCancellationRequested();
        return await ProduceAsync(location, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<byte[]> ProduceAsync(PieceLocation location, CancellationToken cancellationToken)
    {
        var plaintextBuffer = ArrayPool<byte>.Shared.Rent(_pieceSize);
        try
        {
            var read = await _source
                .ReadPieceAsync(location.FileIndex, location.LocalPieceIndex,
                    plaintextBuffer.AsMemory(0, location.Length), cancellationToken)
                .ConfigureAwait(false);

            if (read != location.Length)
            {
                throw new TransferFailedException(
                    Protocol.TransferErrorCode.Unknown,
                    $"读取本地文件时第 {location.GlobalIndex} 个分片只读到 {read} 字节，" +
                    $"期望 {location.Length} 字节。文件可能在传输期间被改动了。");
            }

            var ciphertext = new byte[PieceCipher.GetCiphertextLength(location.Length)];

            // AesGcm 实例方法按文档「不保证线程安全」，并发链路的加密必须串行化。
            // 加密是纯 CPU 操作、微秒级，这个锁不会成为瓶颈（瓶颈在网络与磁盘）。
            lock (_encryptGate)
            {
                _cipher.Encrypt(
                    location.FileIndex,
                    location.LocalPieceIndex,
                    plaintextBuffer.AsSpan(0, location.Length),
                    ciphertext);
            }

            Interlocked.Increment(ref _encryptions);
            return ciphertext;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(plaintextBuffer);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cipher.Dispose();

        lock (_gate)
        {
            _entries.Clear();
            _lru.Clear();
            _usedBytes = 0;
        }
    }
}
