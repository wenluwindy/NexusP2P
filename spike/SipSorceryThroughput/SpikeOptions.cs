namespace SipSorceryThroughput;

/// <summary>
/// spike 的可调参数。全部有默认值，命令行可覆盖，方便扫不同的分片大小和水位。
/// </summary>
internal sealed class SpikeOptions
{
    /// <summary>本次要发送的总字节数。</summary>
    public long TotalBytes { get; private init; } = 1L * 1024 * 1024 * 1024;

    /// <summary>单条 DataChannel 消息的大小。浏览器跨实现的安全上限是 256 KiB。</summary>
    public int ChunkBytes { get; private init; } = 64 * 1024;

    /// <summary>
    /// 背压高水位：bufferedAmount 超过它就停止投递。
    /// SIPSorcery 没有 onbufferedamountlow 事件，只能轮询。
    /// </summary>
    public long HighWaterBytes { get; private init; } = 8L * 1024 * 1024;

    /// <summary>监听端口。localhost 属于 secure context，所以 http 也能用 WebRTC。</summary>
    public int Port { get; private init; } = 5080;

    /// <summary>打开 SIPSorcery 的详细日志。</summary>
    public bool Verbose { get; private init; }

    /// <summary>
    /// 用反射改写 SctpDataSender 的节拍周期（毫秒）。0 表示不动，用库的默认值 50。
    /// 仅用于诊断，不是可交付方案。
    /// </summary>
    public int BurstPeriodMs { get; private init; }

    /// <summary>
    /// 反向模式：浏览器发、.NET 收。用来分离「发送端慢」和「整个关联都慢」。
    /// </summary>
    public bool Reverse { get; private init; }

    public static SpikeOptions Parse(string[] args)
    {
        long ReadLong(string name, long fallback)
        {
            var idx = Array.IndexOf(args, name);
            if (idx < 0 || idx + 1 >= args.Length) return fallback;
            return long.TryParse(args[idx + 1], out var v) ? v : fallback;
        }

        return new SpikeOptions
        {
            TotalBytes = ReadLong("--size-mb", 1024) * 1024 * 1024,
            ChunkBytes = (int)ReadLong("--chunk-kb", 64) * 1024,
            HighWaterBytes = ReadLong("--high-water-mb", 8) * 1024 * 1024,
            Port = (int)ReadLong("--port", 5080),
            Verbose = args.Contains("--verbose"),
            BurstPeriodMs = (int)ReadLong("--burst-ms", 0),
            Reverse = args.Contains("--reverse"),
        };
    }

    public void Print()
    {
        Console.WriteLine("=== SIPSorcery DataChannel 吞吐 spike ===");
        Console.WriteLine($"  总量      : {TotalBytes / 1024.0 / 1024:N0} MiB");
        Console.WriteLine($"  分片      : {ChunkBytes / 1024:N0} KiB");
        Console.WriteLine($"  背压水位  : {HighWaterBytes / 1024.0 / 1024:N0} MiB");
        Console.WriteLine($"  分片总数  : {TotalBytes / ChunkBytes:N0}");
        Console.WriteLine($"  SCTP 节拍 : {(BurstPeriodMs > 0 ? $"{BurstPeriodMs} ms（反射改写）" : "50 ms（库默认）")}");
        Console.WriteLine($"  方向      : {(Reverse ? "浏览器 -> .NET（反向）" : ".NET -> 浏览器（正向）")}");
        Console.WriteLine();
    }
}
