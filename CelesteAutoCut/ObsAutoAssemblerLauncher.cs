using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Celeste.Mod;
using Monocle;

namespace Celeste.Mod.CelesteAutoCut;

internal sealed class ObsAutoAssemblerLauncher {
    private const string Tag = "CelesteAutoCut";
    private readonly ReplayController replayController;
    private readonly object gate = new();
    private Process? process;
    private Task? startTask;
    private int launchAttemptId;
    private bool stopping;

    public ObsAutoAssemblerLauncher(ReplayController replayController) {
        this.replayController = replayController;
    }

    public void Start() {
        lock (gate) {
            if (!CelesteAutoCutModule.Settings.EnableObsAutoAssembler || process is { HasExited: false } || startTask is { IsCompleted: false }) {
                return;
            }

            int attemptId = ++launchAttemptId;
            startTask = Task.Run(() => StartCore(attemptId));
        }
    }

    public void Stop() {
        Process? processToStop;
        lock (gate) {
            launchAttemptId++;
            processToStop = process;
            process = null;
            stopping = processToStop is not null;
        }

        try {
            if (processToStop != null && !processToStop.HasExited) {
                processToStop.Kill(entireProcessTree: true);
                processToStop.WaitForExit(2000);
            }
        } catch (Exception e) {
            Log($"Failed to stop OBS auto-assembler helper: {e.Message}", LogLevel.Warn);
        } finally {
            processToStop?.Dispose();
        }
    }

    private void StartCore(int attemptId) {
        try {
            string helperPath = ResolveHelperPath();
            if (!File.Exists(helperPath)) {
                Log($"OBS auto-assembler helper not found: {helperPath}", LogLevel.Warn);
                return;
            }

            string workingDir = ResolveWorkingDirectory();
            Directory.CreateDirectory(workingDir);
            string roomEventsPath = Path.Combine(replayController.ReplayDirectory, SafeRelativeFileName(CelesteAutoCutModule.Settings.RoomClipEventFileName, "room_events.jsonl"));
            string stdoutPath = Path.Combine(workingDir, "obs-auto-stdout.log");
            string stderrPath = Path.Combine(workingDir, "obs-auto-stderr.log");

            var startInfo = new ProcessStartInfo {
                FileName = helperPath,
                Arguments = "--urls http://127.0.0.1:38500",
                WorkingDirectory = Path.GetDirectoryName(helperPath) ?? AppContext.BaseDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.Environment["CELESTE_REPLAY_WORKING_DIRECTORY"] = workingDir;
            startInfo.Environment["CELESTE_REPLAY_ROOM_EVENTS_PATH"] = roomEventsPath;
            startInfo.Environment["CELESTE_REPLAY_AUTO_ASSEMBLE_ON_STOP"] = "true";
            startInfo.Environment["CELESTE_REPLAY_REQUIRE_EXISTING_FILES"] = "true";
            startInfo.Environment["CELESTE_REPLAY_PARENT_PID"] = Environment.ProcessId.ToString();
            startInfo.Environment["CELESTE_REPLAY_PARENT_START_TICKS"] = Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks.ToString();
            if (!string.IsNullOrWhiteSpace(CelesteAutoCutModule.Settings.ObsAutoAssemblerFfmpegPath)) {
                startInfo.Environment["CELESTE_REPLAY_FFMPEG_PATH"] = CelesteAutoCutModule.Settings.ObsAutoAssemblerFfmpegPath;
            }

            var startedProcess = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            startedProcess.Exited += (_, _) => OnProcessExited(startedProcess);

            if (!startedProcess.Start()) {
                startedProcess.Dispose();
                Log($"OBS auto-assembler helper did not start: {helperPath}", LogLevel.Warn);
                return;
            }

            lock (gate) {
                if (attemptId != launchAttemptId) {
                    try {
                        if (!startedProcess.HasExited) {
                            startedProcess.Kill(entireProcessTree: true);
                            startedProcess.WaitForExit(2000);
                        }
                    } catch {
                        // best effort only
                    } finally {
                        startedProcess.Dispose();
                    }
                    return;
                }

                process = startedProcess;
                stopping = false;
            }

            _ = PumpOutputAsync(startedProcess.StandardOutput, stdoutPath);
            _ = PumpOutputAsync(startedProcess.StandardError, stderrPath);
            Log($"Started OBS auto-assembler helper in background: {helperPath}");
        } catch (Exception e) {
            Log($"Failed to start OBS auto-assembler helper: {e}", LogLevel.Error);
        } finally {
            lock (gate) {
                if (attemptId == launchAttemptId) {
                    startTask = null;
                }
            }
        }
    }

    private void OnProcessExited(Process exitedProcess) {
        int? exitCode = null;
        bool expectedExit = false;
        try {
            exitCode = exitedProcess.ExitCode;
        } catch {
            // best effort only
        }

        lock (gate) {
            expectedExit = stopping;
            if (ReferenceEquals(process, exitedProcess)) {
                process = null;
            }
            stopping = false;
        }

        if (expectedExit || exitCode == 0) {
            exitedProcess.Dispose();
            return;
        }

        Log($"OBS auto-assembler helper exited with code {exitCode?.ToString() ?? "unknown"}.", LogLevel.Warn);
        exitedProcess.Dispose();
    }

    private static async Task PumpOutputAsync(StreamReader reader, string path) {
        await using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        await using var writer = new StreamWriter(stream) { AutoFlush = true };
        while (!reader.EndOfStream) {
            string? line = await reader.ReadLineAsync();
            if (line == null) {
                break;
            }
            await writer.WriteLineAsync(line);
        }
    }

    private static string SafeRelativeFileName(string? fileName, string fallback) {
        if (string.IsNullOrWhiteSpace(fileName)) {
            fileName = fallback;
        }

        fileName = Path.GetFileName(fileName);
        foreach (char c in Path.GetInvalidFileNameChars()) {
            fileName = fileName.Replace(c, '_');
        }

        return string.IsNullOrWhiteSpace(fileName) ? fallback : fileName;
    }

    private static string ResolveHelperPath() {
        string relative = CelesteAutoCutModule.Settings.ObsAutoAssemblerRelativePath;
        return BundledObsHelper.ResolveHelperPath(relative);
    }

    private string ResolveWorkingDirectory() {
        string name = CelesteAutoCutModule.Settings.ObsAutoAssemblerWorkingDirectoryName;
        if (string.IsNullOrWhiteSpace(name)) {
            name = "obs_auto";
        }
        return Path.Combine(replayController.ReplayDirectory, Path.GetFileName(name));
    }

    private static void Log(string message, LogLevel level = LogLevel.Info) {
        Logger.Log(level, Tag, message);
        if (level >= LogLevel.Warn) {
            try {
                Engine.Commands?.Log($"[{Tag}] {message}");
            } catch {
                // Engine.Commands is not always safe during early module startup.
            }
        }
    }
}

