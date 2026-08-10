using NexusP2P.Core.Manifest;

namespace NexusP2P.Transfer;

/// <summary>
/// 发送端读取分片明文的来源。
///
/// <para>抽象出来是为了让 <see cref="SendSession"/> 能在内存里被完整测试 ——
/// 见 AD-1。真实实现读磁盘文件，测试实现读内存字节数组，两者对状态机无差别。</para>
/// </summary>
public interface IPieceSource : IAsyncDisposable
{
    /// <summary>
    /// 把指定分片的明文读进 <paramref name="destination"/>，返回读到的字节数。
    /// 正常情况下应恰好填满调用方按清单算出的期望长度。
    /// </summary>
    Task<int> ReadPieceAsync(
        int fileIndex,
        long localPieceIndex,
        Memory<byte> destination,
        CancellationToken cancellationToken = default);
}

/// <summary>从磁盘读。发送方的真实实现。</summary>
public sealed class FilePieceSource : IPieceSource
{
    private readonly TransferManifest _manifest;
    private readonly string _root;
    private readonly Dictionary<int, string> _resolvedPaths = [];

    private Microsoft.Win32.SafeHandles.SafeFileHandle? _openHandle;
    private int _openFileIndex = -1;
    private bool _disposed;

    /// <param name="root">清单里相对路径的基准目录。</param>
    public FilePieceSource(TransferManifest manifest, string root)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        _manifest = manifest;
        _root = Path.GetFullPath(root);
    }

    public async Task<int> ReadPieceAsync(
        int fileIndex,
        long localPieceIndex,
        Memory<byte> destination,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var handle = Open(fileIndex);
        var offset = _manifest.Parameters.PieceOffset(localPieceIndex);

        return await RandomAccess.ReadAsync(handle, destination, offset, cancellationToken)
            .ConfigureAwait(false);
    }

    private Microsoft.Win32.SafeHandles.SafeFileHandle Open(int fileIndex)
    {
        if (_openFileIndex == fileIndex && _openHandle is not null)
        {
            return _openHandle;
        }

        _openHandle?.Dispose();
        _openHandle = null;
        _openFileIndex = -1;

        if (!_resolvedPaths.TryGetValue(fileIndex, out var path))
        {
            // 自己的清单也走 SafePath —— 发送方同样不该造出穿越路径
            path = SafePath.ResolveWithin(_root, _manifest.Entries[fileIndex].Path);
            _resolvedPaths[fileIndex] = path;
        }

        // FileShare.Read：允许别人读，但我们不加写锁 ——
        // 用户可能在传输期间打开这个文件看，不该被我们挡住
        _openHandle = File.OpenHandle(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.Asynchronous);
        _openFileIndex = fileIndex;

        return _openHandle;
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        _openHandle?.Dispose();
        _openHandle = null;
        return ValueTask.CompletedTask;
    }
}

/// <summary>从内存读。测试用，让协议能在完全没有磁盘和网络的情况下跑通。</summary>
public sealed class MemoryPieceSource(TransferManifest manifest, IReadOnlyDictionary<string, byte[]> files)
    : IPieceSource
{
    public Task<int> ReadPieceAsync(
        int fileIndex,
        long localPieceIndex,
        Memory<byte> destination,
        CancellationToken cancellationToken = default)
    {
        var entry = manifest.Entries[fileIndex];
        var content = files[entry.Path];
        var offset = manifest.Parameters.PieceOffset(localPieceIndex);
        var length = manifest.Parameters.PieceLength(entry.Length, localPieceIndex);

        content.AsMemory((int)offset, length).CopyTo(destination);
        return Task.FromResult(length);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
