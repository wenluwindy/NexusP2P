using NexusP2P.Core.Crypto;
using NexusP2P.Transfer.Storage;
using NexusP2P.Transfer.Tests.Storage;

namespace NexusP2P.Transfer.Tests.FanOut;

/// <summary>
/// Task 9.1：密文缓存（AD-13）。正确性不依赖缓存 ——
/// 容量 0 的退化路径与命中路径产出的密文必须逐字节一致。
/// </summary>
public sealed class CipherPieceCacheTests : IDisposable
{
    private readonly TransferFixture _fixture = new TransferFixture()
        .With("a.bin", 16 * 1024, seed: 1)
        .With("b.bin", 6 * 1024, seed: 2);

    private readonly TransferSecret _secret = TransferSecret.Generate();

    public void Dispose() => _fixture.Dispose();

    private async Task<(CipherPieceCache Cache, PieceLocator Locator)> CreateAsync(long capacity)
    {
        var manifest = await _fixture.BuildManifestAsync();
        var source = new MemoryPieceSource(manifest, _fixture.Files);
        return (new CipherPieceCache(manifest, source, _secret, capacity), new PieceLocator(manifest));
    }

    [Fact]
    public async Task 命中时不再读盘不再加密()
    {
        var (cache, locator) = await CreateAsync(CipherPieceCache.DefaultCapacityBytes);
        using var scope = cache;

        var location = locator.Locate(0);

        var first = await cache.GetCiphertextAsync(location);
        var second = await cache.GetCiphertextAsync(location);

        Assert.Equal(1, cache.Encryptions);   // 只加密了一次
        Assert.Equal(1, cache.Hits);
        Assert.True(first.Span.SequenceEqual(second.Span));
    }

    [Fact]
    public async Task 并发请求同一分片只加密一次()
    {
        var (cache, locator) = await CreateAsync(CipherPieceCache.DefaultCapacityBytes);
        using var scope = cache;

        var location = locator.Locate(1);

        var results = await Task.WhenAll(Enumerable.Range(0, 16)
            .Select(i => Task.Run(async () => await cache.GetCiphertextAsync(location))));

        Assert.Equal(1, cache.Encryptions);
        foreach (var ciphertext in results)
        {
            Assert.True(results[0].Span.SequenceEqual(ciphertext.Span));
        }
    }

    [Fact]
    public async Task 容量_0_全部旁路_端到端结果不变()
    {
        var (bypass, locator) = await CreateAsync(0);
        using var bypassScope = bypass;
        var (cached, _) = await CreateAsync(CipherPieceCache.DefaultCapacityBytes);
        using var cachedScope = cached;

        for (var i = 0; i < locator.TotalPieces; i++)
        {
            var location = locator.Locate(i);
            var direct = await bypass.GetCiphertextAsync(location);
            var viaCache = await cached.GetCiphertextAsync(location);

            Assert.True(direct.Span.SequenceEqual(viaCache.Span),
                $"分片 {i} 的密文在旁路与缓存两条路径下不一致");
        }

        Assert.Equal(0, bypass.UsedBytes);   // 旁路：什么都不留
        Assert.Equal(locator.TotalPieces, (int)bypass.Encryptions);   // 每次都真加密
    }

    [Fact]
    public async Task 密文与_V1_单链路路径逐字节一致()
    {
        // V1 路径：PieceCipher 直接加密 —— AD-13 的前提是两条路径产出完全相同
        var manifest = await _fixture.BuildManifestAsync();
        var locator = new PieceLocator(manifest);
        var source = new MemoryPieceSource(manifest, _fixture.Files);
        using var cache = new CipherPieceCache(manifest, source, _secret);
        using var v1Cipher = new PieceCipher(_secret, manifest.Hash);

        for (var i = 0; i < locator.TotalPieces; i++)
        {
            var location = locator.Locate(i);

            var plaintext = _fixture.Piece(manifest, location.FileIndex, location.LocalPieceIndex);
            var expected = new byte[PieceCipher.GetCiphertextLength(location.Length)];
            v1Cipher.Encrypt(location.FileIndex, location.LocalPieceIndex, plaintext.Span, expected);

            var actual = await cache.GetCiphertextAsync(location);
            Assert.True(actual.Span.SequenceEqual(expected), $"分片 {i} 与 V1 路径不一致");
        }
    }

    [Fact]
    public async Task LRU_淘汰后内存占用有界()
    {
        // 分片密文 = 4096 + 16 字节。容量给 3 个分片多一点：第 4 个进来要淘汰最旧的
        var pieceCiphertext = 4096 + PieceCipher.TagSize;
        var (cache, locator) = await CreateAsync(pieceCiphertext * 3 + 100);
        using var scope = cache;

        Assert.True(locator.TotalPieces >= 5, "测试内容至少要有 5 个分片");

        for (var i = 0; i < locator.TotalPieces; i++)
        {
            await cache.GetCiphertextAsync(locator.Locate(i));
            Assert.True(cache.UsedBytes <= pieceCiphertext * 3 + 100,
                $"缓存占用 {cache.UsedBytes} 超出容量上限");
        }

        // 最早的分片已被淘汰：再取要重新加密
        var encryptionsBefore = cache.Encryptions;
        await cache.GetCiphertextAsync(locator.Locate(0));
        Assert.Equal(encryptionsBefore + 1, cache.Encryptions);

        // 最新用过的分片还在：命中
        var hitsBefore = cache.Hits;
        await cache.GetCiphertextAsync(locator.Locate(0));
        Assert.Equal(hitsBefore + 1, cache.Hits);
    }

    [Fact]
    public async Task 单个分片装不下容量时直接旁路不占缓存()
    {
        var (cache, locator) = await CreateAsync(10);   // 比任何分片密文都小
        using var scope = cache;

        var ciphertext = await cache.GetCiphertextAsync(locator.Locate(0));

        Assert.False(ciphertext.IsEmpty);
        Assert.Equal(0, cache.UsedBytes);
    }
}
