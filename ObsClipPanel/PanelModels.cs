namespace ObsClipPanel;

public sealed record PanelSettings
{
    public string ObsWebSocketUrl { get; init; } = "ws://127.0.0.1:4455";
    public string ObsWebSocketPassword { get; init; } = "";
    public string WorkingDirectory { get; init; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CelesteAutoCutObsPanel");
    public string RoomEventsPath { get; init; } = @"D:\Steam\steamapps\common\Celeste\CelesteAutoCutReplays\room_event_*.jsonl";
    public string OutputDirectory { get; init; } = "";
    public string FfmpegPath { get; init; } = "";
    public long PollIntervalMs { get; init; } = 1000;
    public long PreRollMs { get; init; } = 250;
    public long PostRollMs { get; init; } = 500;
    public long MaxAnchorGapMs { get; init; } = 2000;
    public long MaxCutErrorMs { get; init; } = 100;
    public string ClipIntensity { get; init; } = ObsClipSidecar.ClipIntensityModes.Low;
    public bool SplitOnPause { get; init; } = true;
    public bool RequireExistingFiles { get; init; } = true;
    public bool AutoAssembleOnStop { get; init; } = true;
    public bool LogOutputEnabled { get; init; }
    public string FinalOutputName { get; init; } = "{recording_start_local}.mp4";
}

public sealed record PanelSettingsInput
{
    public string ObsWebSocketUrl { get; init; } = "ws://127.0.0.1:4455";
    public string ObsWebSocketPassword { get; init; } = "";
    public string WorkingDirectory { get; init; } = "";
    public string RoomEventsPath { get; init; } = "";
    public string OutputDirectory { get; init; } = "";
    public string FfmpegPath { get; init; } = "";
    public long PollIntervalMs { get; init; } = 1000;
    public long PreRollMs { get; init; } = 250;
    public long PostRollMs { get; init; } = 500;
    public long MaxAnchorGapMs { get; init; } = 2000;
    public long MaxCutErrorMs { get; init; } = 100;
    public string ClipIntensity { get; init; } = ObsClipSidecar.ClipIntensityModes.Low;
    public bool SplitOnPause { get; init; } = true;
    public bool RequireExistingFiles { get; init; } = true;
    public bool AutoAssembleOnStop { get; init; } = true;
    public bool LogOutputEnabled { get; init; }
    public string FinalOutputName { get; init; } = "{recording_start_local}.mp4";
}

public sealed record PanelSettingsView
{
    public string ObsWebSocketUrl { get; init; } = "";
    public string ObsWebSocketPassword { get; init; } = "";
    public string WorkingDirectory { get; init; } = "";
    public string RoomEventsPath { get; init; } = "";
    public string OutputDirectory { get; init; } = "";
    public string FfmpegPath { get; init; } = "";
    public long PollIntervalMs { get; init; }
    public long PreRollMs { get; init; }
    public long PostRollMs { get; init; }
    public long MaxAnchorGapMs { get; init; }
    public long MaxCutErrorMs { get; init; }
    public string ClipIntensity { get; init; } = ObsClipSidecar.ClipIntensityModes.Low;
    public bool SplitOnPause { get; init; }
    public bool RequireExistingFiles { get; init; }
    public bool AutoAssembleOnStop { get; init; }
    public bool LogOutputEnabled { get; init; }
    public string FinalOutputName { get; init; } = "";
}

public sealed record SessionPaths
{
    public string SessionDirectory { get; init; } = "";
    public string ObsEventsPath { get; init; } = "";
    public string SessionManifestPath { get; init; } = "";
    public string ClipIntervalsPath { get; init; } = "";
    public string AssemblyDirectory { get; init; } = "";
    public string FinalOutputPath { get; init; } = "";
}

public sealed record PanelStatus
{
    public bool ObsConnected { get; init; }
    public bool ObsIdentified { get; init; }
    public bool RecordingActive { get; init; }
    public bool RecordingPaused { get; init; }
    public long OutputDurationMs { get; init; }
    public string OutputPath { get; init; } = "";
    public string ObsWebSocketVersion { get; init; } = "";
    public string ObsStudioVersion { get; init; } = "";
    public string CurrentSessionId { get; init; } = "";
    public SessionPaths Paths { get; init; } = new();
    public string LastError { get; init; } = "";
    public string LastInfo { get; init; } = "";
    public DateTimeOffset? LastEventUtc { get; init; }
    public DateTimeOffset? LastSuccessfulAssembleUtc { get; init; }
    public int LastClipCount { get; init; }
    public int LastInvalidClipCount { get; init; }
}

public sealed record PanelSnapshot
{
    public PanelSettingsView Settings { get; init; } = new();
    public PanelStatus Status { get; init; } = new();
}

public sealed record ApiResult(bool Success, string Message)
{
    public object? Data { get; init; }

    public static ApiResult Ok(string message, object? data = null) => new(true, message) { Data = data };
    public static ApiResult Fail(string message) => new(false, message);
}

public sealed record ManualSessionRequest(string? SessionName);
