using System.Text.Json;
using System.Text.Json.Serialization;

namespace ObsClipSidecar;

public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };
}

public sealed record RoomEvent
{
    public int SchemaVersion { get; init; } = 1;
    public string EventId { get; init; } = Guid.NewGuid().ToString("N");
    public string EventType { get; init; } = "";
    public DateTimeOffset Utc { get; init; }
    public string? Chapter { get; init; }
    public string? LevelSet { get; init; }
    public string? MapSid { get; init; }
    public string? Room { get; init; }
    public string? NextRoom { get; init; }
    public long? ChapterTimeMs { get; init; }
    public long? GameFrame { get; init; }
    public string? AttemptId { get; init; }
    public string? SessionId { get; init; }
    public Dictionary<string, object?>? Notes { get; init; }

    public bool IsRoomStart => EventType is "room_start" or "room_enter";
    public bool IsBoundaryEnd => EventType is "transition" or "room_end" or "level_complete";
    public bool IsSuccessfulBoundaryEnd => EventType is "transition" or "level_complete";
    public bool IsAttemptReset => EventType is "death" or "respawn" or "load_start" or "load_end" or "room_start" or "room_enter";
}

public sealed record ObsEvent
{
    public int SchemaVersion { get; init; } = 1;
    public string EventId { get; init; } = Guid.NewGuid().ToString("N");
    public string EventType { get; init; } = "record_status_sample";
    public DateTimeOffset Utc { get; init; }
    public string CaptureSource { get; init; } = "websocket";
    public bool? OutputActive { get; init; }
    public bool? OutputPaused { get; init; }
    public long? OutputDurationMs { get; init; }
    public string? OutputPath { get; init; }
    public string? NewOutputPath { get; init; }
    public long? OutputBytes { get; init; }
    public Dictionary<string, object?>? Raw { get; init; }
}

public sealed record SessionManifest
{
    public int SchemaVersion { get; init; } = 1;
    public string SessionId { get; init; } = "";
    public string CaptureSource { get; init; } = "websocket";
    public ObsCapabilities Capabilities { get; init; } = new();
    public List<RecordingManifest> Recordings { get; init; } = [];
}

public sealed record ObsCapabilities
{
    public string? ObsWebsocketRpcVersion { get; init; }
    public bool SupportsRecordFileChanged { get; init; } = true;
    public bool SupportsOutputDurationAnchors { get; init; } = true;
}

public sealed record RecordingManifest
{
    public string RecordingId { get; init; } = "";
    public DateTimeOffset? StartUtc { get; init; }
    public DateTimeOffset? EndUtc { get; init; }
    public List<RecordingFileManifest> Files { get; init; } = [];
    public List<PauseInterval> Pauses { get; init; } = [];
    public List<ObsAnchor> Anchors { get; init; } = [];
}

public sealed record RecordingFileManifest
{
    public string Path { get; init; } = "";
    public long StartDurationMs { get; init; }
    public long EndDurationMs { get; init; }
}

public sealed record PauseInterval
{
    public long StartDurationMs { get; init; }
    public long EndDurationMs { get; init; }
    public DateTimeOffset? StartUtc { get; init; }
    public DateTimeOffset? EndUtc { get; init; }
    public string Source { get; init; } = "outputPaused";
}

public sealed record ObsAnchor
{
    public DateTimeOffset Utc { get; init; }
    public long OutputDurationMs { get; init; }
    public bool OutputPaused { get; init; }
    public string? OutputPath { get; init; }
    public string Source { get; init; } = "GetRecordStatus";
}

public sealed record BoundaryEstimate
{
    public DateTimeOffset Utc { get; init; }
    public string RecordingId { get; init; } = "";
    public string AlignmentMethod { get; init; } = "obs_output_duration_bracket";
    public ObsAnchor? AnchorBefore { get; init; }
    public ObsAnchor? AnchorAfter { get; init; }
    public long EstimatedOutputDurationMs { get; init; }
    public long ErrorBoundMs { get; init; }
    public bool IsValid { get; init; }
    public List<string> Reasons { get; init; } = [];
}

public sealed record ClipFileSlice
{
    public string SourcePath { get; init; } = "";
    public long SourceStartDurationMs { get; init; }
    public long SourceEndDurationMs { get; init; }
    public double InpointSeconds => SourceStartDurationMs / 1000.0;
    public double OutpointSeconds => SourceEndDurationMs / 1000.0;
}

public sealed record ClipInterval
{
    public string ClipId { get; init; } = Guid.NewGuid().ToString("N");
    public string? Room { get; init; }
    public string? MapSid { get; init; }
    public string RecordingId { get; init; } = "";
    public DateTimeOffset StartUtc { get; init; }
    public DateTimeOffset EndUtc { get; init; }
    public long StartOutputDurationMs { get; init; }
    public long EndOutputDurationMs { get; init; }
    public string AlignmentMethod { get; init; } = "obs_output_duration_bracket";
    public ObsAnchor? AnchorBefore { get; init; }
    public ObsAnchor? AnchorAfter { get; init; }
    public long ErrorBoundMs { get; init; }
    public string PausePolicy { get; init; } = "no_pause_seen";
    public bool IsValid { get; init; } = true;
    public string Status { get; init; } = "kept";
    public List<string> Reasons { get; init; } = [];
    public List<ClipFileSlice> SourceFileMapping { get; init; } = [];
}

public sealed record IntervalGenerationOptions
{
    public long PreRollMs { get; init; } = 250;
    public long PostRollMs { get; init; } = 500;
    public long MaxAllowedAnchorGapMs { get; init; } = 2_000;
    public long MaxAllowedCutErrorMs { get; init; } = 100;
    public bool SplitOnPause { get; init; } = true;
    public bool RequireExistingFiles { get; init; } = false;
}

public sealed record ClipIntervalsDocument
{
    public int SchemaVersion { get; init; } = 1;
    public IntervalGenerationOptions Options { get; init; } = new();
    public List<ClipInterval> Clips { get; init; } = [];
    public List<ClipInterval> InvalidClips { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
}

public sealed record ManifestBuildOptions
{
    public string SessionId { get; init; } = "";
    public string CaptureSource { get; init; } = "websocket";
    public string? ObsWebsocketRpcVersion { get; init; }
    public bool SupportsRecordFileChanged { get; init; } = true;
    public bool SupportsOutputDurationAnchors { get; init; } = true;
}

public sealed record AssemblyOptions
{
    public bool DryRun { get; init; } = true;
    public bool FastPreviewCopy { get; init; } = false;
    public long MaxAllowedCutErrorMs { get; init; } = 100;
    public string FinalOutputPath { get; init; } = "final_useful_run.mp4";
    public string? FfmpegPath { get; init; }
    public bool OverwriteOutput { get; init; } = true;
    public string VideoCodec { get; init; } = "libx264";
    public string AudioCodec { get; init; } = "aac";
    public string Preset { get; init; } = "veryfast";
    public int Crf { get; init; } = 18;
}

public sealed record AssemblyPlan
{
    public int SchemaVersion { get; init; } = 1;
    public string PrecisionMode { get; init; } = "precise_reencode";
    public bool DryRun { get; init; } = true;
    public bool FastPreviewCopy { get; init; }
    public bool Executed { get; init; }
    public string? FfmpegPath { get; init; }
    public string FinalOutputPath { get; init; } = "final_useful_run.mp4";
    public string FfconcatText { get; init; } = "ffconcat version 1.0\n";
    public string FfmpegCommand { get; init; } = "";
    public string? SegmentConcatText { get; init; }
    public List<string> Warnings { get; init; } = [];
    public List<string> IncludedClipIds { get; init; } = [];
}
