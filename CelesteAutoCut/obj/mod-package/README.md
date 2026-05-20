# CelesteAutoCut

CelesteAutoCut 是一个 **Celeste / Everest 模组**，配合 **OBS Studio** 自动生成“只保留有效游玩片段”的通关视频。

典型流程：

1. 启动 Celeste 和 OBS Studio。
2. 在 OBS 中开始录制（推荐 `.mkv`）。
3. 正常游玩；停止 OBS 录制后，helper 会自动生成最终视频。


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

## 游戏内 Mod Options

当前游戏内设置页只保留一个选项：

- `Output Directory` / `输出目录`：最终剪辑视频的输出根目录。留空时使用 OBS 录制目录；填写后，每张地图仍会输出到该目录下对应地图名子文件夹。

旧的 `F5/F6/F7` 输入录制/回放热键、逐帧成功通关导出、内部 helper 路径等选项已经从 Mod Options 移除，避免设置页被长文本撑偏。

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

1. 游戏内 Mod Options 的 `Output Directory` / `输出目录`（留空则跳过）；
2. 本次 OBS 实际录制文件所在目录；
3. OBS 当前配置的默认录制目录；
4. helper 工作目录（兜底）。

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

OBS helper 在每次开始新的 OBS 录制前会先清空这个文件，避免上一次录制或大厅残留事件混入本次剪辑；通过 helper API 点击“开始录制”时会在发送 `StartRecord` 前清空，若检测到外部方式启动录制，也会在新 session 初始化时清空。

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
- `selected_clips.json` / `selected_clips.log`：每次生成最终视频时实际采用的片段清单，包含房间、地图、UTC 边界、OBS 媒体时间、原因标记和无效片段诊断；
- `assembly/assembly_report.json`：ffmpeg 拼接结果与最终输出路径。

如果没有看到最终视频，优先检查最新 session 的 `assembly/assembly_report.json` 和 `clip_intervals.json`。

---

## 当前默认性能策略

- 房间事件记录和内置 OBS helper 作为自动剪辑核心路径始终启用。
- 旧的输入录制/回放、`F5/F6/F7` 热键、成功通关逐帧输入导出已经移除，不再占用 CPU/内存，也不会出现在 Mod Options。
- OBS helper 默认轮询间隔为 `1000ms`，减少后台 CPU 占用。
- 正常运行的 Info 级日志不再刷 Celeste 控制台；只保留警告、错误和手动命令输出。

---

## 房间切换剪辑规则

相邻房间片段会在实际 transition 边界处对齐：

- 上一个房间不会再把 post-roll 延伸进下一个房间；
- 下一个房间不会再把 pre-roll 回卷到上一个房间；
- `clip_intervals.json` 会记录 `adjacent_room_overlap_trimmed`，表示相邻片段已去重对齐。

当前保留区间算法：

- helper 先按事件时间得到有序事件流，然后候选区间生成只使用一个向前游标；每个事件最多被扫描常数次，候选生成复杂度为 O(n)；
- 每个 session 内，若出现 `session_start`，先保留 `session_start -> first room_enter`，原因标记为 `session_intro`；
- 随后从当前 `room_enter` 开始，先保留 `room_enter -> load_level`，原因标记为 `room_entry_load`，并记录当前关卡身份（`MapSid + Room`）；
- 对同一关卡向后贪心寻找第一个无死亡成功终点：
  - `load_level -> transition`：保留为 `final_successful_attempt`，再保留 `transition -> first room_enter`，原因标记为 `transition_to_room_enter`；
  - `load_level -> strawberry_collect`：保留为 `strawberry_collect_success`，再保留 `strawberry_collect -> first room_enter/exit`，原因标记为 `strawberry_collect_to_room_enter` 或 `strawberry_collect_to_exit`，这段尾巴中间允许出现 `death`；
  - `load_level -> level_complete`：保留为 `final_successful_attempt`，然后继续保留后续 `level_complete -> exit`；
  - `load_level -> exit`：不保留成功片段，直接跳到后续 session；
- `death` / `load_end` 会使当前 `load_level` 失效；只有后续同一 `MapSid + Room` 的新 `load_level` 才能重新成为成功片段起点；
- Respawn 的 `load_level` 不再由死亡后一帧的 `Level.Update` 猜测写入，而是在 Celeste 实际执行 `Level.LoadLevel(playerIntro=Respawn)` 并加载完玩家后写入；因此成功片段从 Respawn checkpoint 的实际加载完成点开始，不会把死亡动画当作片段开头；
- Celeste 死亡后会按 `Session.RespawnPoint` / 当前房间 spawn 点重新 `LoadLevel(Respawn)`：如果该 checkpoint 位于房间末尾，随后无死亡进入下一个房间，则 `Respawn load_level -> transition` 算成功片段；如果回到房间开头或中途 checkpoint，也同样从该 Respawn load 开始，只有后续再次 `death` 才会丢弃这次尝试；
- 若成功片段从 `load_level(playerIntro=Respawn)` 开始，该片段起点不会应用 pre-roll，会直接从人物加载完毕的时间点开始，避免死亡前画面残留；
- 不符合上述模式的事件不会生成片段，诊断会写入 `clip_intervals.json` 的 `warnings`，不会刷 Celeste 控制台；
- 事件时间上首尾相连且属于同一地图的候选区间会合并成一个 `merged_linear_interval`，不会删除 1ms 这类过短候选；这样既保留转场时间，又避免 ffmpeg 生成只有音频没有视频帧的超短 segment；
- 如果最后一个保留片段的 `post-roll` 超出 OBS 实际录制文件尾，生成区间时会夹到录制文件末尾；只有事件本身已经超出录制文件时才继续标记为 `source_file_mapping_gap`。
- 子进程资源释放：模组关闭时会终止 helper 进程树，等待 stdout/stderr 输出管道泵结束后再释放 `Process`；helper 调用 ffmpeg 时也会在等待退出后释放进程对象。
- 如果 `room_enter -> load_level` 没有和后续成功区间时间连续合并，会在 `load_level` 后应用一次 `PostRollMs` delay，原因标记为 `room_entry_load_delay`，避免进房片段刚加载完就硬切；
- 折返抑制已取消：`A -> B -> A -> B`、支路返回、同名房间再进入都按同一套线性事件规则处理。

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

当前版本已移除 `Discarded failed checkpoint attempt` 这类正常失败尝试日志；旧的成功通关逐帧输入记录功能也已删除。房间事件重置、helper 正常退出等非错误路径不再向控制台输出常规日志。

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
..\.dotnet\dotnet.exe build .\ObsClipPanel\ObsClipPanel.csproj -c Release
..\.dotnet\dotnet.exe publish .\CelesteAutoCut\CelesteAutoCut.csproj -c Release
```

当前自测覆盖：

- 首房间初始 `Transition load_level` 不截断房间开头；
- 死亡房间保留“进入房间 -> 初始 `load_level`”，成功片段从 `Respawn load_level` 精确开始而不是从 `death` 或死亡前 pre-roll 开始；
- 分支后再次进入同名房间时，每次访问独立生成片段；
- 折返抑制已取消，来回折返会按普通 `room_enter/load_level/transition` 事件生成片段；
- 支路返回 Hub 后继续前进的路线会被保留；
- `load_level -> level_complete` 和 `level_complete -> exit` 会作为最终通关片段保留；
- 一次录制中多个地图 SID 会拆成多个独立输出；
- 多地图输出路径发生冲突时会自动加地图子文件夹避免覆盖；
- 草莓房要保留“本次 `load_level` -> 拿草莓”，并确认 `strawberry_collect -> first room_enter/exit` 中间允许死亡且不会生成重复成功片段；
- 精确拼接模式的最终 concat 会重新编码，避免输出视频时间戳异常膨胀。

真实环境脚本：

```powershell
.\Scripts\real-zip-only-test.ps1
```

脚本会先进入 1A 建立房间事件 session，再启动 OBS 录制并播放 1A TAS；如果 1A TAS 复用了进入录制前已经存在的同一房间 session，验证会复用该 session，而不是误判为“录制后没有新的 room event”。验证同时检查最终视频位于地图名子文件夹，且文件名仍为录制开始本地时间；可用 `ffprobe` 对比最终视频时长和 `clip_intervals.json` 的有效片段总时长，确认成片没有因时间戳异常变长或卡住。
