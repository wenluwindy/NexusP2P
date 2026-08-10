using System.Collections.Immutable;
using NexusP2P.Core.Hashing;
using NexusP2P.Core.Manifest;

namespace NexusP2P.Transfer.Tests.Storage;

/// <summary>
/// 造测试用的内容与清单，并管理一个临时目录。
/// 用小参数（1 KiB 叶子 / 4 KiB 分片）让边界用例跑得快。
/// </summary>
internal sealed class TransferFixture : IDisposable
{
    public static readonly MerkleParameters SmallParameters = new(1024, 4096);

    private readonly List<string> _temporaryDirectories = [];

    public MerkleParameters Parameters { get; init; } = SmallParameters;

    /// <summary>路径 → 内容。</summary>
    public Dictionary<string, byte[]> Files { get; } = [];

    public List<string> EmptyDirectories { get; } = [];

    public static byte[] Content(int length, int seed = 0)
    {
        var bytes = new byte[length];
        for (var i = 0; i < length; i++)
        {
            bytes[i] = (byte)((((i + 1) * (seed + 31)) ^ (i >> 7)) & 0xFF);
        }

        return bytes;
    }

    public TransferFixture With(string path, byte[] content)
    {
        Files[path] = content;
        return this;
    }

    public TransferFixture With(string path, int length, int seed = 0) => With(path, Content(length, seed));

    public TransferFixture WithEmptyDirectory(string path)
    {
        EmptyDirectories.Add(path);
        return this;
    }

    public async Task<TransferManifest> BuildManifestAsync()
    {
        var entries = new List<ManifestEntry>(Files.Count);

        foreach (var (path, content) in Files)
        {
            using var hasher = new FileHasher(Parameters);
            using var stream = new MemoryStream(content, writable: false);
            var result = await hasher.ComputeAsync(stream);
            entries.Add(ManifestEntry.FromHashResult(path, result));
        }

        return TransferManifest.Create(Parameters, entries, EmptyDirectories);
    }

    /// <summary>某个文件某个分片的明文。</summary>
    public ReadOnlyMemory<byte> Piece(TransferManifest manifest, int fileIndex, long localPieceIndex)
    {
        var entry = manifest.Entries[fileIndex];
        var content = Files[entry.Path];
        var offset = Parameters.PieceOffset(localPieceIndex);
        var length = Parameters.PieceLength(entry.Length, localPieceIndex);
        return content.AsMemory((int)offset, length);
    }

    public string CreateTemporaryDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "nexusp2p-test",
            Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(path);
        _temporaryDirectories.Add(path);
        return path;
    }

    public static ImmutableArray<Hash256> PieceRootsOf(TransferManifest manifest, int fileIndex) =>
        manifest.Entries[fileIndex].PieceRoots;

    public void Dispose()
    {
        foreach (var directory in _temporaryDirectories)
        {
            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
            catch (IOException)
            {
                // 测试清理失败不该让测试失败
            }
        }
    }
}
