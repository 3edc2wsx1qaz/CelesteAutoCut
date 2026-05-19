# CelesteReplay

CelesteReplay 是一个 **Celeste / Everest 模组**，用于配合 **OBS Studio** 自动生成通关精简视频。

它会在你正常录制和游玩时：

- 记录每个房间的进入 / 通过时间
- 根据 OBS 录制时间轴自动换算出每个房间对应片段
- 自动剪掉房间之间的多余部分
- 将保留片段按通关顺序拼接成一个最终视频

目标流程只有 3 步：

1. 启动 **Celeste** 和 **OBS Studio**（顺序不限）
2. 在 OBS 中开始录制（**推荐 `.mkv`**）
3. 正常游玩

游戏结束并停止录制后，模组会自动在 **OBS 默认录制目录** 中生成成片。

---

## 依赖

### 必需

- Celeste
- Everest / EverestCore
- OBS Studio（OBS 28+ 默认已内置 obs-websocket）
- ffmpeg
---

## 安装

把发布包放到：

```text
<Celeste>/Mods/CelesteReplay.zip
```

例如：

```text
D:\Steam\steamapps\common\Celeste\Mods\CelesteReplay.zip
```

首次运行时，模组会自动解压并启动内置 helper 到：

```text
<Celeste>/CelesteReplayTools/ObsClipPanel/
```

游戏退出后，helper 会自动跟随退出。



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

---

## 输出位置与命名

### 最终成片文件名

最终视频默认会按 **录制开始时间（本地时间）** 命名，例如：

```text
2026-05-18 23-18-37.mp4
```

默认命名与 OBS 原始录制常见形式对应，例如：

- 原始录制：`2026-05-18 23-18-37.mkv`
- 最终成片：`2026-05-18 23-18-37.mp4`

### 输出目录优先级

1. **本次实际录制文件所在目录**
2. **OBS 当前配置的默认录制目录**
3. helper 工作目录（兜底）

通常情况下，你会在：

```text
<OBS 默认录制目录>/2026-05-18 23-18-37.mp4
```

看到最终成片。

---

## 会话与中间产物

模组工作目录：

```text
<Celeste>/CelesteReplayReplays/obs_auto/
```

每次录制会生成一个 session：

```text
<Celeste>/CelesteReplayReplays/obs_auto/sessions/<session-id>/
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
  - 相对于 `<Celeste>/CelesteReplayTools/`
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
- 最终输出到 OBS 默认录制目录
- 最终文件名按录制开始时间命名

最新真实测试结果（2026-05-18）：

- session：`D:\Steam\steamapps\common\Celeste\CelesteReplayReplays\obs_auto\sessions\20260518-151837`
- 原始录制：`E:\obs_video\2026-05-18 23-18-37.mkv`
- 最终视频：`E:\obs_video\2026-05-18 23-18-37.mp4`
- 有效片段数：`20`
- 无效片段数：`0`

自动化测试脚本：

```powershell
CelesteReplay\Scripts\real-zip-only-test.ps1
```

该脚本现在会在测试前自动清理 `Mods` 目录中的旧 `CelesteReplay-*.zip` 副本，避免误加载旧包。

---

## 开发 / 打包

发布命令：

```powershell
.\.dotnet\dotnet.exe publish .\CelesteReplay\CelesteReplay\CelesteReplay.csproj -c Release
```

打包产物：

- 发布 zip：`CelesteReplay\artifacts\release\CelesteReplay.zip`
- 同步副本：`CelesteReplay\CelesteReplay.zip`
- publish 目录：`CelesteReplay\artifacts\publish\`

zip 中会包含：

- `everest.yaml`
- `README.md`
- `bin/CelesteReplay.dll`
- `bin/CelesteReplay.deps.json`

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
<Celeste>/CelesteReplayReplays/obs_auto/
```

### 2. 明明重新打包了，但游戏里还是旧行为

先检查 `Mods` 目录里是否残留了额外的旧包，例如：

- `CelesteReplay-20260518-225330.zip`

删除旧副本，只保留一个正式的：

```text
CelesteReplay.zip
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
- `CelesteReplay/CelesteReplay.zip`
  - 方便直接拖进 Mods 的副本

如果你只关心安装，请直接使用：

```text
CelesteReplay/artifacts/release/CelesteReplay.zip
```

---

## 2026-05-19 最新实现说明

- 游戏侧运行时现在只保留 `Player.OnDie` 事件监听。
- 原先依赖 `Level.OnEnter / OnLoadLevel / OnTransitionTo / OnComplete / OnExit` 的逻辑，改为在运行时根据当前 `Level / Session / Room` 状态变化推断。
- 同时保留了前一轮的性能优化：
  - `room_clip_session.json` 改为节流写盘；
  - 输入采集去掉每帧 `LINQ / HashSet` 临时分配。
