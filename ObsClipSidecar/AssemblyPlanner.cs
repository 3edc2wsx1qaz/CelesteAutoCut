using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace ObsClipSidecar;

public sealed class AssemblyPlanner
{
    public AssemblyPlan CreatePlan(ClipIntervalsDocument intervals, AssemblyOptions? options = null)
    {
        options ??= new AssemblyOptions();
        var kept = GetKeptClips(intervals);
        var warnings = new List<string>();
        warnings.AddRange(intervals.Warnings);
        if (intervals.InvalidClips.Count > 0)
        {
            warnings.Add($"invalid_clips_excluded:{intervals.InvalidClips.Count}");
        }

        var needsPrecise = ShouldUsePreciseMode(kept, options);
        var precisionMode = options.FastPreviewCopy && !needsPrecise
            ? "fast_preview_stream_copy"
            : "precise_reencode";

        if (options.FastPreviewCopy && needsPrecise)
        {
            warnings.Add("fast_preview_copy_rejected_precise_cut_required");
        }

        var sliceList = FlattenSlices(kept);
        var previewConcat = BuildFfconcatFromSlices(sliceList);
        var preciseConcat = BuildPreciseSegmentConcat(sliceList.Count);
        var command = precisionMode == "fast_preview_stream_copy"
            ? $"ffmpeg -safe 0 -f concat -i clip_plan.ffconcat -c copy {Quote(options.FinalOutputPath)}"
            : $"ffmpeg # precise segment-reencode + final concat reencode pipeline required -> {Quote(options.FinalOutputPath)}";

        return new AssemblyPlan
        {
            PrecisionMode = precisionMode,
            DryRun = options.DryRun,
            FastPreviewCopy = options.FastPreviewCopy,
            FfmpegPath = options.FfmpegPath,
            FinalOutputPath = options.FinalOutputPath,
            FfconcatText = previewConcat,
            SegmentConcatText = preciseConcat,
            FfmpegCommand = command,
            Warnings = warnings.Distinct().ToList(),
            IncludedClipIds = kept.Select(c => c.ClipId).ToList()
        };
    }

    public AssemblyPlan WriteDryRunArtifacts(ClipIntervalsDocument intervals, string outputDirectory, AssemblyOptions? options = null)
    {
        outputDirectory = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(outputDirectory);
        var plan = CreatePlan(intervals, options);
        File.WriteAllText(Path.Combine(outputDirectory, "clip_plan.ffconcat"), plan.FfconcatText, new UTF8Encoding(false));
        if (!string.IsNullOrWhiteSpace(plan.SegmentConcatText))
        {
            File.WriteAllText(Path.Combine(outputDirectory, "precise_segments.ffconcat"), plan.SegmentConcatText, new UTF8Encoding(false));
        }

        JsonFile.Write(Path.Combine(outputDirectory, "assembly_report.json"), plan);
        return plan;
    }

    public AssemblyPlan Assemble(ClipIntervalsDocument intervals, string outputDirectory, AssemblyOptions options)
    {
        outputDirectory = Path.GetFullPath(outputDirectory);
        var plan = WriteDryRunArtifacts(intervals, outputDirectory, options);
        if (options.DryRun)
        {
            return plan;
        }

        var kept = GetKeptClips(intervals);
        if (kept.Count == 0)
        {
            throw new InvalidOperationException("No valid clips available for assembly.");
        }

        var ffmpegPath = ResolveFfmpegPath(options.FfmpegPath);
        var finalOutputPath = Path.IsPathRooted(options.FinalOutputPath)
            ? options.FinalOutputPath
            : Path.Combine(outputDirectory, options.FinalOutputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(finalOutputPath)) ?? outputDirectory);

        if (plan.PrecisionMode == "fast_preview_stream_copy")
        {
            RunFfmpeg(ffmpegPath, $"-y -safe 0 -f concat -i {Quote(Path.Combine(outputDirectory, "clip_plan.ffconcat"))} -c copy {Quote(finalOutputPath)}", outputDirectory);
        }
        else
        {
            ExecutePrecisePipeline(kept, outputDirectory, finalOutputPath, ffmpegPath, options);
        }

        var executedPlan = plan with
        {
            Executed = true,
            FfmpegPath = ffmpegPath,
            FinalOutputPath = finalOutputPath
        };
        JsonFile.Write(Path.Combine(outputDirectory, "assembly_report.json"), executedPlan);
        return executedPlan;
    }

    public List<MapAssemblyOutput> AssembleByMap(
        ClipIntervalsDocument intervals,
        string outputDirectory,
        Func<string?, string> finalOutputPathResolver,
        AssemblyOptions options)
    {
        outputDirectory = Path.GetFullPath(outputDirectory);
        var groups = GroupKeptClipsByMap(intervals);
        if (groups.Count == 0)
        {
            throw new InvalidOperationException("No valid clips available for assembly.");
        }

        var targets = ResolveMapOutputTargets(groups, finalOutputPathResolver, outputDirectory);

        var outputs = new List<MapAssemblyOutput>();
        for (var index = 0; index < targets.Count; index++)
        {
            var target = targets[index];
            var group = target.Group;
            var finalOutputPath = target.FinalOutputPath;
            var groupIntervals = intervals with
            {
                Clips = group.Clips,
                InvalidClips = intervals.InvalidClips
                    .Where(c => string.Equals(c.MapSid ?? string.Empty, group.MapSid ?? string.Empty, StringComparison.Ordinal))
                    .ToList()
            };
            var groupOutputDirectory = index == 0
                ? outputDirectory
                : Path.Combine(outputDirectory, SafeArtifactName(group.MapSid, index));
            var plan = options.DryRun
                ? WriteDryRunArtifacts(groupIntervals, groupOutputDirectory, options with { FinalOutputPath = finalOutputPath })
                : Assemble(groupIntervals, groupOutputDirectory, options with { FinalOutputPath = finalOutputPath });
            var warnings = plan.Warnings;
            if (target.CollisionAdjusted)
            {
                warnings = warnings
                    .Concat(["final_output_collision_avoided_by_map_folder"])
                    .Distinct()
                    .ToList();
            }
            outputs.Add(new MapAssemblyOutput
            {
                MapSid = group.MapSid,
                FinalOutputPath = plan.FinalOutputPath,
                AssemblyDirectory = groupOutputDirectory,
                ClipCount = group.Clips.Count,
                PrecisionMode = plan.PrecisionMode,
                Warnings = warnings
            });
        }

        JsonFile.Write(Path.Combine(outputDirectory, "map_outputs.json"), outputs);
        return outputs;
    }

    private static void ExecutePrecisePipeline(List<ClipInterval> kept, string outputDirectory, string finalOutputPath, string ffmpegPath, AssemblyOptions options)
    {
        var sliceList = FlattenSlices(kept);
        var segmentsDir = Path.Combine(outputDirectory, "precise_segments");
        Directory.CreateDirectory(segmentsDir);

        var segmentPaths = new List<string>();
        for (var index = 0; index < sliceList.Count; index++)
        {
            var slice = sliceList[index];
            var segmentPath = Path.Combine(segmentsDir, $"segment_{index:D4}.mp4");
            segmentPaths.Add(segmentPath);
            var durationSeconds = Math.Max(0.001, (slice.SourceEndDurationMs - slice.SourceStartDurationMs) / 1000.0);
            var arguments =
                $"{OverwriteFlag(options)} -ss {FormatSeconds(slice.SourceStartDurationMs)} -i {Quote(slice.SourcePath)} -t {durationSeconds.ToString("0.###", CultureInfo.InvariantCulture)} " +
                $"-map 0:v:0 -map 0:a? -c:v {options.VideoCodec} -preset {options.Preset} -crf {options.Crf} -pix_fmt yuv420p -c:a {options.AudioCodec} -movflags +faststart {Quote(segmentPath)}";
            RunFfmpeg(ffmpegPath, arguments, outputDirectory);
        }

        var concatText = BuildSegmentConcat(segmentPaths);
        var concatPath = Path.Combine(outputDirectory, "precise_segments.ffconcat");
        File.WriteAllText(concatPath, concatText, new UTF8Encoding(false));
        RunFfmpeg(
            ffmpegPath,
            $"{OverwriteFlag(options)} -safe 0 -f concat -i {Quote(concatPath)} " +
            $"-map 0:v:0 -map 0:a? -c:v {options.VideoCodec} -preset {options.Preset} -crf {options.Crf} -pix_fmt yuv420p -c:a {options.AudioCodec} -movflags +faststart {Quote(finalOutputPath)}",
            outputDirectory);
    }

    private static string ResolveFfmpegPath(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var full = Path.GetFullPath(configuredPath);
            if (File.Exists(full))
            {
                return full;
            }

            throw new FileNotFoundException($"ffmpeg not found at configured path '{configuredPath}'.", configuredPath);
        }

        var envPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in envPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = Path.Combine(dir, "ffmpeg.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("ffmpeg.exe was not found. Pass --ffmpeg <path> to assemble.");
    }

    private static void RunFfmpeg(string ffmpegPath, string arguments, string workingDirectory)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = ffmpegPath,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException($"Failed to start ffmpeg: {ffmpegPath}");

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        Task.WaitAll(stdoutTask, stderrTask);
        var stdout = stdoutTask.Result;
        var stderr = stderrTask.Result;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"ffmpeg failed with exit code {process.ExitCode}.{Environment.NewLine}{stdout}{Environment.NewLine}{stderr}".Trim());
        }
    }

    private static bool ShouldUsePreciseMode(List<ClipInterval> kept, AssemblyOptions options) =>
        !options.FastPreviewCopy ||
        kept.Any(c =>
            c.ErrorBoundMs > options.MaxAllowedCutErrorMs ||
            c.PausePolicy == "supported_interval_subtraction" ||
            c.SourceFileMapping.Count != 1 ||
            c.Reasons.Any(r => r.Contains("death", StringComparison.OrdinalIgnoreCase) || r.Contains("pause", StringComparison.OrdinalIgnoreCase)));

    private static List<ClipInterval> GetKeptClips(ClipIntervalsDocument intervals) =>
        intervals.Clips.Where(c => c.IsValid && c.SourceFileMapping.Count > 0).ToList();

    private static List<MapClipGroup> GroupKeptClipsByMap(ClipIntervalsDocument intervals)
    {
        var groups = new List<MapClipGroup>();
        foreach (var clip in GetKeptClips(intervals))
        {
            var mapSid = string.IsNullOrWhiteSpace(clip.MapSid) ? null : clip.MapSid;
            var group = groups.FirstOrDefault(g => string.Equals(g.MapSid ?? string.Empty, mapSid ?? string.Empty, StringComparison.Ordinal));
            if (group is null)
            {
                group = new MapClipGroup(mapSid);
                groups.Add(group);
            }

            group.Clips.Add(clip);
        }

        return groups;
    }

    private static List<MapOutputTarget> ResolveMapOutputTargets(
        List<MapClipGroup> groups,
        Func<string?, string> finalOutputPathResolver,
        string fallbackDirectory)
    {
        var targets = groups
            .Select((group, index) => new MapOutputTarget(group, index, finalOutputPathResolver(group.MapSid)))
            .ToList();
        var collisions = targets
            .GroupBy(t => FullPathKey(t.FinalOutputPath), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .SelectMany(g => g)
            .ToHashSet();

        foreach (var target in targets.Where(collisions.Contains))
        {
            var fullPath = Path.GetFullPath(target.FinalOutputPath);
            var directory = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrWhiteSpace(directory))
            {
                directory = fallbackDirectory;
            }

            target.FinalOutputPath = Path.Combine(directory, SafeArtifactName(target.Group.MapSid, target.Index), Path.GetFileName(fullPath));
            target.CollisionAdjusted = true;
        }

        return targets;
    }

    private static string FullPathKey(string path) => Path.GetFullPath(path);

    private static List<ClipFileSlice> FlattenSlices(List<ClipInterval> clips) =>
        clips.SelectMany(c => c.SourceFileMapping).ToList();

    private static string BuildFfconcatFromSlices(List<ClipFileSlice> slices)
    {
        var sb = new StringBuilder();
        sb.AppendLine("ffconcat version 1.0");
        foreach (var slice in slices)
        {
            sb.Append("file ").AppendLine(QuoteForFfconcat(slice.SourcePath));
            sb.Append("inpoint ").AppendLine(slice.InpointSeconds.ToString("0.###", CultureInfo.InvariantCulture));
            sb.Append("outpoint ").AppendLine(slice.OutpointSeconds.ToString("0.###", CultureInfo.InvariantCulture));
        }

        return sb.ToString();
    }

    private static string BuildPreciseSegmentConcat(int segmentCount)
    {
        var segmentPaths = Enumerable.Range(0, segmentCount)
            .Select(i => $"precise_segments/segment_{i:D4}.mp4")
            .ToList();
        return BuildSegmentConcat(segmentPaths);
    }

    private static string BuildSegmentConcat(IEnumerable<string> paths)
    {
        var sb = new StringBuilder();
        sb.AppendLine("ffconcat version 1.0");
        foreach (var path in paths)
        {
            sb.Append("file ").AppendLine(QuoteForFfconcat(path));
        }

        return sb.ToString();
    }

    private static string OverwriteFlag(AssemblyOptions options) => options.OverwriteOutput ? "-y" : "-n";

    private static string FormatSeconds(long durationMs) => (durationMs / 1000.0).ToString("0.###", CultureInfo.InvariantCulture);

    private static string Quote(string path) => '"' + path.Replace("\"", "\\\"") + '"';

    private static string QuoteForFfconcat(string path) => "'" + path.Replace("'", "'\\''") + "'";

    private static string SafeArtifactName(string? value, int index)
    {
        var name = string.IsNullOrWhiteSpace(value) ? "UnknownMap" : value.Trim().Replace('\\', '_').Replace('/', '_');
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }

        name = name.Trim().TrimEnd('.');
        return string.IsNullOrWhiteSpace(name) ? $"map_{index:D2}" : $"{index:D2}_{name}";
    }

    private sealed record MapClipGroup(string? MapSid)
    {
        public List<ClipInterval> Clips { get; } = [];
    }

    private sealed class MapOutputTarget(MapClipGroup group, int index, string finalOutputPath)
    {
        public MapClipGroup Group { get; } = group;
        public int Index { get; } = index;
        public string FinalOutputPath { get; set; } = finalOutputPath;
        public bool CollisionAdjusted { get; set; }
    }
}
