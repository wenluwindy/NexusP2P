using System.Buffers.Binary;
using System.Diagnostics;

namespace SipSorceryThroughput;

/// <summary>
/// 反向测试用：.NET 作接收端，统计浏览器灌过来的数据。
/// 目的是分离「SIPSorcery 发送端慢」和「整个 SCTP 关联都慢」这两种可能。
/// </summary>
internal sealed class Receiver
{
    private readonly Stopwatch _sw = new();
    private long _bytes;
    private long _expectedSeq;
    private bool _sequenceOk = true;
    private long _firstBadSeq = -1;
    private TimeSpan _lastReport;
    private long _lastReportBytes;

    public long Bytes => _bytes;
    public bool SequenceOk => _sequenceOk;
    public long FirstBadSeq => _firstBadSeq;
    public double Seconds => _sw.Elapsed.TotalSeconds;
    public double ThroughputMiBps => _bytes / 1024.0 / 1024 / Math.Max(_sw.Elapsed.TotalSeconds, 1e-9);

    public void OnChunk(byte[] data)
    {
        if (!_sw.IsRunning) _sw.Start();
        _bytes += data.Length;

        if (data.Length >= 16)
        {
            var head = BinaryPrimitives.ReadInt64BigEndian(data);
            var tail = BinaryPrimitives.ReadInt64BigEndian(data.AsSpan(data.Length - 8));
            if (_sequenceOk && (head != _expectedSeq || tail != _expectedSeq))
            {
                _sequenceOk = false;
                _firstBadSeq = _expectedSeq;
            }
        }

        _expectedSeq++;

        if (_sw.Elapsed - _lastReport >= TimeSpan.FromSeconds(2))
        {
            var mibps = (_bytes - _lastReportBytes) / 1024.0 / 1024
                        / (_sw.Elapsed - _lastReport).TotalSeconds;
            Console.WriteLine($"  [{_sw.Elapsed:mm\\:ss}] 已收 {_bytes / 1024.0 / 1024,8:N0} MiB | {mibps,7:N1} MiB/s");
            _lastReport = _sw.Elapsed;
            _lastReportBytes = _bytes;
        }
    }

    public void Stop() => _sw.Stop();
}
