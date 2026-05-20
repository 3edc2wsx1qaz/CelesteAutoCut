# CelesteAutoCut ObsClipPanel

本地服务 + OBS Custom Browser Dock 面板。

## 启动

```powershell
powershell -ExecutionPolicy Bypass -File .\start-obs-clip-panel.ps1
```

默认地址：

```text
http://127.0.0.1:38500
```

## 在 OBS 里打开

OBS -> `View` -> `Docks` -> `Custom Browser Docks...`

新增一个 Dock：

- Name: `Celeste Auto Cut`
- URL: `http://127.0.0.1:38500`

## 先确认 OBS websocket 已开启

OBS -> `Tools` -> `WebSocket Server Settings`

- 勾选 `Enable WebSocket server`
- 端口默认 `4455`
- 如果启用了密码，把同样的密码填进面板

## 功能

- 连接 OBS websocket
- 开始/停止/暂停/继续录制
- 触发录制分段文件
- 自动写 `obs_events.jsonl`
- 读取 Celeste 的 `room_event_*.jsonl`
- 生成 `session_manifest.json`
- 生成 `clip_intervals.json`
- 调 ffmpeg 产出最终视频

## 当前默认路径

- room events: `D:\Steam\steamapps\common\Celeste\CelesteAutoCutReplays\room_event_*.jsonl`
- OBS websocket: `ws://127.0.0.1:4455`

这些都可以在面板里修改并保存。

