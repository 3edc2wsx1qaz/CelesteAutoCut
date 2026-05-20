using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Celeste.Mod.CelesteAutoCut;

public sealed class RoomClipEvent {
    public int SchemaVersion { get; set; } = 1;
    public string EventId { get; set; } = Guid.NewGuid().ToString("N");
    public string EventType { get; set; } = string.Empty;
    public string Utc { get; set; } = DateTime.UtcNow.ToString("O");
    public string SessionId { get; set; } = string.Empty;
    public string AttemptId { get; set; } = string.Empty;
    public string? MapSid { get; set; }
    public string? AreaMode { get; set; }
    public string? Chapter { get; set; }
    public string? Room { get; set; }
    public string? NextRoom { get; set; }
    public long? ChapterTimeMs { get; set; }
    public long GameFrame { get; set; }
    public Dictionary<string, string?> Notes { get; set; } = [];
}

public sealed class RoomClipSessionStatus {
    public int SchemaVersion { get; set; } = 1;
    public bool Active { get; set; }
    public string SessionId { get; set; } = string.Empty;
    public string AttemptId { get; set; } = string.Empty;
    public string? MapSid { get; set; }
    public string? AreaMode { get; set; }
    public string? Chapter { get; set; }
    public string? CurrentRoom { get; set; }
    public long GameFrame { get; set; }
    public long? ChapterTimeMs { get; set; }
    public string UpdatedAtUtc { get; set; } = DateTime.UtcNow.ToString("O");
}

internal static class RoomClipEventTypes {
    public const string SessionStart = "session_start";
    public const string LoadLevel = "load_level";
    public const string RoomEnter = "room_enter";
    public const string Transition = "transition";
    public const string Death = "death";
    public const string StrawberryCollect = "strawberry_collect";
    public const string PlayerPositionSample = "player_position_sample";
    public const string LevelComplete = "level_complete";
    public const string Exit = "exit";
    public const string SessionEnd = "session_end";
    public const string Reset = "reset";
}

