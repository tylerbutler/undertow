using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Undertow.Abstractions;
using Undertow.Server;
using Undertow.Storage.Memory;
using Undertow.Storage.Sqlite;

// --healthcheck argv mode: the chiseled runtime image has no shell or wget, so
// the container HEALTHCHECK re-runs this binary. 127.0.0.1 rather than
// localhost for the same IPv4 reason the Gleam Dockerfile documents.
if (args.Contains("--healthcheck"))
{
    var healthPort = Environment.GetEnvironmentVariable("PORT") ?? "3000";
    using var healthClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
    try
    {
        var response = await healthClient.GetAsync($"http://127.0.0.1:{healthPort}/health");
        return response.IsSuccessStatusCode ? 0 : 1;
    }
    catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
    {
        return 1;
    }
}

var builder = WebApplication.CreateSlimBuilder(args);
builder.Logging.AddSimpleConsole();

var config = UndertowConfig.FromEnvironment(
    Environment.GetEnvironmentVariable,
    line => Console.WriteLine(line));

if (config.JwtSecret.Length == 0)
{
    Console.Error.WriteLine("UNDERTOW_JWT_SECRET is required");
    return 1;
}

builder.Services.AddSingleton(config);
builder.Services.AddSingleton(TimeProvider.System);

switch (config.StorageBackend)
{
    case "ets" or "shelf":
        Directory.CreateDirectory(config.DataDir);
        var sqlite = SqliteStorage.OpenFile(Path.Combine(config.DataDir, "undertow.db"));
        builder.Services.AddSingleton<IDocumentStore>(sqlite);
        builder.Services.AddSingleton<IGitObjectStore>(sqlite);
        break;
    case "memory":
        builder.Services.AddSingleton<IDocumentStore>(new MemoryDocumentStore());
        builder.Services.AddSingleton<IGitObjectStore>(new MemoryGitObjectStore());
        break;
    default:
        Console.Error.WriteLine($"unsupported storage backend: {config.StorageBackend}");
        return 1;
}

builder.Services.AddSingleton<DocumentService>();

// Socket runtime: registry, broadcaster, document sessions, channel dispatch.
builder.Services.AddSingleton<Undertow.Runtime.SocketRegistry>();
builder.Services.AddSingleton<Undertow.Runtime.IChannelBroadcaster>(sp =>
    new Undertow.Runtime.LocalBroadcaster(sp.GetRequiredService<Undertow.Runtime.SocketRegistry>()));
builder.Services.AddSingleton(sp => new Undertow.Runtime.DocumentRegistry(
    sp.GetRequiredService<IDocumentStore>(), sp.GetRequiredService<IGitObjectStore>(),
    sp.GetRequiredService<TimeProvider>(), config.CompatRestoreMsnFromSummary,
    config.OpPruneBelowSummary));
builder.Services.AddSingleton<Undertow.Runtime.IChannelHandler>(sp =>
    new Undertow.Runtime.DocumentChannel(
        sp.GetRequiredService<Undertow.Runtime.DocumentRegistry>(),
        sp.GetRequiredService<IDocumentStore>(), sp.GetRequiredService<IGitObjectStore>(),
        config.Tenant, config.JwtSecret, config.MaxFrameBytes, sp.GetRequiredService<TimeProvider>()));
builder.Services.AddSingleton(sp => new Undertow.Runtime.ChannelDispatcher(
    sp.GetRequiredService<Undertow.Runtime.SocketRegistry>(),
    sp.GetRequiredService<Undertow.Runtime.IChannelBroadcaster>(),
    sp.GetRequiredService<Undertow.Runtime.IChannelHandler>()));
builder.Services.AddSingleton(Undertow.Protocol.OriginPolicyBox.FromEnv(config.AllowedOrigins));
builder.Services.AddSingleton(sp => new Undertow.Runtime.TransportGuards(
    new Undertow.Runtime.ConnectionLimiter(config.MaxConnectionsPerIp, config.MaxConnections),
    config.MessageRate, config.MessageBurst, config.JoinRate, config.JoinBurst,
    sp.GetRequiredService<TimeProvider>()));
builder.Services.AddSingleton(sp => new Undertow.Transports.PhoenixTransport(
    sp.GetRequiredService<Undertow.Runtime.ChannelDispatcher>(),
    sp.GetRequiredService<Undertow.Runtime.SocketRegistry>(),
    sp.GetRequiredService<Undertow.Protocol.OriginPolicyBox>(),
    sp.GetRequiredService<Undertow.Runtime.TransportGuards>(),
    config.MaxFrameBytes, sp.GetRequiredService<TimeProvider>()));
builder.Services.AddSingleton(sp => new Undertow.Transports.SocketIoTransport(
    sp.GetRequiredService<Undertow.Runtime.ChannelDispatcher>(),
    sp.GetRequiredService<Undertow.Runtime.SocketRegistry>(),
    sp.GetRequiredService<Undertow.Protocol.OriginPolicyBox>(),
    sp.GetRequiredService<Undertow.Runtime.TransportGuards>(),
    config.MaxFrameBytes, sp.GetRequiredService<TimeProvider>()));

// The two reapers: the coordinator liveness sweep (stale RSNs must not pin
// MSN) and idle-document eviction.
builder.Services.AddSingleton(sp => new Undertow.Runtime.SocketSweeper(
    sp.GetRequiredService<Undertow.Runtime.SocketRegistry>(),
    sp.GetRequiredService<Undertow.Runtime.ChannelDispatcher>(),
    sp.GetRequiredService<TimeProvider>(), config.HeartbeatTimeoutMs));
builder.Services.AddSingleton(sp => new Undertow.Runtime.DocumentIdleSweeper(
    sp.GetRequiredService<Undertow.Runtime.DocumentRegistry>(),
    sp.GetRequiredService<TimeProvider>(), config.DocIdleMs));
builder.Services.AddHostedService<SocketSweeperService>();
builder.Services.AddHostedService<DocumentIdleSweeperService>();

// Post-parity, opt-in telemetry: traces + ASP.NET Core metrics over OTLP.
// Off unless an endpoint is configured, so the parity path adds nothing.
var otelEndpoint = Environment.GetEnvironmentVariable("UNDERTOW_OTEL_ENDPOINT");
if (!string.IsNullOrEmpty(otelEndpoint))
{
    builder.Services.AddOpenTelemetry()
        .ConfigureResource(resource => resource.AddService("undertow"))
        .WithTracing(tracing => tracing
            .AddAspNetCoreInstrumentation()
            .AddOtlpExporter(options => options.Endpoint = new Uri(otelEndpoint)))
        .WithMetrics(metrics => metrics
            .AddAspNetCoreInstrumentation()
            .AddOtlpExporter(options => options.Endpoint = new Uri(otelEndpoint)));
}

builder.WebHost.UseUrls($"http://{config.Bind}:{config.Port}");

var app = builder.Build();

app.UseWebSockets();
app.Map(Undertow.Transports.PhoenixTransport.Path, (HttpContext context) =>
    context.RequestServices.GetRequiredService<Undertow.Transports.PhoenixTransport>().HandleAsync(context));
app.Use(async (context, next) =>
{
    if (Undertow.Transports.SocketIoTransport.Matches(context.Request.Path))
        await context.RequestServices.GetRequiredService<Undertow.Transports.SocketIoTransport>()
            .HandleAsync(context);
    else
        await next();
});

// RestLess rewrites the request method, so it must run before route matching —
// and WebApplication would otherwise auto-insert UseRouting at the very start
// of the pipeline, so routing is anchored explicitly after RestLess.
app.UseRestLess();
app.UseRouting();
app.MapUndertowRoutes();

app.Run();
return 0;

public partial class Program;
