using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Celeste;
using Celeste.Mod;
using Monocle;

namespace Celeste.Mod.CelesteAutoCut;

internal sealed class RoomClipRecorder {
    private const string Tag = "CelesteAutoCut";
    private const long StatusWriteIntervalFrames = 15;
    private readonly ReplayController replayController;
    private readonly JsonSerializerOptions jsonOptions = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };
    private readonly JsonSerializerOptions statusJsonOptions = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private bool active;
    private bool statusDirty;
    private bool pendingInitialLoadLevel;
    private bool pendingRespawnLoadLevel;
    private bool chapterCompleteLogged;
    private string sessionId = string.Empty;
    private string attemptId = string.Empty;
    private string currentRoom = string.Empty;
    private string? lastObservedRoom;
    private Session? observedSession;
    private string? mapSid;
    private string? areaMode;
    private string? chapter;
    private long gameFrame;
    private long lastStatusWriteFrame = long.MinValue;

    public RoomClipRecorder(ReplayController replayController) {
        this.replayController = replayController;
    }

    public string EventLogPath => Path.Combine(replayController.ReplayDirectory, SafeFileName(CelesteAutoCutModule.Settings.RoomClipEventFileName, "room_events.jsonl"));
    public string StatusPath => Path.Combine(replayController.ReplayDirectory, SafeFileName(CelesteAutoCutModule.Settings.RoomClipStatusFileName, "room_clip_session.json"));
    public bool Active => active;

    public void Start(Session session, bool fromSaveData) {
        if (!CelesteAutoCutModule.Settings.EnableRoomClipRecorder) {
            Log("Room clip recorder disabled by settings.", LogLevel.Warn);
            return;
        }

        if (active) {
            Stop("restart", discard: false);
        }

        Directory.CreateDirectory(replayController.ReplayDirectory);

        active = true;
        sessionId = Guid.NewGuid().ToString("N");
        attemptId = Guid.NewGuid().ToString("N");
        gameFrame = 0;
        observedSession = session;
        mapSid = session.Area.SID;
        areaMode = session.Area.Mode.ToString();
        chapter = AreaData.Get(session.Area)?.Name;
        currentRoom = session.Level ?? string.Empty;
        lastObservedRoom = currentRoom;
        pendingInitialLoadLevel = true;
        pendingRespawnLoadLevel = false;
        chapterCompleteLogged = false;
        statusDirty = true;
        lastStatusWriteFrame = long.MinValue;

        WriteEvent(RoomClipEventTypes.SessionStart, currentRoom, notes: new Dictionary<string, string?> {
            ["fromSaveData"] = fromSaveData.ToString(),
            ["sessionLevel"] = session.Level,
            ["source"] = "observed_state"
        });
        if (!string.IsNullOrWhiteSpace(currentRoom)) {
            WriteEvent(RoomClipEventTypes.RoomEnter, currentRoom, notes: new Dictionary<string, string?> {
                ["reason"] = "session_start",
                ["source"] = "observed_state"
            });
        }
    }

    public void TickFrame() {
        if (active) {
            gameFrame++;
            WriteStatus();
        }
    }

    public void ObserveLevel(Level level, bool chapterComplete) {
        if (!CelesteAutoCutModule.Settings.EnableRoomClipRecorder) {
            return;
        }

        Session session = level.Session;
        string observedRoom = session.Level ?? string.Empty;
        if (!active || !ReferenceEquals(observedSession, session)) {
            Start(session, fromSaveData: false);
            observedRoom = session.Level ?? string.Empty;
        }

        if (!string.Equals(lastObservedRoom, observedRoom, StringComparison.Ordinal)) {
            string fromRoom = currentRoomOr(lastObservedRoom);
            WriteEvent(RoomClipEventTypes.Transition, fromRoom, observedRoom, new Dictionary<string, string?> {
                ["source"] = "observed_state"
            });

            currentRoom = observedRoom;
            lastObservedRoom = observedRoom;
            attemptId = Guid.NewGuid().ToString("N");
            pendingRespawnLoadLevel = false;
            chapterCompleteLogged = false;

            WriteEvent(RoomClipEventTypes.RoomEnter, currentRoom, notes: new Dictionary<string, string?> {
                ["reason"] = "transition",
                ["source"] = "observed_state"
            });
            WriteLoadLevel(observedRoom, playerIntro: "Transition");
        } else if (pendingInitialLoadLevel) {
            WriteLoadLevel(observedRoom, playerIntro: "Transition");
        } else if (pendingRespawnLoadLevel) {
            WriteLoadLevel(observedRoom, playerIntro: "Respawn");
        }

        if (chapterComplete && !chapterCompleteLogged) {
            WriteEvent(RoomClipEventTypes.LevelComplete, currentRoomOr(observedRoom), notes: new Dictionary<string, string?> {
                ["reason"] = "chapter_complete",
                ["source"] = "observed_state"
            });
            chapterCompleteLogged = true;
        }
    }

    public void ObserveExitedLevel(string reason) {
        if (!active) {
            ResetObservedState();
            return;
        }

        WriteEvent(RoomClipEventTypes.Exit, currentRoom, notes: new Dictionary<string, string?> {
            ["mode"] = reason,
            ["source"] = "observed_state"
        });
        Stop(reason, discard: false);
        ResetObservedState();
    }

    public void OnDeath(Player player) {
        if (!active || !CelesteAutoCutModule.Settings.EnableRoomClipRecorder) {
            return;
        }

        WriteEvent(RoomClipEventTypes.Death, currentRoomOr(player.SceneAs<Level>()?.Session.Level), notes: new Dictionary<string, string?> {
            ["x"] = player.Position.X.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["y"] = player.Position.Y.ToString(System.Globalization.CultureInfo.InvariantCulture)
        });
        attemptId = Guid.NewGuid().ToString("N");
        pendingRespawnLoadLevel = true;
    }

    public void Shutdown() {
        if (!active) {
            return;
        }

        Stop("unload", discard: false);
        ResetObservedState();
    }

    public void ResetLogs() {
        Directory.CreateDirectory(replayController.ReplayDirectory);
        if (File.Exists(EventLogPath)) {
            File.Delete(EventLogPath);
        }
        if (File.Exists(StatusPath)) {
            File.Delete(StatusPath);
        }
        active = false;
        sessionId = string.Empty;
        attemptId = string.Empty;
        currentRoom = string.Empty;
        mapSid = null;
        areaMode = null;
        chapter = null;
        gameFrame = 0;
        statusDirty = false;
        lastStatusWriteFrame = long.MinValue;
        ResetObservedState();
        Log($"Room clip logs reset: {EventLogPath}");
    }

    public RoomClipSessionStatus GetStatus() => new() {
        Active = active,
        SessionId = sessionId,
        AttemptId = attemptId,
        MapSid = mapSid,
        AreaMode = areaMode,
        Chapter = chapter,
        CurrentRoom = currentRoom,
        GameFrame = gameFrame,
        ChapterTimeMs = TryGetChapterTimeMs(),
        UpdatedAtUtc = DateTime.UtcNow.ToString("O")
    };

    private void Stop(string reason, bool discard) {
        if (!active) {
            return;
        }

        if (!discard) {
            WriteEvent(RoomClipEventTypes.SessionEnd, currentRoom, notes: new Dictionary<string, string?> {
                ["reason"] = reason
            });
        }

        active = false;
        statusDirty = true;
        WriteStatus(force: true);
    }

    private void WriteLoadLevel(string room, string playerIntro) {
        pendingInitialLoadLevel = false;
        pendingRespawnLoadLevel = false;
        WriteEvent(RoomClipEventTypes.LoadLevel, room, notes: new Dictionary<string, string?> {
            ["playerIntro"] = playerIntro,
            ["isFromLoader"] = "False",
            ["source"] = "observed_state"
        });
    }

    private void WriteEvent(string eventType, string? room, string? nextRoom = null, Dictionary<string, string?>? notes = null) {
        Directory.CreateDirectory(replayController.ReplayDirectory);
        var entry = new RoomClipEvent {
            EventType = eventType,
            Utc = DateTime.UtcNow.ToString("O"),
            SessionId = sessionId,
            AttemptId = attemptId,
            MapSid = mapSid,
            AreaMode = areaMode,
            Chapter = chapter,
            Room = room,
            NextRoom = nextRoom,
            ChapterTimeMs = TryGetChapterTimeMs(),
            GameFrame = gameFrame,
            Notes = notes ?? []
        };

        File.AppendAllText(EventLogPath, JsonSerializer.Serialize(entry, jsonOptions) + Environment.NewLine);
        statusDirty = true;
        WriteStatus(force: true);
    }

    private void WriteStatus(bool force = false) {
        if (!force) {
            if (!statusDirty) {
                return;
            }

            if (active && lastStatusWriteFrame != long.MinValue && (gameFrame - lastStatusWriteFrame) < StatusWriteIntervalFrames) {
                return;
            }
        }

        Directory.CreateDirectory(replayController.ReplayDirectory);
        File.WriteAllText(StatusPath, JsonSerializer.Serialize(GetStatus(), statusJsonOptions));
        statusDirty = false;
        lastStatusWriteFrame = gameFrame;
    }

    private void ResetObservedState() {
        observedSession = null;
        lastObservedRoom = null;
        pendingInitialLoadLevel = false;
        pendingRespawnLoadLevel = false;
        chapterCompleteLogged = false;
    }

    private long? TryGetChapterTimeMs() {
        try {
            if (Engine.Scene is Level level) {
                return level.Session.Time / TimeSpan.TicksPerMillisecond;
            }
        } catch {
            // keep best effort only
        }

        return null;
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

    private string currentRoomOr(string? fallback)
        => string.IsNullOrWhiteSpace(currentRoom) ? (fallback ?? string.Empty) : currentRoom;

    private static void Log(string message, LogLevel level = LogLevel.Info) {
        Logger.Log(level, Tag, message);
        Engine.Commands?.Log($"[{Tag}] {message}");
    }
}
