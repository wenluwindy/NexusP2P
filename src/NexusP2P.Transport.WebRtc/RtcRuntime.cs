using NexusP2P.Transport.WebRtc.Interop;

namespace NexusP2P.Transport.WebRtc;

/// <summary>
/// libdatachannel 的进程级初始化。
///
/// <para><c>rtcPreload</c> 会提前把原生库与线程池起好。不调它也能用，
/// 但第一次建连接会额外花几百毫秒 —— 而那正是用户输完码等着的时刻。</para>
///
/// <para>刻意<b>不</b>在这里调 <c>rtcCleanup</c>：它会阻塞等所有原生线程退出，
/// 放进进程退出路径上容易造成关不掉的窗口。原生资源随进程一起消失就够了。
/// 需要在进程存活期间彻底收干净的场景（比如测试宿主）可以显式调
/// <see cref="Shutdown"/>。</para>
/// </summary>
public static class RtcRuntime
{
    private static readonly Lock Gate = new();
    private static bool _initialized;

    public static void EnsureInitialized()
    {
        if (_initialized)
        {
            return;
        }

        lock (Gate)
        {
            if (_initialized)
            {
                return;
            }

            NativeRtc.Preload();
            _initialized = true;
        }
    }

    /// <summary>
    /// 释放原生库持有的全部资源。会阻塞等原生线程退出，
    /// 所以只在确实需要「彻底收干净」的场景调用。
    /// </summary>
    public static void Shutdown()
    {
        lock (Gate)
        {
            if (!_initialized)
            {
                return;
            }

            NativeRtc.Cleanup();
            _initialized = false;
        }
    }
}
