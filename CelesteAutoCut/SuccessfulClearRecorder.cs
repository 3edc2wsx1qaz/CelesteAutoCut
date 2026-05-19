using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Celeste;
using Celeste.Mod;
using Monocle;

namespace Celeste.Mod.CelesteAutoCut;

internal sealed class SuccessfulClearRecorder {
    private const string Tag = "CelesteAutoCut";
    private readonly ReplayController replayController;

    private readonly List<ReplayFrame> currentAttempt = [];
    private readonly List<ReplaySegment> successfulSegments = [];
    private readonly HashSet<string> savedSegmentKeys = [];

    private bool active;
    private bool chapterCompleteHandled;
    private string currentCheckpoint = "";
    private string? lastObservedRoom;
    private Session? observedSession;
    private string? areaSid;
    private string? areaMode;
    private string? chapterName;
    private string? startCheckpoint;

    public SuccessfulClearRecorder(ReplayController replayController) {
        this.replayController = replayController;
    }

    public void Start(Session session) {
        Stop(discard: true);

        if (!CelesteAutoCutModule.Settings.AutoExportSuccessfulClearRecords) {
            return;
        }

        areaSid = session.Area.SID;
        areaMode = session.Area.Mode.ToString();
        chapterName = AreaData.Get(session.Area)?.Name;
        currentCheckpoint = CheckpointName(session.Level);
        startCheckpoint = currentCheckpoint;
        observedSession = session;
        lastObservedRoom = session.Level ?? string.Empty;
        chapterCompleteHandled = false;
        active = true;

        Log($"Successful-clear recording started at checkpoint '{currentCheckpoint}'.");
    }

    public void RecordFrame() {
        if (!active || replayController.Mode == ReplayMode.Playing) {
            return;
        }

        currentAttempt.Add(ReplayFrame.Capture(
            CelesteAutoCutModule.Settings.RecordKeyboard,
            CelesteAutoCutModule.Settings.RecordGamepad,
            CelesteAutoCutModule.Settings.ToggleRecordingKey,
            CelesteAutoCutModule.Settings.PlayKey,
            CelesteAutoCutModule.Settings.StopKey
        ));
    }

    public void OnDeath() {
        if (!active) {
            return;
        }

        currentAttempt.Clear();
        Log("Discarded failed checkpoint attempt.");
    }

    public void ObserveLevel(Level level, bool chapterComplete) {
        if (!CelesteAutoCutModule.Settings.AutoExportSuccessfulClearRecords) {
            return;
        }

        if (!active && chapterCompleteHandled && ReferenceEquals(observedSession, level.Session)) {
            return;
        }

        if (!active || !ReferenceEquals(observedSession, level.Session)) {
            Start(level.Session);
        }

        string observedRoom = level.Session.Level ?? string.Empty;
        if (!string.Equals(lastObservedRoom, observedRoom, StringComparison.Ordinal)) {
            if (level.Session.LevelData?.HasCheckpoint == true) {
                CompleteCurrentSegment(CheckpointName(observedRoom));
            }

            lastObservedRoom = observedRoom;
        }

        if (chapterComplete && !chapterCompleteHandled) {
            string finalCheckpoint = CheckpointName($"{level.Session.Level}:complete");
            CompleteCurrentSegment(finalCheckpoint);
            Export(finalCheckpoint);
            Stop(discard: true);
            observedSession = level.Session;
            lastObservedRoom = observedRoom;
            chapterCompleteHandled = true;
        }
    }

    public void ObserveExitedLevel() {
        Stop(discard: true);
    }

    public void Stop(bool discard) {
        if (!active && currentAttempt.Count == 0 && successfulSegments.Count == 0) {
            return;
        }

        if (!discard) {
            Log("Successful-clear recording stopped.");
        }

        active = false;
        currentAttempt.Clear();
        successfulSegments.Clear();
        savedSegmentKeys.Clear();
        currentCheckpoint = "";
        chapterCompleteHandled = false;
        lastObservedRoom = null;
        observedSession = null;
        areaSid = null;
        areaMode = null;
        chapterName = null;
        startCheckpoint = null;
    }

    private void CompleteCurrentSegment(string nextCheckpoint) {
        if (string.Equals(currentCheckpoint, nextCheckpoint, StringComparison.Ordinal)) {
            currentAttempt.Clear();
            return;
        }

        string segmentKey = $"{currentCheckpoint}->{nextCheckpoint}";
        if (currentAttempt.Count > 0 && savedSegmentKeys.Add(segmentKey)) {
            successfulSegments.Add(new ReplaySegment {
                FromCheckpoint = currentCheckpoint,
                ToCheckpoint = nextCheckpoint,
                Frames = currentAttempt.ToList()
            });
            Log($"Saved first successful segment '{segmentKey}' ({currentAttempt.Count} frames).");
        }

        currentCheckpoint = nextCheckpoint;
        currentAttempt.Clear();
    }

    private void Export(string finalCheckpoint) {
        if (successfulSegments.Count == 0) {
            Log("No successful checkpoint segments to export.", LogLevel.Warn);
            return;
        }

        var export = new SuccessfulClearReplayFile {
            AreaSid = areaSid,
            AreaMode = areaMode,
            ChapterName = chapterName,
            StartCheckpoint = startCheckpoint,
            FinalCheckpoint = finalCheckpoint,
            Segments = successfulSegments
                .Select(segment => new ReplaySegment {
                    FromCheckpoint = segment.FromCheckpoint,
                    ToCheckpoint = segment.ToCheckpoint,
                    Frames = segment.Frames.ToList()
                })
                .ToList(),
            Frames = successfulSegments.SelectMany(segment => segment.Frames).ToList()
        };

        Directory.CreateDirectory(replayController.ReplayDirectory);
        var options = new JsonSerializerOptions { WriteIndented = true };
        string json = JsonSerializer.Serialize(export, options);

        string latestPath = Path.Combine(replayController.ReplayDirectory, SafeFileName(CelesteAutoCutModule.Settings.SuccessfulClearFileName, "last_successful_clear.json"));
        File.WriteAllText(latestPath, json);

        if (CelesteAutoCutModule.Settings.WriteTimestampedSuccessfulClearRecords) {
            string timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            string sid = SafeFileName(areaSid ?? "unknown", "unknown").Replace(".json", "");
            string stampedPath = Path.Combine(replayController.ReplayDirectory, $"{timestamp}_{sid}_{areaMode}_clear.json");
            File.WriteAllText(stampedPath, json);
        }

        Log($"Exported successful clear: {export.Segments.Count} segments, {export.Frames.Count} frames.");
    }

    private static string CheckpointName(string? roomName) {
        return string.IsNullOrWhiteSpace(roomName) ? "unknown" : roomName;
    }

    private static string SafeFileName(string? fileName, string fallback) {
        if (string.IsNullOrWhiteSpace(fileName)) {
            fileName = fallback;
        }

        fileName = Path.GetFileName(fileName);
        foreach (char c in Path.GetInvalidFileNameChars()) {
            fileName = fileName.Replace(c, '_');
        }

        return string.IsNullOrWhiteSpace(fileName) ? fallback : fileName;
    }

    private static void Log(string message, LogLevel level = LogLevel.Info) {
        Logger.Log(level, Tag, message);
        Engine.Commands?.Log($"[{Tag}] {message}");
    }
}

