using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using ObsClipSidecar;

namespace ObsClipPanel;

public sealed class PanelCoordinator : BackgroundService
{
    private const string CaptureSource = "websocket-auto";
    private readonly PanelStateStore stateStore;
    private readonly ObsWebSocketClient obsClient = new();
    private readonly SemaphoreSlim operationLock = new(1, 1);
    private readonly SemaphoreSlim sessionLock = new(1, 1);
    private readonly object gate = new();
    private PanelStatus status = new();
    private string currentSessionId = "";
    private bool recordingSessionActive;
    private bool autoAssembleInFlight;

    public PanelCoordinator(PanelStateStore stateStore)
    {
        this.stateStore = stateStore;
        obsClient.EventReceived += HandleObsEventAsync;
    }

    public PanelSnapshot GetSnapshot() => new()
    {
        Settings = stateStore.GetSettingsView(),
        Status = GetStatus()
    };

    public PanelStatus GetStatus()
    {
        lock (gate)
        {
            var snapshot = status;
            return snapshot with { Paths = string.IsNullOrWhiteSpace(currentSessionId) ? new SessionPaths() : EnsureSessionPaths(currentSessionId, stateStore.GetSettings(), snapshot.OutputPath) };
        }
    }

    public async Task<ApiResult> SaveSettingsAsync(PanelSettingsInput input)
    {
        await operationLock.WaitAsync();
        try
        {
            var settings = stateStore.Save(input);
            EnsureWorkingDirectory(settings);
            SetInfo("Settings saved.");
            return ApiResult.Ok("Settings saved.", GetSnapshot());
        }
        finally
        {
            operationLock.Release();
        }
    }

    public async Task<ApiResult> ResetSessionAsync(string? sessionName)
    {
        await operationLock.WaitAsync();
        try
        {
            var settings = stateStore.GetSettings();
            currentSessionId = SanitizeSessionName(sessionName);
            var paths = EnsureSessionPaths(currentSessionId, settings, GetStatus().OutputPath);
            if (File.Exists(paths.ObsEventsPath)) File.Delete(paths.ObsEventsPath);
            if (File.Exists(paths.SessionManifestPath)) File.Delete(paths.SessionManifestPath);
            if (File.Exists(paths.ClipIntervalsPath)) File.Delete(paths.ClipIntervalsPath);
            DeleteClipSelectionLogs(paths);
            if (Directory.Exists(paths.AssemblyDirectory)) Directory.Delete(paths.AssemblyDirectory, recursive: true);
            UpdateStatus(s => s with
            {
                CurrentSessionId = currentSessionId,
                Paths = paths,
                OutputDurationMs = 0,
                OutputPath = "",
                LastClipCount = 0,
                LastInvalidClipCount = 0,
                LastError = "",
                LastInfo = $"Session reset: {currentSessionId}"
            });
            return ApiResult.Ok($"Session reset: {currentSessionId}", GetSnapshot());
        }
        finally
        {
            operationLock.Release();
        }
    }

    public async Task<ApiResult> ConnectObsAsync()
    {
        await operationLock.WaitAsync();
        try
        {
            if (obsClient.IsConnected)
            {
                return ApiResult.Ok("OBS websocket already connected.", GetSnapshot());
            }

            var settings = stateStore.GetSettings();
            EnsureCurrentSession(settings);
            await obsClient.ConnectAsync(new Uri(settings.ObsWebSocketUrl), settings.ObsWebSocketPassword, CancellationToken.None);
            var hello = obsClient.Hello;
            UpdateStatus(s => s with
            {
                ObsConnected = obsClient.IsConnected,
                ObsIdentified = obsClient.IsIdentified,
                ObsStudioVersion = hello?.ObsStudioVersion ?? "",
                ObsWebSocketVersion = hello?.ObsWebSocketVersion ?? "",
                CurrentSessionId = currentSessionId,
                Paths = EnsureSessionPaths(currentSessionId, settings, GetStatus().OutputPath),
                LastError = "",
                LastInfo = "Connected to OBS websocket."
            });
            await AppendCapabilitiesEventAsync(settings, hello);
            await SampleRecordStatusAsync(settings, forceAppend: true, CancellationToken.None);
            return ApiResult.Ok("Connected to OBS websocket.", GetSnapshot());
        }
        catch (Exception ex)
        {
            SetError(ex.Message);
            return ApiResult.Fail(ex.Message);
        }
        finally
        {
            operationLock.Release();
        }
    }

    public async Task<ApiResult> DisconnectObsAsync()
    {
        await operationLock.WaitAsync();
        try
        {
            await obsClient.DisconnectAsync();
            UpdateStatus(s => s with { ObsConnected = false, ObsIdentified = false, RecordingActive = false, RecordingPaused = false, LastInfo = "Disconnected from OBS websocket." });
            return ApiResult.Ok("Disconnected from OBS websocket.", GetSnapshot());
        }
        finally
        {
            operationLock.Release();
        }
    }

    public Task<ApiResult> StartRecordAsync() => InvokeObsRequestAsync("StartRecord", successMessage: "Recording started.", requireNotRecording: true);
    public Task<ApiResult> StopRecordAsync() => InvokeObsRequestAsync("StopRecord", successMessage: "Recording stopped.");
    public Task<ApiResult> PauseRecordAsync() => InvokeObsRequestAsync("PauseRecord", successMessage: "Recording paused.");
    public Task<ApiResult> ResumeRecordAsync() => InvokeObsRequestAsync("ResumeRecord", successMessage: "Recording resumed.");
    public Task<ApiResult> SplitRecordFileAsync() => InvokeObsRequestAsync("SplitRecordFile", successMessage: "Recording file split triggered.");

    public async Task<ApiResult> BuildFinalVideoAsync()
    {
        await operationLock.WaitAsync();
        try
        {
            var settings = stateStore.GetSettings();
            EnsureCurrentSession(settings);
            var paths = EnsureSessionPaths(currentSessionId, settings, GetStatus().OutputPath);
            if (!File.Exists(paths.ObsEventsPath))
            {
                throw new FileNotFoundException($"OBS event log not found: {paths.ObsEventsPath}");
            }

            var obsEvents = AppendOnlyJsonl.ReadAll<ObsEvent>(paths.ObsEventsPath);
            var manifest = new ManifestBuilder().Build(obsEvents, new ManifestBuildOptions
            {
                SessionId = currentSessionId,
                CaptureSource = CaptureSource,
                ObsWebsocketRpcVersion = GetStatus().ObsWebSocketVersion,
                SupportsRecordFileChanged = true,
                SupportsOutputDurationAnchors = true
            });
            JsonFile.Write(paths.SessionManifestPath, manifest);

            var roomEventsPath = ResolveRoomEventsPath(settings.RoomEventsPath, manifest);
            if (string.IsNullOrWhiteSpace(roomEventsPath) || !File.Exists(roomEventsPath))
            {
                throw new FileNotFoundException($"Room event log not found: {settings.RoomEventsPath}");
            }

            var roomEvents = FilterRoomEventsForManifest(AppendOnlyJsonl.ReadAll<RoomEvent>(roomEventsPath), manifest, settings);
            var intervals = new IntervalGenerator().Generate(roomEvents, manifest, new IntervalGenerationOptions
            {
                PreRollMs = settings.PreRollMs,
                PostRollMs = settings.PostRollMs,
                MaxAllowedAnchorGapMs = settings.MaxAnchorGapMs,
                MaxAllowedCutErrorMs = settings.MaxCutErrorMs,
                SplitOnPause = settings.SplitOnPause,
                RequireExistingFiles = settings.RequireExistingFiles
            });
            JsonFile.Write(paths.ClipIntervalsPath, intervals);
            WriteClipSelectionLog(paths, currentSessionId, intervals);

            var recordingOutputPath = manifest.Recordings
                .SelectMany(r => r.Files)
                .Select(f => f.Path)
                .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path))
                ?? GetStatus().OutputPath;
            var recordingStartUtc = manifest.Recordings
                .Select(r => r.StartUtc)
                .FirstOrDefault(v => v.HasValue);
            var primaryMapSid = intervals.Clips
                .Select(c => c.MapSid)
                .FirstOrDefault(mapSid => !string.IsNullOrWhiteSpace(mapSid))
                ?? roomEvents.Select(e => e.MapSid).FirstOrDefault(mapSid => !string.IsNullOrWhiteSpace(mapSid));
            var finalOutputPath = stateStore.ResolveFinalOutputPath(settings.FinalOutputName, recordingOutputPath, recordingStartUtc, primaryMapSid);
            paths = EnsureSessionPaths(currentSessionId, settings, recordingOutputPath, recordingStartUtc, primaryMapSid) with { FinalOutputPath = finalOutputPath };

            var ffmpegPath = await EnsureFfmpegAsync(settings, CancellationToken.None);
            var assemblyOutputs = new AssemblyPlanner().AssembleByMap(
                intervals,
                paths.AssemblyDirectory,
                mapSid => stateStore.ResolveFinalOutputPath(settings.FinalOutputName, recordingOutputPath, recordingStartUtc, mapSid),
                new AssemblyOptions
                {
                    DryRun = false,
                    FastPreviewCopy = false,
                    MaxAllowedCutErrorMs = settings.MaxCutErrorMs,
                    FfmpegPath = ffmpegPath
                });
            var primaryAssembly = assemblyOutputs.FirstOrDefault();
            if (primaryAssembly is not null)
            {
                finalOutputPath = primaryAssembly.FinalOutputPath;
                paths = paths with { FinalOutputPath = finalOutputPath };
            }

            UpdateStatus(s => s with
            {
                CurrentSessionId = currentSessionId,
                Paths = paths,
                LastClipCount = intervals.Clips.Count,
                LastInvalidClipCount = intervals.InvalidClips.Count,
                LastSuccessfulAssembleUtc = DateTimeOffset.UtcNow,
                LastError = "",
                LastInfo = assemblyOutputs.Count == 1
                    ? $"Built final video with {intervals.Clips.Count} valid clip(s)."
                    : $"Built {assemblyOutputs.Count} map video(s) with {intervals.Clips.Count} valid clip(s)."
            });
            autoAssembleInFlight = false;

            return ApiResult.Ok("Built final video.", new
            {
                paths,
                clips = intervals.Clips.Count,
                invalidClips = intervals.InvalidClips.Count,
                mapOutputs = assemblyOutputs,
                precisionMode = primaryAssembly?.PrecisionMode ?? "none",
                finalOutput = finalOutputPath,
                warnings = assemblyOutputs.SelectMany(o => o.Warnings).Distinct().ToList()
            });
        }
        catch (Exception ex)
        {
            autoAssembleInFlight = false;
            SetError(ex.Message);
            return ApiResult.Fail(ex.Message);
        }
        finally
        {
            operationLock.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var settings = stateStore.GetSettings();
                if (!obsClient.IsConnected)
                {
                    await TryAutoConnectAsync(settings, stoppingToken);
                }
                else
                {
                    await SampleRecordStatusAsync(settings, forceAppend: false, stoppingToken);
                }

                await Task.Delay(TimeSpan.FromMilliseconds(settings.PollIntervalMs), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                SetError(ex.Message);
                await Task.Delay(1000, stoppingToken);
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await obsClient.DisconnectAsync();
        await base.StopAsync(cancellationToken);
    }

    private async Task SampleRecordStatusAsync(PanelSettings settings, bool forceAppend, CancellationToken cancellationToken)
    {
        if (!obsClient.IsConnected)
        {
            return;
        }

        var result = await obsClient.RequestAsync("GetRecordStatus", null, cancellationToken);
        if (!result.Success)
        {
            throw new InvalidOperationException($"GetRecordStatus failed: {result.Comment}");
        }

        var outputActive = GetBool(result.ResponseData, "outputActive");
        var outputPaused = GetBool(result.ResponseData, "outputPaused");
        var outputDurationMs = GetLong(result.ResponseData, "outputDuration");
        var outputBytes = GetLong(result.ResponseData, "outputBytes");
        var reportedOutputPath = GetString(result.ResponseData, "outputPath");
        var statusBefore = GetStatus();
        if (outputActive && !statusBefore.RecordingActive)
        {
            await BeginRecordingSessionAsync(settings, reportedOutputPath ?? statusBefore.OutputPath, cancellationToken);
        }

        var outputPath = reportedOutputPath ?? statusBefore.OutputPath;
        var shouldAppend = forceAppend || outputActive || statusBefore.RecordingActive != outputActive || statusBefore.RecordingPaused != outputPaused || statusBefore.OutputDurationMs != outputDurationMs || !string.Equals(statusBefore.OutputPath, outputPath, StringComparison.OrdinalIgnoreCase);
        if (shouldAppend)
        {
            EnsureCurrentSession(settings);
            var paths = EnsureSessionPaths(currentSessionId, settings, outputPath);
            await AppendOnlyJsonl.AppendAsync(paths.ObsEventsPath, new ObsEvent
            {
                EventType = "record_status_sample",
                Utc = DateTimeOffset.UtcNow,
                CaptureSource = CaptureSource,
                OutputActive = outputActive,
                OutputPaused = outputPaused,
                OutputDurationMs = outputDurationMs,
                OutputBytes = outputBytes,
                OutputPath = string.IsNullOrWhiteSpace(outputPath) ? null : outputPath
            }, cancellationToken);
        }

        UpdateStatus(s => s with
        {
            ObsConnected = obsClient.IsConnected,
            ObsIdentified = obsClient.IsIdentified,
            RecordingActive = outputActive,
            RecordingPaused = outputPaused,
            OutputDurationMs = outputDurationMs,
            OutputPath = outputPath,
            LastEventUtc = DateTimeOffset.UtcNow,
            CurrentSessionId = currentSessionId,
            Paths = EnsureSessionPaths(currentSessionId, settings, outputPath)
        });

        if (!outputActive && statusBefore.RecordingActive)
        {
            recordingSessionActive = false;
            if (settings.AutoAssembleOnStop)
            {
                QueueAutoAssemble();
            }
        }
    }

    private async Task HandleObsEventAsync(ObsEventEnvelope envelope)
    {
        try
        {
            var settings = stateStore.GetSettings();
            EnsureCurrentSession(settings);
            var paths = EnsureSessionPaths(currentSessionId, settings, GetStatus().OutputPath);
            switch (envelope.EventType)
            {
                case "RecordStateChanged":
                {
                    var active = GetBool(envelope.EventData, "outputActive");
                    var outputPath = GetString(envelope.EventData, "outputPath");
                    var outputState = GetString(envelope.EventData, "outputState");
                    var isStoppingTransition = string.Equals(outputState, "OBS_WEBSOCKET_OUTPUT_STOPPING", StringComparison.OrdinalIgnoreCase);
                    if (active && !GetStatus().RecordingActive)
                    {
                        await BeginRecordingSessionAsync(settings, outputPath, CancellationToken.None);
                        paths = EnsureSessionPaths(currentSessionId, settings, outputPath);
                    }
                    await AppendOnlyJsonl.AppendAsync(paths.ObsEventsPath, new ObsEvent
                    {
                        EventType = "record_state",
                        Utc = DateTimeOffset.UtcNow,
                        CaptureSource = CaptureSource,
                        OutputActive = active,
                        OutputPaused = GetStatus().RecordingPaused,
                        OutputDurationMs = GetStatus().OutputDurationMs,
                        OutputPath = outputPath,
                        Raw = envelope.EventData
                    });
                    UpdateStatus(s => s with
                    {
                        RecordingActive = active || (isStoppingTransition && s.RecordingActive),
                        OutputPath = outputPath ?? s.OutputPath,
                        LastEventUtc = DateTimeOffset.UtcNow,
                        CurrentSessionId = currentSessionId,
                        Paths = EnsureSessionPaths(currentSessionId, settings, outputPath ?? s.OutputPath)
                    });

                    if (!active && !isStoppingTransition && settings.AutoAssembleOnStop)
                    {
                        recordingSessionActive = false;
                        QueueAutoAssemble();
                    }
                    break;
                }
                case "RecordFileChanged":
                {
                    var newOutputPath = GetString(envelope.EventData, "newOutputPath");
                    await AppendOnlyJsonl.AppendAsync(paths.ObsEventsPath, new ObsEvent
                    {
                        EventType = "record_file_changed",
                        Utc = DateTimeOffset.UtcNow,
                        CaptureSource = CaptureSource,
                        OutputActive = true,
                        OutputPaused = GetStatus().RecordingPaused,
                        OutputDurationMs = GetStatus().OutputDurationMs,
                        OutputPath = GetStatus().OutputPath,
                        NewOutputPath = newOutputPath,
                        Raw = envelope.EventData
                    });
                    UpdateStatus(s => s with
                    {
                        OutputPath = newOutputPath ?? s.OutputPath,
                        LastEventUtc = DateTimeOffset.UtcNow,
                        CurrentSessionId = currentSessionId,
                        Paths = EnsureSessionPaths(currentSessionId, settings, newOutputPath ?? s.OutputPath)
                    });
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            SetError(ex.Message);
        }
    }

    private async Task<ApiResult> InvokeObsRequestAsync(string requestType, string successMessage, bool requireNotRecording = false)
    {
        await operationLock.WaitAsync();
        try
        {
            if (!obsClient.IsConnected)
            {
                return ApiResult.Fail("OBS websocket is not connected.");
            }

            if (requireNotRecording && (GetStatus().RecordingActive || recordingSessionActive))
            {
                return ApiResult.Fail("OBS recording is already active.");
            }

            var result = await obsClient.RequestAsync(requestType, null, CancellationToken.None);
            if (!result.Success)
            {
                SetError(string.IsNullOrWhiteSpace(result.Comment) ? $"{requestType} failed." : result.Comment);
                return ApiResult.Fail(string.IsNullOrWhiteSpace(result.Comment) ? $"{requestType} failed." : result.Comment);
            }

            SetInfo(successMessage);
            return ApiResult.Ok(successMessage, result.ResponseData);
        }
        catch (Exception ex)
        {
            SetError(ex.Message);
            return ApiResult.Fail(ex.Message);
        }
        finally
        {
            operationLock.Release();
        }
    }

    private async Task TryAutoConnectAsync(PanelSettings settings, CancellationToken cancellationToken)
    {
        try
        {
            EnsureCurrentSession(settings);
            await obsClient.ConnectAsync(new Uri(settings.ObsWebSocketUrl), settings.ObsWebSocketPassword, cancellationToken);
            var hello = obsClient.Hello;
            UpdateStatus(s => s with
            {
                ObsConnected = obsClient.IsConnected,
                ObsIdentified = obsClient.IsIdentified,
                ObsStudioVersion = hello?.ObsStudioVersion ?? "",
                ObsWebSocketVersion = hello?.ObsWebSocketVersion ?? "",
                CurrentSessionId = currentSessionId,
                Paths = EnsureSessionPaths(currentSessionId, settings, GetStatus().OutputPath),
                LastError = "",
                LastInfo = "Connected to OBS websocket automatically."
            });
            await AppendCapabilitiesEventAsync(settings, hello);
            await SampleRecordStatusAsync(settings, forceAppend: true, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            UpdateStatus(s => s with
            {
                ObsConnected = false,
                ObsIdentified = false,
                LastError = ex.Message,
                LastInfo = "Waiting for OBS websocket..."
            });
        }
    }

    private async Task BeginRecordingSessionAsync(PanelSettings settings, string? recordingOutputPath, CancellationToken cancellationToken)
    {
        await sessionLock.WaitAsync(cancellationToken);
        try
        {
            if (recordingSessionActive)
            {
                return;
            }

            currentSessionId = SanitizeSessionName(null);
            var paths = EnsureSessionPaths(currentSessionId, settings, recordingOutputPath);
            if (File.Exists(paths.ObsEventsPath)) File.Delete(paths.ObsEventsPath);
            if (File.Exists(paths.SessionManifestPath)) File.Delete(paths.SessionManifestPath);
            if (File.Exists(paths.ClipIntervalsPath)) File.Delete(paths.ClipIntervalsPath);
            DeleteClipSelectionLogs(paths);
            if (Directory.Exists(paths.AssemblyDirectory)) Directory.Delete(paths.AssemblyDirectory, recursive: true);
            Directory.CreateDirectory(paths.AssemblyDirectory);
            recordingSessionActive = true;
            autoAssembleInFlight = false;
            await AppendCapabilitiesEventAsync(settings, obsClient.Hello);
            UpdateStatus(s => s with
            {
                CurrentSessionId = currentSessionId,
                Paths = paths,
                LastClipCount = 0,
                LastInvalidClipCount = 0,
                LastError = "",
                LastInfo = $"Recording session started automatically: {currentSessionId}"
            });
        }
        finally
        {
            sessionLock.Release();
        }
    }

    private void QueueAutoAssemble()
    {
        if (autoAssembleInFlight)
        {
            return;
        }

        autoAssembleInFlight = true;
        _ = Task.Run(BuildFinalVideoAsync);
    }

    private async Task AppendCapabilitiesEventAsync(PanelSettings settings, ObsHello? hello)
    {
        EnsureCurrentSession(settings);
        var paths = EnsureSessionPaths(currentSessionId, settings, GetStatus().OutputPath);
        await AppendOnlyJsonl.AppendAsync(paths.ObsEventsPath, new ObsEvent
        {
            EventType = "capabilities",
            Utc = DateTimeOffset.UtcNow,
            CaptureSource = CaptureSource,
            Raw = new Dictionary<string, object?>
            {
                ["obsWebsocketRpcVersion"] = hello?.RpcVersion,
                ["obsWebSocketVersion"] = hello?.ObsWebSocketVersion,
                ["obsStudioVersion"] = hello?.ObsStudioVersion,
                ["supportsRecordFileChanged"] = true,
                ["supportsOutputDurationAnchors"] = true
            }
        });
    }

    private static List<RoomEvent> FilterRoomEventsForManifest(List<RoomEvent> events, SessionManifest manifest, PanelSettings settings)
    {
        var startUtc = manifest.Recordings.Select(r => r.StartUtc).Where(v => v.HasValue).Min();
        var endUtc = manifest.Recordings.Select(r => r.EndUtc).Where(v => v.HasValue).Max();
        if (!startUtc.HasValue || !endUtc.HasValue)
        {
            return events;
        }

        var padMs = Math.Max(settings.PreRollMs + settings.PostRollMs, 5_000);
        var minUtc = startUtc.Value.AddMilliseconds(-padMs);
        var maxUtc = endUtc.Value.AddMilliseconds(padMs);
        return events.Where(e => e.Utc >= minUtc && e.Utc <= maxUtc).ToList();
    }

    private static string? ResolveRoomEventsPath(string configuredPath, SessionManifest manifest)
    {
        var candidates = EnumerateRoomEventCandidates(configuredPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(File.Exists)
            .ToList();
        if (candidates.Count == 0)
        {
            return null;
        }

        if (candidates.Count == 1)
        {
            return candidates[0];
        }

        return candidates
            .Select(path => new
            {
                Path = path,
                RelevantEventCount = CountRelevantRoomEvents(path, manifest),
                LastWriteTimeUtc = File.GetLastWriteTimeUtc(path)
            })
            .OrderByDescending(candidate => candidate.RelevantEventCount)
            .ThenByDescending(candidate => candidate.LastWriteTimeUtc)
            .First()
            .Path;
    }

    private static IEnumerable<string> EnumerateRoomEventCandidates(string configuredPath)
    {
        var fullPath = Path.GetFullPath(configuredPath);
        if (Directory.Exists(fullPath))
        {
            foreach (var path in Directory.EnumerateFiles(fullPath, "room_event_*.jsonl"))
            {
                yield return path;
            }
            yield break;
        }

        var directory = Path.GetDirectoryName(fullPath) ?? ".";
        var fileName = Path.GetFileName(fullPath);
        if (fileName.IndexOf('*') >= 0 || fileName.IndexOf('?') >= 0)
        {
            if (Directory.Exists(directory))
            {
                foreach (var path in Directory.EnumerateFiles(directory, fileName))
                {
                    yield return path;
                }
            }
            yield break;
        }

        yield return fullPath;
        if (string.Equals(fileName, "room_events.jsonl", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fileName, "room_event.jsonl", StringComparison.OrdinalIgnoreCase))
        {
            if (Directory.Exists(directory))
            {
                foreach (var path in Directory.EnumerateFiles(directory, "room_event_*.jsonl"))
                {
                    yield return path;
                }
            }
        }
    }

    private static int CountRelevantRoomEvents(string path, SessionManifest manifest)
    {
        try
        {
            var events = AppendOnlyJsonl.ReadAll<RoomEvent>(path);
            var startUtc = manifest.Recordings.Select(r => r.StartUtc).Where(v => v.HasValue).Min();
            var endUtc = manifest.Recordings.Select(r => r.EndUtc).Where(v => v.HasValue).Max();
            if (!startUtc.HasValue || !endUtc.HasValue)
            {
                return events.Count;
            }

            var minUtc = startUtc.Value.AddSeconds(-5);
            var maxUtc = endUtc.Value.AddSeconds(5);
            return events.Count(e => e.Utc >= minUtc && e.Utc <= maxUtc);
        }
        catch
        {
            return -1;
        }
    }

    private static async Task<string> EnsureFfmpegAsync(PanelSettings settings, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(settings.FfmpegPath) && File.Exists(settings.FfmpegPath))
        {
            return Path.GetFullPath(settings.FfmpegPath);
        }

        foreach (var candidate in EnumerateFfmpegCandidates(settings.WorkingDirectory))
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        var ffmpegRoot = Path.Combine(settings.WorkingDirectory, "tools", "ffmpeg");
        Directory.CreateDirectory(ffmpegRoot);
        var archivePath = Path.Combine(ffmpegRoot, "ffmpeg-release-essentials.zip");
        using var http = new HttpClient();
        using var response = await http.GetAsync("https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip", cancellationToken);
        response.EnsureSuccessStatusCode();
        await using (var fs = new FileStream(archivePath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await response.Content.CopyToAsync(fs, cancellationToken);
        }

        System.IO.Compression.ZipFile.ExtractToDirectory(archivePath, ffmpegRoot, overwriteFiles: true);
        var downloaded = EnumerateFfmpegCandidates(settings.WorkingDirectory).FirstOrDefault(File.Exists);
        return downloaded ?? throw new FileNotFoundException("Downloaded ffmpeg archive but ffmpeg.exe was not found.");
    }

    private static IEnumerable<string> EnumerateFfmpegCandidates(string workingDirectory)
    {
        var explicitDir = Path.Combine(workingDirectory, "tools", "ffmpeg");
        if (Directory.Exists(explicitDir))
        {
            foreach (var path in Directory.EnumerateFiles(explicitDir, "ffmpeg.exe", SearchOption.AllDirectories))
            {
                yield return path;
            }
        }

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            yield return Path.Combine(dir, "ffmpeg.exe");
        }
    }

    private void EnsureCurrentSession(PanelSettings settings)
    {
        if (string.IsNullOrWhiteSpace(currentSessionId))
        {
            currentSessionId = SanitizeSessionName(null);
            var paths = EnsureSessionPaths(currentSessionId, settings, GetStatus().OutputPath);
            UpdateStatus(s => s with { CurrentSessionId = currentSessionId, Paths = paths });
        }
    }

    private static string SanitizeSessionName(string? requested)
    {
        var baseName = string.IsNullOrWhiteSpace(requested)
            ? DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss")
            : requested.Trim();
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            baseName = baseName.Replace(c, '_');
        }
        return string.IsNullOrWhiteSpace(baseName) ? DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss") : baseName;
    }

    private static void EnsureWorkingDirectory(PanelSettings settings)
    {
        Directory.CreateDirectory(settings.WorkingDirectory);
        Directory.CreateDirectory(Path.Combine(settings.WorkingDirectory, "sessions"));
    }

    private static void WriteClipSelectionLog(SessionPaths paths, string sessionId, ClipIntervalsDocument intervals)
    {
        Directory.CreateDirectory(paths.SessionDirectory);
        var generatedAtUtc = DateTimeOffset.UtcNow;
        var document = new
        {
            schemaVersion = 1,
            sessionId,
            generatedAtUtc,
            validClipCount = intervals.Clips.Count,
            invalidClipCount = intervals.InvalidClips.Count,
            warnings = intervals.Warnings,
            clips = intervals.Clips.Select(ToClipSelectionEntry).ToList(),
            invalidClips = intervals.InvalidClips.Select(ToClipSelectionEntry).ToList()
        };
        JsonFile.Write(Path.Combine(paths.SessionDirectory, "selected_clips.json"), document);

        var text = new StringBuilder();
        text.AppendLine($"sessionId={sessionId}");
        text.AppendLine($"generatedAtUtc={generatedAtUtc:O}");
        text.AppendLine($"validClipCount={intervals.Clips.Count}");
        text.AppendLine($"invalidClipCount={intervals.InvalidClips.Count}");
        text.AppendLine("kept:");
        foreach (var clip in intervals.Clips)
        {
            text.AppendLine(FormatClipSelectionLine(clip));
        }

        if (intervals.InvalidClips.Count > 0)
        {
            text.AppendLine("invalid:");
            foreach (var clip in intervals.InvalidClips)
            {
                text.AppendLine(FormatClipSelectionLine(clip));
            }
        }

        if (intervals.Warnings.Count > 0)
        {
            text.AppendLine("warnings:");
            foreach (var warning in intervals.Warnings)
            {
                text.AppendLine($"- {warning}");
            }
        }

        File.WriteAllText(Path.Combine(paths.SessionDirectory, "selected_clips.log"), text.ToString());
    }

    private static void DeleteClipSelectionLogs(SessionPaths paths)
    {
        var jsonPath = Path.Combine(paths.SessionDirectory, "selected_clips.json");
        var textPath = Path.Combine(paths.SessionDirectory, "selected_clips.log");
        if (File.Exists(jsonPath)) File.Delete(jsonPath);
        if (File.Exists(textPath)) File.Delete(textPath);
    }

    private static object ToClipSelectionEntry(ClipInterval clip) => new
    {
        clip.ClipId,
        clip.Status,
        clip.Room,
        clip.MapSid,
        clip.RecordingId,
        clip.StartUtc,
        clip.EndUtc,
        clip.StartOutputDurationMs,
        clip.EndOutputDurationMs,
        durationMs = clip.EndOutputDurationMs - clip.StartOutputDurationMs,
        clip.ErrorBoundMs,
        clip.PausePolicy,
        clip.Reasons,
        clip.SourceFileMapping
    };

    private static string FormatClipSelectionLine(ClipInterval clip)
        => $"- {clip.Status} {clip.ClipId} map={clip.MapSid ?? ""} room={clip.Room ?? ""} " +
           $"utc={clip.StartUtc:O}->{clip.EndUtc:O} " +
           $"mediaMs={clip.StartOutputDurationMs}->{clip.EndOutputDurationMs} " +
           $"durationMs={clip.EndOutputDurationMs - clip.StartOutputDurationMs} " +
           $"reasons={string.Join(",", clip.Reasons)}";

    private SessionPaths EnsureSessionPaths(string sessionId, PanelSettings settings, string? recordingOutputPath = null, DateTimeOffset? recordingStartUtc = null, string? mapSid = null)
    {
        EnsureWorkingDirectory(settings);
        var sessionDirectory = Path.Combine(settings.WorkingDirectory, "sessions", sessionId);
        Directory.CreateDirectory(sessionDirectory);
        var assemblyDirectory = Path.Combine(sessionDirectory, "assembly");
        Directory.CreateDirectory(assemblyDirectory);
        return new SessionPaths
        {
            SessionDirectory = sessionDirectory,
            ObsEventsPath = Path.Combine(sessionDirectory, "obs_events.jsonl"),
            SessionManifestPath = Path.Combine(sessionDirectory, "session_manifest.json"),
            ClipIntervalsPath = Path.Combine(sessionDirectory, "clip_intervals.json"),
            AssemblyDirectory = assemblyDirectory,
            FinalOutputPath = stateStore.ResolveFinalOutputPath(settings.FinalOutputName, recordingOutputPath, recordingStartUtc, mapSid)
        };
    }

    private void UpdateStatus(Func<PanelStatus, PanelStatus> updater)
    {
        lock (gate)
        {
            status = updater(status);
        }
    }

    private void SetError(string message) => UpdateStatus(s => s with { LastError = message, LastInfo = "" });
    private void SetInfo(string message) => UpdateStatus(s => s with { LastError = "", LastInfo = message });

    private static bool GetBool(IReadOnlyDictionary<string, object?> data, string key)
    {
        if (!data.TryGetValue(key, out var value) || value is null)
        {
            return false;
        }

        return value switch
        {
            bool b => b,
            JsonElement { ValueKind: JsonValueKind.True } => true,
            JsonElement { ValueKind: JsonValueKind.False } => false,
            JsonElement { ValueKind: JsonValueKind.String } json => bool.TryParse(json.GetString(), out var parsed) && parsed,
            string s => bool.TryParse(s, out var parsed) && parsed,
            _ => false
        };
    }

    private static long GetLong(IReadOnlyDictionary<string, object?> data, string key)
    {
        if (!data.TryGetValue(key, out var value) || value is null)
        {
            return 0;
        }

        return value switch
        {
            long l => l,
            int i => i,
            JsonElement { ValueKind: JsonValueKind.Number } json => json.GetInt64(),
            JsonElement { ValueKind: JsonValueKind.String } json when long.TryParse(json.GetString(), out var parsed) => parsed,
            string s when long.TryParse(s, out var parsed) => parsed,
            _ => 0
        };
    }

    private static string? GetString(IReadOnlyDictionary<string, object?> data, string key)
    {
        if (!data.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        return value switch
        {
            string s => s,
            JsonElement { ValueKind: JsonValueKind.String } json => json.GetString(),
            _ => value.ToString()
        };
    }

}
