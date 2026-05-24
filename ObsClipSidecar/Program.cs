namespace ObsClipSidecar;

public static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0 || args[0] is "--help" or "-h" or "help")
            {
                PrintUsage();
                return args.Length == 0 ? 1 : 0;
            }

            return args[0] switch
            {
                "self-test" or "--self-test" => SelfTests.Run(),
                "generate-intervals" => GenerateIntervals(args.Skip(1).ToArray()),
                "build-session-manifest" => BuildSessionManifest(args.Skip(1).ToArray()),
                "assemble-dry-run" => Assemble(args.Skip(1).ToArray(), dryRun: true),
                "assemble" => Assemble(args.Skip(1).ToArray(), dryRun: false),
                "append-room-event" => AppendRoomEvent(args.Skip(1).ToArray()),
                "append-obs-event" => AppendObsEvent(args.Skip(1).ToArray()),
                _ => Unknown(args[0])
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static int BuildSessionManifest(string[] args)
    {
        var values = ParseArgs(args);
        var obsEventsPath = Required(values, "obs-events");
        var outputPath = Required(values, "out");
        var options = new ManifestBuildOptions
        {
            SessionId = values.GetValueOrDefault("session-id") ?? "",
            CaptureSource = values.GetValueOrDefault("capture-source") ?? "websocket",
            ObsWebsocketRpcVersion = values.GetValueOrDefault("obs-rpc-version"),
            SupportsRecordFileChanged = GetBool(values, "supports-record-file-changed", true),
            SupportsOutputDurationAnchors = GetBool(values, "supports-output-duration-anchors", true)
        };

        var events = AppendOnlyJsonl.ReadAll<ObsEvent>(obsEventsPath);
        var manifest = new ManifestBuilder().Build(events, options);
        JsonFile.Write(outputPath, manifest);
        Console.WriteLine($"Built session manifest with {manifest.Recordings.Count} recording(s) -> {outputPath}");
        return 0;
    }

    private static int GenerateIntervals(string[] args)
    {
        var values = ParseArgs(args);
        var roomPath = Required(values, "room-events");
        var manifestPath = Required(values, "session-manifest");
        var outputPath = Required(values, "out");
        var options = new IntervalGenerationOptions
        {
            PreRollMs = GetLong(values, "pre-roll-ms", 250),
            PostRollMs = GetLong(values, "post-roll-ms", 500),
            MaxAllowedAnchorGapMs = GetLong(values, "max-anchor-gap-ms", 2_000),
            MaxAllowedCutErrorMs = GetLong(values, "max-cut-error-ms", 100),
            ClipIntensity = ClipIntensityModes.Normalize(values.GetValueOrDefault("clip-intensity")),
            SplitOnPause = GetBool(values, "split-on-pause", true),
            RequireExistingFiles = GetBool(values, "require-existing-files", false)
        };

        var events = AppendOnlyJsonl.ReadAll<RoomEvent>(roomPath);
        var manifest = JsonFile.Read<SessionManifest>(manifestPath);
        var doc = new IntervalGenerator().Generate(events, manifest, options);
        JsonFile.Write(outputPath, doc);
        Console.WriteLine($"Generated {doc.Clips.Count} valid clips and {doc.InvalidClips.Count} invalid clips -> {outputPath}");
        return doc.InvalidClips.Count == 0 ? 0 : 2;
    }

    private static int Assemble(string[] args, bool dryRun)
    {
        var values = ParseArgs(args);
        var intervalsPath = Required(values, "clip-intervals");
        var outDir = Required(values, "out-dir");
        var doc = JsonFile.Read<ClipIntervalsDocument>(intervalsPath);
        var options = new AssemblyOptions
        {
            DryRun = dryRun,
            FastPreviewCopy = GetBool(values, "fast-preview-copy", false),
            MaxAllowedCutErrorMs = GetLong(values, "max-cut-error-ms", 100),
            FinalOutputPath = values.GetValueOrDefault("final-output") ?? "final_useful_run.mp4",
            FfmpegPath = values.GetValueOrDefault("ffmpeg"),
            OverwriteOutput = GetBool(values, "overwrite-output", true),
            VideoCodec = values.GetValueOrDefault("video-codec") ?? "libx264",
            AudioCodec = values.GetValueOrDefault("audio-codec") ?? "aac",
            Preset = values.GetValueOrDefault("preset") ?? "veryfast",
            Crf = (int)GetLong(values, "crf", 18)
        };

        var planner = new AssemblyPlanner();
        var plan = dryRun
            ? planner.WriteDryRunArtifacts(doc, outDir, options)
            : planner.Assemble(doc, outDir, options);
        Console.WriteLine($"{(dryRun ? "Wrote dry-run" : "Assembled")} artifacts -> {outDir}");
        Console.WriteLine($"precisionMode={plan.PrecisionMode} finalOutput={plan.FinalOutputPath}");
        return 0;
    }

    private static int AppendRoomEvent(string[] args)
    {
        var values = ParseArgs(args);
        var path = Required(values, "path");
        var eventType = Required(values, "event-type");
        var roomEvent = new RoomEvent
        {
            EventType = eventType,
            Utc = GetUtc(values),
            Room = values.GetValueOrDefault("room"),
            NextRoom = values.GetValueOrDefault("next-room"),
            MapSid = values.GetValueOrDefault("map-sid"),
            SessionId = values.GetValueOrDefault("session-id")
        };
        AppendOnlyJsonl.AppendAsync(path, roomEvent).GetAwaiter().GetResult();
        Console.WriteLine($"Appended room event {eventType} -> {path}");
        return 0;
    }

    private static int AppendObsEvent(string[] args)
    {
        var values = ParseArgs(args);
        var path = Required(values, "path");
        var obsEvent = new ObsEvent
        {
            EventType = Required(values, "event-type"),
            Utc = GetUtc(values),
            CaptureSource = values.GetValueOrDefault("capture-source") ?? "websocket",
            OutputActive = values.TryGetValue("output-active", out var active) ? bool.Parse(active) : null,
            OutputPaused = values.TryGetValue("output-paused", out var paused) ? bool.Parse(paused) : null,
            OutputDurationMs = values.TryGetValue("output-duration-ms", out var duration) ? long.Parse(duration) : null,
            OutputPath = values.GetValueOrDefault("output-path"),
            NewOutputPath = values.GetValueOrDefault("new-output-path")
        };
        AppendOnlyJsonl.AppendAsync(path, obsEvent).GetAwaiter().GetResult();
        Console.WriteLine($"Appended OBS event {obsEvent.EventType} -> {path}");
        return 0;
    }

    private static Dictionary<string, string> ParseArgs(string[] args)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            var token = args[i];
            if (!token.StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Unexpected argument '{token}'. Use --name value.");
            }

            var name = token[2..];
            if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                result[name] = "true";
            }
            else
            {
                result[name] = args[++i];
            }
        }

        return result;
    }

    private static string Required(Dictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException($"Missing required --{key} argument.");

    private static long GetLong(Dictionary<string, string> values, string key, long defaultValue) =>
        values.TryGetValue(key, out var value) ? long.Parse(value) : defaultValue;

    private static bool GetBool(Dictionary<string, string> values, string key, bool defaultValue) =>
        values.TryGetValue(key, out var value) ? bool.Parse(value) : defaultValue;

    private static DateTimeOffset GetUtc(Dictionary<string, string> values) =>
        values.TryGetValue("utc", out var value) ? DateTimeOffset.Parse(value).ToUniversalTime() : DateTimeOffset.UtcNow;

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"Unknown command '{command}'.");
        PrintUsage();
        return 1;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("ObsClipSidecar (.NET 8, BCL-only)");
        Console.WriteLine("Commands:");
        Console.WriteLine("  self-test");
        Console.WriteLine("  build-session-manifest --obs-events obs_events.jsonl --out session_manifest.json [--session-id run1]");
        Console.WriteLine("  generate-intervals --room-events room_events.jsonl --session-manifest session_manifest.json --out clip_intervals.json [--max-anchor-gap-ms 2000] [--clip-intensity high|low]");
        Console.WriteLine("  assemble-dry-run --clip-intervals clip_intervals.json --out-dir out [--fast-preview-copy false] [--final-output final_useful_run.mp4]");
        Console.WriteLine("  assemble --clip-intervals clip_intervals.json --out-dir out --ffmpeg C:\\tools\\ffmpeg.exe [--final-output final_useful_run.mp4]");
        Console.WriteLine("  append-room-event --path room_events.jsonl --event-type room_start --room a [--utc 2026-05-18T00:00:00Z]");
        Console.WriteLine("  append-obs-event --path obs_events.jsonl --event-type record_status_sample --output-duration-ms 1234 --output-active true --output-paused false");
    }
}
