using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace NexusP2P.Integration.Tests.Signaling;

/// <summary>
/// 跑在反向代理后面时，入房限速必须按<b>真实客户端</b>的 IP 算。
///
/// <para>不处理转发头的后果不是「限速略微不准」，而是彻底失效：
/// 经过代理之后每个请求的来源 IP 都是代理自己，所有人共用一个配额 ——
/// 几十次尝试之后全体被 429，而想枚举文件码的人也只占大家共用的那一份。</para>
///
/// <para>反过来，不在代理后面却信任 <c>X-Forwarded-For</c> 更糟：
/// 那个头客户端可以随便写，于是伪造一下就能绕过限速。所以这个开关
/// <b>必须显式打开</b>，两种方向都要有测试盯着。</para>
///
/// <para><b>必须用真实 Kestrel，不能用 <c>TestServer</c>。</b>
/// TestServer 不设置 <c>RemoteIpAddress</c>，而 ForwardedHeaders 中间件把
/// 「没有远端 IP」当作可信来源 —— 于是转发头永远被采信，
/// 「信任范围」这件事在 TestServer 上根本测不出来。</para>
/// </summary>
public sealed class ReverseProxyTests
{
    private const int JoinLimit = 3;

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>把日志抓下来，用于验证配错时到底喊没喊。</summary>
    private sealed class LogSink : ILoggerProvider
    {
        private readonly List<string> _lines = [];
        private readonly Lock _gate = new();

        public IReadOnlyList<string> Lines
        {
            get
            {
                lock (_gate)
                {
                    return [.. _lines];
                }
            }
        }

        public ILogger CreateLogger(string categoryName) => new Sink(this);

        public void Dispose()
        {
        }

        private void Add(string line)
        {
            lock (_gate)
            {
                _lines.Add(line);
            }
        }

        private sealed class Sink(LogSink owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (IsEnabled(logLevel))
                {
                    owner.Add(formatter(state, exception));
                }
            }
        }
    }

    private static WebApplication Build(
        int port, bool behindProxy, string[]? knownProxies = null, LogSink? sink = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration["Signaling:PublicOrigin"] = "https://test.example.com";
        builder.Configuration["Signaling:JoinAttemptsPerMinute"] = JoinLimit.ToString();
        builder.Configuration["Signaling:BehindReverseProxy"] = behindProxy ? "true" : "false";

        for (var i = 0; i < (knownProxies?.Length ?? 0); i++)
        {
            builder.Configuration[$"Signaling:KnownProxies:{i}"] = knownProxies![i];
        }

        builder.Logging.ClearProviders();
        if (sink is not null)
        {
            builder.Logging.AddProvider(sink);
        }

        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");

        NexusP2P.Signaling.SignalingHost.ConfigureServices(builder);
        var app = builder.Build();
        NexusP2P.Signaling.SignalingHost.Configure(app);
        return app;
    }

    /// <summary>
    /// 拿一个不存在的码去入房。返回状态码。
    ///
    /// <para>限速判定在 WebSocket 升级<b>之前</b>，所以普通 GET 也能触发：
    /// 没被限速时因为不是 WebSocket 请求而得到 400，被限速则是 429。</para>
    /// </summary>
    private static async Task<HttpStatusCode> TryJoinAsync(
        HttpClient client, string origin, string forwardedFor)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"{origin}/signal/join/123456789?role=receiver");
        request.Headers.Add("X-Forwarded-For", forwardedFor);

        using var response = await client.SendAsync(request);
        return response.StatusCode;
    }

    [Fact]
    public async Task 代理模式下不同客户端各自计数()
    {
        var port = GetFreePort();
        var origin = $"http://127.0.0.1:{port}";
        var app = Build(port, behindProxy: true);

        await app.StartAsync();
        try
        {
            using var client = new HttpClient();

            // 同一个客户端打满配额
            for (var i = 0; i < JoinLimit; i++)
            {
                Assert.Equal(
                    HttpStatusCode.BadRequest, await TryJoinAsync(client, origin, "203.0.113.5"));
            }

            Assert.Equal(
                HttpStatusCode.TooManyRequests, await TryJoinAsync(client, origin, "203.0.113.5"));

            // 另一个客户端不该被连带 —— 这正是不处理转发头时会坏掉的地方
            Assert.Equal(
                HttpStatusCode.BadRequest, await TryJoinAsync(client, origin, "203.0.113.99"));
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task 没开代理模式时不采信转发头()
    {
        // X-Forwarded-For 是客户端可以随便写的。不在代理后面却信它，
        // 等于任何人换个假 IP 就能重置自己的配额。
        var port = GetFreePort();
        var origin = $"http://127.0.0.1:{port}";
        var app = Build(port, behindProxy: false);

        await app.StartAsync();
        try
        {
            using var client = new HttpClient();

            for (var i = 0; i < JoinLimit; i++)
            {
                Assert.Equal(
                    HttpStatusCode.BadRequest, await TryJoinAsync(client, origin, $"203.0.113.{i}"));
            }

            // 每次都换一个伪造的 IP，仍然应该被限住（真实来源始终是 127.0.0.1）
            Assert.Equal(
                HttpStatusCode.TooManyRequests, await TryJoinAsync(client, origin, "203.0.113.250"));
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task 显式列了代理就只信任它们()
    {
        // 代理列的是另一台机器，请求实际来自 127.0.0.1 —— 转发头不该被采信。
        // 不清掉默认信任的 loopback 网段的话，这条会失败。
        var port = GetFreePort();
        var origin = $"http://127.0.0.1:{port}";
        var app = Build(port, behindProxy: true, knownProxies: ["198.51.100.7"]);

        await app.StartAsync();
        try
        {
            using var client = new HttpClient();

            for (var i = 0; i < JoinLimit; i++)
            {
                Assert.Equal(
                    HttpStatusCode.BadRequest, await TryJoinAsync(client, origin, $"203.0.113.{i}"));
            }

            Assert.Equal(
                HttpStatusCode.TooManyRequests, await TryJoinAsync(client, origin, "203.0.113.250"));
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public void 配了代理却没打开开关会拒绝启动()
    {
        // 「配置看着对、行为却不对」是最难查的一类错。必须在起服务时就打掉，
        // 而不是等限速出问题才让人去猜。
        var failure = Assert.ThrowsAny<Exception>(
            () => Build(GetFreePort(), behindProxy: false, knownProxies: ["198.51.100.7"]));

        Assert.Contains("BehindReverseProxy", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 代理地址写错会拒绝启动()
    {
        var failure = Assert.ThrowsAny<Exception>(
            () => Build(GetFreePort(), behindProxy: true, knownProxies: ["不是一个 IP"]));

        Assert.Contains("KnownProxies", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 转发头没被采信时会把该填的_IP_喊出来()
    {
        // 配错的现象是「文件照样传得动，只有限速悄悄失效」——
        // 没有主动提示的话根本不会有人去查。
        // 面板类环境（1Panel / 宝塔）尤其容易撞上：代理跑在 Docker 里，
        // 源地址是网桥地址而不是 127.0.0.1。
        var port = GetFreePort();
        var origin = $"http://127.0.0.1:{port}";
        var sink = new LogSink();
        var app = Build(port, behindProxy: true, knownProxies: ["198.51.100.7"], sink: sink);

        await app.StartAsync();
        try
        {
            using var client = new HttpClient();
            await TryJoinAsync(client, origin, "203.0.113.5");
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }

        var warning = Assert.Single(sink.Lines, l => l.Contains("X-Forwarded-For", StringComparison.Ordinal));

        // 必须给出可直接照抄的东西：该往 KnownProxies 里填的那个地址
        Assert.Contains("KnownProxies", warning, StringComparison.Ordinal);
        Assert.Contains("127.0.0.1", warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 没开代理模式却收到转发头也会提示()
    {
        var port = GetFreePort();
        var origin = $"http://127.0.0.1:{port}";
        var sink = new LogSink();
        var app = Build(port, behindProxy: false, sink: sink);

        await app.StartAsync();
        try
        {
            using var client = new HttpClient();
            await TryJoinAsync(client, origin, "203.0.113.5");
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }

        var warning = Assert.Single(sink.Lines, l => l.Contains("X-Forwarded-For", StringComparison.Ordinal));
        Assert.Contains("BehindReverseProxy", warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 配置正确时不发警告()
    {
        // 正常情况下不该刷日志 —— 警告要是天天出现，就没人当真了
        var port = GetFreePort();
        var origin = $"http://127.0.0.1:{port}";
        var sink = new LogSink();
        var app = Build(port, behindProxy: true, sink: sink);   // 默认信任本机，而请求正是来自本机

        await app.StartAsync();
        try
        {
            using var client = new HttpClient();
            await TryJoinAsync(client, origin, "203.0.113.5");
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }

        Assert.DoesNotContain(sink.Lines, l => l.Contains("X-Forwarded-For", StringComparison.Ordinal));
    }

    [Fact]
    public async Task health_里能看出代理模式是否生效()
    {
        // 这一项配错了没有任何直接现象：不在代理后面也能正常传文件，
        // 只有限速会悄悄失效。所以必须能一眼查出来。
        var port = GetFreePort();
        var app = Build(port, behindProxy: true);

        await app.StartAsync();
        try
        {
            using var client = new HttpClient();
            var health = await client.GetStringAsync($"http://127.0.0.1:{port}/health");

            Assert.Contains("\"behindReverseProxy\":true", health, StringComparison.Ordinal);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
