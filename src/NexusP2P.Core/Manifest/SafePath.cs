using System.Buffers;
using System.Diagnostics.CodeAnalysis;

namespace NexusP2P.Core.Manifest;

/// <summary>
/// 清单里的相对路径校验与落地解析。
///
/// <para><b>这是本项目唯一的高危安全面。</b>接收方按发送方给的清单落盘，
/// 而清单里的路径是完全不可信的输入。一条 <c>../../Windows/System32/x.dll</c>
/// 就能把任意文件写到任意位置。</para>
///
/// <para>清单里的路径规范形式：相对、以 <c>/</c> 分隔、无首尾斜杠、无空段。
/// 反斜杠<b>一律拒绝</b> —— 否则 Windows 接收方会把 <c>a\..\..\b</c> 当成穿越，
/// 而只按 <c>/</c> 切分的校验根本看不见它。</para>
/// </summary>
public static class SafePath
{
    public const int MaxSegmentLength = 255;
    public const int MaxPathLength = 1024;

    /// <summary>Windows 设备名。作为任何一段的主名（第一个点之前）出现都要拒绝。</summary>
    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>Windows 文件名非法字符。冒号单独处理（盘符与 NTFS 备用数据流）。</summary>
    private static readonly SearchValues<char> InvalidNameChars =
        SearchValues.Create(['<', '>', '"', '|', '?', '*']);

    /// <summary>
    /// 双向文本控制字符。它们能让 <c>photo_txt.exe</c> 显示成 <c>photo_exe.txt</c>，
    /// 是已知的文件名欺骗手法。检查成本极低，直接拒。
    /// </summary>
    private static readonly SearchValues<char> BidiControlChars = SearchValues.Create(
    [
        '‎', '‏',                                   // LRM / RLM
        '‪', '‫', '‬', '‭', '‮',     // LRE / RLE / PDF / LRO / RLO
        '⁦', '⁧', '⁨', '⁩',               // LRI / RLI / FSI / PDI
    ]);

    /// <summary>
    /// 校验一条来自清单的路径。<paramref name="error"/> 在返回 false 时给出
    /// 可读的原因 —— 传输被拒时用户需要知道为什么。
    /// </summary>
    public static bool IsSafe([NotNullWhen(true)] string? path, [NotNullWhen(false)] out string? error)
    {
        if (string.IsNullOrEmpty(path))
        {
            error = "路径为空。";
            return false;
        }

        if (path.Length > MaxPathLength)
        {
            error = $"路径长度 {path.Length} 超过上限 {MaxPathLength}。";
            return false;
        }

        if (path.Contains('\\', StringComparison.Ordinal))
        {
            error = "路径含反斜杠；清单里只允许用 '/' 作分隔符。";
            return false;
        }

        if (path.Contains(':', StringComparison.Ordinal))
        {
            error = "路径含冒号（可能是盘符或 NTFS 备用数据流）。";
            return false;
        }

        if (path[0] == '/')
        {
            error = "路径是绝对路径。";
            return false;
        }

        foreach (var c in path)
        {
            if (char.IsControl(c))
            {
                error = $"路径含控制字符 U+{(int)c:X4}。";
                return false;
            }
        }

        if (path.AsSpan().IndexOfAny(BidiControlChars) >= 0)
        {
            error = "路径含双向文本控制字符，可能用于伪装文件名。";
            return false;
        }

        if (path.AsSpan().IndexOfAny(InvalidNameChars) >= 0)
        {
            error = "路径含 Windows 文件名非法字符（< > \" | ? *）。";
            return false;
        }

        foreach (var segment in path.Split('/'))
        {
            if (!IsSegmentSafe(segment, out error))
            {
                return false;
            }
        }

        error = null;
        return true;
    }

    private static bool IsSegmentSafe(string segment, [NotNullWhen(false)] out string? error)
    {
        if (segment.Length == 0)
        {
            error = "路径含空段（首尾斜杠或连续斜杠）。";
            return false;
        }

        if (segment.Length > MaxSegmentLength)
        {
            error = $"路径段长度 {segment.Length} 超过上限 {MaxSegmentLength}。";
            return false;
        }

        if (segment is "." or "..")
        {
            error = $"路径含 \"{segment}\" 段。";
            return false;
        }

        // Windows 会静默丢掉结尾的点与空格："a. " 会变成 "a"，
        // 于是校验过的路径与实际落地的路径不是同一个 —— 直接拒。
        var last = segment[^1];
        if (last is '.' or ' ')
        {
            error = $"路径段 \"{segment}\" 以点或空格结尾；Windows 会静默去掉它。";
            return false;
        }

        var dotIndex = segment.IndexOf('.', StringComparison.Ordinal);
        var stem = dotIndex < 0 ? segment : segment[..dotIndex];
        if (ReservedDeviceNames.Contains(stem))
        {
            error = $"路径段 \"{segment}\" 使用了 Windows 保留设备名 \"{stem}\"。";
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>
    /// 把清单路径解析成 <paramref name="rootDirectory"/> 下的绝对路径。
    ///
    /// <para>除了跑一遍 <see cref="IsSafe"/>，还会在拼接后再次确认结果仍然位于
    /// 根目录内部 —— 纵深防御。就算 <see cref="IsSafe"/> 哪天漏了一种写法，
    /// 这一步也会把它挡住。</para>
    /// </summary>
    public static string ResolveWithin(string rootDirectory, string manifestPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(rootDirectory);

        if (!IsSafe(manifestPath, out var error))
        {
            throw new UnsafePathException(manifestPath ?? "<null>", error);
        }

        var root = Path.GetFullPath(rootDirectory);
        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        var relative = manifestPath.Replace('/', Path.DirectorySeparatorChar);
        var resolved = Path.GetFullPath(Path.Combine(root, relative));

        // 大小写不敏感比较：目标平台是 Windows。根目录是我们自己给的，
        // 所以这里不会因为大小写差异误判。
        if (!resolved.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnsafePathException(manifestPath,
                $"解析后逃出了根目录：\"{resolved}\" 不在 \"{root}\" 之内。");
        }

        return resolved;
    }

    /// <summary>
    /// 把本机的相对路径转成清单里的规范形式（反斜杠换成 <c>/</c>）。
    /// 转换后仍会走一遍 <see cref="IsSafe"/> —— 发送方也不该造出非法路径。
    /// </summary>
    public static bool TryToManifestPath(
        string? localRelativePath,
        [NotNullWhen(true)] out string? manifestPath,
        [NotNullWhen(false)] out string? error)
    {
        manifestPath = null;

        if (string.IsNullOrEmpty(localRelativePath))
        {
            error = "路径为空。";
            return false;
        }

        var candidate = localRelativePath.Replace('\\', '/').Trim('/');

        if (!IsSafe(candidate, out error))
        {
            return false;
        }

        manifestPath = candidate;
        return true;
    }
}

/// <summary>清单里出现了不安全的路径。</summary>
public sealed class UnsafePathException(string path, string reason)
    : Exception($"拒绝不安全的路径 \"{path}\"：{reason}")
{
    public string Path { get; } = path;

    public string Reason { get; } = reason;
}
