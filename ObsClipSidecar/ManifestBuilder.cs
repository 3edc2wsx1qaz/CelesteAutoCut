namespace ObsClipSidecar;

public sealed class ManifestBuilder
{
    public SessionManifest Build(IReadOnlyList<ObsEvent> events, ManifestBuildOptions? options = null)
    {
        options ??= new ManifestBuildOptions();
        var sessionId = string.IsNullOrWhiteSpace(options.SessionId)
            ? $"session_{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffZ}"
            : options.SessionId;

        var capabilities = new ObsCapabilities
        {
            ObsWebsocketRpcVersion = options.ObsWebsocketRpcVersion,
            SupportsRecordFileChanged = options.SupportsRecordFileChanged,
            SupportsOutputDurationAnchors = options.SupportsOutputDurationAnchors
        };
        var manifest = new SessionManifest
        {
            SessionId = sessionId,
            CaptureSource = options.CaptureSource,
            Capabilities = capabilities
        };

        var sortedEvents = events
            .Select((e, i) => (Event: e, Index: i))
            .OrderBy(x => x.Event.Utc)
            .ThenBy(x => x.Index)
            .Select(x => x.Event)
            .ToList();

        RecordingBuilder? current = null;
        var recordingIndex = 0;

        foreach (var e in sortedEvents)
        {
            capabilities = ApplyCapabilityHints(capabilities, e);
            var duration = e.OutputDurationMs ?? current?.LastKnownDurationMs ?? 0;
            var active = e.OutputActive ?? current is not null;
            var paused = e.OutputPaused ?? current?.Paused ?? false;

            if (ShouldStartRecording(e, current))
            {
                current = new RecordingBuilder(
                    recordingId: $"recording_{recordingIndex++:D4}",
                    startUtc: e.Utc,
                    initialDurationMs: duration,
                    initialPath: e.OutputPath ?? e.NewOutputPath);
            }

            if (current is null)
            {
                continue;
            }

            current.LastKnownDurationMs = duration;
            current.LastObservedUtc = e.Utc;
            if (!string.IsNullOrWhiteSpace(e.OutputPath))
            {
                current.CurrentPath = e.OutputPath;
            }

            AddAnchorIfAvailable(current, e);
            UpdatePauseState(current, e, duration, paused);
            UpdateFileSplits(current, e, duration);

            if (ShouldStopRecording(e, active))
            {
                FinalizeRecording(current, e.Utc, duration);
                manifest.Recordings.Add(current.Build());
                current = null;
            }
        }

        if (current is not null)
        {
            FinalizeRecording(current, current.LastObservedUtc ?? current.StartUtc, current.LastKnownDurationMs);
            manifest.Recordings.Add(current.Build());
        }

        return manifest with { Capabilities = capabilities };
    }

    private static ObsCapabilities ApplyCapabilityHints(ObsCapabilities capabilities, ObsEvent e)
    {
        if (!string.Equals(e.EventType, "capabilities", StringComparison.OrdinalIgnoreCase) || e.Raw is null)
        {
            return capabilities;
        }

        if (TryGetString(e.Raw, "obsWebsocketRpcVersion", out var rpcVersion))
        {
            capabilities = capabilities with { ObsWebsocketRpcVersion = rpcVersion };
        }

        if (TryGetBool(e.Raw, "supportsRecordFileChanged", out var supportsRecordFileChanged))
        {
            capabilities = capabilities with { SupportsRecordFileChanged = supportsRecordFileChanged };
        }

        if (TryGetBool(e.Raw, "supportsOutputDurationAnchors", out var supportsOutputDurationAnchors))
        {
            capabilities = capabilities with { SupportsOutputDurationAnchors = supportsOutputDurationAnchors };
        }

        return capabilities;
    }

    private static bool ShouldStartRecording(ObsEvent e, RecordingBuilder? current) =>
        current is null && (
            string.Equals(e.EventType, "record_state", StringComparison.OrdinalIgnoreCase) && e.OutputActive == true ||
            string.Equals(e.EventType, "record_started", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(e.EventType, "record_status_sample", StringComparison.OrdinalIgnoreCase) && e.OutputActive == true);

    private static bool ShouldStopRecording(ObsEvent e, bool active) =>
        string.Equals(e.EventType, "stop_record", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(e.EventType, "record_stopped", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(e.EventType, "record_state", StringComparison.OrdinalIgnoreCase) && active == false && !IsStoppingTransition(e) ||
        string.Equals(e.EventType, "record_status_sample", StringComparison.OrdinalIgnoreCase) && active == false;

    private static bool IsStoppingTransition(ObsEvent e)
    {
        if (e.Raw is null || !TryGetString(e.Raw, "outputState", out var outputState) || string.IsNullOrWhiteSpace(outputState))
        {
            return false;
        }

        return string.Equals(outputState, "OBS_WEBSOCKET_OUTPUT_STOPPING", StringComparison.OrdinalIgnoreCase);
    }

    private static void AddAnchorIfAvailable(RecordingBuilder current, ObsEvent e)
    {
        if (e.OutputDurationMs is not long outputDurationMs)
        {
            return;
        }

        current.Anchors.Add(new ObsAnchor
        {
            Utc = e.Utc,
            OutputDurationMs = outputDurationMs,
            OutputPaused = e.OutputPaused ?? current.Paused,
            OutputPath = e.OutputPath ?? current.CurrentPath,
            Source = e.EventType
        });
    }

    private static void UpdatePauseState(RecordingBuilder current, ObsEvent e, long duration, bool paused)
    {
        var explicitPauseStart = string.Equals(e.EventType, "pause_start", StringComparison.OrdinalIgnoreCase);
        var explicitPauseEnd = string.Equals(e.EventType, "pause_end", StringComparison.OrdinalIgnoreCase);

        if ((explicitPauseStart || paused) && !current.Paused)
        {
            current.Paused = true;
            current.PauseStartDurationMs = duration;
            current.PauseStartUtc = e.Utc;
            return;
        }

        if ((explicitPauseEnd || !paused) && current.Paused)
        {
            current.Paused = false;
            current.Pauses.Add(new PauseInterval
            {
                StartDurationMs = current.PauseStartDurationMs,
                EndDurationMs = duration,
                StartUtc = current.PauseStartUtc,
                EndUtc = e.Utc,
                Source = e.EventType
            });
        }
    }

    private static void UpdateFileSplits(RecordingBuilder current, ObsEvent e, long duration)
    {
        if (!string.Equals(e.EventType, "record_file_changed", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        current.EnsureCurrentFile(duration);
        current.CloseCurrentFile(duration);
        current.CurrentPath = e.NewOutputPath ?? e.OutputPath ?? current.CurrentPath;
        current.CurrentFileStartDurationMs = duration;
    }

    private static void FinalizeRecording(RecordingBuilder current, DateTimeOffset endUtc, long duration)
    {
        if (current.Paused)
        {
            current.Paused = false;
            current.Pauses.Add(new PauseInterval
            {
                StartDurationMs = current.PauseStartDurationMs,
                EndDurationMs = duration,
                StartUtc = current.PauseStartUtc,
                EndUtc = endUtc,
                Source = "recording_stop"
            });
        }

        current.CloseCurrentFile(duration);
        current.EndUtc = endUtc;
    }

    private static bool TryGetBool(Dictionary<string, object?> raw, string key, out bool value)
    {
        value = false;
        if (!raw.TryGetValue(key, out var obj) || obj is null)
        {
            return false;
        }

        if (obj is bool b)
        {
            value = b;
            return true;
        }

        if (obj is string s && bool.TryParse(s, out var parsed))
        {
            value = parsed;
            return true;
        }

        if (obj is System.Text.Json.JsonElement json)
        {
            if (json.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False)
            {
                value = json.GetBoolean();
                return true;
            }

            if (json.ValueKind == System.Text.Json.JsonValueKind.String && bool.TryParse(json.GetString(), out parsed))
            {
                value = parsed;
                return true;
            }
        }

        return false;
    }

    private static bool TryGetString(Dictionary<string, object?> raw, string key, out string? value)
    {
        value = null;
        if (!raw.TryGetValue(key, out var obj) || obj is null)
        {
            return false;
        }

        if (obj is string s)
        {
            value = s;
            return true;
        }

        if (obj is System.Text.Json.JsonElement json && json.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            value = json.GetString();
            return true;
        }

        return false;
    }

    private sealed class RecordingBuilder
    {
        public RecordingBuilder(string recordingId, DateTimeOffset startUtc, long initialDurationMs, string? initialPath)
        {
            RecordingId = recordingId;
            StartUtc = startUtc;
            LastObservedUtc = startUtc;
            LastKnownDurationMs = initialDurationMs;
            CurrentPath = initialPath;
            CurrentFileStartDurationMs = Math.Max(0, initialDurationMs);
        }

        public string RecordingId { get; }
        public DateTimeOffset StartUtc { get; }
        public DateTimeOffset? EndUtc { get; set; }
        public DateTimeOffset? LastObservedUtc { get; set; }
        public long LastKnownDurationMs { get; set; }
        public string? CurrentPath { get; set; }
        public long CurrentFileStartDurationMs { get; set; }
        public bool Paused { get; set; }
        public long PauseStartDurationMs { get; set; }
        public DateTimeOffset? PauseStartUtc { get; set; }
        public List<RecordingFileManifest> Files { get; } = [];
        public List<PauseInterval> Pauses { get; } = [];
        public List<ObsAnchor> Anchors { get; } = [];

        public void EnsureCurrentFile(long duration)
        {
            if (string.IsNullOrWhiteSpace(CurrentPath))
            {
                CurrentPath = $"{RecordingId}_{Files.Count:D2}.mp4";
            }

            if (Files.Count == 0 && CurrentFileStartDurationMs > duration)
            {
                CurrentFileStartDurationMs = duration;
            }
        }

        public void CloseCurrentFile(long endDurationMs)
        {
            EnsureCurrentFile(endDurationMs);
            if (string.IsNullOrWhiteSpace(CurrentPath))
            {
                return;
            }

            var start = Math.Min(CurrentFileStartDurationMs, endDurationMs);
            var end = Math.Max(CurrentFileStartDurationMs, endDurationMs);
            if (Files.LastOrDefault() is { Path: var lastPath, EndDurationMs: var lastEnd } && lastPath == CurrentPath && lastEnd == end)
            {
                return;
            }

            Files.Add(new RecordingFileManifest
            {
                Path = CurrentPath,
                StartDurationMs = start,
                EndDurationMs = end
            });
        }

        public RecordingManifest Build() => new()
        {
            RecordingId = RecordingId,
            StartUtc = StartUtc,
            EndUtc = EndUtc ?? LastObservedUtc,
            Files = Files,
            Pauses = Pauses,
            Anchors = Anchors
        };
    }
}
