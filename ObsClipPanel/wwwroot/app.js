const ids = [
  'obsUrl','obsPassword','workingDirectory','roomEventsPath','outputDirectory','ffmpegPath','finalOutputName',
  'pollIntervalMs','preRollMs','postRollMs','maxAnchorGapMs','maxCutErrorMs','clipIntensity',
  'splitOnPause','requireExistingFiles','autoAssembleOnStop','logOutputEnabled'
];

const el = Object.fromEntries(ids.map(id => [id, document.getElementById(id)]));
const statusBox = document.getElementById('statusBox');
const messageBox = document.getElementById('messageBox');
const badges = document.getElementById('badges');
const artifactPaths = document.getElementById('artifactPaths');

async function api(path, method='GET', body) {
  const res = await fetch(path, {
    method,
    headers: body ? { 'Content-Type': 'application/json' } : {},
    body: body ? JSON.stringify(body) : undefined
  });
  return await res.json();
}

function fillSettings(settings) {
  el.obsUrl.value = settings.obsWebSocketUrl || '';
  el.obsPassword.value = settings.obsWebSocketPassword || '';
  el.workingDirectory.value = settings.workingDirectory || '';
  el.roomEventsPath.value = settings.roomEventsPath || '';
  el.outputDirectory.value = settings.outputDirectory || '';
  el.ffmpegPath.value = settings.ffmpegPath || '';
  el.finalOutputName.value = settings.finalOutputName || '';
  el.pollIntervalMs.value = settings.pollIntervalMs ?? 500;
  el.preRollMs.value = settings.preRollMs ?? 250;
  el.postRollMs.value = settings.postRollMs ?? 500;
  el.maxAnchorGapMs.value = settings.maxAnchorGapMs ?? 2000;
  el.maxCutErrorMs.value = settings.maxCutErrorMs ?? 100;
  el.clipIntensity.value = settings.clipIntensity || 'low';
  el.splitOnPause.checked = !!settings.splitOnPause;
  el.requireExistingFiles.checked = !!settings.requireExistingFiles;
  el.autoAssembleOnStop.checked = !!settings.autoAssembleOnStop;
  el.logOutputEnabled.checked = !!settings.logOutputEnabled;
}

function readSettings() {
  return {
    obsWebSocketUrl: el.obsUrl.value,
    obsWebSocketPassword: el.obsPassword.value,
    workingDirectory: el.workingDirectory.value,
    roomEventsPath: el.roomEventsPath.value,
    outputDirectory: el.outputDirectory.value,
    ffmpegPath: el.ffmpegPath.value,
    finalOutputName: el.finalOutputName.value,
    pollIntervalMs: Number(el.pollIntervalMs.value || 500),
    preRollMs: Number(el.preRollMs.value || 250),
    postRollMs: Number(el.postRollMs.value || 500),
    maxAnchorGapMs: Number(el.maxAnchorGapMs.value || 2000),
    maxCutErrorMs: Number(el.maxCutErrorMs.value || 100),
    clipIntensity: el.clipIntensity.value || 'low',
    splitOnPause: el.splitOnPause.checked,
    requireExistingFiles: el.requireExistingFiles.checked,
    autoAssembleOnStop: el.autoAssembleOnStop.checked,
    logOutputEnabled: el.logOutputEnabled.checked
  };
}

function renderSnapshot(snapshot) {
  if (!snapshot) return;
  fillSettings(snapshot.settings);
  const s = snapshot.status;
  statusBox.textContent = JSON.stringify(s, null, 2);
  artifactPaths.textContent = [
    `sessionDir: ${s.paths?.sessionDirectory || ''}`,
    `obsEvents: ${s.paths?.obsEventsPath || ''}`,
    `manifest: ${s.paths?.sessionManifestPath || ''}`,
    `intervals: ${s.paths?.clipIntervalsPath || ''}`,
    `assemblyDir: ${s.paths?.assemblyDirectory || ''}`,
    `finalOutput: ${s.paths?.finalOutputPath || ''}`,
  ].join('\n');
  badges.innerHTML = '';
  addBadge(s.obsConnected ? 'OBS 已连接' : 'OBS 未连接', s.obsConnected ? 'ok' : 'err');
  addBadge(s.recordingActive ? '录制中' : '未录制', s.recordingActive ? 'ok' : 'warn');
  addBadge(s.recordingPaused ? '已暂停' : '未暂停', s.recordingPaused ? 'warn' : 'ok');
  addBadge(`session: ${s.currentSessionId || '-'}`, '');
  addBadge(`duration: ${s.outputDurationMs || 0} ms`, '');
  if (s.lastError) addBadge(`错误: ${s.lastError}`, 'err');
  if (s.lastInfo) addBadge(s.lastInfo, 'ok');
}

function addBadge(text, cls) {
  const span = document.createElement('span');
  span.className = `badge ${cls}`.trim();
  span.textContent = text;
  badges.appendChild(span);
}

async function refresh() {
  const snapshot = await api('/api/status');
  renderSnapshot(snapshot);
}

async function invoke(path, method='POST', body) {
  const result = await api(path, method, body);
  messageBox.textContent = JSON.stringify(result, null, 2);
  await refresh();
}

document.getElementById('saveSettings').onclick = () => invoke('/api/settings', 'POST', readSettings());
document.getElementById('resetSession').onclick = () => {
  const name = prompt('输入会话名（可留空自动生成 UTC 时间戳）', '');
  invoke('/api/session/reset', 'POST', { sessionName: name });
};
document.getElementById('connectObs').onclick = () => invoke('/api/obs/connect');
document.getElementById('disconnectObs').onclick = () => invoke('/api/obs/disconnect');
document.getElementById('startRecord').onclick = () => invoke('/api/obs/start-record');
document.getElementById('stopRecord').onclick = () => invoke('/api/obs/stop-record');
document.getElementById('pauseRecord').onclick = () => invoke('/api/obs/pause-record');
document.getElementById('resumeRecord').onclick = () => invoke('/api/obs/resume-record');
document.getElementById('splitRecordFile').onclick = () => invoke('/api/obs/split-record-file');
document.getElementById('buildFinal').onclick = () => invoke('/api/pipeline/build-final');

refresh();
setInterval(refresh, 1500);
