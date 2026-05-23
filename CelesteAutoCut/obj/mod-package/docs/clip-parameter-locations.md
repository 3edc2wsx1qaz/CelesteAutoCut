# 剪辑参数代码位置速查

本文记录 CelesteAutoCut 常改剪辑参数在代码里的位置。行号可能随代码变化；优先按“常量/属性名”搜索。

## 房间进入后的 player_position_sample 采样

文件：`CelesteAutoCut/RoomClipRecorder.cs`

| 参数 | 当前值 | 代码位置 / 搜索名 | 作用 |
| --- | ---: | --- | --- |
| 进房 load 后开始采样延迟 | 45 帧 | `PlayerPositionSampleStartDelayFrames` | `room_enter -> load_level` 后，从 load 后第 45 帧开始观察人物坐标。 |
| 进房采样间隔 | 10 帧 | `PlayerPositionSampleIntervalFrames` | 进房采样窗口内，每 10 帧取一次候选。 |
| 进房采样窗口 | 480 帧（约 8 秒） | `PlayerPositionSampleWindowFrames` | 进房 load 的候选采样窗口长度。 |
| 非进房 load 固定采样延迟 | 60 帧 | `FixedLoadPositionSampleDelayFrames` | Respawn/checkpoint 等非进房 `load_level` 不跑窗口，只在 load 后固定第 60 帧采样一次。 |
| 静止稳定判定 | 连续 10 帧速度为 0 | `ConsecutiveStationarySampleFrames` | 从候选帧起，连续 10 帧 `Speed == Vector2.Zero` 才把该候选视为 `stationary=true`。 |

相关逻辑：

- `ObserveLoadLevel(...)`：设置本次 load 的采样起点和窗口终点。
- `ObservePlayerPositionSample(...)`：按采样间隔收集候选，并优先保留静止候选。
- `ResolvePendingPlayerPositionSample(...)`：连续速度为 0 的帧数累计到 `ConsecutiveStationarySampleFrames` 后，才落盘静止样本。
- `RecordPlayerPositionSample(...)`：写入 `player_position_sample` 事件。

## room_entry death-reload 回退窗口

文件：`ObsClipSidecar/IntervalGenerator.cs`

| 参数 | 当前值 | 代码位置 / 搜索名 | 作用 |
| --- | ---: | --- | --- |
| death-reload 回退窗口 | 5 秒 | `RoomEntryDeathReloadWindow` | 当单独的 `room_enter -> load_level` 缺少可用 `player_position_sample` 时，只在初始 load 后 5 秒内寻找同房间 `death -> load_level` 作为回退片段终点。 |

相关逻辑：

- `Generate(...)` 中 `IsStandaloneRoomEntryLoad(...)` 分支：优先使用 `player_position_sample` 作为进房片段终点；没有采样时才调用 death-reload 回退。
- `TryFindRoomEntryDeathReloadEnd(...)`：用 `RoomEntryDeathReloadWindow` 计算窗口结束时间，并寻找同房间 death 后的 reload。

## load_level 边界改用 player_position_sample 时间戳

文件：`ObsClipSidecar/IntervalGenerator.cs`

| 搜索名 | 作用 |
| --- | --- |
| `TryFindLoadLevelPlayerPositionBoundary` | 给所有可匹配到采样的 `load_level` 起点/终点边界改用对应 `player_position_sample.Utc`。 |
| `load_level_player_position_start` | 成功把片段起点从 `load_level` 时间改成采样时间时写入的 reason。 |
| `load_level_player_position_end` | 成功把片段终点从 `load_level` 时间改成采样时间时写入的 reason。 |
| `room_entry_load_player_position_end` | 单独进房片段使用初始 load 的采样作为终点时写入的 reason；末尾 `room_enter -> load_level` 也会直接采用对应 sample 的时间戳。 |
| `room_entry_load_death_reload` | 单独进房片段没有采样，改用 death-reload 回退时写入的 reason。 |

起始段选择顺序：有 `session_start` 时从 `session_start` 开始；没有 `session_start` 时从第一个 `room_enter` 开始；如果整段事件缺少 `room_enter`，`AddSyntheticRoomEnterForLoadPositionFallback(...)` 会用第一个 `load_level` / `player_position_sample` 合成入口，按 `room_enter -> load_position` 片段继续后续匹配。

## OBS helper / 面板设置参数

主要默认值：`ObsClipPanel/PanelModels.cs`

| 参数 | 当前默认值 | 代码位置 / 搜索名 | 作用 |
| --- | ---: | --- | --- |
| `PreRollMs` | 250 ms | `PanelSettings.PreRollMs` | 普通片段起点前摇。Respawn load 等动态采样起点会跳过 pre-roll。 |
| `PostRollMs` | 500 ms | `PanelSettings.PostRollMs` | 普通片段终点后摇；缺少进房采样且没有 death-reload 时也会用于固定 delay。 |
| `MaxAnchorGapMs` | 2000 ms | `PanelSettings.MaxAnchorGapMs` | OBS 时间锚点最大容差。 |
| `MaxCutErrorMs` | 100 ms | `PanelSettings.MaxCutErrorMs` | ffmpeg 切点误差容忍。 |
| `SplitOnPause` | true | `PanelSettings.SplitOnPause` | pause 区间是否自动切开。 |
| `RequireExistingFiles` | true | `PanelSettings.RequireExistingFiles` | 是否要求源录制文件存在。 |
| `LogOutputEnabled` | false | `CelesteAutoCut/CelesteReplaySettings.cs` / `ObsClipPanel/PanelModels.cs` | 默认关闭；关闭时成功生成视频后清理全部匹配的 `room_event_*.jsonl`、helper stdout/stderr，以及当前 `obs_auto/sessions/<session-id>/` 工作目录；活动日志被清理后不会被 late sample 重新创建成只含 `player_position_sample` 的残片。 |

设置流向：

- `CelesteAutoCut/CelesteReplaySettings.cs`：Celeste Mod Options 中的 `LogOutputEnabled`。
- `Dialog/English.txt` 与 `Dialog/Simplified Chinese.txt`：Mod Options 文案。
- `CelesteAutoCut/ObsAutoAssemblerLauncher.cs`：把 `LogOutputEnabled` 传给 helper 环境变量 `CELESTE_REPLAY_LOG_OUTPUT_ENABLED`，并决定是否写 helper stdout/stderr 日志。
- `ObsClipPanel/PanelStateStore.cs`：读取/保存 helper 面板设置，并读取环境变量覆盖。
- `ObsClipPanel/PanelCoordinator.cs`：生成区间时把面板设置传给 `IntervalGenerator`；成功合成后按 `LogOutputEnabled` 决定是否清理日志。

## 测试位置

文件：`ObsClipSidecar/SelfTests.cs`

常用搜索名：

- `RoomEntryDeathReloadUsedWithoutSample`
- `RoomEntryDeathReloadUsesReloadSampleBoundary`
- `RoomEntryDeathReloadIgnoresDeathsOutsideWindow`
- `RoomEntryStationarySampleBeatsDeathReload`
- `RoomEntryChoosesLatestStationarySample`

这些测试覆盖进房采样优先级、death-reload 回退、窗口外 death 忽略、静止采样优先等核心行为。
