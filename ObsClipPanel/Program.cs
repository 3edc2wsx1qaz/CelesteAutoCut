using ObsClipPanel;

if (ParentProcessLifetimeService.ShouldTerminateBeforeStartup())
{
    return;
}

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<PanelStateStore>();
builder.Services.AddSingleton<PanelCoordinator>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<PanelCoordinator>());
builder.Services.AddHostedService<ParentProcessLifetimeService>();

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/status", (PanelCoordinator coordinator) => coordinator.GetSnapshot());
app.MapPost("/api/settings", async (PanelSettingsInput input, PanelCoordinator coordinator) => await coordinator.SaveSettingsAsync(input));
app.MapPost("/api/session/reset", async (ManualSessionRequest request, PanelCoordinator coordinator) => await coordinator.ResetSessionAsync(request.SessionName));
app.MapPost("/api/obs/connect", async (PanelCoordinator coordinator) => await coordinator.ConnectObsAsync());
app.MapPost("/api/obs/disconnect", async (PanelCoordinator coordinator) => await coordinator.DisconnectObsAsync());
app.MapPost("/api/obs/start-record", async (PanelCoordinator coordinator) => await coordinator.StartRecordAsync());
app.MapPost("/api/obs/stop-record", async (PanelCoordinator coordinator) => await coordinator.StopRecordAsync());
app.MapPost("/api/obs/pause-record", async (PanelCoordinator coordinator) => await coordinator.PauseRecordAsync());
app.MapPost("/api/obs/resume-record", async (PanelCoordinator coordinator) => await coordinator.ResumeRecordAsync());
app.MapPost("/api/obs/split-record-file", async (PanelCoordinator coordinator) => await coordinator.SplitRecordFileAsync());
app.MapPost("/api/pipeline/build-final", async (PanelCoordinator coordinator) => await coordinator.BuildFinalVideoAsync());

try
{
    app.Run();
}
catch (OperationCanceledException)
{
    // Parent-process watcher can stop the host during startup/shutdown; treat as normal exit.
}
