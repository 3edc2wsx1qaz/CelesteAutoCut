# CelesteAutoCut

CelesteAutoCut 是一个用于 **Celeste / Everest** 的自动剪辑模组。它会结合 Celeste 的房间事件和 OBS 的录制时间轴，把一整段录制自动剪成只保留有效游玩片段的成片。

适合这些场景：

- 录制练图、通关、跑图过程后，自动去掉死亡尝试和无效等待；
- 按地图分别输出最终视频；
- 保留转场、进房、草莓收集、通关等关键片段；
- 用 OBS 正常录制，不需要改变游玩习惯。

## 运行需求

- Windows x64
- Celeste + Everest
- OBS Studio
- OBS WebSocket 已启用
- 首次自动合成视频时需要可用的 `ffmpeg.exe`

OBS 28 及以上通常自带 WebSocket。请在 OBS 中打开：

```text
Tools -> WebSocket Server Settings
```

勾选 `Enable WebSocket server`，默认端口为 `4455`。

## 安装

从 GitHub Releases 下载 `CelesteAutoCut.zip`，放到 Celeste 的 `Mods` 目录：

```text
<Celeste>/Mods/CelesteAutoCut.zip
```

Steam 默认路径通常是：

```text
D:\Steam\steamapps\common\Celeste\Mods\CelesteAutoCut.zip
```

然后启动 Celeste。首次运行时，模组会自动释放内置 OBS helper 到：

```text
<Celeste>/CelesteAutoCutTools/ObsClipPanel/
```

helper 会随游戏启动，游戏退出后也会自动退出。

## 快速开始

1. 启动 OBS Studio。
2. 启动 Celeste，并确认 Everest 已加载 CelesteAutoCut。
3. 在 OBS 中开始录制，推荐录制为 `.mkv`。
4. 正常游玩。
5. 停止 OBS 录制。
6. 等待 helper 自动生成最终视频。

默认情况下，停止 OBS 录制后会自动合成最终视频；不需要手动导出回放，也不需要按额外热键。多次录制会生成多个视频，目前暂无自动将一个地图多次录制的视频自动合并为一个视频的功能。

## OBS 面板

CelesteAutoCut 会在本机启动一个 helper 面板：

```text
http://127.0.0.1:38500
```

你可以把它添加到 OBS Custom Browser Dock：

```text
View -> Docks -> Custom Browser Docks...
```

新增一个 dock：

```text
Name: Celeste Auto Cut
URL:  http://127.0.0.1:38500
```

面板可以用于：

- 连接 OBS WebSocket；
- 查看当前录制状态和 session 路径；
- 手动开始、停止、暂停、继续录制；
- 手动触发“生成最终视频”；
- 设置 OBS WebSocket 密码、输出目录、`ffmpeg.exe` 路径和最终文件名模板。

## 输出位置

最终视频默认使用录制开始的本地时间命名：

```text
yyyy-MM-dd HH-mm-ss.mp4
```

例如：

```text
2026-05-19 20-31-08.mp4
```

输出目录优先级：

1. 游戏内 Mod Options 的 `Output Directory` / `输出目录`；
2. 本次 OBS 录制文件所在目录；
3. OBS 当前配置的默认录制目录；
4. helper 工作目录。

最终视频会放进地图名子文件夹。例如：

```text
E:\obs_video\1-ForsakenCity\2026-05-19 20-31-08.mp4
E:\obs_video\Cabob\2026-05-19 20-31-08.mp4
```

如果同一次 OBS 录制跨了多个地图，CelesteAutoCut 会按地图分别输出多个视频，避免互相覆盖。

## 游戏内设置

在 Everest 的 Mod Options 里可以设置：

| 选项 | 说明 |
| --- | --- |
| `Output Directory` / `输出目录` | 最终剪辑视频的输出根目录。留空时使用 OBS 录制目录。每张地图仍会输出到对应地图名子文件夹。 |
| `Output Logs` / `输出日志` | 默认关闭。开启后会保留 room event、OBS event、clip intervals、片段选择、assembly 报告、ffconcat 和 helper stdout/stderr 日志，方便排查问题。关闭时成功生成最终视频后，会清理所有匹配的 `room_event_*.jsonl`、helper stdout/stderr 以及本次 `obs_auto/sessions/<session-id>/` 工作目录。 |

## 中间文件和日志

房间事件写在：

```text
<Celeste>/CelesteAutoCutReplays/room_event_yyyyMMdd-HHmmssfff.jsonl
```

OBS helper 的工作目录是：

```text
<Celeste>/CelesteAutoCutReplays/obs_auto/
```

每次录制会创建一个 session：

```text
<Celeste>/CelesteAutoCutReplays/obs_auto/sessions/<session-id>/
```

常见排查文件：

- `obs_events.jsonl`：OBS 录制事件和时间轴采样；
- `session_manifest.json`：录制段、文件、时间锚点；
- `clip_intervals.json`：计算出的有效片段；
- `selected_clips.json` / `selected_clips.log`：本次合成实际采用的片段；
- `assembly/assembly_report.json`：ffmpeg 合成结果和最终输出路径。

`输出日志` 关闭时，成功生成最终视频后会自动清理所有匹配的 `room_event_*.jsonl`、helper stdout/stderr，以及本次录制对应的 `obs_auto/sessions/<session-id>/` 临时工作目录；开启后会保留。清理后的活动日志不会被后续延迟到达的 `player_position_sample` 重新创建成 sample-only 残片，保留下来的 `room_event_*.jsonl` 应始终是完整事件日志。

剪辑算法会始终保留最后匹配到的 `room_enter -> load_level` 进房 intro 片段；即使后续没有找到可配对的 `load_level -> transition / strawberry_collect / level_complete` 成功段，也不要求必须收到 `exit`。因此 OBS 录制在房间内被直接中断、没有 Celeste `exit` 信号时，末尾这段 intro 仍会保留。起始边界优先选择 `session_start`；没有 `session_start` 时选择第一个 `room_enter`；如果缺少 `room_enter`，会用第一个 load / `player_position_sample` 合成一个入口片段继续匹配。单独的末尾 `room_enter -> load_level` 片段会直接使用对应 `player_position_sample` 的时间戳作为终点；没有 sample 时使用 load 后延迟并按录制文件末尾截断。

## 常见问题

### 停止 OBS 录制后没有最终视频

先检查：

- OBS 是否真的开始并停止了录制；
- OBS WebSocket 是否启用；
- 面板中的 WebSocket 地址、端口和密码是否正确；
- `<Celeste>/CelesteAutoCutReplays/obs_auto/sessions/<session-id>/` 是否生成；
- `assembly/assembly_report.json` 里的 `finalOutputPath` 和错误信息；
- OBS 录制目录下是否生成了地图名子文件夹。

### ffmpeg 相关错误

helper 会优先使用你在面板里设置的 `ffmpeg.exe`。如果没有设置，它会尝试在工作目录中查找，必要时下载 ffmpeg essentials 包。

## 开发和打包

用户只需要下载 Release zip。下面命令仅供开发者使用。

运行自测：

```powershell
..\.dotnet\dotnet.exe run --project .\ObsClipSidecar\ObsClipSidecar.csproj -- self-test
```

构建 OBS helper：

```powershell
..\.dotnet\dotnet.exe build .\ObsClipPanel\ObsClipPanel.csproj -c Release
```

发布并生成 zip：

```powershell
..\.dotnet\dotnet.exe publish .\CelesteAutoCut\CelesteAutoCut.csproj -c Release
```

产物：

- `CelesteAutoCut.zip`
- `artifacts/release/CelesteAutoCut.zip`
- `artifacts/publish/`

## 下一步计划

[] 支持将同一个地图录制的多段视频合成一个视频
[] 支持自动识别炼金等特殊场景
