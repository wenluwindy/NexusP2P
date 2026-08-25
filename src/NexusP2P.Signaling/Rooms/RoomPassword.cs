using System.Security.Cryptography;

namespace NexusP2P.Signaling.Rooms;

/// <summary>
/// 房间口令的校验材料（PBKDF2-SHA256）。
///
/// <para><b>为什么用慢哈希</b>：房间本身只活几分钟到几小时，但用户设置的
/// 口令可能是他在别处也在用的密码 —— 即使是短命凭证，也不落明文。</para>
///
/// <para><b>口令的角色</b>：九位文件码是「能收这次传输」的唯一凭证；
/// 口令是<b>可选的第二道门槛</b>（FilePizza 的做法，移植为信令层实现）。
/// 不设置口令的房间行为与从前完全一致。</para>
///
/// <para><b>与威胁模型的关系</b>：口令经由信令服务器（WSS 查询参数）传递
/// 并在此校验，服务器看得到它 —— 这与本项目「密钥由信令协商的连接送达」
/// 的既有信任边界一致：传输内容的机密性不依赖口令，而依赖 AES-256-GCM；
/// 口令只挡「拿到文件码但没拿到口令」的人。</para>
/// </summary>
public sealed record RoomPassword
{
    /// <summary>PBKDF2 迭代次数。错口令也要付这个代价，是穷举的天然减速带。</summary>
    private const int Iterations = 100_000;

    private const int SaltSize = 16;

    private const int HashSize = 32;

    /// <summary>每个房间的独立盐。同样不该让人反查「两个房间是否同口令」。</summary>
    public byte[] Salt { get; }

    public byte[] Hash { get; }

    private RoomPassword(byte[] salt, byte[] hash)
    {
        Salt = salt;
        Hash = hash;
    }

    public static RoomPassword Create(string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);

        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashSize);
        return new RoomPassword(salt, hash);
    }

    /// <summary>
    /// 校验一个口令。空口令永远不匹配 —— 「没带口令」与「带错口令」
    /// 在这里是同一种失败，对外也必须是同一种失败（防枚举）。
    /// </summary>
    public bool Matches(string? presented)
    {
        if (string.IsNullOrEmpty(presented))
        {
            return false;
        }

        Span<byte> candidate = stackalloc byte[HashSize];
        Rfc2898DeriveBytes.Pbkdf2(presented, Salt, candidate, Iterations, HashAlgorithmName.SHA256);

        return CryptographicOperations.FixedTimeEquals(candidate, Hash);
    }
}
