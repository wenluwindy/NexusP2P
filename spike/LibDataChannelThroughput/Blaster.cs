using System.Buffers.Binary;
using System.Diagnostics;
using DataChannelDotnet;

namespace LibDataChannelThroughput;

/// <summary>
/// 往 DataChannel 上灌数据并测量。与 SipSorcery spike 的同名类逻辑一致 ——
/// 只有传输实现不同，测量方式必须相同才可比。
/// </summary>
internal sealed class Blaster(SpikeOptions options)
{
    public sealed record Result(
        long BytesSent,
        double Seconds,
        double ThroughputMiBps,
        long PeakManagedBytes,
        long PeakWorkingSetBytes,
        long PeakBufferedAmount,
        long StallCount,
        double StallSeconds);

    public async Task<Result> RunAsync(
        IRtcDataChannel channel, NativeChannel native, CancellationToken cancellationToken)
    {
        var process = Process.GetCurrentProcess();
        var chunkCount = options.TotalBytes / options.ChunkBytes;

        long peakManaged = 0, peakWorkingSet = 0, peakBuffered = 0, stallCount = 0;
        double stallSeconds = 0;
        long bytesSent = 0;

        var stopwatch = Stopwatch.StartNew();
        var lastReport = TimeSpan.Zero;
        var lastReportBytes = 0L;

        for (long sequence = 0; sequence < chunkCount; sequence++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // ---- 背压：libdatachannel 有 rtcGetBufferedAmount，用它轮询 ----
            if (native.BufferedAmount > options.HighWaterBytes)
            {
                var stallStart = stopwatch.Elapsed;
                stallCount++;
                while (native.BufferedAmount > options.HighWaterBytes / 2)
                {
                    if (!channel.IsOpen)
                    {
                        throw new IOException("传输中途通道关闭。");
                    }

                    await Task.Delay(1, cancellationToken);
                }

                stallSeconds += (stopwatch.Elapsed - stallStart).TotalSeconds;
            }

            // 每片新分配，与 SipSorcery spike 保持一致 ——
            // 这样内存曲线反映的是库内部队列的持有量而不是缓冲区复用的假象
            var buffer = new byte[options.ChunkBytes];
            BinaryPrimitives.WriteInt64BigEndian(buffer, sequence);
            BinaryPrimitives.WriteInt64BigEndian(buffer.AsSpan(options.ChunkBytes - 8), sequence);

            channel.Send(buffer);
            bytesSent += options.ChunkBytes;

            var buffered = native.BufferedAmount;
            if (buffered > peakBuffered) peakBuffered = buffered;

            if (stopwatch.Elapsed - lastReport >= TimeSpan.FromSeconds(1))
            {
                var managed = GC.GetTotalMemory(false);
                process.Refresh();
                var workingSet = process.WorkingSet64;
                if (managed > peakManaged) peakManaged = managed;
                if (workingSet > peakWorkingSet) peakWorkingSet = workingSet;

                var deltaBytes = bytesSent - lastReportBytes;
                var deltaSeconds = (stopwatch.Elapsed - lastReport).TotalSeconds;

                Console.WriteLine(
                    $"  [{stopwatch.Elapsed:mm\\:ss}] " +
                    $"已发 {bytesSent / 1024.0 / 1024,8:N0} MiB | " +
                    $"{deltaBytes / 1024.0 / 1024 / deltaSeconds,8:N1} MiB/s | " +
                    $"缓冲 {buffered / 1024.0 / 1024,6:N1} MiB | " +
                    $"托管堆 {managed / 1024.0 / 1024,6:N1} MiB | " +
                    $"工作集 {workingSet / 1024.0 / 1024,7:N1} MiB");

                lastReport = stopwatch.Elapsed;
                lastReportBytes = bytesSent;
            }
        }

        Console.WriteLine("  发送循环结束，等待缓冲排空…");
        var drainStart = stopwatch.Elapsed;
        while (native.BufferedAmount > 0)
        {
            if (!channel.IsOpen)
            {
                throw new IOException("排空阶段通道关闭。");
            }

            if (stopwatch.Elapsed - drainStart > TimeSpan.FromMinutes(5))
            {
                throw new TimeoutException(
                    $"缓冲排空超时，仍剩 {native.BufferedAmount / 1024.0 / 1024:N1} MiB。");
            }

            await Task.Delay(10, cancellationToken);
        }

        stopwatch.Stop();

        process.Refresh();
        if (process.WorkingSet64 > peakWorkingSet) peakWorkingSet = process.WorkingSet64;
        var finalManaged = GC.GetTotalMemory(false);
        if (finalManaged > peakManaged) peakManaged = finalManaged;

        return new Result(
            bytesSent,
            stopwatch.Elapsed.TotalSeconds,
            bytesSent / 1024.0 / 1024 / stopwatch.Elapsed.TotalSeconds,
            peakManaged,
            peakWorkingSet,
            peakBuffered,
            stallCount,
            stallSeconds);
    }
}
