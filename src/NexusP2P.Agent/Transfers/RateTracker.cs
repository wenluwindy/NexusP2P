namespace NexusP2P.Agent.Transfers;

/// <summary>
/// 滑动窗口速率估算。
///
/// <para>用窗口而不是「总字节 / 总耗时」：后者在长传输里会被早期的慢启动
/// 永久拖住，用户看到的数字永远追不上实际速度，剩余时间也就一直是错的。</para>
/// </summary>
public sealed class RateTracker(TimeSpan? window = null)
{
    private readonly TimeSpan _window = window ?? TimeSpan.FromSeconds(3);
    private readonly Queue<(DateTimeOffset At, long Bytes)> _samples = new();
    private readonly Lock _gate = new();

    private long _lastTotal;

    /// <summary>记一次进度。<paramref name="totalBytes"/> 是累计值而不是增量。</summary>
    public void Record(long totalBytes, DateTimeOffset now)
    {
        lock (_gate)
        {
            // 续传时累计值可能比上次小（新会话从 0 开始计），此时重置窗口
            var delta = totalBytes - _lastTotal;
            if (delta < 0)
            {
                _samples.Clear();
                _lastTotal = totalBytes;
                return;
            }

            _lastTotal = totalBytes;
            _samples.Enqueue((now, delta));

            var cutoff = now - _window;
            while (_samples.Count > 0 && _samples.Peek().At < cutoff)
            {
                _samples.Dequeue();
            }
        }
    }

    /// <summary>当前速率（字节/秒）。样本不足时返回 0。</summary>
    public double BytesPerSecond(DateTimeOffset now)
    {
        lock (_gate)
        {
            var cutoff = now - _window;
            while (_samples.Count > 0 && _samples.Peek().At < cutoff)
            {
                _samples.Dequeue();
            }

            if (_samples.Count < 2)
            {
                return 0;
            }

            var span = (now - _samples.Peek().At).TotalSeconds;
            if (span <= 0)
            {
                return 0;
            }

            return _samples.Sum(s => s.Bytes) / span;
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _samples.Clear();
            _lastTotal = 0;
        }
    }
}
