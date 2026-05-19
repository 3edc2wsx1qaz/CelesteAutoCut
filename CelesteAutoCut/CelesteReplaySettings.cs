using Celeste.Mod;
using Microsoft.Xna.Framework.Input;

namespace Celeste.Mod.CelesteAutoCut;

public class CelesteAutoCutSettings : EverestModuleSettings {
    public static CelesteAutoCutSettings Instance { get; private set; } = null!;

    public CelesteAutoCutSettings() {
        Instance = this;
    }

    public bool Enabled { get; set; } = true;

    [SettingSubText("Relative to the CelesteAutoCutReplays folder.")]
    public string ReplayFileName { get; set; } = "last_replay.json";

    public bool RecordKeyboard { get; set; } = true;

    public bool RecordGamepad { get; set; } = true;

    public bool AutoRecordOnLevelStart { get; set; } = false;

    [SettingSubText("Writes append-only room clip events for OBS assembly.")]
    public bool EnableRoomClipRecorder { get; set; } = true;

    [SettingSubText("Relative to the CelesteAutoCutReplays folder.")]
    public string RoomClipEventFileName { get; set; } = "room_events.jsonl";

    [SettingSubText("Relative to the CelesteAutoCutReplays folder.")]
    public string RoomClipStatusFileName { get; set; } = "room_clip_session.json";

    [SettingSubText("Launches the bundled OBS auto-assembler helper in the background. User flow becomes: start Celeste + OBS, start OBS recording, play normally.")]
    public bool EnableObsAutoAssembler { get; set; } = true;

    [SettingSubText("Relative to the auto-extracted CelesteAutoCutTools folder in the game root. Absolute paths are also allowed for custom helpers.")]
    public string ObsAutoAssemblerRelativePath { get; set; } = "ObsClipPanel\\ObsClipPanel.exe";

    [SettingSubText("Relative to the CelesteAutoCutReplays folder.")]
    public string ObsAutoAssemblerWorkingDirectoryName { get; set; } = "obs_auto";

    [SettingSubText("Optional absolute ffmpeg.exe path. Leave empty to auto-find or auto-download.")]
    public string ObsAutoAssemblerFfmpegPath { get; set; } = "";

    [SettingSubText("Records full per-frame inputs for successful checkpoint segments. Disabled by default to keep CPU/memory low.")]
    public bool AutoExportSuccessfulClearRecords { get; set; } = false;

    [SettingSubText("Relative to the CelesteAutoCutReplays folder.")]
    public string SuccessfulClearFileName { get; set; } = "last_successful_clear.json";

    public bool WriteTimestampedSuccessfulClearRecords { get; set; } = true;

    public Keys ToggleRecordingKey { get; set; } = Keys.F5;

    public Keys PlayKey { get; set; } = Keys.F6;

    public Keys StopKey { get; set; } = Keys.F7;
}

