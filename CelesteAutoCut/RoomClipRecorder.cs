using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Celeste;
using Celeste.Mod;
using Microsoft.Xna.Framework;
using Monocle;

namespace Celeste.Mod.CelesteAutoCut;

internal sealed class RoomClipRecorder {
    private const long StatusWriteIntervalFrames = 60;
    private const long PlayerPositionSampleStartDelayFrames = 45;
    private const long PlayerPositionSampleIntervalFrames = 10;
    private const long PlayerPositionSampleWindowFrames = 300;
    private const long FixedLoadPositionSampleDelayFrames = 60;
    private const string EventLogPrefix = "room_event_";
    private const string EventLogExtension = ".jsonl";
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
    private long playerPositionSampleStartFrame = long.MinValue;
    private long playerPositionSampleUntilFrame = long.MinValue;
    private long lastPlayerPositionSampleFrame = long.MinValue;
    private PlayerPositionSampleMode playerPositionSampleMode;
    private PendingPlayerPositionSample? pendingPlayerPositionSample;
    private RoomClipEvent? bestPlayerPositionSampleEvent;
    private bool bestPlayerPositionSampleIsStationary;
    private double bestPlayerPositionSampleDistanceSquared = double.MaxValue;
    private long bestPlayerPositionSampleFrame = long.MinValue;
    private string eventLogPath = string.Empty;

    public RoomClipRecorder(ReplayController replayController) {
        this.replayController = replayController;
    }

    public string EventLogPath => string.IsNullOrWhiteSpace(eventLogPath)
        ? ResolveLatestEventLogPath(replayController.ReplayDirectory)
        : eventLogPath;
    public string StatusPath => Path.Combine(replayController.ReplayDirectory, StatusFileName);
    public bool Active => active;

    public void Start(Session session, bool fromSaveData) {
        if (active) {
            Stop("restart", discard: false);
        }

        Directory.CreateDirectory(replayController.ReplayDirectory);
        eventLogPath = CreateTimestampedEventLogPath(replayController.ReplayDirectory);

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
            ["sessionLevel"] = session.Level
        });
        if (!string.IsNullOrWhiteSpace(currentRoom)) {
            WriteEvent(RoomClipEventTypes.RoomEnter, currentRoom);
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
            FlushPlayerPositionSample();
            string fromRoom = currentRoomOr(lastObservedRoom);
            WriteEvent(RoomClipEventTypes.Transition, fromRoom, observedRoom);

            currentRoom = observedRoom;
            lastObservedRoom = observedRoom;
            attemptId = Guid.NewGuid().ToString("N");
            chapterCompleteLogged = false;

            WriteEvent(RoomClipEventTypes.RoomEnter, currentRoom);
            WriteLoadLevel(level, session, observedRoom, playerIntro: "Transition", isFromLoader: false, source: "observed_state", isRoomEntryLoad: true);
        } else if (pendingInitialLoadLevel) {
            WriteLoadLevel(level, session, observedRoom, playerIntro: "Transition", isFromLoader: false, source: "observed_state", isRoomEntryLoad: true);
        }

        if (chapterComplete && !chapterCompleteLogged) {
            WriteEvent(RoomClipEventTypes.LevelComplete, currentRoomOr(observedRoom));
            chapterCompleteLogged = true;
        }
    }

    public void ObserveExitedLevel(string reason) {
        if (!active) {
            ResetObservedState();
            return;
        }

        FlushPlayerPositionSample();
        WriteEvent(RoomClipEventTypes.Exit, currentRoom);
        ClearPlayerPositionSampling();
        Stop(reason, discard: false);
        ResetObservedState();
    }

    public void OnDeath(Player player) {
        if (!active) {
            return;
        }

        FlushPlayerPositionSample();
        WriteEvent(RoomClipEventTypes.Death, currentRoomOr(player.SceneAs<Level>()?.Session.Level));
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
        WriteLoadLevel(level, session, observedRoom, playerIntro, isFromLoader, source: "level_load_hook", isRoomEntryLoad: false);
    }

    public void OnStrawberryCollect(Strawberry strawberry) {
        if (!active) {
            return;
        }

        WriteEvent(RoomClipEventTypes.StrawberryCollect, currentRoomOr(strawberry.SceneAs<Level>()?.Session.Level));
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
        foreach (var path in Directory.EnumerateFiles(replayController.ReplayDirectory, EventLogPrefix + "*" + EventLogExtension)) {
            File.Delete(path);
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
        eventLogPath = string.Empty;
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
        EventLogPath = string.IsNullOrWhiteSpace(eventLogPath) ? null : eventLogPath,
        GameFrame = gameFrame,
        ChapterTimeMs = TryGetChapterTimeMs(),
        UpdatedAtUtc = DateTime.UtcNow.ToString("O")
    };

    private void Stop(string reason, bool discard) {
        if (!active) {
            return;
        }

        if (!discard) {
            FlushPlayerPositionSample();
            WriteEvent(RoomClipEventTypes.SessionEnd, currentRoom, notes: new Dictionary<string, string?> {
                ["reason"] = reason
            });
        }

        active = false;
        ClearPlayerPositionSampling();
        statusDirty = true;
        WriteStatus(force: true);
    }

    private void WriteLoadLevel(Level level, Session session, string room, string playerIntro, bool isFromLoader, string source, bool isRoomEntryLoad) {
        pendingInitialLoadLevel = false;
        var spawnPoint = ResolveSpawnPoint(level, session);
        var notes = new Dictionary<string, string?> {
            ["playerIntro"] = playerIntro
        };
        AddRespawnPointNotes(notes, session);
        AddSpawnPointNotes(notes, spawnPoint);
        var loadEvent = WriteEvent(RoomClipEventTypes.LoadLevel, room, notes: notes);
        SchedulePlayerPositionSampling(loadEvent, room, spawnPoint, isRoomEntryLoad);
    }

    private static void AddRespawnPointNotes(Dictionary<string, string?> notes, Session session) {
        var respawnPoint = session.RespawnPoint;
        if (!respawnPoint.HasValue) {
            return;
        }

        notes["respawnPointX"] = respawnPoint.Value.X.ToString(System.Globalization.CultureInfo.InvariantCulture);
        notes["respawnPointY"] = respawnPoint.Value.Y.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void AddSpawnPointNotes(Dictionary<string, string?> notes, Vector2? spawnPoint) {
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

    private void SchedulePlayerPositionSampling(RoomClipEvent loadEvent, string room, Vector2? spawnPoint, bool isRoomEntryLoad) {
        if (!spawnPoint.HasValue) {
            FlushPlayerPositionSample();
            ClearPlayerPositionSampling();
            return;
        }

        playerPositionSampleLoadEventId = loadEvent.EventId;
        playerPositionSampleRoom = room;
        playerPositionSampleSpawnPoint = spawnPoint;
        playerPositionSampleMode = isRoomEntryLoad
            ? PlayerPositionSampleMode.RoomEntryBestWithinWindow
            : PlayerPositionSampleMode.FixedFrameAfterLoad;
        playerPositionSampleStartFrame = gameFrame + (isRoomEntryLoad ? PlayerPositionSampleStartDelayFrames : FixedLoadPositionSampleDelayFrames);
        playerPositionSampleUntilFrame = playerPositionSampleStartFrame + (isRoomEntryLoad ? PlayerPositionSampleWindowFrames : 0);
        lastPlayerPositionSampleFrame = long.MinValue;
        bestPlayerPositionSampleEvent = null;
        bestPlayerPositionSampleIsStationary = false;
        bestPlayerPositionSampleDistanceSquared = double.MaxValue;
        bestPlayerPositionSampleFrame = long.MinValue;
    }

    private void ObservePlayerPositionSample(Level? level) {
        if (level is null ||
            string.IsNullOrWhiteSpace(playerPositionSampleLoadEventId) ||
            !playerPositionSampleSpawnPoint.HasValue) {
            return;
        }

        if (playerPositionSampleMode == PlayerPositionSampleMode.RoomEntryBestWithinWindow &&
            gameFrame > playerPositionSampleUntilFrame) {
            FlushPlayerPositionSample();
            ClearPlayerPositionSampling();
            return;
        }

        if (gameFrame < playerPositionSampleStartFrame) {
            return;
        }

        var session = level.Session;
        var room = session.Level ?? string.Empty;
        if (!string.Equals(room, playerPositionSampleRoom ?? string.Empty, StringComparison.Ordinal)) {
            FlushPlayerPositionSample();
            ClearPlayerPositionSampling();
            return;
        }

        var player = level.Tracker.GetEntity<Player>();
        if (player is null) {
            if (playerPositionSampleMode == PlayerPositionSampleMode.FixedFrameAfterLoad) {
                ClearPlayerPositionSampling();
            }
            return;
        }

        bool isStationary = player.Speed.LengthSquared() <= 0.0001f;
        ResolvePendingPlayerPositionSample(isStationary);
        var spawnPoint = playerPositionSampleSpawnPoint.Value;
        if (playerPositionSampleMode == PlayerPositionSampleMode.FixedFrameAfterLoad) {
            bestPlayerPositionSampleEvent = CreatePlayerPositionSampleEvent(room, player.Position, spawnPoint, isStationary: null, DateTime.UtcNow.ToString("O"), gameFrame);
            FlushPlayerPositionSample();
            ClearPlayerPositionSampling();
            return;
        }

        if (lastPlayerPositionSampleFrame != long.MinValue &&
            (gameFrame - lastPlayerPositionSampleFrame) < PlayerPositionSampleIntervalFrames) {
            return;
        }

        lastPlayerPositionSampleFrame = gameFrame;
        var distanceSquared = DistanceSquared(player.Position, spawnPoint);
        var sampleUtc = DateTime.UtcNow.ToString("O");
        if (isStationary) {
            pendingPlayerPositionSample = new PendingPlayerPositionSample(room, player.Position, spawnPoint, distanceSquared, gameFrame, sampleUtc);
            return;
        }

        RecordPlayerPositionSample(room, player.Position, spawnPoint, isStationary: false, distanceSquared, gameFrame, sampleUtc);
    }

    private void ResolvePendingPlayerPositionSample(bool nextFrameIsStationary) {
        if (!pendingPlayerPositionSample.HasValue || gameFrame <= pendingPlayerPositionSample.Value.GameFrame) {
            return;
        }

        var pending = pendingPlayerPositionSample.Value;
        pendingPlayerPositionSample = null;
        RecordPlayerPositionSample(pending.Room, pending.PlayerPosition, pending.SpawnPoint, nextFrameIsStationary, pending.DistanceSquared, pending.GameFrame, pending.Utc);
    }

    private void RecordPlayerPositionSample(string room, Vector2 playerPosition, Vector2 spawnPoint, bool isStationary, double distanceSquared, long sampleFrame, string sampleUtc) {
        if (!IsBetterPlayerPositionSample(isStationary, distanceSquared, sampleFrame)) {
            return;
        }

        bestPlayerPositionSampleIsStationary = isStationary;
        bestPlayerPositionSampleDistanceSquared = distanceSquared;
        bestPlayerPositionSampleFrame = sampleFrame;
        bestPlayerPositionSampleEvent = CreatePlayerPositionSampleEvent(room, playerPosition, spawnPoint, isStationary, sampleUtc, sampleFrame);
    }

    private RoomClipEvent CreatePlayerPositionSampleEvent(string room, Vector2 playerPosition, Vector2 spawnPoint, bool? isStationary, string sampleUtc, long sampleFrame) {
        return CreateEvent(RoomClipEventTypes.PlayerPositionSample, room, notes: new Dictionary<string, string?> {
            ["loadEventId"] = playerPositionSampleLoadEventId,
            ["x"] = playerPosition.X.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["y"] = playerPosition.Y.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["spawnPointX"] = spawnPoint.X.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["spawnPointY"] = spawnPoint.Y.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["stationary"] = isStationary?.ToString()
        }, utcOverride: sampleUtc, gameFrameOverride: sampleFrame);
    }

    private void ClearPlayerPositionSampling() {
        playerPositionSampleLoadEventId = null;
        playerPositionSampleRoom = null;
        playerPositionSampleSpawnPoint = null;
        playerPositionSampleStartFrame = long.MinValue;
        playerPositionSampleUntilFrame = long.MinValue;
        lastPlayerPositionSampleFrame = long.MinValue;
        playerPositionSampleMode = PlayerPositionSampleMode.None;
        pendingPlayerPositionSample = null;
        bestPlayerPositionSampleEvent = null;
        bestPlayerPositionSampleIsStationary = false;
        bestPlayerPositionSampleDistanceSquared = double.MaxValue;
        bestPlayerPositionSampleFrame = long.MinValue;
    }

    private RoomClipEvent WriteEvent(string eventType, string? room, string? nextRoom = null, Dictionary<string, string?>? notes = null, bool updateStatus = true) {
        var entry = CreateEvent(eventType, room, nextRoom, notes);
        AppendEvent(entry);
        if (updateStatus) {
            statusDirty = true;
            WriteStatus(force: true);
        }

        return entry;
    }

    private RoomClipEvent CreateEvent(string eventType, string? room, string? nextRoom = null, Dictionary<string, string?>? notes = null, string? utcOverride = null, long? gameFrameOverride = null) {
        Directory.CreateDirectory(replayController.ReplayDirectory);
        return new RoomClipEvent {
            EventType = eventType,
            EventId = eventType == RoomClipEventTypes.LoadLevel ? Guid.NewGuid().ToString("N") : null,
            Utc = utcOverride ?? DateTime.UtcNow.ToString("O"),
            SessionId = sessionId,
            AttemptId = attemptId,
            MapSid = mapSid,
            AreaMode = null,
            Chapter = null,
            Room = room,
            NextRoom = nextRoom,
            ChapterTimeMs = TryGetChapterTimeMs(),
            GameFrame = gameFrameOverride ?? gameFrame,
            Notes = CompactNotes(notes)
        };
    }

    private void AppendEvent(RoomClipEvent entry) {
        Directory.CreateDirectory(replayController.ReplayDirectory);
        if (string.IsNullOrWhiteSpace(eventLogPath)) {
            eventLogPath = CreateTimestampedEventLogPath(replayController.ReplayDirectory);
        }

        using (var stream = new FileStream(EventLogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
        using (var writer = new StreamWriter(stream)) {
            writer.WriteLine(JsonSerializer.Serialize(entry, jsonOptions));
        }
    }

    private static string CreateTimestampedEventLogPath(string replayDirectory) {
        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff", System.Globalization.CultureInfo.InvariantCulture);
        var path = Path.Combine(replayDirectory, EventLogPrefix + timestamp + EventLogExtension);
        for (var suffix = 1; File.Exists(path); suffix++) {
            path = Path.Combine(replayDirectory, $"{EventLogPrefix}{timestamp}-{suffix}{EventLogExtension}");
        }

        return path;
    }

    private static string ResolveLatestEventLogPath(string replayDirectory) {
        if (!Directory.Exists(replayDirectory)) {
            return Path.Combine(replayDirectory, EventLogPrefix + DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff", System.Globalization.CultureInfo.InvariantCulture) + EventLogExtension);
        }

        var latest = Directory.EnumerateFiles(replayDirectory, EventLogPrefix + "*" + EventLogExtension)
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .FirstOrDefault();
        return latest?.FullName ?? Path.Combine(replayDirectory, EventLogPrefix + DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff", System.Globalization.CultureInfo.InvariantCulture) + EventLogExtension);
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

    private static Dictionary<string, string?>? CompactNotes(Dictionary<string, string?>? notes) {
        if (notes is null || notes.Count == 0) {
            return null;
        }

        foreach (string key in new[] {
            "source",
            "reason",
            "mode",
            "isFromLoader",
            "sampleIntervalFrames",
            "sampleWindowFrames",
            "hasRespawnPoint",
            "hasSpawnPoint",
            "entityId",
            "level",
            "id",
            "golden",
            "winged",
            "moon"
        }) {
            notes.Remove(key);
        }

        foreach (string key in notes.Keys.Where(key => string.IsNullOrWhiteSpace(notes[key])).ToList()) {
            notes.Remove(key);
        }

        return notes.Count == 0 ? null : notes;
    }

    private bool IsBetterPlayerPositionSample(bool isStationary, double distanceSquared, long sampleFrame) {
        if (bestPlayerPositionSampleEvent is null) {
            return true;
        }

        if (isStationary != bestPlayerPositionSampleIsStationary) {
            return isStationary;
        }

        if (isStationary) {
            return sampleFrame > bestPlayerPositionSampleFrame;
        }

        return distanceSquared < bestPlayerPositionSampleDistanceSquared;
    }

    private void FlushPlayerPositionSample() {
        if (pendingPlayerPositionSample.HasValue) {
            ResolvePendingPlayerPositionSample(nextFrameIsStationary: false);
        }

        if (bestPlayerPositionSampleEvent is null) {
            return;
        }

        AppendEvent(bestPlayerPositionSampleEvent);
        bestPlayerPositionSampleEvent = null;
    }

    private static double DistanceSquared(Vector2 left, Vector2 right) {
        double dx = left.X - right.X;
        double dy = left.Y - right.Y;
        return (dx * dx) + (dy * dy);
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

    private enum PlayerPositionSampleMode {
        None,
        RoomEntryBestWithinWindow,
        FixedFrameAfterLoad
    }

    private readonly record struct PendingPlayerPositionSample(
        string Room,
        Vector2 PlayerPosition,
        Vector2 SpawnPoint,
        double DistanceSquared,
        long GameFrame,
        string Utc);

}
