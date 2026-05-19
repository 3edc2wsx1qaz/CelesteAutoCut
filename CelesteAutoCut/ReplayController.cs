using System;
using System.IO;
using System.Text.Json;
using Celeste;
using Celeste.Mod;
using Monocle;

namespace Celeste.Mod.CelesteAutoCut;

internal enum ReplayMode {
    Idle,
    Recording,
    Playing
}

internal sealed class ReplayController {
    private const string Tag = "CelesteAutoCut";
    private const string ReplayFolder = "CelesteAutoCutReplays";

    private ReplayFile? replay;
    private int playbackFrame;

    public ReplayMode Mode { get; private set; } = ReplayMode.Idle;

    public string ReplayDirectory => Path.Combine(Everest.PathGame, ReplayFolder);

    public string ReplayPath {
        get {
            string fileName = CelesteAutoCutModule.Settings.ReplayFileName;
            if (string.IsNullOrWhiteSpace(fileName)) {
                fileName = "last_replay.json";
            }

            fileName = Path.GetFileName(fileName)
                .Replace(Path.DirectorySeparatorChar, '_')
                .Replace(Path.AltDirectorySeparatorChar, '_');

            if (string.IsNullOrWhiteSpace(fileName)) {
                fileName = "last_replay.json";
            }

            return Path.Combine(ReplayDirectory, fileName);
        }
    }

    public void StartRecording() {
        Stop();

        replay = new ReplayFile();
        if (Engine.Scene is Level level) {
            replay.AreaSid = level.Session.Area.SID;
            replay.LevelName = level.Session.LevelData?.Name;
        }

        Mode = ReplayMode.Recording;
        Log("Recording started.");
    }

    public void ToggleRecording() {
        if (Mode == ReplayMode.Recording) {
            Stop();
        } else {
            StartRecording();
        }
    }

    public void RecordFrame() {
        if (Mode != ReplayMode.Recording || replay == null) {
            return;
        }

        replay.Frames.Add(ReplayFrame.Capture(
            CelesteAutoCutModule.Settings.RecordKeyboard,
            CelesteAutoCutModule.Settings.RecordGamepad,
            CelesteAutoCutModule.Settings.ToggleRecordingKey,
            CelesteAutoCutModule.Settings.PlayKey,
            CelesteAutoCutModule.Settings.StopKey
        ));
    }

    public void StartPlayback() {
        Stop();

        if (!File.Exists(ReplayPath)) {
            Log($"Replay file not found: {ReplayPath}", LogLevel.Warn);
            return;
        }

        try {
            replay = JsonSerializer.Deserialize<ReplayFile>(File.ReadAllText(ReplayPath));
        } catch (Exception e) {
            Logger.Log(LogLevel.Error, Tag, $"Failed to load replay: {e}");
            return;
        }

        if (replay == null || replay.Frames.Count == 0) {
            Log("Replay file has no frames.", LogLevel.Warn);
            replay = null;
            return;
        }

        playbackFrame = 0;
        Mode = ReplayMode.Playing;
        Log($"Playback started: {replay.Frames.Count} frames.");
    }

    public void PlaybackFrame() {
        if (Mode != ReplayMode.Playing || replay == null) {
            return;
        }

        if (playbackFrame >= replay.Frames.Count) {
            Stop();
            return;
        }

        replay.Frames[playbackFrame].Feed(
            CelesteAutoCutModule.Settings.RecordKeyboard,
            CelesteAutoCutModule.Settings.RecordGamepad
        );
        playbackFrame++;

        if (playbackFrame >= replay.Frames.Count) {
            Stop();
        }
    }

    public void Stop() {
        if (Mode == ReplayMode.Recording && replay != null) {
            SaveReplay();
        } else if (Mode == ReplayMode.Playing) {
            Log("Playback stopped.");
        }

        Mode = ReplayMode.Idle;
        replay = null;
        playbackFrame = 0;
    }

    private void SaveReplay() {
        Directory.CreateDirectory(ReplayDirectory);

        try {
            var options = new JsonSerializerOptions {
                WriteIndented = true
            };
            File.WriteAllText(ReplayPath, JsonSerializer.Serialize(replay, options));
            Log($"Saved {replay?.Frames.Count ?? 0} frames to {ReplayPath}");
        } catch (Exception e) {
            Logger.Log(LogLevel.Error, Tag, $"Failed to save replay: {e}");
        }
    }

    private static void Log(string message, LogLevel level = LogLevel.Info) {
        Logger.Log(level, Tag, message);
        Engine.Commands?.Log($"[{Tag}] {message}");
    }
}

