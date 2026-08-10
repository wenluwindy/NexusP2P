using System.Collections.Immutable;
using NexusP2P.Core.Hashing;
using NexusP2P.Core.Manifest;

namespace NexusP2P.Core.Tests.Manifest;

public sealed class TransferManifestTests
{
    private static readonly MerkleParameters Small = new(1024, 4096);

    private static Hash256 H(byte seed)
    {
        var bytes = new byte[Hash256.Size];
        Array.Fill(bytes, seed);
        return new Hash256(bytes);
    }

    /// <summary>造一个长度与分片数自洽的条目。</summary>
    private static ManifestEntry Entry(string path, long length, byte seed = 1)
    {
        var pieceCount = (int)Small.PieceCount(length);
        var roots = ImmutableArray.CreateBuilder<Hash256>(pieceCount);
        for (var i = 0; i < pieceCount; i++)
        {
            roots.Add(H((byte)((seed + i) & 0xFF)));
        }

        return new ManifestEntry(path, length, H(seed), roots.MoveToImmutable());
    }

    private static TransferManifest Manifest(params ManifestEntry[] entries) =>
        TransferManifest.Create(Small, entries);

    // ---- 构造与不变式 ----

    [Fact]
    public void 单文件清单()
    {
        var manifest = Manifest(Entry("a.txt", 5000));

        Assert.Single(manifest.Entries);
        Assert.Equal(5000, manifest.TotalLength);
        Assert.Equal(2, manifest.TotalPieces);
        Assert.NotEqual(Hash256.Zero, manifest.Hash);
    }

    [Fact]
    public void 文件夹清单含嵌套目录()
    {
        var manifest = Manifest(
            Entry("MyStuff/readme.txt", 100),
            Entry("MyStuff/sub/deep/a.bin", 10_000),
            Entry("MyStuff/sub/b.bin", 0));

        Assert.Equal(3, manifest.Entries.Length);
        Assert.Equal(10_100, manifest.TotalLength);
    }

    [Fact]
    public void 空文件也有一个分片()
    {
        var manifest = Manifest(Entry("empty.bin", 0));

        Assert.Equal(1, manifest.Entries[0].PieceCount);
        Assert.Equal(1, manifest.TotalPieces);
    }

    [Fact]
    public void 条目按路径序数升序排列()
    {
        var manifest = Manifest(
            Entry("z.txt", 1),
            Entry("a.txt", 1),
            Entry("m/b.txt", 1));

        var paths = manifest.Entries.Select(e => e.Path).ToArray();

        Assert.Equal(["a.txt", "m/b.txt", "z.txt"], paths);
    }

    [Fact]
    public void 输入顺序不影响清单哈希()
    {
        // 排序是规范化的一部分。同一份内容无论条目怎么排，哈希必须相同 ——
        // 否则「关掉重开、用新码续传」会因为哈希变了而失效。
        var a = Entry("a.txt", 100);
        var b = Entry("b.txt", 200);
        var c = Entry("c/d.txt", 300);

        Assert.Equal(Manifest(a, b, c).Hash, Manifest(c, a, b).Hash);
        Assert.Equal(Manifest(a, b, c).Hash, Manifest(b, c, a).Hash);
    }

    [Fact]
    public void 空清单被拒绝()
    {
        Assert.Throws<ArgumentException>(() => TransferManifest.Create(Small, []));
    }

    [Fact]
    public void 重复路径被拒绝()
    {
        Assert.Throws<ArgumentException>(() => Manifest(Entry("a.txt", 1), Entry("a.txt", 2)));
    }

    [Fact]
    public void 仅大小写不同的重复路径也被拒绝()
    {
        // Windows 上 a.txt 与 A.TXT 是同一个文件；放过去后一个会静默覆盖前一个
        Assert.Throws<ArgumentException>(() => Manifest(Entry("a.txt", 1), Entry("A.TXT", 2)));
    }

    [Fact]
    public void 不安全的路径在建条目时就被拒绝()
    {
        Assert.Throws<UnsafePathException>(() => Entry("../escape.txt", 1));
    }

    [Fact]
    public void 分片数与长度不符的条目被拒绝()
    {
        // 手工造一个撒谎的条目：长度需要 2 个分片，只给 1 个
        var bogus = new ManifestEntry("a.txt", 5000, H(1), [H(1)]);

        var ex = Assert.Throws<ArgumentException>(() => TransferManifest.Create(Small, [bogus]));
        Assert.Contains("分片", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 分片根为空的条目被拒绝()
    {
        Assert.Throws<ArgumentException>(() => new ManifestEntry("a.txt", 0, H(1), []));
    }

    [Fact]
    public void 长度为负的条目被拒绝()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ManifestEntry("a.txt", -1, H(1), [H(1)]));
    }

    // ---- 序列化往返 ----

    [Fact]
    public void 序列化往返无损()
    {
        var original = Manifest(
            Entry("MyStuff/a.txt", 5000, seed: 3),
            Entry("MyStuff/sub/b.bin", 0, seed: 7),
            Entry("MyStuff/我的文件.dat", 12_345, seed: 11));

        var restored = TransferManifest.Deserialize(original.Serialize());

        Assert.Equal(original.Hash, restored.Hash);
        Assert.Equal(original.TotalLength, restored.TotalLength);
        Assert.Equal(original.TotalPieces, restored.TotalPieces);
        Assert.Equal(original.Parameters, restored.Parameters);
        Assert.Equal(original.Entries.ToArray(), restored.Entries.ToArray());

        for (var i = 0; i < original.Entries.Length; i++)
        {
            Assert.Equal(original.Entries[i].PieceRoots.ToArray(), restored.Entries[i].PieceRoots.ToArray());
        }
    }

    [Fact]
    public void 序列化是确定性的()
    {
        var manifest = Manifest(Entry("a.txt", 5000), Entry("b.txt", 1));

        Assert.Equal(manifest.Serialize(), manifest.Serialize());
    }

    [Fact]
    public void 非默认分片参数也能往返()
    {
        var parameters = new MerkleParameters(2048, 8192);
        var pieceCount = (int)parameters.PieceCount(20_000);
        var roots = Enumerable.Range(0, pieceCount).Select(i => H((byte)i)).ToImmutableArray();
        var manifest = TransferManifest.Create(parameters, [new ManifestEntry("a.bin", 20_000, H(9), roots)]);

        var restored = TransferManifest.Deserialize(manifest.Serialize());

        Assert.Equal(2048, restored.Parameters.LeafSize);
        Assert.Equal(8192, restored.Parameters.PieceSize);
        Assert.Equal(manifest.Hash, restored.Hash);
    }

    // ---- 内容任何改动都改哈希 ----

    [Fact]
    public void 路径改动会改变清单哈希()
    {
        Assert.NotEqual(Manifest(Entry("a.txt", 100)).Hash, Manifest(Entry("b.txt", 100)).Hash);
    }

    [Fact]
    public void 长度改动会改变清单哈希()
    {
        Assert.NotEqual(Manifest(Entry("a.txt", 100)).Hash, Manifest(Entry("a.txt", 101)).Hash);
    }

    [Fact]
    public void 分片参数改动会改变清单哈希()
    {
        var roots = ImmutableArray.Create(H(1));
        var entry = new ManifestEntry("a.txt", 100, H(1), roots);

        var withSmall = TransferManifest.Create(new MerkleParameters(1024, 4096), [entry]);
        var withOther = TransferManifest.Create(new MerkleParameters(2048, 8192), [entry]);

        Assert.NotEqual(withSmall.Hash, withOther.Hash);
    }

    // ---- 解析恶意与畸形输入 ----

    [Fact]
    public void 魔数不对的数据被拒绝()
    {
        var data = new byte[64];

        Assert.Throws<InvalidManifestException>(() => TransferManifest.Deserialize(data));
    }

    [Fact]
    public void 空数据被拒绝()
    {
        Assert.Throws<InvalidManifestException>(() => TransferManifest.Deserialize([]));
    }

    [Fact]
    public void 版本号不认识的数据被拒绝()
    {
        var data = Manifest(Entry("a.txt", 100)).Serialize();
        data[8] = 99;   // 魔数 8 字节之后是版本号

        var ex = Assert.Throws<InvalidManifestException>(() => TransferManifest.Deserialize(data));
        Assert.Contains("版本", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(10)]
    [InlineData(30)]
    [InlineData(60)]
    public void 被截断的数据被拒绝而不是崩溃(int keepBytes)
    {
        var full = Manifest(Entry("a.txt", 5000)).Serialize();
        var truncated = full.AsSpan(0, Math.Min(keepBytes, full.Length)).ToArray();

        Assert.Throws<InvalidManifestException>(() => TransferManifest.Deserialize(truncated));
    }

    [Fact]
    public void 逐字节截断都不会抛出意料之外的异常()
    {
        // 模糊测试的穷人版：把每一个前缀都喂进去，确认只会抛 InvalidManifestException，
        // 不会出现 IndexOutOfRange、OverflowException 之类的解析崩溃。
        var full = Manifest(Entry("dir/a.txt", 9000), Entry("dir/b.txt", 0)).Serialize();

        for (var length = 0; length < full.Length; length++)
        {
            var prefix = full.AsSpan(0, length).ToArray();

            var ex = Record.Exception(() => TransferManifest.Deserialize(prefix));

            Assert.True(
                ex is InvalidManifestException,
                $"截断到 {length} 字节时抛出了 {ex?.GetType().Name ?? "无异常"}，应为 InvalidManifestException");
        }
    }

    [Fact]
    public void 末尾有多余数据被拒绝()
    {
        var full = Manifest(Entry("a.txt", 100)).Serialize();
        var padded = full.Concat(new byte[] { 0xFF, 0xFF }).ToArray();

        var ex = Assert.Throws<InvalidManifestException>(() => TransferManifest.Deserialize(padded));
        Assert.Contains("多余", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 条目数为零被拒绝()
    {
        var data = Manifest(Entry("a.txt", 100)).Serialize();
        // 魔数(8) + 版本(1) + leafSize(4) + pieceSize(4) = 17，之后是条目数
        data[17] = 0;
        data[18] = 0;
        data[19] = 0;
        data[20] = 0;

        Assert.Throws<InvalidManifestException>(() => TransferManifest.Deserialize(data));
    }

    [Fact]
    public void 条目数撒谎成天文数字不会诱发巨额分配()
    {
        var data = Manifest(Entry("a.txt", 100)).Serialize();
        // 把条目数改成 int.MaxValue。若实现照着这个数字预分配，就会 OOM。
        data[17] = 0x7F;
        data[18] = 0xFF;
        data[19] = 0xFF;
        data[20] = 0xFF;

        Assert.Throws<InvalidManifestException>(() => TransferManifest.Deserialize(data));
    }

    [Fact]
    public void 未按规范顺序排列的清单被拒绝()
    {
        // 手工拼一份两条目、但顺序反了的清单。若放过去，同一份内容会有
        // 两个不同的清单哈希，续传的锚点就不唯一了。
        var ordered = Manifest(Entry("a.txt", 0, seed: 1), Entry("b.txt", 0, seed: 2)).Serialize();

        // 两个条目结构完全一样（长度 0 -> 1 个分片），可以整块交换。
        // 头部 = 魔数(8) + 版本(1) + leafSize(4) + pieceSize(4) + 条目数(4)
        // 尾部 = 空目录数(4)
        const int Header = 8 + 1 + 4 + 4 + 4;
        const int Trailer = 4;
        var entrySize = (ordered.Length - Header - Trailer) / 2;

        var swapped = (byte[])ordered.Clone();
        ordered.AsSpan(Header + entrySize, entrySize).CopyTo(swapped.AsSpan(Header));
        ordered.AsSpan(Header, entrySize).CopyTo(swapped.AsSpan(Header + entrySize));

        var ex = Assert.Throws<InvalidManifestException>(() => TransferManifest.Deserialize(swapped));
        Assert.Contains("顺序", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 解析出的不安全路径被拒绝()
    {
        // 直接构造一份带穿越路径的清单字节流，绕过 Create 的校验，
        // 确认解析侧独立地把它挡住。
        var data = BuildRawManifest("../escape.txt", 0);

        var ex = Assert.Throws<InvalidManifestException>(() => TransferManifest.Deserialize(data));
        Assert.Contains("不安全", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 解析出的负长度被拒绝()
    {
        var data = BuildRawManifest("a.txt", -1);

        Assert.Throws<InvalidManifestException>(() => TransferManifest.Deserialize(data));
    }

    [Fact]
    public void 非法分片参数被拒绝()
    {
        var data = Manifest(Entry("a.txt", 100)).Serialize();
        // leafSize 置 0（魔数 8 + 版本 1 之后）
        data[9] = 0;
        data[10] = 0;
        data[11] = 0;
        data[12] = 0;

        var ex = Assert.Throws<InvalidManifestException>(() => TransferManifest.Deserialize(data));
        Assert.Contains("参数", ex.Message, StringComparison.Ordinal);
    }

    // ---- 空目录 ----

    [Fact]
    public void 空目录能往返()
    {
        var original = TransferManifest.Create(
            Small,
            [Entry("proj/src/a.cs", 100)],
            ["proj/logs", "proj/tmp/cache"]);

        var restored = TransferManifest.Deserialize(original.Serialize());

        Assert.Equal(["proj/logs", "proj/tmp/cache"], restored.Directories.ToArray());
        Assert.Equal(original.Hash, restored.Hash);
    }

    [Fact]
    public void 空目录参与清单哈希()
    {
        var withoutDirs = TransferManifest.Create(Small, [Entry("a.txt", 100)]);
        var withDirs = TransferManifest.Create(Small, [Entry("a.txt", 100)], ["logs"]);

        Assert.NotEqual(withoutDirs.Hash, withDirs.Hash);
    }

    [Fact]
    public void 空目录输入顺序不影响哈希()
    {
        var first = TransferManifest.Create(Small, [Entry("a.txt", 1)], ["z", "a", "m/n"]);
        var second = TransferManifest.Create(Small, [Entry("a.txt", 1)], ["m/n", "z", "a"]);

        Assert.Equal(first.Hash, second.Hash);
        Assert.Equal(["a", "m/n", "z"], first.Directories.ToArray());
    }

    [Fact]
    public void 不安全的空目录路径被拒绝()
    {
        Assert.Throws<UnsafePathException>(
            () => TransferManifest.Create(Small, [Entry("a.txt", 1)], ["../escape"]));
    }

    [Fact]
    public void 同一个路径既是文件又是目录会被拒绝()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => TransferManifest.Create(Small, [Entry("a", 1)], ["a"]));

        Assert.Contains("同时被当作", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 重复的空目录被拒绝()
    {
        Assert.Throws<ArgumentException>(
            () => TransferManifest.Create(Small, [Entry("a.txt", 1)], ["logs", "LOGS"]));
    }

    [Fact]
    public void GetAllDirectories_包含文件路径隐含的目录()
    {
        var manifest = TransferManifest.Create(
            Small,
            [Entry("proj/src/deep/a.cs", 100), Entry("proj/readme.md", 10)],
            ["proj/logs"]);

        var all = manifest.GetAllDirectories();

        Assert.Equal(["proj", "proj/logs", "proj/src", "proj/src/deep"], all.ToArray());
    }

    [Fact]
    public void GetAllDirectories_父目录排在子目录之前()
    {
        // 落盘时按这个顺序创建目录，父目录必须先存在
        var manifest = TransferManifest.Create(
            Small,
            [Entry("a/b/c/d/e.txt", 1)],
            []);

        var all = manifest.GetAllDirectories();

        for (var i = 1; i < all.Length; i++)
        {
            var parentDepth = all[i - 1].Count(c => c == '/');
            var childDepth = all[i].Count(c => c == '/');
            Assert.True(parentDepth <= childDepth, $"\"{all[i - 1]}\" 排在了 \"{all[i]}\" 之前但更深");
        }

        Assert.Equal(["a", "a/b", "a/b/c", "a/b/c/d"], all.ToArray());
    }

    [Fact]
    public void GetAllDirectories_单文件在根下时为空()
    {
        var manifest = Manifest(Entry("a.txt", 1));

        Assert.Empty(manifest.GetAllDirectories());
    }

    /// <summary>手工拼一份单条目清单，可以放入 Create 不允许的非法值。</summary>
    private static byte[] BuildRawManifest(string path, long length)
    {
        var pathBytes = System.Text.Encoding.UTF8.GetBytes(path);
        var pieceCount = length < 0 ? 1 : (int)Small.PieceCount(length);

        var buffer = new List<byte>();
        buffer.AddRange("NXP2PMAN"u8);
        buffer.Add(1);
        buffer.AddRange(BigEndian(Small.LeafSize));
        buffer.AddRange(BigEndian(Small.PieceSize));
        buffer.AddRange(BigEndian(1));
        buffer.AddRange([(byte)(pathBytes.Length >> 8), (byte)(pathBytes.Length & 0xFF)]);
        buffer.AddRange(pathBytes);
        buffer.AddRange(BigEndian64(length));
        buffer.AddRange(H(1).ToArray());
        for (var i = 0; i < pieceCount; i++)
        {
            buffer.AddRange(H((byte)i).ToArray());
        }

        buffer.AddRange(BigEndian(0));   // 空目录数

        return [.. buffer];

        static byte[] BigEndian(int value) =>
            [(byte)(value >> 24), (byte)((value >> 16) & 0xFF), (byte)((value >> 8) & 0xFF), (byte)(value & 0xFF)];

        static byte[] BigEndian64(long value)
        {
            var bytes = new byte[8];
            for (var i = 0; i < 8; i++)
            {
                bytes[i] = (byte)((value >> ((7 - i) * 8)) & 0xFF);
            }

            return bytes;
        }
    }
}
