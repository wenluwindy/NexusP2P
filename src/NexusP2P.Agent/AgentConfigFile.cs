using System.Text.Json;
using System.Text.Json.Serialization;

namespace NexusP2P.Agent;

/// <summary>
/// 部署配置文件（AD-8）：<c>nexusp2p.json</c>，放在可执行文件旁边。
///
/// <para>放在 exe 旁边而不是用户目录，是为了配合「打个包拷到两台电脑」这种用法：
/// 改一次配置，整个目录拷过去就都对了。放用户目录的话每台机器都得再配一遍。</para>
///
/// <para>与 <see cref="Settings.AgentSettings"/>（用户偏好，存在
/// <c>%APPDATA%</c>）刻意分开：一个是部署前定好的，一个是用户自己选的。
/// 混在一起会让「换服务器」变成要去动用户文件。</para>
/// </summary>
public sealed record AgentConfigFile
{
    /// <summary>约定的文件名。</summary>
    public const string FileName = "nexusp2p.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        // 允许注释和多余逗号：这是给人手改的文件，
        // 因为一个尾逗号就打不开程序说不过去
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
    };

    /// <summary>信令服务器基址，如 <c>https://p2p.example.com</c>。</summary>
    [JsonPropertyName("signaling")]
    public string? Signaling { get; init; }

    /// <summary>
    /// ICE 服务器的<b>兜底</b>配置，如 <c>stun:stun.example.com:3478</c>。
    ///
    /// <para>正常情况下不用填：信令服务器会在进房时下发带时限凭据的 TURN 地址，
    /// 那份优先。这里只是在服务端没配 TURN 时留个手动指定的余地。</para>
    /// </summary>
    [JsonPropertyName("iceServers")]
    public string[]? IceServers { get; init; }

    /// <summary>
    /// 从可执行文件所在目录读配置。
    ///
    /// <para><b>读不出来不算错。</b>配置文件是可选的 —— 命令行参数和环境变量
    /// 都能替代它。所以这里返回 null 而不是抛异常，由调用方决定缺了要不要报错。</para>
    /// </summary>
    /// <param name="warning">
    /// 文件存在但读不了时的说明。<b>必须显示给用户</b>：
    /// 静默忽略一个写坏的配置文件，用户会对着「必须指定信令服务器」发愣，
    /// 而他明明已经写好了配置。
    /// </param>
    public static AgentConfigFile? TryLoad(out string? warning)
    {
        return TryLoadFrom(DefaultPath, out warning);
    }

    /// <summary>配置文件的约定位置：可执行文件旁边。</summary>
    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, FileName);

    internal static AgentConfigFile? TryLoadFrom(string path, out string? warning)
    {
        warning = null;

        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var config = JsonSerializer.Deserialize<AgentConfigFile>(File.ReadAllText(path), JsonOptions);

            if (config is null)
            {
                warning = $"配置文件 {path} 内容为空，已忽略。";
                return null;
            }

            return config;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            warning = $"配置文件 {path} 读取失败，已忽略：{ex.Message}";
            return null;
        }
    }
}
