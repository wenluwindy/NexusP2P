using NexusP2P.Core.Hashing;

namespace NexusP2P.Core.Tests.Hashing;

public sealed class FileHasherTests
{
    /// <summary>小参数让边界用例跑得快：1 KiB 叶子、4 KiB 分片（4 个叶子）。</summary>
    private static readonly MerkleParameters Small = new(1024, 4096);

    private static byte[] Pattern(int length)
    {
        var bytes = new byte[length];
        for (var i = 0; i < length; i++)
        {
            // 用非平凡的模式，纯零字节会掩盖偏移错误。
            // 显式掩码到一个字节：构建开了 CheckForOverflowUnderflow，
            // 裸的 (byte) 收窄在越界时会抛 OverflowException。
            bytes[i] = (byte)(((i * 31) ^ (i >> 8)) & 0xFF);
        }

        return bytes;
    }

    private static async Task<FileHashResult> HashAsync(byte[] content, MerkleParameters? parameters = null)
    {
        using var hasher = new FileHasher(parameters ?? Small);
        using var stream = new MemoryStream(content, writable: false);
        return await hasher.ComputeAsync(stream);
    }

    [Fact]
    public async Task 空文件产出一个分片()
    {
        var result = await HashAsync([]);

        Assert.Equal(0, result.Length);
        Assert.Single(result.PieceRoots);
        Assert.NotEqual(Hash256.Zero, result.Root);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1023)]
    [InlineData(1024)]      // 恰好一个叶子
    [InlineData(1025)]
    [InlineData(4095)]
    [InlineData(4096)]      // 恰好一个分片
    [InlineData(4097)]
    [InlineData(8192)]      // 恰好两个分片
    [InlineData(12289)]
    public async Task 长度与分片数与参数一致(int length)
    {
        var result = await HashAsync(Pattern(length));

        Assert.Equal(length, result.Length);
        Assert.Equal(Small.PieceCount(length), result.PieceCount);
    }

    [Fact]
    public async Task 相同内容产出相同根()
    {
        var content = Pattern(10_000);

        var first = await HashAsync(content);
        var second = await HashAsync(content);

        Assert.Equal(first.Root, second.Root);

        // 必须转成数组再比：ImmutableArray<T> 的 IEquatable 是底层数组的引用相等
        Assert.Equal(first.PieceRoots.ToArray(), second.PieceRoots.ToArray());
    }

    [Fact]
    public async Task 相等性按根比较而不是按底层数组引用()
    {
        // 把 ImmutableArray 引用相等这个坑钉住：FileHashResult 显式覆盖了
        // Equals，若哪天被删掉或退回自动生成的版本，这条会失败。
        var content = Pattern(10_000);

        var first = await HashAsync(content);
        var second = await HashAsync(content);

        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
    }

    [Fact]
    public async Task 任何一个字节的改动都会改变根()
    {
        var content = Pattern(10_000);
        var baseline = await HashAsync(content);

        // 抽查几个关键位置：首字节、叶子边界、分片边界、末字节
        foreach (var index in new[] { 0, 1023, 1024, 4095, 4096, 9999 })
        {
            var mutated = Pattern(10_000);
            mutated[index] ^= 0xFF;

            var actual = await HashAsync(mutated);

            Assert.NotEqual(baseline.Root, actual.Root);
        }
    }

    [Fact]
    public async Task 内容截断会改变根()
    {
        // 长度被绑进根，所以截断必然改根 —— 即使被截掉的部分全是零
        var full = await HashAsync(Pattern(5000));
        var truncated = await HashAsync(Pattern(5000).AsSpan(0, 4000).ToArray());

        Assert.NotEqual(full.Root, truncated.Root);
    }

    [Fact]
    public async Task 不可_seek_的流也能处理()
    {
        // 刻意不依赖 Length 或 Seek，好让同一份代码能处理管道与网络流
        var content = Pattern(10_000);
        var expected = await HashAsync(content);

        using var hasher = new FileHasher(Small);
        using var stream = new NonSeekableStream(content);
        var actual = await hasher.ComputeAsync(stream);

        Assert.Equal(expected.Root, actual.Root);
        Assert.Equal(expected.Length, actual.Length);
    }

    [Fact]
    public async Task 分片读取的流也能处理()
    {
        // 每次只返回几个字节，模拟网络流的碎片化读取。
        // 如果实现里假设了「一次 Read 就能填满缓冲区」，这里会崩。
        var content = Pattern(10_000);
        var expected = await HashAsync(content);

        using var hasher = new FileHasher(Small);
        using var stream = new DribbleStream(content, bytesPerRead: 7);
        var actual = await hasher.ComputeAsync(stream);

        Assert.Equal(expected.Root, actual.Root);
        Assert.Equal(expected.PieceRoots.ToArray(), actual.PieceRoots.ToArray());
    }

    [Fact]
    public async Task 进度报告单调递增且终值等于长度()
    {
        // Progress<T> 在没有 SynchronizationContext 时把回调投到线程池，
        // 多次 Report 可能并发执行，所以收集容器必须加锁。
        var reported = new List<long>();
        var gate = new Lock();
        var progress = new Progress<long>(v =>
        {
            lock (gate)
            {
                reported.Add(v);
            }
        });

        using var hasher = new FileHasher(Small);
        using var stream = new MemoryStream(Pattern(10_000));
        var result = await hasher.ComputeAsync(stream, progress);

        // Progress<T> 是异步投递的，给它一点时间收尾
        await Task.Delay(100);

        long[] snapshot;
        lock (gate)
        {
            snapshot = [.. reported];
        }

        Assert.NotEmpty(snapshot);
        Assert.Equal(result.Length, snapshot.Max());
    }

    [Fact]
    public async Task 取消会抛出_OperationCanceledException()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        using var hasher = new FileHasher(Small);
        using var stream = new MemoryStream(Pattern(10_000));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => hasher.ComputeAsync(stream, cancellationToken: cts.Token));
    }

    [Fact]
    public async Task 返回的分片根列表不会被文件根计算破坏()
    {
        // ComputeRoot 是原地折叠的。如果 FileHasher 没有传副本进去，
        // 返回给调用方的分片根就会被折叠过程覆盖成一堆中间节点。
        var result = await HashAsync(Pattern(20_000));

        Assert.True(result.PieceCount > 1, "用例需要多个分片才有意义");

        // 用分片根重新算一次文件根，必须与返回的根一致
        using var hasher = new MerkleHasher();
        var recomputed = hasher.ComputeFileRoot([.. result.PieceRoots], result.Length);

        Assert.Equal(result.Root, recomputed);
    }

    [Fact]
    public async Task 默认参数下的大文件也正确()
    {
        // 用默认的 64 KiB/1 MiB 参数跑一次真实尺寸，确认没有把小参数写死进逻辑
        var content = Pattern((2 * MerkleParameters.DefaultPieceSize) + 12345);

        var result = await HashAsync(content, MerkleParameters.Default);

        Assert.Equal(content.Length, result.Length);
        Assert.Equal(3, result.PieceCount);
    }

    private sealed class NonSeekableStream(byte[] content) : MemoryStream(content, writable: false)
    {
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
    }

    private sealed class DribbleStream(byte[] content, int bytesPerRead) : Stream
    {
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var available = Math.Min(Math.Min(bytesPerRead, buffer.Length), content.Length - _position);
            if (available <= 0)
            {
                return 0;
            }

            content.AsSpan(_position, available).CopyTo(buffer);
            _position += available;
            return available;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
