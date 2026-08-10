using NexusP2P.Signaling;

var builder = WebApplication.CreateBuilder(args);
SignalingHost.ConfigureServices(builder);

var app = builder.Build();
SignalingHost.Configure(app);

app.Run();

/// <summary>供测试用 WebApplicationFactory 引用。</summary>
public partial class Program;
