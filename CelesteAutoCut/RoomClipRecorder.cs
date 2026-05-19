using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Celeste;
using Celeste.Mod;
using Microsoft.Xna.Framework;
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
    private string sessionId = string.Empty;
    private string attemptId = string.Empty;
    private string currentRoom = string.Empty;
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
        mapSid = session.Area.SID;
        areaMode = session.Area.Mode.ToString();
        chapter = AreaData.Get(session.Area)?.Name;
        currentRoom = session.Level ?? string.Empty;
        statusDirty = true;
        lastStatusWriteFrame = long.MinValue;

        WriteEvent(RoomClipEventTypes.SessionStart, currentRoom, notes: new Dictionary<string, string?> {
            ["fromSaveData"] = fromSaveData.ToString(),
            ["sessionLevel"] = session.Level
        });
        if (!string.IsNullOrWhiteSpace(currentRoom)) {
            WriteEvent(RoomClipEventTypes.RoomEnter, currentRoom, notes: new Dictionary<string, string?> {
                ["reason"] = "session_start"
            });
        }
    }

    public void TickFrame() {
        if (active) {
            gameFrame++;
            WriteStatus();
        }
    }

    public void OnLoadLevel(Level level, Player.IntroTypes playerIntro, bool isFromLoader) {
        if (!active || !CelesteAutoCutModule.Settings.EnableRoomClipRecorder) {
            return;
        }

        string room = level.Session.Level ?? string.Empty;
        if (string.IsNullOrWhiteSpace(currentRoom)) {
            currentRoom = room;
            WriteEvent(RoomClipEventTypes.RoomEnter, currentRoom, notes: new Dictionary<string, string?> {
                ["reason"] = "late_initialize"
            });
        }

        WriteEvent(RoomClipEventTypes.LoadLevel, room, notes: new Dictionary<string, string?> {
            ["playerIntro"] = playerIntro.ToString(),
            ["isFromLoader"] = isFromLoader.ToString()
        });
    }

    public void OnTransitionTo(Level level, LevelData? nextLevelData, Vector2 direction) {
        if (!active || !CelesteAutoCutModule.Settings.EnableRoomClipRecorder || nextLevelData == null) {
            return;
        }

        string fromRoom = currentRoomOr(level.Session.Level);
        string nextRoom = nextLevelData.Name ?? string.Empty;
        WriteEvent(RoomClipEventTypes.Transition, fromRoom, nextRoom, new Dictionary<string, string?> {
            ["directionX"] = direction.X.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["directionY"] = direction.Y.ToString(System.Globalization.CultureInfo.InvariantCulture)
        });

        currentRoom = nextRoom;
        attemptId = Guid.NewGuid().ToString("N");
        WriteEvent(RoomClipEventTypes.RoomEnter, currentRoom, notes: new Dictionary<string, string?> {
            ["reason"] = "transition"
        });
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
    }

    public void OnComplete(Level level) {
        if (!active || !CelesteAutoCutModule.Settings.EnableRoomClipRecorder) {
            return;
        }

        WriteEvent(RoomClipEventTypes.LevelComplete, currentRoomOr(level.Session.Level), notes: new Dictionary<string, string?> {
            ["reason"] = "chapter_complete"
        });
    }

    public void OnExit(Level level, LevelExit exit, LevelExit.Mode mode, Session session) {
        if (!active) {
            return;
        }

        WriteEvent(RoomClipEventTypes.Exit, currentRoomOr(session.Level), notes: new Dictionary<string, string?> {
            ["mode"] = mode.ToString(),
            ["exitType"] = exit?.GetType().Name
        });
        Stop(mode.ToString(), discard: false);
    }


    public void Shutdown() {
        if (!active) {
            return;
        }

        Stop("unload", discard: false);
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

