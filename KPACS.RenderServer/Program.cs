// ------------------------------------------------------------------------------------------------
// KPACS.RenderServer - Program.cs
// Entry point for the headless GPU rendering server.
// Hosts gRPC services: SessionService, VolumeService, RenderService, InputService.
// ------------------------------------------------------------------------------------------------

using KPACS.RenderServer.Services;
using KPACS.Viewer.Models;
using KPACS.Viewer.Plugins;
using KPACS.Viewer.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddGrpc(options =>
{
    // Allow large frames in responses (up to 16 MB for high-quality snapshots).
    options.MaxReceiveMessageSize = 16 * 1024 * 1024;
    options.MaxSendMessageSize = 16 * 1024 * 1024;
});

// Resolve the imagebox database path — use the same default as K-PACS Viewer.
string imageboxDbPath = builder.Configuration["RenderServer:ImageboxDatabasePath"] ?? "";
if (string.IsNullOrWhiteSpace(imageboxDbPath))
{
    imageboxDbPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "KPACS.Viewer.Avalonia", "Imagebox", "imagebox.db");
}

if (!File.Exists(imageboxDbPath))
{
    Console.Error.WriteLine($"WARNING: Imagebox database not found at '{imageboxDbPath}'. " +
        "Study browser will return empty results. Import studies in K-PACS Viewer first, " +
        "or set RenderServer:ImageboxDatabasePath in appsettings.json.");
}

// ImageboxRepository opened in read-only mode for the render server.
builder.Services.AddSingleton(new ImageboxRepository(imageboxDbPath));

// Core singletons.
builder.Services.AddSingleton<SessionManager>();
builder.Services.AddSingleton<VolumeManager>();
builder.Services.AddSingleton<FrameEncoder>();
builder.Services.AddSingleton<RenderOrchestrator>();

// Plugin manager — discovers and manages server-side plugins.
string pluginScratchRoot = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "KPACS.RenderServer", "plugin-scratch");
string pluginSearchPath = builder.Configuration["RenderServer:PluginDirectory"]
    ?? Path.Combine(AppContext.BaseDirectory, "Plugins");
var pluginManager = new PluginManager(pluginScratchRoot, dataDirectory: null, hostVersion: "0.7.0");
int pluginsFound = pluginManager.DiscoverPlugins(pluginSearchPath);
builder.Services.AddSingleton(pluginManager);

// Background service that reaps idle sessions.
builder.Services.AddHostedService<SessionReaperService>();

var app = builder.Build();

app.MapGrpcService<SessionServiceImpl>();
app.MapGrpcService<VolumeServiceImpl>();
app.MapGrpcService<RenderServiceImpl>();
app.MapGrpcService<InputServiceImpl>();
app.MapGrpcService<StudyBrowserServiceImpl>();
app.MapGrpcService<PluginProxyServiceImpl>();

// Minimal health-check endpoint for load balancers / monitoring.
app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

app.Logger.LogInformation("K-PACS Render Server starting on {Urls} ({PluginsFound} plugins discovered)",
    string.Join(", ", app.Urls), pluginsFound);
app.Run();
