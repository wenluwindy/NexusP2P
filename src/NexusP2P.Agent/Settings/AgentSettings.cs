using System.Text.Json.Serialization;

namespace NexusP2P.Agent.Settings;

/// <summary>
/// 用户偏好。持久化到 <c>%APPDATA%/NexusP2P/settings.json</c>。
///
/// <para>与<b>部署配置</b>（AD-8，信令地址等）分开：那些是部署前定好、
/// 用户不该改的；这里是用户自己的选择。混在一起会让「重装后配置丢了」
/// 和「换服务器要改用户文件」这两种事纠缠在一起。</para>
/// </summary>
public sealed record AgentSettings
{
    /// <summary>配置文件格式版本，为将来的迁移留出余地。</summary>
    [JsonPropertyName("version")]
    public int Version { get; init; } = 1;

    /// <summary>
    /// 接收文件的落盘目录（AD-9）。null 表示用默认值。
    ///
    /// <para>用户改过之后要记住，下次打开还是这个目录。</para>
    /// </summary>
    [JsonPropertyName("receiveDirectory")]
    public string? ReceiveDirectory { get; init; }

    /// <summary>传输完成或失败时弹系统通知。</summary>
    [JsonPropertyName("showNotifications")]
    public bool ShowNotifications { get; init; } = true;

    /// <summary>关闭窗口时最小化到托盘而不是退出。</summary>
    [JsonPropertyName("minimizeToTrayOnClose")]
    public bool MinimizeToTrayOnClose { get; init; } = true;

    /// <summary>默认的接收目录：下载文件夹下的一个子目录。</summary>
    public static string DefaultReceiveDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads",
            "NexusP2P");

    /// <summary>实际生效的接收目录。</summary>
    public string EffectiveReceiveDirectory =>
        string.IsNullOrWhiteSpace(ReceiveDirectory) ? DefaultReceiveDirectory : ReceiveDirectory;
}
