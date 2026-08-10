namespace LibDataChannelThroughput;

/// <summary>
/// spike 参数。与 SipSorcery spike 的同名类保持一致的默认值，
/// 否则两次测量不可比。
/// </summary>
internal sealed class SpikeOptions
{
    public long TotalBytes { get; private init; } = 1L * 1024 * 1024 * 1024;

    public int ChunkBytes { get; private init; } = 64 * 1024;

    public long HighWaterBytes { get; private init; } = 8L * 1024 * 1024;

    public int Port { get; private init; } = 5180;

    public bool Verbose { get; private init; }

    /// <summary>
    /// SCTP 发送缓冲区（字节）。0 表示用库的默认值。
    ///
    /// <para>这正是 SIPSorcery 写死成 <c>const</c> 而无法调的那类旋钮。
    /// libdatachannel 允许调，所以顺便量一下它的影响。</para>
    /// </summary>
    public int SctpSendBufferSize { get; private init; }

    /// <summary>SCTP 队列上的最大块数。0 表示默认。</summary>
    public int SctpMaxChunksOnQueue { get; private init; }

    /// <summary>单条消息上限。浏览器跨实现的安全值是 256 KiB。</summary>
    public int MaxMessageSize { get; private init; } = 256 * 1024;

    public static SpikeOptions Parse(string[] args)
    {
        long ReadLong(string name, long fallback)
        {
            var index = Array.IndexOf(args, name);
            if (index < 0 || index + 1 >= args.Length) return fallback;
            return long.TryParse(args[index + 1], out var value) ? value : fallback;
        }

        return new SpikeOptions
        {
            TotalBytes = ReadLong("--size-mb", 1024) * 1024 * 1024,
            ChunkBytes = (int)ReadLong("--chunk-kb", 64) * 1024,
            HighWaterBytes = ReadLong("--high-water-mb", 8) * 1024 * 1024,
            Port = (int)ReadLong("--port", 5180),
            SctpSendBufferSize = (int)ReadLong("--sctp-send-kb", 0) * 1024,
            SctpMaxChunksOnQueue = (int)ReadLong("--sctp-max-chunks", 0),
            MaxMessageSize = (int)ReadLong("--max-message-kb", 256) * 1024,
            Verbose = args.Contains("--verbose"),
        };
    }

    public void Print()
    {
        Console.WriteLine("=== libdatachannel DataChannel 吞吐 spike ===");
        Console.WriteLine($"  总量        : {TotalBytes / 1024.0 / 1024:N0} MiB");
        Console.WriteLine($"  分片        : {ChunkBytes / 1024:N0} KiB");
        Console.WriteLine($"  背压水位    : {HighWaterBytes / 1024.0 / 1024:N0} MiB");
        Console.WriteLine($"  单条上限    : {MaxMessageSize / 1024:N0} KiB");
        Console.WriteLine($"  SCTP 发送缓冲: " +
                          (SctpSendBufferSize > 0 ? $"{SctpSendBufferSize / 1024} KiB" : "库默认"));
        Console.WriteLine($"  SCTP 队列上限: " +
                          (SctpMaxChunksOnQueue > 0 ? SctpMaxChunksOnQueue.ToString() : "库默认"));
        Console.WriteLine();
    }
}
