using System;
using Celeste;
using Celeste.Mod;
using Microsoft.Xna.Framework.Input;
using Monocle;

namespace Celeste.Mod.CelesteAutoCut;

public sealed class CelesteAutoCutModule : EverestModule {
    public static CelesteAutoCutModule Instance { get; private set; } = null!;
    public static CelesteAutoCutSettings Settings => (CelesteAutoCutSettings) Instance._Settings;

    private readonly ReplayController controller = new();
    private readonly SuccessfulClearRecorder successfulClearRecorder;
    private readonly RoomClipRecorder roomClipRecorder;
    private readonly ObsAutoAssemblerLauncher obsAutoAssemblerLauncher;
    private KeyboardState previousRawKeyboard;
    private bool obsAutoAssemblerStartPending;
    private bool observedLevelSceneLastFrame;
    private int obsAutoAssemblerReadyFrames;
    private const int ObsAutoAssemblerStartupDelayFrames = 30;

    public override Type SettingsType => typeof(CelesteAutoCutSettings);

    public CelesteAutoCutModule() {
        Instance = this;
        successfulClearRecorder = new SuccessfulClearRecorder(controller);
        roomClipRecorder = new RoomClipRecorder(controller);
        obsAutoAssemblerLauncher = new ObsAutoAssemblerLauncher(controller);
    }

    public override void Load() {
        obsAutoAssemblerStartPending = true;
        On.Monocle.MInput.Update += OnMInputUpdate;
        On.Celeste.Level.Update += OnLevelUpdate;
        On.Celeste.Strawberry.OnCollect += OnStrawberryCollect;
        Everest.Events.Player.OnDie += OnPlayerDie;
    }

    public override void Unload() {
        On.Monocle.MInput.Update -= OnMInputUpdate;
        On.Celeste.Level.Update -= OnLevelUpdate;
        On.Celeste.Strawberry.OnCollect -= OnStrawberryCollect;
        Everest.Events.Player.OnDie -= OnPlayerDie;
        controller.Stop();
        successfulClearRecorder.Stop(discard: true);
        roomClipRecorder.Shutdown();
        obsAutoAssemblerLauncher.Stop();
    }

    private void OnPlayerDie(Player player) {
        if (Settings.Enabled) {
            successfulClearRecorder.OnDeath();
            roomClipRecorder.OnDeath(player);
        }
    }

    private void OnStrawberryCollect(On.Celeste.Strawberry.orig_OnCollect orig, Strawberry self) {
        orig(self);

        if (Settings.Enabled) {
            roomClipRecorder.OnStrawberryCollect(self);
        }
    }

    private void OnMInputUpdate(On.Monocle.MInput.orig_Update orig) {
        orig();

        if (obsAutoAssemblerStartPending) {
            if (Engine.Scene is Overworld || Engine.Scene is Level) {
                obsAutoAssemblerReadyFrames++;
                if (obsAutoAssemblerReadyFrames >= ObsAutoAssemblerStartupDelayFrames) {
                    obsAutoAssemblerStartPending = false;
                    obsAutoAssemblerLauncher.Start();
                }
            } else {
                obsAutoAssemblerReadyFrames = 0;
            }
        }

        if (!Settings.Enabled) {
            return;
        }

        HandleHotkeys();

        if (controller.Mode == ReplayMode.Playing) {
            controller.PlaybackFrame();
        } else if (controller.Mode == ReplayMode.Recording) {
            controller.RecordFrame();
        }

        successfulClearRecorder.RecordFrame();
        ObserveSceneExit();
    }

    private void OnLevelUpdate(On.Celeste.Level.orig_Update orig, Level self) {
        orig(self);

        if (!Settings.Enabled) {
            return;
        }

        bool chapterComplete = RuntimeLevelState.IsChapterComplete(self);
        roomClipRecorder.ObserveLevel(self, chapterComplete);
        successfulClearRecorder.ObserveLevel(self, chapterComplete);

        if (Settings.AutoRecordOnLevelStart && controller.Mode == ReplayMode.Idle && !observedLevelSceneLastFrame) {
            controller.StartRecording();
        }

        observedLevelSceneLastFrame = true;
        roomClipRecorder.TickFrame();
    }

    private void ObserveSceneExit() {
        if (observedLevelSceneLastFrame && Engine.Scene is not Level) {
            roomClipRecorder.ObserveExitedLevel("scene_changed");
            successfulClearRecorder.ObserveExitedLevel();
            observedLevelSceneLastFrame = false;
        }
    }

    private void HandleHotkeys() {
        KeyboardState current = Keyboard.GetState();

        if (Pressed(current, Settings.StopKey)) {
            controller.Stop();
        } else if (Pressed(current, Settings.ToggleRecordingKey)) {
            controller.ToggleRecording();
        } else if (Pressed(current, Settings.PlayKey)) {
            controller.StartPlayback();
        }

        previousRawKeyboard = current;
    }

    private bool Pressed(KeyboardState current, Keys key) {
        return key != Keys.None && current.IsKeyDown(key) && !previousRawKeyboard.IsKeyDown(key);
    }

    [Command("replay_record", "Start or stop CelesteAutoCut recording.")]
    private static void CommandRecord() {
        Instance.controller.ToggleRecording();
    }

    [Command("replay_play", "Play the configured CelesteAutoCut replay file.")]
    private static void CommandPlay() {
        Instance.controller.StartPlayback();
    }

    [Command("replay_stop", "Stop CelesteAutoCut recording or playback.")]
    private static void CommandStop() {
        Instance.controller.Stop();
    }

    [Command("replay_room_clip_reset", "Delete CelesteAutoCut room clip event logs.")]
    private static void CommandRoomClipReset() {
        Instance.roomClipRecorder.ResetLogs();
    }

    [Command("replay_room_clip_status", "Print CelesteAutoCut room clip recorder status path and state.")]
    private static void CommandRoomClipStatus() {
        var status = Instance.roomClipRecorder.GetStatus();
        Engine.Commands?.Log($"[CelesteAutoCut] roomClip active={status.Active} sessionId={status.SessionId} attemptId={status.AttemptId} room={status.CurrentRoom} gameFrame={status.GameFrame}");
        Engine.Commands?.Log($"[CelesteAutoCut] roomClip statusPath={Instance.roomClipRecorder.StatusPath}");
    }

    [Command("replay_room_clip_path", "Print CelesteAutoCut room clip event log path.")]
    private static void CommandRoomClipPath() {
        Engine.Commands?.Log($"[CelesteAutoCut] roomClip eventLog={Instance.roomClipRecorder.EventLogPath}");
    }
}

internal static class RuntimeLevelState {
    private const System.Reflection.BindingFlags Flags =
        System.Reflection.BindingFlags.Instance |
        System.Reflection.BindingFlags.Public |
        System.Reflection.BindingFlags.NonPublic;

    private static readonly System.Reflection.PropertyInfo? CompletedProperty = typeof(Level).GetProperty("Completed", Flags);
    private static readonly System.Reflection.FieldInfo? CompletedField = typeof(Level).GetField("Completed", Flags);

    public static bool IsChapterComplete(Level level) {
        try {
            if (CompletedProperty?.PropertyType == typeof(bool)) {
                return (bool) CompletedProperty.GetValue(level)!;
            }

            if (CompletedField?.FieldType == typeof(bool)) {
                return (bool) CompletedField.GetValue(level)!;
            }
        } catch {
            // best effort only
        }

        return false;
    }
}
