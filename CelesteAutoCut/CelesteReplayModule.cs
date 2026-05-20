using System;
using Celeste;
using Celeste.Mod;
using Monocle;

namespace Celeste.Mod.CelesteAutoCut;

public sealed class CelesteAutoCutModule : EverestModule {
    public static CelesteAutoCutModule Instance { get; private set; } = null!;
    public static CelesteAutoCutSettings Settings => (CelesteAutoCutSettings) Instance._Settings;

    private readonly ReplayController controller = new();
    private readonly RoomClipRecorder roomClipRecorder;
    private readonly ObsAutoAssemblerLauncher obsAutoAssemblerLauncher;
    private bool obsAutoAssemblerStartPending;
    private bool observedLevelSceneLastFrame;
    private int obsAutoAssemblerReadyFrames;
    private const int ObsAutoAssemblerStartupDelayFrames = 30;

    public override Type SettingsType => typeof(CelesteAutoCutSettings);

    public CelesteAutoCutModule() {
        Instance = this;
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
        roomClipRecorder.Shutdown();
        obsAutoAssemblerLauncher.Stop();
    }

    private void OnPlayerDie(Player player) {
        roomClipRecorder.OnDeath(player);
    }

    private void OnStrawberryCollect(On.Celeste.Strawberry.orig_OnCollect orig, Strawberry self) {
        orig(self);

        roomClipRecorder.OnStrawberryCollect(self);
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

        ObserveSceneExit();
    }

    private void OnLevelUpdate(On.Celeste.Level.orig_Update orig, Level self) {
        orig(self);

        bool chapterComplete = RuntimeLevelState.IsChapterComplete(self);
        roomClipRecorder.ObserveLevel(self, chapterComplete);
        observedLevelSceneLastFrame = true;
        roomClipRecorder.TickFrame();
    }

    private void ObserveSceneExit() {
        if (observedLevelSceneLastFrame && Engine.Scene is not Level) {
            roomClipRecorder.ObserveExitedLevel("scene_changed");
            observedLevelSceneLastFrame = false;
        }
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
