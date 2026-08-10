using NexusP2P.Transfer.Storage;

namespace NexusP2P.Transfer.Tests.Storage;

public sealed class PieceBitfieldTests
{
    [Fact]
    public void 新建的位图全空()
    {
        var bitfield = new PieceBitfield(20);

        Assert.Equal(20, bitfield.Count);
        Assert.Equal(0, bitfield.SetCount);
        Assert.False(bitfield.IsComplete);
        Assert.All(Enumerable.Range(0, 20), i => Assert.False(bitfield[i]));
    }

    [Fact]
    public void 置位与清位()
    {
        var bitfield = new PieceBitfield(20);

        bitfield.Set(5);
        Assert.True(bitfield[5]);
        Assert.Equal(1, bitfield.SetCount);

        bitfield.Clear(5);
        Assert.False(bitfield[5]);
        Assert.Equal(0, bitfield.SetCount);
    }

    [Fact]
    public void 重复置位不会重复计数()
    {
        // SetCount 是增量维护的，重复操作必须幂等，
        // 否则进度会飘、IsComplete 会永远达不到
        var bitfield = new PieceBitfield(10);

        bitfield.Set(3);
        bitfield.Set(3);
        bitfield.Set(3);

        Assert.Equal(1, bitfield.SetCount);
    }

    [Fact]
    public void 重复清位不会重复计数()
    {
        var bitfield = new PieceBitfield(10);
        bitfield.Set(3);

        bitfield.Clear(3);
        bitfield.Clear(3);

        Assert.Equal(0, bitfield.SetCount);
    }

    [Fact]
    public void 全部置位后_IsComplete()
    {
        var bitfield = new PieceBitfield(17);   // 刻意不是 8 的倍数

        for (var i = 0; i < 17; i++)
        {
            bitfield.Set(i);
        }

        Assert.True(bitfield.IsComplete);
        Assert.Equal(17, bitfield.SetCount);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(20)]
    [InlineData(int.MaxValue)]
    public void 越界下标被拒绝(int index)
    {
        var bitfield = new PieceBitfield(20);

        Assert.Throws<ArgumentOutOfRangeException>(() => bitfield[index]);
        Assert.Throws<ArgumentOutOfRangeException>(() => bitfield.Set(index));
        Assert.Throws<ArgumentOutOfRangeException>(() => bitfield.Clear(index));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void 非正的分片数被拒绝(int count)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PieceBitfield(count));
    }

    [Fact]
    public void MissingIndices_按升序给出缺失的分片()
    {
        var bitfield = new PieceBitfield(10);
        bitfield.Set(0);
        bitfield.Set(3);
        bitfield.Set(9);

        Assert.Equal([1, 2, 4, 5, 6, 7, 8], bitfield.MissingIndices().ToArray());
    }

    [Fact]
    public void MissingButAvailableIn_给出该向对方请求的分片()
    {
        var mine = new PieceBitfield(8);
        mine.Set(0);
        mine.Set(1);

        var theirs = new PieceBitfield(8);
        theirs.Set(1);
        theirs.Set(2);
        theirs.Set(5);

        // 我缺 2~7，对方有 1、2、5 -> 交集是 2 和 5
        Assert.Equal([2, 5], mine.MissingButAvailableIn(theirs).ToArray());
    }

    [Fact]
    public void MissingButAvailableIn_长度不一致被拒绝()
    {
        var mine = new PieceBitfield(8);
        var theirs = new PieceBitfield(9);

        Assert.Throws<ArgumentException>(() => mine.MissingButAvailableIn(theirs).ToArray());
    }

    // ---- 序列化 ----

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(1000)]
    [InlineData(20_480)]      // 20 GiB / 1 MiB 的真实规模
    public void 序列化往返无损(int count)
    {
        var original = new PieceBitfield(count);
        for (var i = 0; i < count; i += 3)
        {
            original.Set(i);
        }

        var restored = PieceBitfield.Deserialize(original.Serialize(), count);

        Assert.Equal(original.Count, restored.Count);
        Assert.Equal(original.SetCount, restored.SetCount);
        for (var i = 0; i < count; i++)
        {
            Assert.Equal(original[i], restored[i]);
        }
    }

    [Fact]
    public void 二十四万分片的位图只有几十_KB()
    {
        // 位图要能塞进一条逻辑消息，所以尺寸必须可控
        var bitfield = new PieceBitfield(240_000);

        Assert.True(bitfield.Serialize().Length < 40_000,
            $"位图 {bitfield.Serialize().Length} 字节，超出预期");
    }

    [Fact]
    public void 分片数与清单不符被拒绝()
    {
        var bitfield = new PieceBitfield(10);

        Assert.Throws<ArgumentException>(() => PieceBitfield.Deserialize(bitfield.Serialize(), 11));
    }

    [Fact]
    public void 字节数不符被拒绝()
    {
        var data = new PieceBitfield(10).Serialize();

        Assert.Throws<ArgumentException>(() => PieceBitfield.Deserialize(data.AsSpan(0, data.Length - 1), 10));
        Assert.Throws<ArgumentException>(
            () => PieceBitfield.Deserialize(data.Concat(new byte[] { 0 }).ToArray(), 10));
    }

    [Fact]
    public void 短于头部被拒绝()
    {
        Assert.Throws<ArgumentException>(() => PieceBitfield.Deserialize(new byte[3], 10));
    }

    [Fact]
    public void 末字节的越界位被置起时被拒绝()
    {
        // 若放过，同一个位图会有多种字节表示，位图就没法用于相等性或哈希
        var data = new PieceBitfield(10).Serialize();
        data[^1] = 0xFF;   // 10 个分片只用到最后一个字节的低 2 位

        var ex = Assert.Throws<ArgumentException>(() => PieceBitfield.Deserialize(data, 10));
        Assert.Contains("超出分片数", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 分片数是八的倍数时不做越界位检查()
    {
        var bitfield = new PieceBitfield(16);
        for (var i = 0; i < 16; i++)
        {
            bitfield.Set(i);
        }

        var restored = PieceBitfield.Deserialize(bitfield.Serialize(), 16);

        Assert.True(restored.IsComplete);
    }
}
