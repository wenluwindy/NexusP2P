using System.Collections.Immutable;
using NexusP2P.Core.Hashing;

namespace NexusP2P.Core.Manifest;

/// <summary>
/// 清单里的一个文件。单文件传输就是只有一项的清单，文件夹是多项 ——
/// 让上层完全不必区分两者。
/// </summary>
public sealed record ManifestEntry
{
    /// <summary>相对路径，以 <c>/</c> 分隔。已通过 <see cref="SafePath.IsSafe"/> 校验。</summary>
    public string Path { get; }

    public long Length { get; }

    public Hash256 Root { get; }

    /// <summary>逐分片的根。让任意分片能脱离整体独立校验。</summary>
    public ImmutableArray<Hash256> PieceRoots { get; }

    public ManifestEntry(string path, long length, Hash256 root, ImmutableArray<Hash256> pieceRoots)
    {
        if (!SafePath.IsSafe(path, out var error))
        {
            throw new UnsafePathException(path ?? "<null>", error);
        }

        ArgumentOutOfRangeException.ThrowIfNegative(length);

        if (pieceRoots.IsDefaultOrEmpty)
        {
            // 空文件也有恰好一个分片（含一个空叶子），所以分片根永远不为空
            throw new ArgumentException("分片根列表不能为空。", nameof(pieceRoots));
        }

        Path = path;
        Length = length;
        Root = root;
        PieceRoots = pieceRoots;
    }

    public int PieceCount => PieceRoots.Length;

    public static ManifestEntry FromHashResult(string path, FileHashResult result) =>
        new(path, result.Length, result.Root, result.PieceRoots);

    // 与 FileHashResult 同一个理由：ImmutableArray<T> 的 IEquatable 是底层数组的
    // 引用相等，record 自动生成的 Equals 会用上它，导致内容相同却判为不等。
    // 按 Path + Root 比较：Root 已经把长度和全部分片根密码学地承诺进去了。
    public bool Equals(ManifestEntry? other) =>
        other is not null &&
        string.Equals(Path, other.Path, StringComparison.Ordinal) &&
        Root == other.Root;

    public override int GetHashCode() => HashCode.Combine(Path, Root);
}
