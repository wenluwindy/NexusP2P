using Microsoft.Win32.SafeHandles;
using NexusP2P.Core.Hashing;
using NexusP2P.Core.Manifest;

namespace NexusP2P.Transfer.Storage;

/// <summary>重扫进度。只在 <c>.meta</c> 丢失或损坏时才会发生。</summary>
public readonly record struct RescanProgress(int FileIndex, int FileCount, long BytesScanned, long BytesTotal);

/// <summary>
/// 接收端的分片落盘与断点续传。
///
/// <para><b>磁盘布局</b>：临时文件放在目标目录下的 <c>.nexusp2p</c> 子目录里 ——
/// 与最终文件同卷，所以完成时的重命名是廉价且原子的。</para>
/// <code>
/// &lt;目标目录&gt;/.nexusp2p/&lt;清单哈希&gt;.&lt;文件序号&gt;.part
/// &lt;目标目录&gt;/.nexusp2p/&lt;清单哈希&gt;.meta       整次传输一张位图
/// &lt;目标目录&gt;/&lt;清单里的相对路径&gt;               完成后落到这里
/// </code>
///
/// <para><b>为什么用「清单哈希 + 文件序号」而不是文件根命名</b>：
/// 按文件根命名看起来更漂亮（内容寻址，不同传输之间还能复用），
/// 但一次传输里两个内容相同的文件会撞名，数据全写进同一个 <c>.part</c>，
/// 收尾时第一个文件把它移走、后面的就找不到了。
/// 文件夹里有重复文件很常见，而<b>多个空文件必然撞名</b>。</para>
///
/// <para>清单哈希本身也是内容派生的，所以「关掉重开、生成新文件码」
/// 仍然能续上（同一份内容 → 同一个清单哈希 → 同一批 <c>.part</c>）。
/// 唯一失去的是「不同传输之间共享同一个文件」这种锦上添花的复用。</para>
///
/// <para><c>.part</c> 里存的是<b>明文</b> —— Merkle 根是对明文算的，
/// 而且文件最终形态本来就是明文，存密文只会让续传需要持久化密钥。</para>
///
/// <para>本类型<b>不是线程安全的</b>，由单个接收会话串行驱动。</para>
/// </summary>
public sealed class PieceStore : IAsyncDisposable
{
    /// <summary>临时文件所在的子目录名。</summary>
    public const string WorkDirectoryName = ".nexusp2p";

    /// <summary>每写多少个分片刷一次 <c>.meta</c>。断电最多丢这么多分片的进度。</summary>
    private const int MetaFlushInterval = 32;

    private readonly string _destinationRoot;
    private readonly string _workDirectory;
    private readonly TransferManifest _manifest;
    private readonly PieceHasher _pieceHasher;
    private readonly string _metaPath;

    private SafeFileHandle? _openHandle;
    private int _openFileIndex = -1;
    private int _piecesSinceFlush;
    private bool _disposed;

    private PieceStore(
        string destinationRoot,
        string workDirectory,
        TransferManifest manifest,
        PieceLocator locator,
        PieceBitfield bitfield)
    {
        _destinationRoot = destinationRoot;
        _workDirectory = workDirectory;
        _manifest = manifest;
        Locator = locator;
        Bitfield = bitfield;
        _pieceHasher = new PieceHasher(manifest.Parameters);
        _metaPath = Path.Combine(workDirectory, $"{manifest.Hash}.meta");
    }

    public PieceLocator Locator { get; }

    public PieceBitfield Bitfield { get; }

    /// <summary>已完成的字节数。用于进度显示。</summary>
    public long CompletedBytes
    {
        get
        {
            long total = 0;
            for (var i = 0; i < Locator.TotalPieces; i++)
            {
                if (Bitfield[i])
                {
                    total += Locator.Locate(i).Length;
                }
            }

            return total;
        }
    }

    /// <summary>
    /// 打开（或恢复）一次传输的仓储。
    ///
    /// <para>顺序刻意如此：先建目录 → 建/校验 <c>.part</c> → 读 <c>.meta</c>，
    /// 读不出来就全量重扫。所有可能失败的准备工作都在<b>开始接收之前</b>做完 ——
    /// 20 GB 传了 50 分钟才发现磁盘满，是这个产品能出现的最难受的结局。</para>
    /// </summary>
    public static async Task<PieceStore> OpenAsync(
        string destinationRoot,
        TransferManifest manifest,
        IProgress<RescanProgress>? rescanProgress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationRoot);
        ArgumentNullException.ThrowIfNull(manifest);

        var root = Path.GetFullPath(destinationRoot);
        Directory.CreateDirectory(root);

        var workDirectory = Path.Combine(root, WorkDirectoryName);
        Directory.CreateDirectory(workDirectory);

        EnsureSpaceAvailable(root, manifest.TotalLength);

        // 清单里的目录先建好。路径全部过 SafePath —— 它们是不可信输入。
        foreach (var directory in manifest.GetAllDirectories())
        {
            Directory.CreateDirectory(SafePath.ResolveWithin(root, directory));
        }

        var locator = new PieceLocator(manifest);
        var bitfield = new PieceBitfield(locator.TotalPieces);

        var parts = PrepareParts(workDirectory, manifest);

        var store = new PieceStore(root, workDirectory, manifest, locator, bitfield);

        // 刚新建出来的 .part 里不可能有任何已完成的分片，所以：
        //
        // 一、**有新建的就不能信 .meta**。它描述的是某个已经不在了的 .part，
        //     照它设位图等于声称拥有一堆全零的数据 —— 那要等到最后整体根校验
        //     才会暴露，白传一场。
        // 二、**全是新建的就连重扫都不必**。预分配过的文件是满长度的，
        //     重扫会把 20 GiB 全零数据读一遍再算一遍 SHA-256 才开始传，
        //     而结果必然是「什么都没有」。
        if (parts.AnyReused)
        {
            // 有新建的就绕过 .meta 直接重扫；否则先试 .meta，读不了才退回重扫
            var mustRescan = parts.AnyCreated || !store.TryLoadMeta(out _);

            if (mustRescan)
            {
                // 不是错误，是设计好的降级路径。全量重扫约 10~20 秒/20 GiB。
                rescanProgress?.Report(
                    new RescanProgress(0, locator.FileCount, 0, manifest.TotalLength));

                await store.RescanAsync(rescanProgress, cancellationToken).ConfigureAwait(false);

                // 只有真扫出进度才写 .meta。一份全零的 .meta 是有害的：
                // 进程若被强杀，它会被当成「什么都没完成」而**挡掉**下次的重扫，
                // 而重扫本来能从 .part 里把已写的分片找回来。
                if (store.Bitfield.SetCount > 0)
                {
                    await store.FlushMetaAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }

        return store;
    }

    /// <summary>
    /// 写入一个分片的明文。
    ///
    /// <para><b>先校验后写盘</b>。校验失败就抛异常且不碰磁盘 ——
    /// 未验证的字节永远不该落盘，否则重扫会把它当成有效数据。</para>
    /// </summary>
    public async Task WritePieceAsync(
        int globalIndex,
        ReadOnlyMemory<byte> plaintext,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var location = Locator.Locate(globalIndex);

        if (plaintext.Length != location.Length)
        {
            throw new PieceRejectedException(globalIndex,
                $"分片长度应为 {location.Length} 字节，实际收到 {plaintext.Length} 字节。");
        }

        if (!_pieceHasher.Verify(plaintext.Span, location.ExpectedRoot))
        {
            throw new PieceRejectedException(globalIndex, "分片 Merkle 校验失败。");
        }

        var handle = OpenPart(location.FileIndex);
        await RandomAccess.WriteAsync(handle, plaintext, location.OffsetInFile, cancellationToken)
            .ConfigureAwait(false);

        Bitfield.Set(globalIndex);

        if (++_piecesSinceFlush >= MetaFlushInterval)
        {
            await FlushMetaAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>把位图落盘。原子替换，这样断电时上一份完好的 <c>.meta</c> 仍在。</summary>
    public async Task FlushMetaAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_openHandle is not null)
        {
            RandomAccess.FlushToDisk(_openHandle);
        }

        var bytes = PartFileMeta.Serialize(_manifest.Hash, Bitfield);
        var temporary = _metaPath + ".tmp";

        await File.WriteAllBytesAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);
        File.Move(temporary, _metaPath, overwrite: true);

        _piecesSinceFlush = 0;
    }

    /// <summary>
    /// 全部分片齐了之后收尾：整体根校验 → 落到最终路径 → 清理临时文件。
    ///
    /// <para><b>整体根校验会重读一遍全部内容</b>（20 GiB 约 10~20 秒）。
    /// 每个分片入库时都校验过，所以这一遍查的是<b>落盘之后</b>的完整性 ——
    /// 磁盘写入错误、驱动 bug、别的程序动了文件，只有重读才能发现。</para>
    ///
    /// <para><paramref name="progress"/> 的回调<b>必须线程安全</b>：
    /// <see cref="Progress{T}"/> 会把回调投到线程池并可能并发执行。</para>
    /// </summary>
    public async Task<IReadOnlyList<string>> FinalizeAsync(
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!Bitfield.IsComplete)
        {
            throw new InvalidOperationException(
                $"还有 {Bitfield.Count - Bitfield.SetCount} 个分片未完成，不能收尾。");
        }

        CloseOpenPart();

        var landed = new List<string>(_manifest.Entries.Length);
        long scanned = 0;

        for (var fileIndex = 0; fileIndex < _manifest.Entries.Length; fileIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var entry = _manifest.Entries[fileIndex];
            var partPath = PartPath(fileIndex);

            using (var hasher = new FileHasher(_manifest.Parameters))
            await using (var stream = new FileStream(
                             partPath, FileMode.Open, FileAccess.Read, FileShare.None,
                             bufferSize: 1024 * 1024, useAsync: true))
            {
                var localProgress = scanned;
                var result = await hasher.ComputeAsync(
                        stream,
                        new Progress<long>(read => progress?.Report(localProgress + read)),
                        cancellationToken)
                    .ConfigureAwait(false);

                if (result.Root != entry.Root)
                {
                    throw new IntegrityException(entry.Path, entry.Root, result.Root);
                }
            }

            scanned += entry.Length;

            var finalPath = SafePath.ResolveWithin(_destinationRoot, entry.Path);
            var parent = Path.GetDirectoryName(finalPath);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            File.Move(partPath, finalPath, overwrite: true);
            landed.Add(finalPath);
        }

        File.Delete(_metaPath);
        TryRemoveWorkDirectoryIfEmpty();

        return landed;
    }

    // ---- 内部实现 ----

    /// <summary>
    /// 建好每个文件的 <c>.part</c>，长度设为最终长度（稀疏）。
    /// 已存在但长度不对的，说明上次是不同的内容或写坏了 —— 重建。
    /// </summary>
    /// <summary>
    /// 每个文件准备一个满长度的 <c>.part</c>。
    ///
    /// <para>返回值区分「沿用了上次留下的」与「新建了」，
    /// 调用方靠它决定还要不要读 <c>.meta</c>、要不要重扫 —— 见 <see cref="OpenAsync"/>。</para>
    /// </summary>
    private static PartPreparation PrepareParts(string workDirectory, TransferManifest manifest)
    {
        var anyReused = false;
        var anyCreated = false;

        for (var i = 0; i < manifest.Entries.Length; i++)
        {
            var entry = manifest.Entries[i];
            var path = BuildPartPath(workDirectory, manifest.Hash, i);

            if (File.Exists(path))
            {
                var actual = new FileInfo(path).Length;
                if (actual == entry.Length)
                {
                    // 长度对得上，里面可能装着上次收到的分片，留给重扫去认领
                    anyReused = true;
                    continue;
                }

                File.Delete(path);
            }

            using var handle = File.OpenHandle(
                path, FileMode.Create, FileAccess.Write, FileShare.None, FileOptions.None);
            RandomAccess.SetLength(handle, entry.Length);
            anyCreated = true;
        }

        return new PartPreparation(anyReused, anyCreated);
    }

    /// <summary>`.part` 的准备结果：沿用了旧的、还是新建了。</summary>
    private readonly record struct PartPreparation(bool AnyReused, bool AnyCreated);

    private static string BuildPartPath(string workDirectory, Hash256 manifestHash, int fileIndex) =>
        Path.Combine(workDirectory, $"{manifestHash}.{fileIndex}.part");

    private string PartPath(int fileIndex) => BuildPartPath(_workDirectory, _manifest.Hash, fileIndex);

    private bool TryLoadMeta(out string? reason)
    {
        reason = null;

        if (!File.Exists(_metaPath))
        {
            reason = ".meta 不存在（首次接收，或上次未完成就被清理了）。";
            return false;
        }

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(_metaPath);
        }
        catch (IOException ex)
        {
            reason = $".meta 读取失败：{ex.Message}";
            return false;
        }

        if (!PartFileMeta.TryDeserialize(
                bytes, _manifest.Hash, Locator.TotalPieces, out var loaded, out reason))
        {
            return false;
        }

        for (var i = 0; i < loaded!.Count; i++)
        {
            if (loaded[i])
            {
                Bitfield.Set(i);
            }
        }

        return true;
    }

    /// <summary>
    /// 全量重扫：逐分片读 <c>.part</c> 并做 Merkle 校验，重建位图。
    /// <c>.meta</c> 丢失或损坏时的降级路径。
    /// </summary>
    private async Task RescanAsync(IProgress<RescanProgress>? progress, CancellationToken cancellationToken)
    {
        var buffer = new byte[_manifest.Parameters.PieceSize];
        long scanned = 0;

        for (var fileIndex = 0; fileIndex < _manifest.Entries.Length; fileIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var entry = _manifest.Entries[fileIndex];
            var (start, end) = Locator.FileRange(fileIndex);
            var handle = OpenPart(fileIndex);

            for (var globalIndex = start; globalIndex < end; globalIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var location = Locator.Locate(globalIndex);
                var target = buffer.AsMemory(0, location.Length);

                var read = await RandomAccess
                    .ReadAsync(handle, target, location.OffsetInFile, cancellationToken)
                    .ConfigureAwait(false);

                if (read == location.Length && _pieceHasher.Verify(target.Span, location.ExpectedRoot))
                {
                    Bitfield.Set(globalIndex);
                }

                scanned += location.Length;
            }

            progress?.Report(new RescanProgress(
                fileIndex + 1, Locator.FileCount, scanned, _manifest.TotalLength));

            _ = entry;
        }
    }

    private SafeFileHandle OpenPart(int fileIndex)
    {
        if (_openFileIndex == fileIndex && _openHandle is not null)
        {
            return _openHandle;
        }

        CloseOpenPart();

        var entry = _manifest.Entries[fileIndex];
        _openHandle = File.OpenHandle(
            PartPath(fileIndex),
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None,
            FileOptions.Asynchronous);
        _openFileIndex = fileIndex;

        return _openHandle;
    }

    private void CloseOpenPart()
    {
        _openHandle?.Dispose();
        _openHandle = null;
        _openFileIndex = -1;
    }

    private void TryRemoveWorkDirectoryIfEmpty()
    {
        try
        {
            if (Directory.Exists(_workDirectory) &&
                !Directory.EnumerateFileSystemEntries(_workDirectory).Any())
            {
                Directory.Delete(_workDirectory);
            }
        }
        catch (IOException)
        {
            // 清理失败无关紧要，留个空目录比因此报错好
        }
    }

    /// <summary>
    /// 传输开始前检查可用空间。提前几秒失败远好过让用户白等五十分钟。
    /// </summary>
    public static void EnsureSpaceAvailable(string directory, long requiredBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentOutOfRangeException.ThrowIfNegative(requiredBytes);

        var full = Path.GetFullPath(directory);
        var driveRoot = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(driveRoot))
        {
            return;   // 拿不到卷信息就不阻止，总比误判好
        }

        long available;
        try
        {
            available = new DriveInfo(driveRoot).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return;
        }

        // 留 64 MiB 余量：目标目录写满会连带影响系统，而且我们还要写 .meta
        const long Headroom = 64L * 1024 * 1024;
        if (available < requiredBytes + Headroom)
        {
            throw new InsufficientDiskSpaceException(full, requiredBytes + Headroom, available);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        // 关闭前把位图落盘。少了这一步，连接中途断开时最近 32 个分片以内的进度
        // 会全部丢失 —— 而 .meta 里那份过期的位图还会**掩盖**磁盘上真实的进度，
        // 导致续传从头再来。这是设计上最容易漏掉的一环。
        //
        // 进程被强杀时仍然会丢一部分，但 .meta 只会「少报」不会「多报」，
        // 代价是重传一小段，方向是安全的。
        try
        {
            if (Bitfield.SetCount > 0 && !Bitfield.IsComplete)
            {
                await FlushMetaAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 落盘失败只是让下次退化为重扫，不该让 Dispose 抛异常
        }

        _disposed = true;
        CloseOpenPart();
        _pieceHasher.Dispose();
    }
}

/// <summary>分片被拒收：长度不对或校验失败。数据不会落盘。</summary>
public sealed class PieceRejectedException(int globalPieceIndex, string reason)
    : Exception($"拒收第 {globalPieceIndex} 个分片：{reason}")
{
    public int GlobalPieceIndex { get; } = globalPieceIndex;

    public string Reason { get; } = reason;
}

/// <summary>落盘后的整体根校验不通过。说明磁盘上的内容与验证过的不一致。</summary>
public sealed class IntegrityException(string path, Hash256 expected, Hash256 actual)
    : Exception($"文件 \"{path}\" 落盘后根校验不一致：期望 {expected}，实际 {actual}。")
{
    public string Path { get; } = path;
}

/// <summary>磁盘空间不足。在传输开始前抛出，而不是传到一半。</summary>
public sealed class InsufficientDiskSpaceException(string directory, long required, long available)
    : Exception($"目录 \"{directory}\" 需要 {required / 1024 / 1024} MiB，" +
                $"只剩 {available / 1024 / 1024} MiB。")
{
    public long RequiredBytes { get; } = required;

    public long AvailableBytes { get; } = available;
}
