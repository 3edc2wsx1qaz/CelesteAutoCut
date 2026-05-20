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
    private const long StatusWriteIntervalFrames = 60;
    private const long PlayerPositionSampleIntervalFrames = 10;
    private const long PlayerPositionSampleWindowFrames = 480;
    private const string EventLogFileName = "room_events.jsonl";
    private const string StatusFileName = "room_clip_session.json";
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
    private string? playerPositionSampleLoadEventId;
    private string? playerPositionSampleRoom;
    private Vector2? playerPositionSampleSpawnPoint;
    private long playerPositionSampleUntilFrame = long.MinValue;
    private long lastPlayerPositionSampleFrame = long.MinValue;

    public RoomClipRecorder(ReplayController replayController) {
        this.replayController = replayController;
    }

    public string EventLogPath => Path.Combine(replayController.ReplayDirectory, EventLogFileName);
    public string StatusPath => Path.Combine(replayController.ReplayDirectory, StatusFileName);
    public bool Active => active;

    public void Start(Session session, bool fromSaveData) {
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

    public void TickFrame(Level? level = null) {
        if (active) {
            gameFrame++;
            ObservePlayerPositionSample(level);
            WriteStatus();
        }
    }

    public void ObserveLevel(Level level, bool chapterComplete) {
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
            chapterCompleteLogged = false;

            WriteEvent(RoomClipEventTypes.RoomEnter, currentRoom, notes: new Dictionary<string, string?> {
                ["reason"] = "transition",
                ["source"] = "observed_state"
            });
            WriteLoadLevel(level, session, observedRoom, playerIntro: "Transition", isFromLoader: false, source: "observed_state");
        } else if (pendingInitialLoadLevel) {
            WriteLoadLevel(level, session, observedRoom, playerIntro: "Transition", isFromLoader: false, source: "observed_state");
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
        ClearPlayerPositionSampling();
        Stop(reason, discard: false);
        ResetObservedState();
    }

    public void OnDeath(Player player) {
        if (!active) {
            return;
        }

        WriteEvent(RoomClipEventTypes.Death, currentRoomOr(player.SceneAs<Level>()?.Session.Level), notes: new Dictionary<string, string?> {
            ["x"] = player.Position.X.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["y"] = player.Position.Y.ToString(System.Globalization.CultureInfo.InvariantCulture)
        });
        ClearPlayerPositionSampling();
        attemptId = Guid.NewGuid().ToString("N");
    }

    public void ObserveLoadLevel(Level level, string playerIntro, bool isFromLoader) {
        if (!string.Equals(playerIntro, "Respawn", StringComparison.OrdinalIgnoreCase)) {
            return;
        }

        Session session = level.Session;
        if (!active || !ReferenceEquals(observedSession, session)) {
            Start(session, fromSaveData: false);
        }

        string observedRoom = session.Level ?? string.Empty;
        currentRoom = observedRoom;
        lastObservedRoom = observedRoom;
        observedSession = session;
        pendingInitialLoadLevel = false;
        WriteLoadLevel(level, session, observedRoom, playerIntro, isFromLoader, source: "level_load_hook");
    }

    public void OnStrawberryCollect(Strawberry strawberry) {
        if (!active) {
            return;
        }

        WriteEvent(RoomClipEventTypes.StrawberryCollect, currentRoomOr(strawberry.SceneAs<Level>()?.Session.Level), notes: new Dictionary<string, string?> {
            ["entityId"] = strawberry.ID.Key,
            ["level"] = strawberry.ID.Level,
            ["id"] = strawberry.ID.ID.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["x"] = strawberry.Position.X.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["y"] = strawberry.Position.Y.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["golden"] = strawberry.Golden.ToString(),
            ["winged"] = strawberry.Winged.ToString(),
            ["moon"] = strawberry.Moon.ToString(),
            ["source"] = "strawberry_on_collect"
        });
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
        ClearPlayerPositionSampling();
        ResetObservedState();
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
        ClearPlayerPositionSampling();
        statusDirty = true;
        WriteStatus(force: true);
    }

    private void WriteLoadLevel(Level level, Session session, string room, string playerIntro, bool isFromLoader, string source) {
        pendingInitialLoadLevel = false;
        var spawnPoint = ResolveSpawnPoint(level, session);
        var notes = new Dictionary<string, string?> {
            ["playerIntro"] = playerIntro,
            ["isFromLoader"] = isFromLoader.ToString(),
            ["source"] = source
        };
        AddRespawnPointNotes(notes, session);
        AddSpawnPointNotes(notes, spawnPoint);
        var loadEvent = WriteEvent(RoomClipEventTypes.LoadLevel, room, notes: notes);
        SchedulePlayerPositionSampling(loadEvent, room, spawnPoint);
    }

    private static void AddRespawnPointNotes(Dictionary<string, string?> notes, Session session) {
        var respawnPoint = session.RespawnPoint;
        notes["hasRespawnPoint"] = respawnPoint.HasValue.ToString();
        if (!respawnPoint.HasValue) {
            return;
        }

        notes["respawnPointX"] = respawnPoint.Value.X.ToString(System.Globalization.CultureInfo.InvariantCulture);
        notes["respawnPointY"] = respawnPoint.Value.Y.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void AddSpawnPointNotes(Dictionary<string, string?> notes, Vector2? spawnPoint) {
        notes["hasSpawnPoint"] = spawnPoint.HasValue.ToString();
        if (!spawnPoint.HasValue) {
            return;
        }

        notes["spawnPointX"] = spawnPoint.Value.X.ToString(System.Globalization.CultureInfo.InvariantCulture);
        notes["spawnPointY"] = spawnPoint.Value.Y.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static Vector2? ResolveSpawnPoint(Level level, Session session) {
        if (session.RespawnPoint.HasValue) {
            return session.RespawnPoint.Value;
        }

        try {
            return session.GetSpawnPoint(new Vector2(level.Bounds.Left, level.Bounds.Bottom));
        } catch {
            // best effort only
        }

        return null;
    }

    private void SchedulePlayerPositionSampling(RoomClipEvent loadEvent, string room, Vector2? spawnPoint) {
        if (!spawnPoint.HasValue) {
            ClearPlayerPositionSampling();
            return;
        }

        playerPositionSampleLoadEventId = loadEvent.EventId;
        playerPositionSampleRoom = room;
        playerPositionSampleSpawnPoint = spawnPoint;
        playerPositionSampleUntilFrame = gameFrame + PlayerPositionSampleWindowFrames;
        lastPlayerPositionSampleFrame = long.MinValue;
    }

    private void ObservePlayerPositionSample(Level? level) {
        if (level is null ||
            string.IsNullOrWhiteSpace(playerPositionSampleLoadEventId) ||
            !playerPositionSampleSpawnPoint.HasValue) {
            return;
        }

        if (gameFrame > playerPositionSampleUntilFrame) {
            ClearPlayerPositionSampling();
            return;
        }

        if (lastPlayerPositionSampleFrame != long.MinValue &&
            (gameFrame - lastPlayerPositionSampleFrame) < PlayerPositionSampleIntervalFrames) {
            return;
        }

        var session = level.Session;
        var room = session.Level ?? string.Empty;
        if (!string.Equals(room, playerPositionSampleRoom ?? string.Empty, StringComparison.Ordinal)) {
            ClearPlayerPositionSampling();
            return;
        }

        var player = level.Tracker.GetEntity<Player>();
        if (player is null) {
            return;
        }

        var spawnPoint = playerPositionSampleSpawnPoint.Value;
        lastPlayerPositionSampleFrame = gameFrame;
        WriteEvent(RoomClipEventTypes.PlayerPositionSample, room, notes: new Dictionary<string, string?> {
            ["loadEventId"] = playerPositionSampleLoadEventId,
            ["x"] = player.Position.X.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["y"] = player.Position.Y.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["spawnPointX"] = spawnPoint.X.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["spawnPointY"] = spawnPoint.Y.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["sampleIntervalFrames"] = PlayerPositionSampleIntervalFrames.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["sampleWindowFrames"] = PlayerPositionSampleWindowFrames.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["source"] = "post_load_player_position_sample"
        }, updateStatus: false);
    }

    private void ClearPlayerPositionSampling() {
        playerPositionSampleLoadEventId = null;
        playerPositionSampleRoom = null;
        playerPositionSampleSpawnPoint = null;
        playerPositionSampleUntilFrame = long.MinValue;
        lastPlayerPositionSampleFrame = long.MinValue;
    }

    private RoomClipEvent WriteEvent(string eventType, string? room, string? nextRoom = null, Dictionary<string, string?>? notes = null, bool updateStatus = true) {
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
        if (updateStatus) {
            statusDirty = true;
            WriteStatus(force: true);
        }

        return entry;
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

}
