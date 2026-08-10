using System.Buffers.Binary;
using System.Diagnostics;
using SIPSorcery.Net;

namespace SipSorceryThroughput;

/// <summary>
/// 往 DataChannel 上灌数据并测量。这是整个 spike 的核心：
/// 我们想知道 (1) 吞吐能到多少 (2) 内存会不会失控 (3) 背压机制是否真的有效。
/// </summary>
internal sealed class Blaster(SpikeOptions options)
{
    public sealed record Result(
        long BytesSent,
        double Seconds,
        double ThroughputMiBps,
        long PeakManagedBytes,
        long PeakWorkingSetBytes,
        ulong PeakBufferedAmount,
        long StallCount,
        double StallSeconds);

    public async Task<Result> RunAsync(RTCDataChannel dc, SctpProbe? probe, CancellationToken ct)
    {
        var proc = Process.GetCurrentProcess();
        var chunkCount = options.TotalBytes / options.ChunkBytes;

        long peakManaged = 0, peakWorkingSet = 0, stallCount = 0;
        ulong peakBuffered = 0;
        double stallSeconds = 0;
        long bytesSent = 0;

        var sw = Stopwatch.StartNew();
        var lastReport = TimeSpan.Zero;
        var lastReportBytes = 0L;

        for (long seq = 0; seq < chunkCount; seq++)
        {
            ct.ThrowIfCancellationRequested();

            // ---- 背压：SIPSorcery 没有 onbufferedamountlow，只能轮询 bufferedAmount ----
            if (dc.bufferedAmount > (ulong)options.HighWaterBytes)
            {
                var stallStart = sw.Elapsed;
                stallCount++;
                while (dc.bufferedAmount > (ulong)options.HighWaterBytes / 2)
                {
                    if (dc.readyState != RTCDataChannelState.open)
                        throw new IOException($"传输中途通道关闭，state={dc.readyState}");
                    await Task.Delay(1, ct);
                }

                stallSeconds += (sw.Elapsed - stallStart).TotalSeconds;
            }

            // 每片新分配。这样内存曲线反映的是 SIPSorcery 内部队列的持有量，
            // 而不是我们自己复用缓冲区造成的假象。64 KiB < 85 KiB，走 Gen0，代价可控。
            var buf = new byte[options.ChunkBytes];
            BinaryPrimitives.WriteInt64BigEndian(buf, seq);
            // 尾部再写一次序号：能同时抓到截断和错序，代价只有 8 字节。
            BinaryPrimitives.WriteInt64BigEndian(buf.AsSpan(options.ChunkBytes - 8), seq);

            dc.send(buf, 0, buf.Length);
            bytesSent += options.ChunkBytes;

            var buffered = dc.bufferedAmount;
            if (buffered > peakBuffered) peakBuffered = buffered;

            // ---- 每秒采样一次内存和速率 ----
            if (sw.Elapsed - lastReport >= TimeSpan.FromSeconds(1))
            {
                var managed = GC.GetTotalMemory(false);
                proc.Refresh();
                var ws = proc.WorkingSet64;
                if (managed > peakManaged) peakManaged = managed;
                if (ws > peakWorkingSet) peakWorkingSet = ws;

                var deltaBytes = bytesSent - lastReportBytes;
                var deltaSecs = (sw.Elapsed - lastReport).TotalSeconds;
                var mibps = deltaBytes / 1024.0 / 1024 / deltaSecs;

                Console.WriteLine(
                    $"  [{sw.Elapsed:mm\\:ss}] " +
                    $"已发 {bytesSent / 1024.0 / 1024,8:N0} MiB | " +
                    $"{mibps,7:N1} MiB/s | " +
                    $"缓冲 {buffered / 1024.0 / 1024,6:N1} MiB | " +
                    $"托管堆 {managed / 1024.0 / 1024,6:N1} MiB | " +
                    $"工作集 {ws / 1024.0 / 1024,6:N1} MiB");

                if (probe is not null)
                {
                    Console.WriteLine($"            SCTP: {probe.Snapshot()}");
                }

                lastReport = sw.Elapsed;
                lastReportBytes = bytesSent;
            }
        }

        // ---- 等待发送缓冲排空，否则测到的是「投递速度」而不是「传输速度」 ----
        Console.WriteLine("  发送循环结束，等待缓冲排空…");
        var drainStart = sw.Elapsed;
        while (dc.bufferedAmount > 0)
        {
            if (dc.readyState != RTCDataChannelState.open)
                throw new IOException($"排空阶段通道关闭，state={dc.readyState}");
            if (sw.Elapsed - drainStart > TimeSpan.FromMinutes(5))
                throw new TimeoutException($"缓冲排空超时，仍剩 {dc.bufferedAmount / 1024.0 / 1024:N1} MiB");
            await Task.Delay(10, ct);
        }

        sw.Stop();

        proc.Refresh();
        if (proc.WorkingSet64 > peakWorkingSet) peakWorkingSet = proc.WorkingSet64;
        var finalManaged = GC.GetTotalMemory(false);
        if (finalManaged > peakManaged) peakManaged = finalManaged;

        return new Result(
            bytesSent,
            sw.Elapsed.TotalSeconds,
            bytesSent / 1024.0 / 1024 / sw.Elapsed.TotalSeconds,
            peakManaged,
            peakWorkingSet,
            peakBuffered,
            stallCount,
            stallSeconds);
    }
}
