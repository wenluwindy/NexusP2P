using System.Text.Json;

namespace NexusP2P.Agent.Settings;

/// <summary>
/// 设置的读写。
///
/// <para><b>设置文件坏了绝不能挡住程序启动。</b>读不出来就退回默认值并重建 ——
/// 用户装个软件传文件，不该因为一个 JSON 少了个括号就打不开。</para>
///
/// <para>写入用「临时文件 + 原子替换」：断电时上一份完好的设置仍在，
/// 而不是留下半截 JSON。</para>
/// </summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly string _path;
    private readonly Lock _gate = new();

    /// <param name="path">设置文件路径。null 表示用 <see cref="DefaultPath"/>。</param>
    public SettingsStore(string? path = null) => _path = path ?? DefaultPath;

    public static string DefaultPath =>
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "NexusP2P",
            "settings.json");

    public string FilePath => _path;

    /// <summary>
    /// 上一次加载时遇到的问题。null 表示一切正常。
    /// UI 可以据此提示「设置文件损坏，已重置为默认值」而不是默默吞掉。
    /// </summary>
    public string? LastLoadWarning { get; private set; }

    public AgentSettings Load()
    {
        lock (_gate)
        {
            LastLoadWarning = null;

            if (!File.Exists(_path))
            {
                return new AgentSettings();
            }

            try
            {
                var json = File.ReadAllText(_path);
                var settings = JsonSerializer.Deserialize<AgentSettings>(json, JsonOptions);

                if (settings is null)
                {
                    LastLoadWarning = "设置文件内容为空，已使用默认值。";
                    return new AgentSettings();
                }

                return settings;
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                // 坏了就用默认值。绝不因为设置文件而让程序打不开。
                LastLoadWarning = $"设置文件读取失败，已使用默认值：{ex.Message}";
                return new AgentSettings();
            }
        }
    }

    /// <summary>保存。原子替换，断电时上一份完好的设置仍在。</summary>
    public void Save(AgentSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        lock (_gate)
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(temporary, _path, overwrite: true);
        }
    }
}
