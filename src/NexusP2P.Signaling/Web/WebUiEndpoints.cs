using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using NexusP2P.Core.Codes;

namespace NexusP2P.Signaling.Web;

/// <summary>
/// 托管网页端前端。
///
/// <para>前端与信令服务器同源，所以浏览器里不需要处理 CORS，
/// WebSocket 也可以直接用相对地址连。</para>
///
/// <para><b>为什么不用默认的 UseStaticFiles</b>：需要两件默认行为给不了的东西 ——
/// <c>/r/{code}</c> 要回退到首页（分享链接是「路径里带码」的形式），
/// 以及给 <c>.js</c> 加上不缓存的头（前端是随服务器一起部署的，
/// 缓存住旧版本会让「改完没生效」变成经常发生的事）。</para>
/// </summary>
public static class WebUiEndpoints
{
    /// <summary>前端产物相对可执行文件的目录名。</summary>
    private const string WebRootDirectory = "wwwroot";

    public static void MapWebUi(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var root = Path.Combine(AppContext.BaseDirectory, WebRootDirectory);
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("NexusP2P.Web");

        if (!Directory.Exists(root))
        {
            // 只发信令、不带界面也是一种合法部署（比如 exe 自带界面时）。
            // 但这必须说出来 —— 否则访问首页得到 404，看起来像服务坏了。
            logger.LogWarning(
                "找不到前端目录 {Root}，网页界面不可用（信令本身仍然正常）。", root);
            return;
        }

        var files = new PhysicalFileProvider(root);
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = files,
            ContentTypeProvider = BuildContentTypes(),
            OnPrepareResponse = context =>
                context.Context.Response.Headers.CacheControl = "no-cache, must-revalidate",
        });

        MapIndexRoutes(app, files);
        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("网页界面已挂载，来自 {Root}。", root);
        }
    }

    /// <summary>
    /// 首页与分享链接路由。
    ///
    /// <para><c>/r/{code}</c> 回退到首页而不是 404：分享链接的形式是
    /// <c>https://域名/r/111111111#密钥</c>，路径里那个码对服务器毫无意义
    /// （码只用于 WebSocket 入房），而 <c>#</c> 后面的密钥
    /// <b>根本不会到达服务器</b> —— 前端从 <c>location</c> 里自己读。</para>
    /// </summary>
    private static void MapIndexRoutes(WebApplication app, PhysicalFileProvider files)
    {
        var index = files.GetFileInfo("index.html");
        if (!index.Exists)
        {
            return;
        }

        // 每次请求重新取 FileInfo：进程运行期间前端文件可能被替换。
        //
        // 这里必须自己加不缓存的头：这两条路由走 Results.File，
        // 不经过 UseStaticFiles 的 OnPrepareResponse。首页被缓存住、
        // 而 .js 每次都重新取，会得到「新 HTML 配旧脚本」或反过来的错配 ——
        // 症状是某个按钮点了完全没反应（脚本在找一个已经改名的元素）。
        IResult ServeIndex(HttpContext context)
        {
            context.Response.Headers.CacheControl = "no-cache, must-revalidate";
            return Results.File(
                files.GetFileInfo("index.html").PhysicalPath!,
                contentType: "text/html; charset=utf-8");
        }

        app.MapGet("/", ServeIndex);

        // 码的格式在这里不校验：校验会让「码不存在」与「格式不对」产生差异，
        // 而服务端刻意不区分这两者（防枚举，见 SignalingEndpoints）。
        // 界面照常打开，真正的判定发生在入房那一刻。
        app.MapGet($"/{ShareLinkFactory.RoomPathSegment}/{{code}}", ServeIndex);
    }

    /// <summary>
    /// 显式的 MIME 映射。
    ///
    /// <para><c>.mjs</c> 与 Worker 用的 <c>.js</c> 必须是
    /// <c>text/javascript</c>，否则浏览器会以 MIME 类型不符为由拒绝加载模块 ——
    /// 现象是「页面空白，控制台报 module 被阻止」。</para>
    /// </summary>
    private static FileExtensionContentTypeProvider BuildContentTypes()
    {
        var provider = new FileExtensionContentTypeProvider();
        provider.Mappings[".js"] = "text/javascript; charset=utf-8";
        provider.Mappings[".mjs"] = "text/javascript; charset=utf-8";
        provider.Mappings[".css"] = "text/css; charset=utf-8";
        provider.Mappings[".html"] = "text/html; charset=utf-8";
        return provider;
    }
}
