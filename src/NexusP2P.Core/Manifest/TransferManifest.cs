using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using NexusP2P.Core.Hashing;

namespace NexusP2P.Core.Manifest;

/// <summary>
/// 一次传输的全部内容描述：参数、文件列表、每个文件的根与分片根。
///
/// <para><b>清单哈希是这次传输的身份</b>，用来给接收端的 <c>.part</c> 文件命名。
/// 发送方每次重发会生成新的文件码，但同一份内容的清单哈希不变 ——
/// 这就是「关掉程序、重开、用新码续传」能成立的原因。</para>
///
/// <para>顶层文件夹名包含在路径里（发送 <c>MyStuff</c> 得到
/// <c>MyStuff/a.txt</c>），所以接收端能自然地重建目录结构，
/// 而不会把一堆文件散落到下载目录里。</para>
/// </summary>
public sealed class TransferManifest
{
    private static readonly byte[] Magic = "NXP2PMAN"u8.ToArray();
    private const byte FormatVersion = 1;

    /// <summary>清单哈希的域分隔前缀，与 <c>Hashing</c> 里的 0x00~0x03 同属一个命名空间。</summary>
    private const byte ManifestHashPrefix = 0x10;

    /// <summary>条目数上限。防止恶意清单诱导巨额分配。</summary>
    public const int MaxEntries = 100_000;

    /// <summary>总分片数上限。约 4 TiB（按 1 MiB 分片），足够而有界。</summary>
    public const long MaxTotalPieces = 4_000_000;

    public MerkleParameters Parameters { get; }

    /// <summary>按路径序数升序排列。顺序是规范化的一部分，决定了清单哈希的稳定性。</summary>
    public ImmutableArray<ManifestEntry> Entries { get; }

    /// <summary>
    /// 需要显式创建的空目录。
    ///
    /// <para>由文件路径隐含的目录（<c>a/b/c.txt</c> 里的 <c>a</c> 与 <c>a/b</c>）
    /// 会在落盘时自然创建，不必列在这里。只有<b>底下完全没有文件</b>的目录才需要 ——
    /// 否则「传一个项目文件夹，结果空的 logs/ 目录没了」是个小而真实的意外。</para>
    /// </summary>
    public ImmutableArray<string> Directories { get; }

    /// <summary>这次传输的身份标识。</summary>
    public Hash256 Hash { get; }

    public long TotalLength { get; }

    public long TotalPieces { get; }

    private TransferManifest(
        MerkleParameters parameters,
        ImmutableArray<ManifestEntry> sortedEntries,
        ImmutableArray<string> sortedDirectories)
    {
        Parameters = parameters;
        Entries = sortedEntries;
        Directories = sortedDirectories;
        TotalLength = sortedEntries.Sum(e => e.Length);
        TotalPieces = sortedEntries.Sum(e => (long)e.PieceCount);
        Hash = ComputeHash(parameters, sortedEntries, sortedDirectories);
    }

    /// <summary>
    /// 从条目集合建清单。会排序、查重、并校验每个条目的分片数与长度自洽。
    /// </summary>
    public static TransferManifest Create(
        MerkleParameters parameters,
        IEnumerable<ManifestEntry> entries,
        IEnumerable<string>? emptyDirectories = null)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(entries);

        var list = entries.ToList();

        if (list.Count == 0)
        {
            throw new ArgumentException("清单至少要有一个条目。", nameof(entries));
        }

        if (list.Count > MaxEntries)
        {
            throw new ArgumentException($"条目数 {list.Count} 超过上限 {MaxEntries}。", nameof(entries));
        }

        // 大小写不敏感查重：Windows 上 a.txt 与 A.TXT 是同一个文件，
        // 若放过去，后一个会静默覆盖前一个。
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in list)
        {
            if (!seen.Add(entry.Path))
            {
                throw new ArgumentException(
                    $"清单里有重复路径（忽略大小写）：\"{entry.Path}\"。", nameof(entries));
            }

            var expectedPieces = parameters.PieceCount(entry.Length);
            if (entry.PieceCount != expectedPieces)
            {
                throw new ArgumentException(
                    $"条目 \"{entry.Path}\" 长度 {entry.Length} 应有 {expectedPieces} 个分片，" +
                    $"实际给了 {entry.PieceCount} 个。", nameof(entries));
            }
        }

        var totalPieces = list.Sum(e => (long)e.PieceCount);
        if (totalPieces > MaxTotalPieces)
        {
            throw new ArgumentException(
                $"总分片数 {totalPieces} 超过上限 {MaxTotalPieces}。", nameof(entries));
        }

        var directories = NormalizeDirectories(emptyDirectories, seen);

        // CompareOrdinal 比较 UTF-16 码元，与 JavaScript 的字符串 < 运算一致 ——
        // 网页端必须能算出同一个清单哈希，排序规则不能有平台差异。
        list.Sort(static (a, b) => string.CompareOrdinal(a.Path, b.Path));

        return new TransferManifest(parameters, [.. list], directories);
    }

    /// <summary>
    /// 校验并规范化空目录列表。<paramref name="takenPaths"/> 是已被文件占用的路径 ——
    /// 同一个路径不能既是文件又是目录。
    /// </summary>
    private static ImmutableArray<string> NormalizeDirectories(
        IEnumerable<string>? directories,
        HashSet<string> takenPaths)
    {
        if (directories is null)
        {
            return [];
        }

        var list = new List<string>();
        var seenDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var directory in directories)
        {
            if (!SafePath.IsSafe(directory, out var error))
            {
                throw new UnsafePathException(directory ?? "<null>", error);
            }

            if (takenPaths.Contains(directory))
            {
                throw new ArgumentException(
                    $"路径 \"{directory}\" 同时被当作文件和目录。", nameof(directories));
            }

            if (!seenDirectories.Add(directory))
            {
                throw new ArgumentException(
                    $"空目录列表里有重复路径（忽略大小写）：\"{directory}\"。", nameof(directories));
            }

            list.Add(directory);
        }

        if (list.Count > MaxEntries)
        {
            throw new ArgumentException(
                $"空目录数 {list.Count} 超过上限 {MaxEntries}。", nameof(directories));
        }

        list.Sort(static (a, b) => string.CompareOrdinal(a, b));
        return [.. list];
    }

    /// <summary>规范二进制形式。同一份内容永远产出同一串字节。</summary>
    public byte[] Serialize()
    {
        var writer = new ArrayBufferWriter<byte>(EstimateSize());
        WriteCanonical(writer, Parameters, Entries, Directories);
        return writer.WrittenSpan.ToArray();
    }

    /// <summary>
    /// 解析清单。<paramref name="data"/> <b>是不可信输入</b> ——
    /// 所有边界都在分配之前校验，所有路径都过 <see cref="SafePath"/>。
    /// 任何一条不合法就整体拒绝，不做部分接受。
    /// </summary>
    public static TransferManifest Deserialize(ReadOnlySpan<byte> data)
    {
        var reader = new SpanReader(data);

        var magic = reader.ReadBytes(Magic.Length);
        if (!magic.SequenceEqual(Magic))
        {
            throw new InvalidManifestException("魔数不匹配，这不是一份清单。");
        }

        var version = reader.ReadByte();
        if (version != FormatVersion)
        {
            throw new InvalidManifestException($"清单版本 {version} 不受支持（本实现只认 {FormatVersion}）。");
        }

        var leafSize = reader.ReadInt32();
        var pieceSize = reader.ReadInt32();

        MerkleParameters parameters;
        try
        {
            parameters = new MerkleParameters(leafSize, pieceSize);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            throw new InvalidManifestException($"清单里的分片参数不合法：{ex.Message}");
        }

        var entryCount = reader.ReadInt32();
        if (entryCount is <= 0 or > MaxEntries)
        {
            throw new InvalidManifestException($"条目数 {entryCount} 不在 1~{MaxEntries} 之间。");
        }

        var entries = new List<ManifestEntry>(Math.Min(entryCount, 1024));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long totalPieces = 0;
        var previousPath = string.Empty;

        for (var i = 0; i < entryCount; i++)
        {
            var pathLength = reader.ReadUInt16();
            if (pathLength == 0 || pathLength > SafePath.MaxPathLength)
            {
                throw new InvalidManifestException($"第 {i} 条的路径字节数 {pathLength} 不合法。");
            }

            var path = Encoding.UTF8.GetString(reader.ReadBytes(pathLength));
            if (!SafePath.IsSafe(path, out var pathError))
            {
                throw new InvalidManifestException($"第 {i} 条的路径不安全：{pathError}");
            }

            if (!seen.Add(path))
            {
                throw new InvalidManifestException($"路径重复（忽略大小写）：\"{path}\"。");
            }

            // 规范形式要求已排序。收到未排序的清单说明对端实现不规范，
            // 或有人想用不同的顺序造出同内容不同哈希的清单。
            if (i > 0 && string.CompareOrdinal(previousPath, path) >= 0)
            {
                throw new InvalidManifestException(
                    $"清单未按规范顺序排列：\"{path}\" 出现在 \"{previousPath}\" 之后。");
            }

            previousPath = path;

            var length = reader.ReadInt64();
            if (length < 0)
            {
                throw new InvalidManifestException($"路径 \"{path}\" 的长度为负数 {length}。");
            }

            var root = reader.ReadHash();

            // 分片数是从长度推导的，不从流里读 —— 这样攻击者没法在这里撒谎，
            // 也就无法诱导一次巨额分配。推导完先查上限再分配。
            var pieceCount = parameters.PieceCount(length);
            totalPieces += pieceCount;
            if (totalPieces > MaxTotalPieces)
            {
                throw new InvalidManifestException($"总分片数超过上限 {MaxTotalPieces}。");
            }

            var pieceRoots = ImmutableArray.CreateBuilder<Hash256>((int)pieceCount);
            for (long p = 0; p < pieceCount; p++)
            {
                pieceRoots.Add(reader.ReadHash());
            }

            entries.Add(new ManifestEntry(path, length, root, pieceRoots.MoveToImmutable()));
        }

        var directoryCount = reader.ReadInt32();
        if (directoryCount is < 0 or > MaxEntries)
        {
            throw new InvalidManifestException($"空目录数 {directoryCount} 不在 0~{MaxEntries} 之间。");
        }

        var directories = new List<string>(Math.Min(directoryCount, 1024));
        var previousDirectory = string.Empty;
        for (var i = 0; i < directoryCount; i++)
        {
            var length = reader.ReadUInt16();
            if (length == 0 || length > SafePath.MaxPathLength)
            {
                throw new InvalidManifestException($"第 {i} 个空目录的路径字节数 {length} 不合法。");
            }

            var directory = Encoding.UTF8.GetString(reader.ReadBytes(length));
            if (!SafePath.IsSafe(directory, out var directoryError))
            {
                throw new InvalidManifestException($"第 {i} 个空目录的路径不安全：{directoryError}");
            }

            if (i > 0 && string.CompareOrdinal(previousDirectory, directory) >= 0)
            {
                throw new InvalidManifestException(
                    $"空目录未按规范顺序排列：\"{directory}\" 出现在 \"{previousDirectory}\" 之后。");
            }

            previousDirectory = directory;
            directories.Add(directory);
        }

        if (!reader.IsAtEnd)
        {
            throw new InvalidManifestException($"清单末尾有 {reader.Remaining} 字节多余数据。");
        }

        // 走 Create 而不是直接构造：让「从字节还原」与「从条目新建」
        // 经过完全相同的校验，避免两条路径的检查漂移。
        return Create(parameters, entries, directories);
    }

    private int EstimateSize()
    {
        var size = Magic.Length + 1 + 4 + 4 + 4 + 4;
        foreach (var entry in Entries)
        {
            size += 2 + Encoding.UTF8.GetByteCount(entry.Path) + 8 + Hash256.Size
                    + (entry.PieceCount * Hash256.Size);
        }

        foreach (var directory in Directories)
        {
            size += 2 + Encoding.UTF8.GetByteCount(directory);
        }

        return size;
    }

    private static Hash256 ComputeHash(
        MerkleParameters parameters,
        ImmutableArray<ManifestEntry> entries,
        ImmutableArray<string> directories)
    {
        var writer = new ArrayBufferWriter<byte>(1024);
        writer.GetSpan(1)[0] = ManifestHashPrefix;
        writer.Advance(1);
        WriteCanonical(writer, parameters, entries, directories);

        Span<byte> digest = stackalloc byte[Hash256.Size];
        SHA256.HashData(writer.WrittenSpan, digest);
        return new Hash256(digest);
    }

    private static void WriteCanonical(
        IBufferWriter<byte> writer,
        MerkleParameters parameters,
        ImmutableArray<ManifestEntry> entries,
        ImmutableArray<string> directories)
    {
        writer.Write(Magic);
        WriteByte(writer, FormatVersion);
        WriteInt32(writer, parameters.LeafSize);
        WriteInt32(writer, parameters.PieceSize);
        WriteInt32(writer, entries.Length);

        foreach (var entry in entries)
        {
            var pathBytes = Encoding.UTF8.GetBytes(entry.Path);
            WriteUInt16(writer, (ushort)pathBytes.Length);
            writer.Write(pathBytes);
            WriteInt64(writer, entry.Length);
            WriteHash(writer, entry.Root);

            foreach (var pieceRoot in entry.PieceRoots)
            {
                WriteHash(writer, pieceRoot);
            }
        }

        WriteInt32(writer, directories.Length);
        foreach (var directory in directories)
        {
            var pathBytes = Encoding.UTF8.GetBytes(directory);
            WriteUInt16(writer, (ushort)pathBytes.Length);
            writer.Write(pathBytes);
        }
    }

    private static void WriteByte(IBufferWriter<byte> writer, byte value)
    {
        writer.GetSpan(1)[0] = value;
        writer.Advance(1);
    }

    private static void WriteUInt16(IBufferWriter<byte> writer, ushort value)
    {
        BinaryPrimitives.WriteUInt16BigEndian(writer.GetSpan(sizeof(ushort)), value);
        writer.Advance(sizeof(ushort));
    }

    private static void WriteInt32(IBufferWriter<byte> writer, int value)
    {
        BinaryPrimitives.WriteInt32BigEndian(writer.GetSpan(sizeof(int)), value);
        writer.Advance(sizeof(int));
    }

    private static void WriteInt64(IBufferWriter<byte> writer, long value)
    {
        BinaryPrimitives.WriteInt64BigEndian(writer.GetSpan(sizeof(long)), value);
        writer.Advance(sizeof(long));
    }

    /// <summary>
    /// 落盘时需要创建的全部目录：文件路径隐含的 + 显式列出的空目录，
    /// 按深度排序好让父目录先创建。
    /// </summary>
    public ImmutableArray<string> GetAllDirectories()
    {
        var all = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var entry in Entries)
        {
            var lastSlash = entry.Path.LastIndexOf('/');
            if (lastSlash > 0)
            {
                AddWithAncestors(all, entry.Path[..lastSlash]);
            }
        }

        foreach (var directory in Directories)
        {
            AddWithAncestors(all, directory);
        }

        // 按段数升序：父目录一定排在子目录之前
        return [.. all.OrderBy(static d => d.Count(static c => c == '/')).ThenBy(static d => d, StringComparer.Ordinal)];

        static void AddWithAncestors(SortedSet<string> target, string directory)
        {
            var current = directory;
            while (current.Length > 0)
            {
                if (!target.Add(current))
                {
                    return;   // 这一条及其祖先都已加过
                }

                var slash = current.LastIndexOf('/');
                current = slash <= 0 ? string.Empty : current[..slash];
            }
        }
    }

    private static void WriteHash(IBufferWriter<byte> writer, Hash256 hash)
    {
        hash.CopyTo(writer.GetSpan(Hash256.Size)[..Hash256.Size]);
        writer.Advance(Hash256.Size);
    }

    /// <summary>只前进的读取器。越界一律抛 <see cref="InvalidManifestException"/>。</summary>
    private ref struct SpanReader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _position;

        public bool IsAtEnd => _position >= _data.Length;

        public int Remaining => _data.Length - _position;

        public ReadOnlySpan<byte> ReadBytes(int count)
        {
            EnsureAvailable(count);
            var slice = _data.Slice(_position, count);
            _position += count;
            return slice;
        }

        public byte ReadByte() => ReadBytes(1)[0];

        public ushort ReadUInt16() => BinaryPrimitives.ReadUInt16BigEndian(ReadBytes(sizeof(ushort)));

        public int ReadInt32() => BinaryPrimitives.ReadInt32BigEndian(ReadBytes(sizeof(int)));

        public long ReadInt64() => BinaryPrimitives.ReadInt64BigEndian(ReadBytes(sizeof(long)));

        public Hash256 ReadHash() => new(ReadBytes(Hash256.Size));

        private void EnsureAvailable(int count)
        {
            if (count < 0 || _position + count > _data.Length)
            {
                throw new InvalidManifestException(
                    $"清单数据被截断：位置 {_position} 处需要 {count} 字节，只剩 {Remaining} 字节。");
            }
        }
    }
}

/// <summary>清单数据不合法或不可信。</summary>
public sealed class InvalidManifestException(string message) : Exception(message);
