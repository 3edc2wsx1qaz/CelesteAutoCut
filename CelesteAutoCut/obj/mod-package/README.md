# CelesteAutoCut

CelesteAutoCut 是一个 **Celeste / Everest 模组**，配合 **OBS Studio** 自动生成“只保留有效游玩片段”的通关视频。

典型流程：

1. 启动 Celeste 和 OBS Studio。
2. 在 OBS 中开始录制（推荐 `.mkv`）。
3. 正常游玩；停止 OBS 录制后，helper 会自动生成最终视频。

当前版本重点修复：房间切换处不再重复保留同一段转场画面；最终成片默认直接放在 OBS 录制目录；成功通关输入记录默认关闭以降低 CPU/内存占用。

---

## 安装

把发布包放到：

```text
<Celeste>/Mods/CelesteAutoCut.zip
```

例如：

```text
D:\Steam\steamapps\common\Celeste\Mods\CelesteAutoCut.zip
```

首次运行时，模组会把内置 helper 解压到：

```text
<Celeste>/CelesteAutoCutTools/ObsClipPanel/
```

游戏退出后，helper 会跟随父进程自动退出。

---

## 输出位置与命名

最终视频默认使用 **录制开始的本地时间** 命名，格式为：

```text
yyyy-MM-dd HH-mm-ss.mp4
```

例如：

```text
2026-05-19 20-31-08.mp4
```

输出目录优先级：

1. 本次 OBS 实际录制文件所在目录；
2. OBS 当前配置的默认录制目录；
3. helper 工作目录（兜底）。

为了避免 mod 地图产物被藏进难找的子目录，当前版本 **不再按地图 SID 自动创建地图子文件夹**。正常情况下，最终视频会直接出现在 OBS 录制目录里。

---

## 中间产物与排查路径

房间事件日志：

```text
<Celeste>/CelesteAutoCutReplays/room_events.jsonl
```

helper 工作目录：

```text
<Celeste>/CelesteAutoCutReplays/obs_auto/
```

每次录制会生成一个 session：

```text
<Celeste>/CelesteAutoCutReplays/obs_auto/sessions/<session-id>/
```

常见文件：

- `obs_events.jsonl`：OBS 事件与录制时间轴采样；
- `session_manifest.json`：录制段、文件、时间锚点；
- `clip_intervals.json`：计算后的有效房间片段；
- `assembly/assembly_report.json`：ffmpeg 拼接结果与最终输出路径。

如果没有看到最终视频，优先检查最新 session 的 `assembly/assembly_report.json` 和 `clip_intervals.json`。

---

## 当前默认性能策略

- `EnableRoomClipRecorder = true`：保留房间事件，用于 OBS 自动剪辑。
- `EnableObsAutoAssembler = true`：启动内置 OBS helper。
- `AutoExportSuccessfulClearRecords = false`：默认关闭逐帧输入记录，避免长时间游玩时占用更多内存。
- OBS helper 默认轮询间隔提高到 `1000ms`，减少后台 CPU 占用。
- 正常运行的 Info 级日志不再刷 Celeste 控制台；只保留警告、错误和手动命令输出。

说明：`AutoExportSuccessfulClearRecords` 只影响额外导出的逐帧输入 JSON，不影响最终视频自动剪辑。

---

## 房间切换剪辑规则

相邻房间片段会在实际 transition 边界处对齐：

- 上一个房间不会再把 post-roll 延伸进下一个房间；
- 下一个房间不会再把 pre-roll 回卷到上一个房间；
- `clip_intervals.json` 会记录 `adjacent_room_overlap_trimmed`，表示相邻片段已去重对齐。

---

## 关于存档

CelesteAutoCut 不会读写或删除 Celeste 存档文件。代码只会写入：

- `<Celeste>/CelesteAutoCutReplays/` 下的事件、session、临时拼接文件；
- OBS 录制目录中的最终视频；
- `<Celeste>/CelesteAutoCutTools/` 下的 helper 文件。

如果某张 mod 地图的存档看起来消失，建议先确认 Everest 是否加载了同一套 mod、同一存档槽，以及 `Mods` 目录里是否只有一个活动的 `CelesteAutoCut*.zip`。

---

## 常用故障排查

### 1. 停止 OBS 录制后没看到最终视频

检查：

- OBS 是否真的开始录制；
- OBS websocket 是否启用并连接；
- `<Celeste>/CelesteAutoCutReplays/obs_auto/sessions/<session-id>/` 是否生成；
- `assembly/assembly_report.json` 中的 `finalOutputPath`。

### 2. 游戏里还是旧行为

在真实环境测试或安装前，确保：

```text
D:\Steam\steamapps\common\Celeste\Mods
```

中只有一个活动的 `CelesteAutoCut*.zip`。删除旧的 `CelesteAutoCut-*.zip` 副本，避免 Everest 加载旧包。

### 3. 控制台出现大量失败尝试日志

当前版本已移除 `Discarded failed checkpoint attempt` 这类正常失败尝试日志，并默认关闭对应的逐帧输入记录功能。

---

## 开发 / 打包

发布命令：

```powershell
..\.dotnet\dotnet.exe publish .\CelesteAutoCut\CelesteAutoCut.csproj -c Release
```

打包产物：

- `artifacts/release/CelesteAutoCut.zip`
- `CelesteAutoCut.zip`
- `artifacts/publish/`

zip 中包含：

- `everest.yaml`
- `README.md`
- `bin/CelesteAutoCut.dll`
- `bin/CelesteAutoCut.deps.json`

helper 以内嵌 payload 方式随 DLL 分发，运行时自动释放。

---

## 验证

本次变更使用的验证：

```powershell
..\.dotnet\dotnet.exe run --project .\ObsClipSidecar\ObsClipSidecar.csproj -- self-test
..\.dotnet\dotnet.exe publish .\CelesteAutoCut\CelesteAutoCut.csproj -c Release
```

真实环境脚本：

```powershell
.\Scripts\real-zip-only-test.ps1
```
