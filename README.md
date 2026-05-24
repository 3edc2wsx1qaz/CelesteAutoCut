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
2. 在 OBS 中开始录制，推荐录制为 `.mkv`。
3. 启动 Celeste，并确认 Everest 已加载 CelesteAutoCut。
4. 正常游玩。
5. 停止 OBS 录制。
6. 等待 helper 自动生成最终视频。

默认情况下，停止 OBS 录制后会自动合成最终视频；不需要手动导出回放，也不需要按额外热键。多次录制会生成多个视频，目前暂无自动将一个地图多次录制的视频自动合并为一个视频的功能。

**注意**: 如果你在进入关卡后开始录制，请手动重试一次以保证进入关卡后的第一个房间被成功录制。

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
- 设置 OBS WebSocket 密码、输出目录、`ffmpeg.exe` 路径、最终文件名模板和剪辑强度。

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
| `Clip Intensity` / `剪辑强度` | 默认 `Low`。选择 `Low` 使用原始 `load_level` 时间戳、保留完整 `room_enter -> load_level -> death -> reload` 进房死亡段、与 High 一致地从 reload 继续后续匹配，并保留更宽松的最终进房尾段；选择 `High` 使用精确 player-position 切点和望远镜/对话后的进房采样。 |
| `Output Logs` / `输出日志` | 默认关闭。开启后会保留 room event、OBS event、clip intervals、片段选择、assembly 报告、ffconcat 和 helper stdout/stderr 日志，方便排查问题。关闭时成功生成最终视频后，会清理所有匹配的 `room_event_*.jsonl`、helper stdout/stderr 以及本次 `obs_auto/sessions/<session-id>/` 工作目录。 |

## 剪辑强度

游戏内 Mod Options 和 OBS 面板里的 `剪辑强度` 默认为 `Low`：

- `Low`：默认模式，线性匹配逻辑与 `High` 对齐，但所有 `load_level` 边界都直接使用原始 `load_level` 时间戳，不会改用 `player_position_sample`。每段会先按时间找 `session_start`，再找 `room_enter`；如果当前 session 没有 `room_enter`，或没有 `session_start` 且在第一个真实 `room_enter` 前先遇到 `load_level`，就用这个 `load_level` 合成 `[room_enter, load_level]` 入口继续匹配。`room_enter -> load_level -> death -> load_level` 会保留完整的 `room_enter -> first load_level -> death -> reload load_level` 进房死亡段；即使前一段 `transition -> room_enter` 紧贴当前进房，也不会把这个进房死亡段合并掉。非最终房间会从 reload `load_level` 继续匹配后续成功/exit/录制末尾片段；如果最后一段也能匹配 `room_enter -> load_level -> death -> reload load_level`，则只保留这整段，不再追加 `reload -> 录制末尾`。这个 death->reload 后续匹配不限制 death 距离初始 load 的时间窗口。最后一个未死亡的未通关进房段会保留 `room_enter -> exit`，没有 `exit` 时保留 `room_enter -> 录制末尾`。
- `High`：使用精确规则。`load_level` 边界会优先用对应的 `player_position_sample` 时间戳微调；进房后如果检测到望远镜或对话事件，会先保留 `load_level -> 望远镜/对话` 这段，并在望远镜/对话结束后重新开始进房采样。

命令行生成 intervals 时也可以传：

```powershell
..\.dotnet\dotnet.exe run --project .\ObsClipSidecar\ObsClipSidecar.csproj -- generate-intervals --room-events room_events.jsonl --session-manifest session_manifest.json --out clip_intervals.json --clip-intensity low
```

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

生成的 zip 和 `bin/` / `obj/` / `artifacts/` 构建产物不纳入 Git 跟踪；发布时重新打包并复制到 Celeste `Mods` 目录即可，不需要因为 zip 二进制变化提交 zip 文件。
- `artifacts/publish/`

## 下一步计划

- [] 支持将同一个地图录制的多段视频合成一个视频
- [] 支持自动识别炼金等特殊场景
- [] 支持以任意顺序在任意时间启动 OBS 和 Celeste
- [] 修复潜在的 bug
