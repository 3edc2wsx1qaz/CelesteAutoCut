# CelesteAutoCut

CelesteAutoCut 是一个 **Celeste / Everest 模组**，用于配合 **OBS Studio** 自动生成通关精简视频。

它会在你正常录制和游玩时：

- 记录每个房间的进入 / 通过时间
- 根据 OBS 录制时间轴自动换算出每个房间对应片段
- 自动剪掉房间之间的多余部分
- 自动消除相邻房间切换处的重复过场片段
- 保留地图第一个房间的进图开场片段
- 如果房间中途死过，再把“第一次进入该房间 -> 落到初始 checkpoint”这段补到该房间通关片段前
- 如果当前房间没通关就结束录制，则把这段首进片段放到整个视频最后
- 将保留片段按通关顺序拼接成一个最终视频

目标流程只有 3 步：

1. 启动 **Celeste** 和 **OBS Studio**（顺序不限）
2. 在 OBS 中开始录制（**推荐 `.mkv`**）
3. 正常游玩

游戏结束并停止录制后，模组会自动在 **OBS 默认录制目录下、以地图名命名的子文件夹** 中生成成片。

---

## 依赖

### 必需

- Celeste
- Everest / EverestCore
- OBS Studio（OBS 28+ 默认已内置 obs-websocket）

### 非必需

- **CelesteTAS**：仅用于自动化测试，不是日常使用所需依赖

> 正式使用时，这个模组 **不依赖额外功能模组**；安装本 zip 后即可使用。

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

首次运行时，模组会自动解压并启动内置 helper 到：

```text
<Celeste>/CelesteAutoCutTools/ObsClipPanel/
```

游戏退出后，helper 会自动跟随退出。

### 重要

在真实环境安装 / 测试时，**不要同时保留多个 `CelesteAutoCut*.zip`**。

尤其要避免在 `Mods` 目录里同时存在：

- `CelesteAutoCut.zip`
- `CelesteAutoCut-时间戳.zip`

否则 Everest 可能加载到旧包，导致你以为新改动没有生效。

---

## 使用方法

1. 打开 Celeste
2. 打开 OBS Studio
3. 在 OBS 中点击开始录制
4. 正常游玩
5. 结束后在 OBS 中停止录制

随后模组会自动：

- 读取房间事件
- 关联录制文件与录制时间轴
- 计算每个房间的有效片段
- 调用 ffmpeg 生成最终视频

### 当前剪辑规则

- 普通通关房间：保留最终成功尝试
- 地图进入后的第一个房间：额外保留一次 `room_enter -> 初始 load_level` 的开场片段
- 房间里死过再通关：额外保留一次 `第一次 room_enter -> 初始 load_level` 的补片段，并接在前一个房间后面
- 房间未通关就停止录制 / 退出：保留该房间的 `room_enter -> 初始 load_level` 片段并放到最终视频末尾
- 相邻房间之间如果因为 pre-roll / post-roll 产生重叠，会自动裁成无缝衔接，不重复播放切房过场

---

## 输出位置与命名

### 最终成片文件名

最终视频默认会**优先沿用 OBS 原始录制文件名（去掉扩展名）**命名；如果拿不到原始录制路径，才回退到录制开始时间（本地时间）。例如：

```text
2026-05-18 23-18-37.mp4
```

默认命名会尽量与 OBS 原始录制文件保持一一对应，例如：

- 原始录制：`2026-05-18 23-18-37.mkv`
- 最终成片：`2026-05-18 23-18-37.mp4`

### 输出目录与地图文件夹

最终成片默认会放到：

```text
<OBS 录制目录>/<地图名>/<录制文件名>.mp4
```

例如：

```text
E:\obs_video\1-ForsakenCity\2026-05-19 01-25-06.mp4
```

其中：

1. `<OBS 录制目录>` 优先取**本次实际录制文件所在目录**
2. 拿不到时回退到 **OBS 当前配置的默认录制目录**
3. 再拿不到时回退到 helper 工作目录
4. `<地图名>` 默认取 `MapSid` 的最后一段，例如 `Celeste/1-ForsakenCity -> 1-ForsakenCity`

通常情况下，你会在：

```text
<OBS 默认录制目录>/<地图名>/2026-05-18 23-18-37.mp4
```

看到最终成片。

---

## 会话与中间产物

模组工作目录：

```text
<Celeste>/CelesteAutoCutReplays/obs_auto/
```

每次录制会生成一个 session：

```text
<Celeste>/CelesteAutoCutReplays/obs_auto/sessions/<session-id>/
```

常见文件：

- `session_manifest.json`：录制段、时间锚点、录制文件信息
- `clip_intervals.json`：计算后的房间片段区间
- `assembly/assembly_report.json`：最终拼接结果
- `obs_events.jsonl`：OBS 事件日志

---

## 支持的录制场景

本模组考虑了以下情况：

- 一次录制打完整张图
- 中途手动停止录制，再开始新的录制
- 同一张图分多段录制后再打完

只要这些录制段都属于同一次游戏流程，模组就会尽量把房间片段正确映射到对应录制文件，再统一拼接。

---

## ffmpeg

项目会自动使用 ffmpeg 进行裁剪和拼接。

优先级如下：

1. 使用你手动指定的 `ffmpeg.exe`
2. 使用系统中可找到的 ffmpeg
3. 使用模组工作目录下自动准备的 ffmpeg

也就是说，**普通使用者通常不需要手动安装 ffmpeg**。

---

## 配置

默认关键开关：

- `EnableRoomClipRecorder = true`
- `EnableObsAutoAssembler = true`

常用配置项：

- `ObsAutoAssemblerRelativePath`
  - 默认：`ObsClipPanel\ObsClipPanel.exe`
  - 相对于 `<Celeste>/CelesteAutoCutTools/`
- `ObsAutoAssemblerWorkingDirectoryName`
  - 默认：`obs_auto`
- `ObsAutoAssemblerFfmpegPath`
  - 可手动指定 ffmpeg 路径

如果你没有特殊需求，保持默认即可。

---

## 已验证流程

本项目已完成真实环境联调，验证过：

- Celeste + Everest 正常启动
- OBS 正常录制
- 1A TAS 自动通关测试
- 自动识别房间通过时间
- 自动裁剪并拼接成片
- 自动去掉相邻房间切换处的重复片段
- 最终输出到 OBS 默认录制目录下的地图名文件夹
- 最终文件名默认与 OBS 原始录制文件名保持一致（仅扩展名变为 `.mp4`）

最近一轮性能修复（2026-05-19）：

- `room_clip_session.json` 不再每帧完整写盘，改为“关键事件立即写 + 常驻状态节流写”
- 成功通关输入录制去掉了每帧 `LINQ / HashSet` 临时分配，降低多 mod 共存时的 GC 压力

最新真实测试结果（2026-05-19）：

- session：`D:\Steam\steamapps\common\Celeste\CelesteAutoCutReplays\obs_auto\sessions\20260518-172506`
- 原始录制：`E:\obs_video\2026-05-19 01-25-06.mkv`
- 最终视频：`E:\obs_video\1-ForsakenCity\2026-05-19 01-25-06.mp4`
- 有效片段数：`20`
- 无效片段数：`0`
- 命名验证：最终 mp4 文件名与本次 OBS 原始录制文件名完全一致（仅扩展名从 `.mkv` 变为 `.mp4`）
- 拼接验证：相邻房间片段边界已对齐为 `0ms` 重叠，不再重复播放切房过场

自动化测试脚本：

```powershell
CelesteReplay\Scripts\real-zip-only-test.ps1
```

该脚本现在会在测试前自动清理 `Mods` 目录中的旧 `CelesteAutoCut-*.zip` 副本，避免误加载旧包；并且在 `assembly_report.json` 未及时出现在原 session 路径时，允许以“最终 mp4 已生成”作为完成证据，避免产物已出但脚本继续长时间等待。

另外，脚本现在会优先恢复上次中断测试遗留的 `blacklist.txt` 备份，并在结束时恢复原有 `CelesteAutoCut` 文件夹模组 / zip / blacklist，避免把本机 mod 环境留在临时测试状态。

本轮代码回归验证（2026-05-19）：

- `ObsClipSidecar` self-test：`16/16 passed`
- `dotnet build .\\CelesteReplay\\CelesteAutoCut.sln -c Release`：通过
- `CelesteReplay\\Scripts\\real-zip-only-test.ps1`：PowerShell 语法解析通过

---

## 开发 / 打包

发布命令：

```powershell
.\.dotnet\dotnet.exe publish .\CelesteReplay\CelesteAutoCut\CelesteAutoCut.csproj -c Release
```

打包产物：

- 发布 zip：`CelesteReplay\artifacts\release\CelesteAutoCut.zip`
- 同步副本：`CelesteReplay\CelesteAutoCut.zip`
- publish 目录：`CelesteReplay\artifacts\publish\`

zip 中会包含：

- `everest.yaml`
- `README.md`
- `bin/CelesteAutoCut.dll`
- `bin/CelesteAutoCut.deps.json`

helper 可执行文件不会以散文件形式放在 zip 根目录，而是以内嵌 payload 的方式随 DLL 一起分发，由模组在运行时自动释放。

---

## 故障排查

### 1. 录制后没有生成最终视频

优先检查：

- OBS 是否真的开始了录制
- OBS 是否启用了 websocket
- 录制格式是否正常（推荐 `.mkv`）
- 是否生成了 session 目录

可查看：

```text
<Celeste>/CelesteAutoCutReplays/obs_auto/
```

### 2. 明明重新打包了，但游戏里还是旧行为

先检查 `Mods` 目录里是否残留了额外的旧包，例如：

- `CelesteAutoCut-20260518-225330.zip`

删除旧副本，只保留一个正式的：

```text
CelesteAutoCut.zip
```

### 3. 游戏关闭后 helper 还在

当前版本已实现父进程跟踪，正常情况下游戏退出后 helper 会自动退出。

### 4. 想保留完整原始录像

模组不会删除 OBS 原始录制文件；它只会额外生成精简后的最终成片。

---

## 适合长期使用的目录说明

- `CelesteReplay/artifacts/release/`
  - 正式发布包目录
- `CelesteReplay/artifacts/publish/`
  - `dotnet publish` 输出
- `CelesteReplay/CelesteAutoCut.zip`
  - 方便直接拖进 Mods 的副本

如果你只关心安装，请直接使用：

```text
CelesteReplay/artifacts/release/CelesteAutoCut.zip
```



