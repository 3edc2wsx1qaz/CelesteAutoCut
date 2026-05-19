# CelesteAutoCut

CelesteAutoCut 是一个 **Celeste / Everest 模组**，配合 **OBS Studio** 自动生成“只保留有效游玩片段”的通关视频。

典型流程：

1. 启动 Celeste 和 OBS Studio。
2. 在 OBS 中开始录制（推荐 `.mkv`）。
3. 正常游玩；停止 OBS 录制后，helper 会自动生成最终视频。

当前版本重点修复：首房间不会再被初始 `load_level` 截掉；有死亡的房间会保留本次进入房间到初始 `load_level` 的片段，之后再接最终成功尝试；一次录制跨多个地图时会按地图分别输出到对应地图名文件夹；房间切换处不再重复保留同一段转场画面；误回已通关上一个房间的来回折返会被剪掉，同时保留支路返回主房间的合法路线；低资源模式默认开启，即使旧配置里曾打开成功通关逐帧输入记录，也不会影响 OBS 自动剪辑的低 CPU/内存路径。

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

最终视频会写入上述目录下的 **地图名子文件夹**，文件夹名取地图 SID 的最后一段，并清理为合法文件名。例如：

```text
E:\obs_video\1-ForsakenCity\2026-05-19 20-31-08.mp4
E:\obs_video\Cabob\2026-05-19 20-31-08.mp4
```

如果手动把最终文件名配置成绝对路径，单地图录制会尊重该绝对路径；若同一次录制跨多个地图且绝对路径会互相覆盖，helper 会自动在该绝对路径所在目录下追加地图子文件夹，避免多个地图成片写到同一个文件。

如果一次 OBS 录制里出现多个不同地图 SID，helper 会按地图分组分别输出多个视频。每个视频仍使用同一个录制开始本地时间作为文件名，并分别进入自己的地图名文件夹。

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
- `LowResourceMode = true`：默认跳过旧的成功通关逐帧输入导出，降低 CPU/内存占用；不影响 OBS 自动剪辑。
- `AutoExportSuccessfulClearRecords = false`：默认关闭逐帧输入记录；在低资源模式开启时，即使旧配置保留为 `true` 也不会采集逐帧输入。
- OBS helper 默认轮询间隔提高到 `1000ms`，减少后台 CPU 占用。
- 正常运行的 Info 级日志不再刷 Celeste 控制台；只保留警告、错误和手动命令输出。

说明：`LowResourceMode` / `AutoExportSuccessfulClearRecords` 只影响额外导出的逐帧输入 JSON，不影响最终视频自动剪辑。

---

## 房间切换剪辑规则

相邻房间片段会在实际 transition 边界处对齐：

- 上一个房间不会再把 post-roll 延伸进下一个房间；
- 下一个房间不会再把 pre-roll 回卷到上一个房间；
- `clip_intervals.json` 会记录 `adjacent_room_overlap_trimmed`，表示相邻片段已去重对齐。

死亡房间的保留规则：

- 正常进房间的 `load_level(playerIntro=Transition)` 不会把房间开头截掉；
- `death` / `load_end` 只标记本次尝试失败，不会再被当作成功片段起点；
- `respawn` 或 `load_level(playerIntro=Respawn)` 才会作为死亡后成功尝试的起点；
- 如果一个房间内发生死亡，会额外保留“本次进入房间 -> 初始 `load_level`”的片段，原因标记为 `room_entry_intro_before_clear`；
- 分支房间或同名房间再次进入时按每次 `room_enter` 独立处理，不会把上一次访问同名房间的死亡尾巴接到本次成功片段前；
- 如果出现 `A -> B -> A -> B` 这种刚进当前房间又退回上一个已通关房间、随后再走回当前房间的折返，剪掉中间的 `B -> A` 失败片段和 `A -> B` 已清房间重走片段；
- 如果是支路路线，例如 `Hub -> Side -> Hub -> Exit`，返回 Hub 后继续去新房间，不会被当成误回退剪掉。

最终拼接规则：

- 精确模式会先把每个片段重编码成临时 segment；
- 最终 concat 阶段也会重新编码一次，避免直接 stream copy 时继承异常视频时间戳，导致成片时长变长或播放到中途卡住。

---

## 关于存档

CelesteAutoCut 不会读写或删除 Celeste 存档文件。代码只会写入：

- `<Celeste>/CelesteAutoCutReplays/` 下的事件、session、临时拼接文件；
- OBS 录制目录下地图名子文件夹中的最终视频；
- `<Celeste>/CelesteAutoCutTools/` 下的 helper 文件。

如果某张 mod 地图的存档看起来消失，建议先确认 Everest 是否加载了同一套 mod、同一存档槽，以及 `Mods` 目录里是否只有一个活动的 `CelesteAutoCut*.zip`。

---

## 常用故障排查

### 1. 停止 OBS 录制后没看到最终视频

检查：

- OBS 是否真的开始录制；
- OBS websocket 是否启用并连接；
- `<Celeste>/CelesteAutoCutReplays/obs_auto/sessions/<session-id>/` 是否生成；
- `assembly/assembly_report.json` 中的 `finalOutputPath`；
- OBS 录制目录下是否生成了对应地图名子文件夹。

### 2. 游戏里还是旧行为

在真实环境测试或安装前，确保：

```text
D:\Steam\steamapps\common\Celeste\Mods
```

中只有一个活动的 `CelesteAutoCut*.zip`。删除旧的 `CelesteAutoCut-*.zip` 副本，避免 Everest 加载旧包。

### 3. 控制台出现大量失败尝试日志

当前版本已移除 `Discarded failed checkpoint attempt` 这类正常失败尝试日志，并通过默认开启的 `LowResourceMode` 跳过对应的逐帧输入记录功能；房间事件重置、成功通关辅助记录、helper 正常退出等非错误路径也不再向控制台输出常规日志。

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

当前自测覆盖：

- 首房间初始 `Transition load_level` 不截断房间开头；
- 死亡房间保留“进入房间 -> 初始 `load_level`”，成功片段从 `Respawn` 开始而不是从 `death` 开始；
- 分支后再次进入同名房间时，每次访问独立生成片段；
- 误回上一个已通关房间后又立刻回到当前房间的折返会被剪掉；
- 支路返回 Hub 后继续前进的路线会被保留；
- 一次录制中多个地图 SID 会拆成多个独立输出；
- 多地图输出路径发生冲突时会自动加地图子文件夹避免覆盖；
- 精确拼接模式的最终 concat 会重新编码，避免输出视频时间戳异常膨胀。

真实环境脚本：

```powershell
.\Scripts\real-zip-only-test.ps1
```

脚本会先进入 1A 建立房间事件 session，再启动 OBS 录制并播放 1A TAS；如果 1A TAS 复用了进入录制前已经存在的同一房间 session，验证会复用该 session，而不是误判为“录制后没有新的 room event”。验证同时检查最终视频位于地图名子文件夹，且文件名仍为录制开始本地时间；可用 `ffprobe` 对比最终视频时长和 `clip_intervals.json` 的有效片段总时长，确认成片没有因时间戳异常变长或卡住。
