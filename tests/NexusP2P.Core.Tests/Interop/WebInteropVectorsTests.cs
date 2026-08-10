using System.Security.Cryptography;
using System.Text;
using NexusP2P.Core.Crypto;
using NexusP2P.Core.Hashing;
using NexusP2P.Core.Manifest;

namespace NexusP2P.Core.Tests.Interop;

/// <summary>
/// 网页端（JavaScript）与本实现的一致性向量。
///
/// <para><b>为什么必须有这组测试。</b>网页端是 C# 之外的第二个协议实现。
/// 清单哈希、分片根、密钥派生、nonce 派生这四样只要有一位不同，症状是
/// 「网页发给 exe 时每一个分片都被拒收」，而对外的错误只会说
/// 「连续 16 个分片校验失败」—— 完全指不到真正的原因，而且两端各自的
/// 单测都是全绿的。</para>
///
/// <para>期望值由 <c>src/NexusP2P.Web/tests/vectors.mjs</c> 在 Node 上
/// 用网页端自己的模块算出。改动任一侧的哈希或加密语义时，重跑那个脚本，
/// 若数值变了就说明两端已经不兼容 —— 那必须是一个有意识的决定。</para>
/// </summary>
public sealed class WebInteropVectorsTests
{
    /// <summary>与 vectors.mjs 里的 SECRET 一致：0,1,2,…,31。</summary>
    private static TransferSecret Secret
    {
        get
        {
            Span<byte> bytes = stackalloc byte[TransferSecret.Size];
            for (var i = 0; i < bytes.Length; i++)
            {
                bytes[i] = (byte)i;
            }

            return new TransferSecret(bytes);
        }
    }

    /// <summary>与 vectors.mjs 的 pattern() 一致。</summary>
    private static byte[] Pattern(int length)
    {
        var data = new byte[length];
        for (var i = 0; i < length; i++)
        {
            data[i] = (byte)(i % 251);
        }

        return data;
    }

    /// <summary>小参数：1 KiB 叶子 / 4 KiB 分片，让向量能跨越分片边界又保持小巧。</summary>
    private static readonly MerkleParameters Small = new(1024, 4096);

    [Theory]
    [InlineData("", "6e340b9cffb37a989ca544e6bb780a2c78901d3fb33738768511a30617afa01d")]
    [InlineData("abc", "609f6e36d2405585188d5cfd761f407c7cc46a7d3f314c88270469dde315fcd1")]
    public void 叶子哈希与网页端一致(string text, string expected)
    {
        using var hasher = new MerkleHasher();

        Assert.Equal(expected, hasher.HashLeaf(Encoding.UTF8.GetBytes(text)).ToString());
    }

    [Fact]
    public void 节点哈希与网页端一致()
    {
        using var hasher = new MerkleHasher();

        var actual = hasher.HashNode(hasher.HashLeaf([1]), hasher.HashLeaf([2]));

        Assert.Equal("6bcf0e2e93e0a18e22789aee965e6553f4fbe93f0acfc4a705d691c8311c4965", actual.ToString());
    }

    /// <summary>三个叶子会走到「奇数节点直接上提」那条路径。</summary>
    [Fact]
    public void 奇数叶子的分片根与网页端一致()
    {
        using var hasher = new MerkleHasher();

        Hash256[] leaves = [hasher.HashLeaf([1]), hasher.HashLeaf([2]), hasher.HashLeaf([3])];
        var actual = hasher.ComputePieceRoot(leaves, 3);

        Assert.Equal("7fa1207b010d346639953bb7d990dacbbf8cb9a32731081911cb6465aa9d34a0", actual.ToString());
    }

    [Fact]
    public async Task 分片根与文件根与网页端一致()
    {
        var result = await HashAsync(Pattern(10000), Small);

        Assert.Equal(
            [
                "30ff551d3bc97bf35bfca8606cac155d23eeb7eb64f73582f9e7ff0956aa3264",
                "94ec7f85478235aff57b2b3e43b0a79208c752d78bb76a4025da9b7b7868bb38",
                "1bac3fc5fa36bd83ff68be3c5790105a5f7b478d9b387479469cd75cce75681c",
            ],
            result.PieceRoots.Select(r => r.ToString()));

        Assert.Equal(
            "6a3abad00352867e351fc7c9daed27792c7ffb2a47bcad0b8cde82132eae9def",
            result.Root.ToString());
    }

    [Fact]
    public async Task 空文件的根与网页端一致()
    {
        var result = await HashAsync([], Small);

        Assert.Equal(
            "c0e50f0d90d6e2fea1e6f53d4f758ed5fc9035a399a8eb215c29c6ff3ea05425",
            Assert.Single(result.PieceRoots).ToString());

        Assert.Equal(
            "cf47a4b1ae0e5cf4bfc325eb995203718a18692fda59074b3cbd9809d5f98227",
            result.Root.ToString());
    }

    /// <summary>
    /// 清单的规范字节形式必须逐字节一致 —— 清单哈希是对它算的，
    /// 而清单哈希又是分片加密的 AAD。
    /// </summary>
    [Fact]
    public async Task 清单序列化与哈希与网页端一致()
    {
        var manifest = await BuildManifestAsync();

        Assert.Equal(
            "4e585032504d414e010000040000001000000000020005612e62696e0000000000000000" +
            "cf47a4b1ae0e5cf4bfc325eb995203718a18692fda59074b3cbd9809d5f98227" +
            "c0e50f0d90d6e2fea1e6f53d4f758ed5fc9035a399a8eb215c29c6ff3ea05425" +
            "000a646f63732f622e74787400000000000027106a3abad00352867e351fc7c9daed2779" +
            "2c7ffb2a47bcad0b8cde82132eae9def" +
            "30ff551d3bc97bf35bfca8606cac155d23eeb7eb64f73582f9e7ff0956aa3264" +
            "94ec7f85478235aff57b2b3e43b0a79208c752d78bb76a4025da9b7b7868bb38" +
            "1bac3fc5fa36bd83ff68be3c5790105a5f7b478d9b387479469cd75cce75681c" +
            "00000001000a646f63732f656d707479",
            Convert.ToHexStringLower(manifest.Serialize()));

        Assert.Equal(
            "a83d69f32fb544cc11767e27749af2b588da4e01317447606f743bdcbdc69fb3",
            manifest.Hash.ToString());

        // 排序规则也必须一致：JS 的字符串比较是 UTF-16 码元序，
        // 与 string.CompareOrdinal 对齐
        Assert.Equal(["a.bin", "docs/b.txt"], manifest.Entries.Select(e => e.Path));
        Assert.Equal(10000, manifest.TotalLength);
        Assert.Equal(4, manifest.TotalPieces);
    }

    [Theory]
    [InlineData(KeyDerivation.ContentPurpose,
        "837ea2631ce995889da88d69fcddbfeaa1ed990dfa786315420482a8c7254434")]
    [InlineData(KeyDerivation.ManifestPurpose,
        "18a1286bada4e901ecbf05cc18048d9a3693b33ff97eeb7f56e3dfdfd4cdc8cf")]
    public void 密钥派生与网页端一致(string purpose, string expected)
    {
        // 空 salt 在 RFC 5869 里等价于 hashLen 个零字节。WebCrypto 与 .NET
        // 在这一点上行为相同 —— 这条测试就是那个假设的证据。
        Assert.Equal(expected, Convert.ToHexStringLower(KeyDerivation.DeriveKey(Secret, purpose)));
    }

    [Theory]
    [InlineData(0, 0L, "000000000000000000000000")]
    [InlineData(1, 0L, "000000010000000000000000")]
    [InlineData(0, 1L, "000000000000000000000001")]
    [InlineData(0x01020304, 21542142465L, "010203040000000504030201")]
    public void nonce_派生与网页端一致(int fileIndex, long pieceIndex, string expected)
    {
        Span<byte> nonce = stackalloc byte[PieceCipher.NonceSize];

        PieceCipher.DeriveNonce(fileIndex, pieceIndex, nonce);

        Assert.Equal(expected, Convert.ToHexStringLower(nonce));
    }

    /// <summary>
    /// 密文可复现是 nonce 由位置派生的直接后果 —— 没有随机 IV，
    /// 所以同样的位置 + 同样的密钥 + 同样的明文必然产出同样的密文。
    /// 这也让这条测试能真的比对到密文本身。
    /// </summary>
    [Theory]
    [InlineData(0, 0L,
        "54cdabb8aa2f7128319de05ac325bdf3309e38049e72c72b4877008472562679" +
        "790b535b492bba5b8eed190e2010f59b3b8cfc0359bb0d377617f31dc07958f1" +
        "c48dcb1a51998e82480b19c69737edd8")]
    [InlineData(2, 5L,
        "8cace3064a44da23eb637e65c4e10f33fc0a1ddb46caae82684260e4597adec7" +
        "4a5d48f02cbe812c47918384a93b6c306c32f3fb756cb1217a2641a0fba064cb" +
        "a14901483f912bec88678c9c743bea73")]
    public async Task 分片密文与网页端一致(int fileIndex, long pieceIndex, string expected)
    {
        var manifest = await BuildManifestAsync();
        using var cipher = new PieceCipher(Secret, manifest.Hash);

        var plaintext = Pattern(64);
        var ciphertext = new byte[PieceCipher.GetCiphertextLength(plaintext.Length)];
        cipher.Encrypt(fileIndex, pieceIndex, plaintext, ciphertext);

        Assert.Equal(expected, Convert.ToHexStringLower(ciphertext));
    }

    private static async Task<FileHashResult> HashAsync(byte[] content, MerkleParameters parameters)
    {
        using var hasher = new FileHasher(parameters);
        using var stream = new MemoryStream(content, writable: false);
        return await hasher.ComputeAsync(stream);
    }

    /// <summary>与 vectors.mjs 里建的清单完全对应。</summary>
    private static async Task<TransferManifest> BuildManifestAsync()
    {
        var big = await HashAsync(Pattern(10000), Small);
        var empty = await HashAsync([], Small);

        return TransferManifest.Create(
            Small,
            [
                ManifestEntry.FromHashResult("docs/b.txt", big),
                ManifestEntry.FromHashResult("a.bin", empty),
            ],
            ["docs/empty"]);
    }
}
