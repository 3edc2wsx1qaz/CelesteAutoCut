using System;
using System.Collections.Generic;
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
        Everest.Events.Level.OnEnter += OnEnter;
        Everest.Events.Level.OnLoadLevel += OnLoadLevel;
        Everest.Events.Level.OnTransitionTo += OnTransitionTo;
        Everest.Events.Level.OnComplete += OnComplete;
        Everest.Events.Level.OnExit += OnExit;
        Everest.Events.Player.OnDie += OnPlayerDie;
    }

    public override void Unload() {
        On.Monocle.MInput.Update -= OnMInputUpdate;
        Everest.Events.Level.OnEnter -= OnEnter;
        Everest.Events.Level.OnLoadLevel -= OnLoadLevel;
        Everest.Events.Level.OnTransitionTo -= OnTransitionTo;
        Everest.Events.Level.OnComplete -= OnComplete;
        Everest.Events.Level.OnExit -= OnExit;
        Everest.Events.Player.OnDie -= OnPlayerDie;
        controller.Stop();
        successfulClearRecorder.Stop(discard: true);
        roomClipRecorder.Shutdown();
        obsAutoAssemblerLauncher.Stop();
    }

    private void OnEnter(Session session, bool fromSaveData) {
        if (Settings.Enabled) {
            successfulClearRecorder.Start(session);
            roomClipRecorder.Start(session, fromSaveData);
        }
    }

    private void OnLoadLevel(Level level, Player.IntroTypes playerIntro, bool isFromLoader) {
        if (Settings.Enabled && !roomClipRecorder.Active) {
            roomClipRecorder.Start(level.Session, fromSaveData: false);
        }

        if (Settings.Enabled && Settings.AutoRecordOnLevelStart && isFromLoader) {
            controller.StartRecording();
        }

        if (Settings.Enabled) {
            roomClipRecorder.OnLoadLevel(level, playerIntro, isFromLoader);
        }
    }

    private void OnTransitionTo(Level level, LevelData nextLevelData, Microsoft.Xna.Framework.Vector2 direction) {
        if (Settings.Enabled) {
            successfulClearRecorder.OnTransitionTo(nextLevelData);
            roomClipRecorder.OnTransitionTo(level, nextLevelData, direction);
        }
    }

    private void OnComplete(Level level) {
        if (Settings.Enabled) {
            successfulClearRecorder.OnChapterComplete(level);
            roomClipRecorder.OnComplete(level);
        }
    }

    private void OnExit(Level level, LevelExit exit, LevelExit.Mode mode, Session session, HiresSnow snow) {
        successfulClearRecorder.Stop(discard: true);
        roomClipRecorder.OnExit(level, exit, mode, session);
    }

    private void OnPlayerDie(Player player) {
        if (Settings.Enabled) {
            successfulClearRecorder.OnDeath();
            roomClipRecorder.OnDeath(player);
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
        roomClipRecorder.TickFrame();
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

    internal static IEnumerable<Keys> HotkeyKeys() {
        yield return Settings.ToggleRecordingKey;
        yield return Settings.PlayKey;
        yield return Settings.StopKey;
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

