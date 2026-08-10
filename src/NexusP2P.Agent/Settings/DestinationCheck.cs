using NexusP2P.Transfer.Storage;

namespace NexusP2P.Agent.Settings;

/// <summary>目标目录的检查结果。</summary>
public readonly record struct DestinationStatus(bool IsUsable, string? Problem)
{
    public static DestinationStatus Ok { get; } = new(true, null);

    public static DestinationStatus Fail(string problem) => new(false, problem);
}

/// <summary>
/// 接收前检查目标目录。
///
/// <para><b>为什么必须在开始之前检查</b>：20 GB 传五十分钟后因为磁盘满而失败，
/// 是这个产品能出现的最难受的结局。提前几秒失败换掉白等五十分钟，很划算。</para>
///
/// <para>检查的是「现在能不能写」而不是「路径看起来对不对」 ——
/// 盘符消失、目录被删、权限变了，只有真去写一下才知道。</para>
/// </summary>
public static class DestinationCheck
{
    /// <summary>
    /// 检查目录可用性。<paramref name="requiredBytes"/> 大于 0 时一并检查可用空间。
    /// </summary>
    public static DestinationStatus Check(string? directory, long requiredBytes = 0)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return DestinationStatus.Fail("接收目录未设置。");
        }

        string full;
        try
        {
            full = Path.GetFullPath(directory);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return DestinationStatus.Fail($"接收目录路径不合法：{ex.Message}");
        }

        try
        {
            Directory.CreateDirectory(full);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DestinationStatus.Fail($"无法创建接收目录 \"{full}\"：{ex.Message}");
        }

        // 真写一个文件。权限、只读介质、盘符消失这些问题，
        // 只有真去写才暴露 —— 检查属性是不够的。
        var probe = Path.Combine(full, $".nexusp2p-write-test-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllBytes(probe, [0]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DestinationStatus.Fail($"接收目录 \"{full}\" 不可写：{ex.Message}");
        }
        finally
        {
            try
            {
                if (File.Exists(probe))
                {
                    File.Delete(probe);
                }
            }
            catch (IOException)
            {
                // 探针文件删不掉不影响结论
            }
        }

        if (requiredBytes > 0)
        {
            try
            {
                PieceStore.EnsureSpaceAvailable(full, requiredBytes);
            }
            catch (InsufficientDiskSpaceException ex)
            {
                return DestinationStatus.Fail(ex.Message);
            }
        }

        return DestinationStatus.Ok;
    }
}
