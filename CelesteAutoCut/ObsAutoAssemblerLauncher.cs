using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Celeste.Mod;
using Monocle;

namespace Celeste.Mod.CelesteAutoCut;

internal sealed class ObsAutoAssemblerLauncher {
    private const string Tag = "CelesteAutoCut";
    private const string HelperRelativePath = "ObsClipPanel\\ObsClipPanel.exe";
    private const string WorkingDirectoryName = "obs_auto";
    private const string RoomEventsFileName = "room_events.jsonl";
    private readonly ReplayController replayController;
    private readonly object gate = new();
    private Process? process;
    private Task? stdoutPumpTask;
    private Task? stderrPumpTask;
    private Task? startTask;
    private int launchAttemptId;
    private bool stopping;

    public ObsAutoAssemblerLauncher(ReplayController replayController) {
        this.replayController = replayController;
    }

    public void Start() {
        lock (gate) {
            if (process is { HasExited: false } || startTask is { IsCompleted: false }) {
                return;
            }

            int attemptId = ++launchAttemptId;
            startTask = Task.Run(() => StartCore(attemptId));
        }
    }

    public void Stop() {
        Process? processToStop;
        Task? stdoutPumpToStop;
        Task? stderrPumpToStop;
        lock (gate) {
            launchAttemptId++;
            processToStop = process;
            stdoutPumpToStop = stdoutPumpTask;
            stderrPumpToStop = stderrPumpTask;
            process = null;
            stdoutPumpTask = null;
            stderrPumpTask = null;
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
            WaitForPumpTasks(stdoutPumpToStop, stderrPumpToStop, 2000);
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
            string roomEventsPath = Path.Combine(replayController.ReplayDirectory, RoomEventsFileName);
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
            if (!string.IsNullOrWhiteSpace(CelesteAutoCutModule.Settings.OutputDirectory)) {
                startInfo.Environment["CELESTE_REPLAY_OUTPUT_DIRECTORY"] = CelesteAutoCutModule.Settings.OutputDirectory;
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
                stdoutPumpTask = null;
                stderrPumpTask = null;
                stopping = false;
            }

            Task stdoutPump = PumpOutputAsync(startedProcess.StandardOutput, stdoutPath);
            Task stderrPump = PumpOutputAsync(startedProcess.StandardError, stderrPath);
            lock (gate) {
                if (ReferenceEquals(process, startedProcess)) {
                    stdoutPumpTask = stdoutPump;
                    stderrPumpTask = stderrPump;
                }
            }
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
        Task? stdoutPumpToWait = null;
        Task? stderrPumpToWait = null;
        try {
            exitCode = exitedProcess.ExitCode;
        } catch {
            // best effort only
        }

        lock (gate) {
            expectedExit = stopping;
            if (ReferenceEquals(process, exitedProcess)) {
                process = null;
                stdoutPumpToWait = stdoutPumpTask;
                stderrPumpToWait = stderrPumpTask;
                stdoutPumpTask = null;
                stderrPumpTask = null;
            }
            stopping = false;
        }

        if (expectedExit) {
            return;
        }

        if (exitCode != 0) {
            Log($"OBS auto-assembler helper exited with code {exitCode?.ToString() ?? "unknown"}.", LogLevel.Warn);
        }

        _ = DisposeAfterPumpsAsync(exitedProcess, stdoutPumpToWait, stderrPumpToWait);
    }

    private static async Task PumpOutputAsync(StreamReader reader, string path) {
        try {
            await using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            await using var writer = new StreamWriter(stream) { AutoFlush = true };
            while (!reader.EndOfStream) {
                string? line = await reader.ReadLineAsync();
                if (line == null) {
                    break;
                }
                await writer.WriteLineAsync(line);
            }
        } catch (ObjectDisposedException) {
            // The helper process can be killed during shutdown while the stream is still draining.
        } catch (InvalidOperationException) {
            // StandardOutput/StandardError may already be closed by Process disposal.
        } catch (IOException) {
            // Pipe closed during normal process termination.
        }
    }

    private static async Task DisposeAfterPumpsAsync(Process process, Task? stdoutPumpTask, Task? stderrPumpTask) {
        try {
            await WaitForPumpTasksAsync(stdoutPumpTask, stderrPumpTask).ConfigureAwait(false);
        } finally {
            process.Dispose();
        }
    }

    private static async Task WaitForPumpTasksAsync(params Task?[] tasks) {
        Task[] activeTasks = tasks.Where(task => task is not null).Cast<Task>().ToArray();
        if (activeTasks.Length == 0) {
            return;
        }

        try {
            await Task.WhenAll(activeTasks).ConfigureAwait(false);
        } catch {
            // PumpOutputAsync already treats stream-closure races as normal shutdown.
        }
    }

    private static void WaitForPumpTasks(Task? stdoutPumpTask, Task? stderrPumpTask, int timeoutMs) {
        Task[] activeTasks = new[] { stdoutPumpTask, stderrPumpTask }
            .Where(task => task is not null)
            .Cast<Task>()
            .ToArray();
        if (activeTasks.Length == 0) {
            return;
        }

        try {
            Task.WaitAll(activeTasks, timeoutMs);
        } catch {
            // PumpOutputAsync already treats stream-closure races as normal shutdown.
        }
    }

    private static string ResolveHelperPath() {
        return BundledObsHelper.ResolveHelperPath(HelperRelativePath);
    }

    private string ResolveWorkingDirectory() {
        return Path.Combine(replayController.ReplayDirectory, WorkingDirectoryName);
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

