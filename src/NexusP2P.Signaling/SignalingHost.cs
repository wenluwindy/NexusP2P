using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using NexusP2P.Signaling.RateLimiting;
using NexusP2P.Signaling.Rooms;
using NexusP2P.Signaling.Signaling;
using NexusP2P.Signaling.Turn;
using NexusP2P.Signaling.Web;

namespace NexusP2P.Signaling;

/// <summary>
/// 信令服务器的组装逻辑。
///
/// <para>抽出来是为了让 <c>Program.cs</c> 与测试<b>走同一条组装路径</b>。
/// 各写一份的后果是：测试里的服务注册漂移之后，测试仍然全绿而真实进程已经坏了。</para>
/// </summary>
public static class SignalingHost
{
    public static void ConfigureServices(WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services
            .AddOptions<SignalingOptions>()
            .Bind(builder.Configuration.GetSection(SignalingOptions.SectionName))
            // 配置错了要在启动时就崩掉，而不是带着错配置跑起来生成一堆
            // 对方打不开的分享链接（AD-8）
            .ValidateOnStart();

        builder.Services.AddSingleton<IValidateOptions<SignalingOptions>, SignalingOptionsValidator>();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<RoomRegistry>();
        builder.Services.AddSingleton<JoinRateLimiter>();
        builder.Services.AddSingleton<TurnCredentialService>();
        builder.Services.AddHostedService<RoomSweeper>();
    }

    public static void Configure(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var options = app.Services.GetRequiredService<IOptions<SignalingOptions>>().Value;

        // 必须在其他中间件之前：后面的东西（尤其是入房限速）看到的
        // RemoteIpAddress 得是真实客户端的，而不是代理的
        if (options.BehindReverseProxy)
        {
            app.UseForwardedHeaders(BuildForwardedHeaders(options));
        }

        // 紧跟在转发头处理之后：这时才能看出它到底有没有生效
        UseProxyDiagnostics(app, options);

        app.UseWebSockets();

        // 健康检查：一眼看出服务是否活着，以及当前有多少活跃房间
        app.MapGet("/health", (RoomRegistry rooms, IOptions<SignalingOptions> options) => Results.Ok(new
        {
            status = "ok",
            activeRooms = rooms.RoomCount,
            publicOrigin = options.Value.PublicOrigin,
            relayConfigured = options.Value.Turn.Urls.Length > 0,
            behindReverseProxy = options.Value.BehindReverseProxy,
        }));

        app.MapSignaling();

        // 网页界面。放在信令之后：信令是这个服务的本职，界面是附带的，
        // 前端目录缺失时只警告不影响信令可用。
        app.MapWebUi();
    }

    /// <summary>
    /// 代理配置错了会怎样：**入房限速悄悄失效**，而没有任何直接现象 ——
    /// 文件照样传得动，只有「九位码不被枚举」这道屏障没了。
    ///
    /// <para>所以这里主动喊出来，而不是等人去猜。两种错各喊一次
    /// （只喊一次，不刷日志）：</para>
    /// <list type="bullet">
    /// <item>开了代理模式，但转发头来自不可信来源 —— 顺手把该填进
    /// <c>KnownProxies</c> 的那个 IP 打出来。<b>1Panel、宝塔这类面板尤其容易撞上</b>：
    /// 它们的 OpenResty 跑在 Docker 里，请求到达时的源地址是网桥地址而不是
    /// <c>127.0.0.1</c>，于是默认「只信本机」不成立。</item>
    /// <item>没开代理模式，却收到了转发头 —— 要么是忘了开，
    /// 要么真有人在伪造。</item>
    /// </list>
    /// </summary>
    private static void UseProxyDiagnostics(WebApplication app, SignalingOptions options)
    {
        var logger = app.Services
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger("NexusP2P.Signaling.Proxy");

        var warnedUntrusted = 0;
        var warnedUnexpected = 0;

        app.Use(async (context, next) =>
        {
            var forwarded = context.Request.Headers["X-Forwarded-For"].ToString();

            if (!string.IsNullOrEmpty(forwarded))
            {
                // 转发头被采信时，中间件会把取用的那一跳挪到 X-Original-For。
                // 用它判断而不是「X-Forwarded-For 还在不在」：多层代理
                // （比如 Cloudflare 再到本机 nginx）时头里会剩下没取用的那几跳，
                // 按「还在」判断会误报。
                var applied = context.Request.Headers.ContainsKey("X-Original-For");
                var remote = context.Connection.RemoteIpAddress;

                if (options.BehindReverseProxy && !applied &&
                    Interlocked.Exchange(ref warnedUntrusted, 1) == 0)
                {
                    logger.LogWarning(
                        "收到 X-Forwarded-For（{Forwarded}）但没有采信：请求来自 {Remote}，不在可信代理范围内。" +
                        "结果是入房限速按 {Remote} 计算 —— 所有人共用一个配额，而枚举文件码的人也不受限。" +
                        "把 Signaling:KnownProxies 设成 [\"{Remote}\"] 即可（面板类环境下代理常在 Docker 里，" +
                        "源地址是网桥地址而不是 127.0.0.1）。",
                        forwarded, remote, remote, remote);
                }
                else if (!options.BehindReverseProxy &&
                         Interlocked.Exchange(ref warnedUnexpected, 1) == 0)
                {
                    logger.LogWarning(
                        "收到 X-Forwarded-For（{Forwarded}）但 Signaling:BehindReverseProxy 是 false，已忽略。" +
                        "若确实跑在反向代理后面，请把它设为 true，否则入房限速会按代理的 IP 计算；" +
                        "若并没有代理，那就是有人在伪造这个头，忽略是正确的。",
                        forwarded);
                }
            }

            await next(context).ConfigureAwait(false);
        });
    }

    /// <summary>
    /// 转发头的处理规则。
    ///
    /// <para><b>只认可信来源的转发头。</b><c>X-Forwarded-For</c> 是客户端能随便写的，
    /// 无条件采信等于让任何人都能伪造 IP 绕过入房限速 —— 而限速正是九位码
    /// 不被枚举的唯一屏障。</para>
    /// </summary>
    private static ForwardedHeadersOptions BuildForwardedHeaders(SignalingOptions options)
    {
        var forwarded = new ForwardedHeadersOptions
        {
            // 只要 For：Proto 与 Host 我们都不用（PublicOrigin 是显式配置的，
            // 刻意不从请求头推断）
            ForwardedHeaders = ForwardedHeaders.XForwardedFor,
        };

        if (options.KnownProxies.Length == 0)
        {
            // 默认信任本机 —— 代理与本服务同机是最常见的部署形态。
            // ForwardedHeadersOptions 的默认值已经包含了 loopback，这里不动它。
            return forwarded;
        }

        // 显式列了代理就<b>只</b>信任它们：清掉默认的 loopback 与已知网段，
        // 免得留下一个比配置更宽的信任面
        forwarded.KnownProxies.Clear();
        forwarded.KnownNetworks.Clear();

        foreach (var proxy in options.KnownProxies)
        {
            // 启动校验已经保证解析得动，这里再挡一次以防绕过校验直接调用
            if (IPAddress.TryParse(proxy, out var address))
            {
                forwarded.KnownProxies.Add(address);
            }
        }

        return forwarded;
    }
}

/// <summary>
/// 定期回收宽限期已过的空房间。
///
/// <para>房间全在内存里，所以「回收」只是从字典里删掉。
/// 不做这件事的后果是内存随时间单调增长。</para>
/// </summary>
public sealed class RoomSweeper(RoomRegistry registry, ILogger<RoomSweeper> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);

        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                var removed = registry.Sweep();
                if (removed > 0 && logger.IsEnabled(LogLevel.Debug))
                {
                    logger.LogDebug("本轮回收了 {Count} 个过期房间。", removed);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 清理失败不该让整个服务停掉
                logger.LogError(ex, "回收过期房间时出错。");
            }
        }
    }
}
