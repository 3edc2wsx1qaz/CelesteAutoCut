using System.Text.Json;
using ObsClipSidecar;

namespace ObsClipPanel;

public sealed class PanelStateStore
{
    private const string SettingsFileName = "panel_settings.json";
    private const string DefaultFinalOutputNameTemplate = "{recording_start_local}.mp4";
    private const string LegacyFinalOutputName = "final_useful_run.mp4";
    private readonly object gate = new();
    private PanelSettings settings;

    public PanelStateStore()
    {
        settings = ApplyEnvironmentOverrides(Load());
        Directory.CreateDirectory(settings.WorkingDirectory);
    }

    public PanelSettings GetSettings()
    {
        lock (gate)
        {
            return settings;
        }
    }

    public PanelSettingsView GetSettingsView()
    {
        var current = GetSettings();
        return new PanelSettingsView
        {
            ObsWebSocketUrl = current.ObsWebSocketUrl,
            ObsWebSocketPassword = current.ObsWebSocketPassword,
            WorkingDirectory = current.WorkingDirectory,
            RoomEventsPath = current.RoomEventsPath,
            OutputDirectory = current.OutputDirectory,
            FfmpegPath = current.FfmpegPath,
            PollIntervalMs = current.PollIntervalMs,
            PreRollMs = current.PreRollMs,
            PostRollMs = current.PostRollMs,
            MaxAnchorGapMs = current.MaxAnchorGapMs,
            MaxCutErrorMs = current.MaxCutErrorMs,
            SplitOnPause = current.SplitOnPause,
            RequireExistingFiles = current.RequireExistingFiles,
            AutoAssembleOnStop = current.AutoAssembleOnStop,
            FinalOutputName = current.FinalOutputName
        };
    }

    public string ResolveFinalOutputPath(string finalOutputName, string? recordingOutputPath = null, DateTimeOffset? recordingStartUtc = null, string? mapSid = null)
    {
        var fileNameOrPath = ExpandFinalOutputName(
            string.IsNullOrWhiteSpace(finalOutputName) ? DefaultFinalOutputNameTemplate : finalOutputName.Trim(),
            recordingOutputPath,
            recordingStartUtc);
        var preferredOutputDirectory = ResolvePreferredOutputDirectory(recordingOutputPath);
        var resolvedPath = Path.IsPathRooted(fileNameOrPath)
            ? Path.GetFullPath(fileNameOrPath)
            : Path.Combine(ResolveMapOutputDirectory(preferredOutputDirectory, mapSid), fileNameOrPath);

        if (!string.IsNullOrWhiteSpace(recordingOutputPath) &&
            string.Equals(Path.GetFullPath(recordingOutputPath), resolvedPath, StringComparison.OrdinalIgnoreCase))
        {
            var directory = Path.GetDirectoryName(resolvedPath) ?? preferredOutputDirectory;
            var stem = Path.GetFileNameWithoutExtension(resolvedPath);
            resolvedPath = Path.Combine(directory, $"{stem} - clipped.mp4");
        }

        return resolvedPath;
    }

    public string ResolvePreferredOutputDirectory(string? recordingOutputPath = null)
    {
        var configuredOutputDirectory = GetSettings().OutputDirectory;
        if (!string.IsNullOrWhiteSpace(configuredOutputDirectory))
        {
            return configuredOutputDirectory;
        }

        if (!string.IsNullOrWhiteSpace(recordingOutputPath))
        {
            var recordingDirectory = Path.GetDirectoryName(Path.GetFullPath(recordingOutputPath));
            if (!string.IsNullOrWhiteSpace(recordingDirectory))
            {
                return recordingDirectory;
            }
        }

        var obsDefaultDirectory = TryGetObsDefaultRecordingDirectory();
        if (!string.IsNullOrWhiteSpace(obsDefaultDirectory))
        {
            return obsDefaultDirectory;
        }

        return GetSettings().WorkingDirectory;
    }

    public PanelSettings Save(PanelSettingsInput input)
    {
        var next = new PanelSettings
        {
            ObsWebSocketUrl = string.IsNullOrWhiteSpace(input.ObsWebSocketUrl) ? "ws://127.0.0.1:4455" : input.ObsWebSocketUrl.Trim(),
            ObsWebSocketPassword = input.ObsWebSocketPassword ?? "",
            WorkingDirectory = NormalizeRequiredPath(input.WorkingDirectory, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CelesteAutoCutObsPanel")),
            RoomEventsPath = NormalizeRequiredPath(input.RoomEventsPath, @"D:\Steam\steamapps\common\Celeste\CelesteAutoCutReplays\room_events.jsonl"),
            OutputDirectory = NormalizeOptionalPath(input.OutputDirectory),
            FfmpegPath = string.IsNullOrWhiteSpace(input.FfmpegPath) ? "" : Path.GetFullPath(input.FfmpegPath.Trim()),
            PollIntervalMs = Math.Max(1000, input.PollIntervalMs),
            PreRollMs = Math.Max(0, input.PreRollMs),
            PostRollMs = Math.Max(0, input.PostRollMs),
            MaxAnchorGapMs = Math.Max(100, input.MaxAnchorGapMs),
            MaxCutErrorMs = Math.Max(0, input.MaxCutErrorMs),
            SplitOnPause = input.SplitOnPause,
            RequireExistingFiles = input.RequireExistingFiles,
            AutoAssembleOnStop = input.AutoAssembleOnStop,
            FinalOutputName = NormalizeConfiguredFinalOutputName(input.FinalOutputName)
        };

        Directory.CreateDirectory(next.WorkingDirectory);
        JsonFile.Write(Path.Combine(next.WorkingDirectory, SettingsFileName), next);
        lock (gate)
        {
            settings = next;
        }
        return next;
    }

    private static PanelSettings Load()
    {
        var roots = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CelesteAutoCutObsPanel"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CelesteReplayObsPanel"),
            Path.Combine(AppContext.BaseDirectory, "panel-state")
        };

        foreach (var root in roots)
        {
            var path = Path.Combine(root, SettingsFileName);
            if (File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<PanelSettings>(File.ReadAllText(path), JsonDefaults.Options) ?? new PanelSettings();
                return MigrateLegacyDefaults(loaded);
            }
        }

        return MigrateLegacyDefaults(LoadFromObsDefaults());
    }

    private static string NormalizeRequiredPath(string candidate, string fallback)
    {
        var value = string.IsNullOrWhiteSpace(candidate) ? fallback : candidate.Trim();
        return Path.GetFullPath(value);
    }

    private static string NormalizeOptionalPath(string? candidate)
    {
        return string.IsNullOrWhiteSpace(candidate) ? "" : Path.GetFullPath(candidate.Trim());
    }

    private static PanelSettings LoadFromObsDefaults()
    {
        var defaults = new PanelSettings();
        var obsConfigPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "obs-studio",
            "plugin_config",
            "obs-websocket",
            "config.json");

        if (!File.Exists(obsConfigPath))
        {
            return defaults;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(obsConfigPath));
            var root = doc.RootElement;
            var port = root.TryGetProperty("server_port", out var portElement) && portElement.TryGetInt32(out var parsedPort)
                ? parsedPort
                : 4455;
            var password = root.TryGetProperty("server_password", out var passwordElement)
                ? passwordElement.GetString() ?? ""
                : "";

            return defaults with
            {
                ObsWebSocketUrl = $"ws://127.0.0.1:{port}",
                ObsWebSocketPassword = password
            };
        }
        catch
        {
            return defaults;
        }
    }

    private static string? TryGetObsDefaultRecordingDirectory()
    {
        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var obsRoot = Path.Combine(appData, "obs-studio");
            var globalIniPath = Path.Combine(obsRoot, "global.ini");
            var profileRoot = Path.Combine(obsRoot, "basic", "profiles");

            string? profileDirName = null;
            if (File.Exists(globalIniPath))
            {
                var globalIni = ParseIni(globalIniPath);
                profileDirName = ReadIni(globalIni, "Basic", "ProfileDir")
                    ?? ReadIni(globalIni, "Basic", "Profile");
            }

            var candidateProfileDirs = new List<string>();
            if (!string.IsNullOrWhiteSpace(profileDirName))
            {
                candidateProfileDirs.Add(Path.Combine(profileRoot, profileDirName));
            }

            if (Directory.Exists(profileRoot))
            {
                candidateProfileDirs.AddRange(Directory.EnumerateDirectories(profileRoot));
            }

            foreach (var profileDir in candidateProfileDirs.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var basicIniPath = Path.Combine(profileDir, "basic.ini");
                if (!File.Exists(basicIniPath))
                {
                    continue;
                }

                var ini = ParseIni(basicIniPath);
                var mode = ReadIni(ini, "Output", "Mode");
                string? candidate = null;

                if (string.Equals(mode, "Simple", StringComparison.OrdinalIgnoreCase))
                {
                    candidate = ReadIni(ini, "SimpleOutput", "FilePath");
                }
                else if (string.Equals(mode, "Advanced", StringComparison.OrdinalIgnoreCase))
                {
                    var recType = ReadIni(ini, "AdvOut", "RecType");
                    candidate = string.Equals(recType, "FFmpeg", StringComparison.OrdinalIgnoreCase)
                        ? ReadIni(ini, "AdvOut", "FFFilePath")
                        : ReadIni(ini, "AdvOut", "RecFilePath");
                    candidate ??= ReadIni(ini, "AdvOut", "FFFilePath");
                }

                if (!string.IsNullOrWhiteSpace(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
        }
        catch
        {
            // fall through to caller fallback
        }

        return null;
    }

    private static Dictionary<string, Dictionary<string, string>> ParseIni(string path)
    {
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var currentSection = string.Empty;

        foreach (var rawLine in File.ReadLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#'))
            {
                continue;
            }

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                currentSection = line[1..^1].Trim();
                if (!result.ContainsKey(currentSection))
                {
                    result[currentSection] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                }
                continue;
            }

            var separatorIndex = line.IndexOf('=');
            if (separatorIndex <= 0)
            {
                continue;
            }

            var key = line[..separatorIndex].Trim();
            var value = line[(separatorIndex + 1)..].Trim();
            if (!result.TryGetValue(currentSection, out var section))
            {
                section = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                result[currentSection] = section;
            }

            section[key] = value;
        }

        return result;
    }

    private static string? ReadIni(Dictionary<string, Dictionary<string, string>> ini, string section, string key)
    {
        return ini.TryGetValue(section, out var values) && values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;
    }

    private static PanelSettings ApplyEnvironmentOverrides(PanelSettings settings)
    {
        string? Read(string name) => Environment.GetEnvironmentVariable(name);
        bool? ReadBool(string name)
            => bool.TryParse(Read(name), out var value) ? value : null;
        long? ReadLong(string name)
            => long.TryParse(Read(name), out var value) ? value : null;

        return settings with
        {
            ObsWebSocketUrl = Read("CELESTE_REPLAY_OBS_WS_URL") ?? settings.ObsWebSocketUrl,
            ObsWebSocketPassword = Read("CELESTE_REPLAY_OBS_WS_PASSWORD") ?? settings.ObsWebSocketPassword,
            WorkingDirectory = NormalizeRequiredPath(Read("CELESTE_REPLAY_WORKING_DIRECTORY") ?? settings.WorkingDirectory, settings.WorkingDirectory),
            RoomEventsPath = NormalizeRequiredPath(Read("CELESTE_REPLAY_ROOM_EVENTS_PATH") ?? settings.RoomEventsPath, settings.RoomEventsPath),
            OutputDirectory = NormalizeOptionalPath(Read("CELESTE_REPLAY_OUTPUT_DIRECTORY") ?? settings.OutputDirectory),
            FfmpegPath = string.IsNullOrWhiteSpace(Read("CELESTE_REPLAY_FFMPEG_PATH")) ? settings.FfmpegPath : Path.GetFullPath(Read("CELESTE_REPLAY_FFMPEG_PATH")!),
            PollIntervalMs = Math.Max(1000, ReadLong("CELESTE_REPLAY_POLL_MS") ?? settings.PollIntervalMs),
            PreRollMs = ReadLong("CELESTE_REPLAY_PRE_ROLL_MS") ?? settings.PreRollMs,
            PostRollMs = ReadLong("CELESTE_REPLAY_POST_ROLL_MS") ?? settings.PostRollMs,
            MaxAnchorGapMs = ReadLong("CELESTE_REPLAY_MAX_ANCHOR_GAP_MS") ?? settings.MaxAnchorGapMs,
            MaxCutErrorMs = ReadLong("CELESTE_REPLAY_MAX_CUT_ERROR_MS") ?? settings.MaxCutErrorMs,
            SplitOnPause = ReadBool("CELESTE_REPLAY_SPLIT_ON_PAUSE") ?? settings.SplitOnPause,
            RequireExistingFiles = ReadBool("CELESTE_REPLAY_REQUIRE_EXISTING_FILES") ?? settings.RequireExistingFiles,
            AutoAssembleOnStop = ReadBool("CELESTE_REPLAY_AUTO_ASSEMBLE_ON_STOP") ?? settings.AutoAssembleOnStop,
            FinalOutputName = NormalizeConfiguredFinalOutputName(Read("CELESTE_REPLAY_FINAL_OUTPUT_NAME") ?? settings.FinalOutputName)
        };
    }

    private static PanelSettings MigrateLegacyDefaults(PanelSettings settings)
    {
        return settings with
        {
            FinalOutputName = MigrateLegacyFinalOutputName(settings.FinalOutputName)
        };
    }

    private static string NormalizeConfiguredFinalOutputName(string? finalOutputName)
    {
        return string.IsNullOrWhiteSpace(finalOutputName)
            ? DefaultFinalOutputNameTemplate
            : finalOutputName.Trim();
    }

    private static string MigrateLegacyFinalOutputName(string? finalOutputName)
    {
        var normalized = NormalizeConfiguredFinalOutputName(finalOutputName);
        return string.Equals(normalized, LegacyFinalOutputName, StringComparison.OrdinalIgnoreCase)
            ? DefaultFinalOutputNameTemplate
            : normalized;
    }

    private static string ExpandFinalOutputName(string finalOutputName, string? recordingOutputPath, DateTimeOffset? recordingStartUtc)
    {
        var template = NormalizeConfiguredFinalOutputName(finalOutputName);
        var baseName = GetRecordingStartBaseName(recordingOutputPath, recordingStartUtc);
        return template.Replace("{recording_start_local}", baseName, StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveMapOutputDirectory(string preferredOutputDirectory, string? mapSid)
        => Path.Combine(preferredOutputDirectory, GetMapFolderName(mapSid));

    private static string GetMapFolderName(string? mapSid)
    {
        if (string.IsNullOrWhiteSpace(mapSid))
        {
            return "UnknownMap";
        }

        var normalized = mapSid.Trim().Replace('\\', '/');
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var leaf = segments.Length > 0 ? segments[^1] : normalized;
        var sanitized = SanitizeFileName(leaf).Trim().TrimEnd('.');
        return string.IsNullOrWhiteSpace(sanitized) ? "UnknownMap" : sanitized;
    }

    private static string GetRecordingStartBaseName(string? recordingOutputPath, DateTimeOffset? recordingStartUtc)
    {
        if (recordingStartUtc.HasValue)
        {
            return recordingStartUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH-mm-ss");
        }

        if (!string.IsNullOrWhiteSpace(recordingOutputPath))
        {
            var stem = Path.GetFileNameWithoutExtension(recordingOutputPath.Trim());
            if (!string.IsNullOrWhiteSpace(stem))
            {
                return SanitizeFileName(stem);
            }
        }

        return DateTimeOffset.Now.ToString("yyyy-MM-dd HH-mm-ss");
    }

    private static string SanitizeFileName(string value)
    {
        var sanitized = value;
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            sanitized = sanitized.Replace(c, '_');
        }

        return sanitized;
    }
}
