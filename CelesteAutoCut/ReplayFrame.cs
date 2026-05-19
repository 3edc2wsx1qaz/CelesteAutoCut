using System;
using System.Collections.Generic;
using Celeste;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Monocle;

namespace Celeste.Mod.CelesteAutoCut;

public sealed class ReplayFile {
    public int Version { get; set; } = 1;
    public string CreatedAtUtc { get; set; } = DateTime.UtcNow.ToString("O");
    public string? AreaSid { get; set; }
    public string? LevelName { get; set; }
    public List<ReplayFrame> Frames { get; set; } = [];
}

public sealed class SuccessfulClearReplayFile {
    public int Version { get; set; } = 1;
    public string CreatedAtUtc { get; set; } = DateTime.UtcNow.ToString("O");
    public string? AreaSid { get; set; }
    public string? AreaMode { get; set; }
    public string? ChapterName { get; set; }
    public string? StartCheckpoint { get; set; }
    public string? FinalCheckpoint { get; set; }
    public List<ReplaySegment> Segments { get; set; } = [];
    public List<ReplayFrame> Frames { get; set; } = [];
}

public sealed class ReplaySegment {
    public string FromCheckpoint { get; set; } = "";
    public string ToCheckpoint { get; set; } = "";
    public int FrameCount => Frames.Count;
    public List<ReplayFrame> Frames { get; set; } = [];
}

public sealed class ReplayFrame {
    public int[] Keys { get; set; } = [];
    public int[] Buttons { get; set; } = [];
    public float LeftStickX { get; set; }
    public float LeftStickY { get; set; }
    public float RightStickX { get; set; }
    public float RightStickY { get; set; }
    public float LeftTrigger { get; set; }
    public float RightTrigger { get; set; }
    public bool DPadUp { get; set; }
    public bool DPadDown { get; set; }
    public bool DPadLeft { get; set; }
    public bool DPadRight { get; set; }

    public static ReplayFrame Capture(bool recordKeyboard, bool recordGamepad, Keys ignoredKey1, Keys ignoredKey2, Keys ignoredKey3) {
        var frame = new ReplayFrame();

        if (recordKeyboard) {
            Keys[] pressedKeys = MInput.Keyboard.CurrentState.GetPressedKeys();
            if (pressedKeys.Length > 0) {
                Array.Sort(pressedKeys);
                int keyCount = 0;
                foreach (Keys key in pressedKeys) {
                    if (!IsIgnoredKey(key, ignoredKey1, ignoredKey2, ignoredKey3)) {
                        keyCount++;
                    }
                }

                if (keyCount > 0) {
                    int[] keys = new int[keyCount];
                    int index = 0;
                    foreach (Keys key in pressedKeys) {
                        if (!IsIgnoredKey(key, ignoredKey1, ignoredKey2, ignoredKey3)) {
                            keys[index++] = (int) key;
                        }
                    }

                    frame.Keys = keys;
                }
            }
        }

        if (recordGamepad) {
            GamePadState state = MInput.GamePads[Input.Gamepad].CurrentState;
            int buttonCount = 0;
            foreach (Buttons button in AllButtons) {
                if (state.IsButtonDown(button)) {
                    buttonCount++;
                }
            }

            if (buttonCount > 0) {
                int[] buttons = new int[buttonCount];
                int index = 0;
                foreach (Buttons button in AllButtons) {
                    if (state.IsButtonDown(button)) {
                        buttons[index++] = (int) button;
                    }
                }

                frame.Buttons = buttons;
            }

            frame.LeftStickX = state.ThumbSticks.Left.X;
            frame.LeftStickY = state.ThumbSticks.Left.Y;
            frame.RightStickX = state.ThumbSticks.Right.X;
            frame.RightStickY = state.ThumbSticks.Right.Y;
            frame.LeftTrigger = state.Triggers.Left;
            frame.RightTrigger = state.Triggers.Right;
            frame.DPadUp = state.DPad.Up == ButtonState.Pressed;
            frame.DPadDown = state.DPad.Down == ButtonState.Pressed;
            frame.DPadLeft = state.DPad.Left == ButtonState.Pressed;
            frame.DPadRight = state.DPad.Right == ButtonState.Pressed;
        }

        return frame;
    }

    public void Feed(bool feedKeyboard, bool feedGamepad) {
        if (feedKeyboard) {
            MInput.Keyboard.PreviousState = MInput.Keyboard.CurrentState;
            var keys = new Keys[Keys.Length];
            for (int i = 0; i < Keys.Length; i++) {
                keys[i] = (Keys) Keys[i];
            }
            MInput.Keyboard.CurrentState = new KeyboardState(keys);
        }

        if (feedGamepad) {
            MInput.GamePadData gamePadData = MInput.GamePads[Input.Gamepad];
            gamePadData.PreviousState = gamePadData.CurrentState;
            gamePadData.CurrentState = ToGamePadState();
        }

        MInput.UpdateVirtualInputs();
    }

    private GamePadState ToGamePadState() {
        Buttons buttons = 0;
        foreach (int button in Buttons) {
            buttons |= (Buttons) button;
        }

        return new GamePadState(
            new GamePadThumbSticks(
                new Vector2(LeftStickX, LeftStickY),
                new Vector2(RightStickX, RightStickY)
            ),
            new GamePadTriggers(LeftTrigger, RightTrigger),
            new Microsoft.Xna.Framework.Input.GamePadButtons(buttons),
            new GamePadDPad(
                DPadUp ? ButtonState.Pressed : ButtonState.Released,
                DPadDown ? ButtonState.Pressed : ButtonState.Released,
                DPadLeft ? ButtonState.Pressed : ButtonState.Released,
                DPadRight ? ButtonState.Pressed : ButtonState.Released
            )
        );
    }

    private static bool IsIgnoredKey(Keys key, Keys ignoredKey1, Keys ignoredKey2, Keys ignoredKey3)
        => key == ignoredKey1 || key == ignoredKey2 || key == ignoredKey3;

    private static readonly Buttons[] AllButtons = CreateAllButtons();

    private static Buttons[] CreateAllButtons() {
        var values = (Buttons[]) Enum.GetValues(typeof(Buttons));
        var buttons = new List<Buttons>(values.Length);
        foreach (Buttons button in values) {
            if (button != 0 && IsSingleButtonFlag(button)) {
                buttons.Add(button);
            }
        }

        buttons.Sort((left, right) => ((int) left).CompareTo((int) right));
        return buttons.ToArray();
    }

    private static bool IsSingleButtonFlag(Buttons button) {
        int value = (int) button;
        return (value & (value - 1)) == 0;
    }
}

