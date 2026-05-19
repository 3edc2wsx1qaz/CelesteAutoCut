using System.Text;

namespace ObsClipSidecar;

public static class SelfTests
{
    public static int Run()
    {
        var tests = new (string Name, Action Body)[]
        {
            ("multiple recordings select matching recording", MultipleRecordings),
            ("cross recording clips are invalidated", CrossRecordingInvalid),
            ("split file maps clip into multiple slices", SplitFile),
            ("pause overlap splits clip and marks pause policy", PauseOverlap),
            ("adjacent rooms trim overlapping transition footage", AdjacentRoomOverlapTrim),
            ("load level becomes attempt reset after death", LoadLevelResetsAttempt),
            ("first room keeps room-entry intro before clear", FirstRoomKeepsIntroBeforeClear),
            ("death room keeps room-entry intro before successful attempt", DeathRoomKeepsIntroBeforeClear),
            ("unfinished room keeps room-entry intro at end", UnfinishedRoomKeepsIntroAtEnd),
            ("missing recording files invalidates clip", MissingRecordingFileMapping),
            ("invalid anchor gaps are reported", InvalidAnchorGap),
            ("manifest builder reconstructs recording and split files", ManifestBuilderReconstructsRecording),
            ("manifest builder honors record_file_changed newOutputPath", ManifestBuilderUsesNewOutputPath),
            ("manifest builder waits for terminal stop path after stopping transition", ManifestBuilderWaitsForTerminalStopPath),
            ("json reader is case insensitive for mod output", CaseInsensitiveJsonRead),
            ("assembly plan defaults to precise mode", AssemblyDefaultsToPrecise)
        };

        var failures = 0;
        foreach (var test in tests)
        {
            try
            {
                test.Body();
                Console.WriteLine($"PASS {test.Name}");
            }
            catch (Exception ex)
            {
                failures++;
                Console.Error.WriteLine($"FAIL {test.Name}: {ex.Message}");
            }
        }

        Console.WriteLine($"Self-test result: {tests.Length - failures}/{tests.Length} passed");
        return failures == 0 ? 0 : 1;
    }

    private static void MultipleRecordings()
    {
        var doc = Generate(StandardRoomEvents(BaseUtc.AddMinutes(10)), new SessionManifest
        {
            SessionId = "s",
            Recordings =
            [
                Recording("r1", BaseUtc, "old.mp4", 0, 10_000),
                Recording("r2", BaseUtc.AddMinutes(10), "new.mp4", 0, 10_000)
            ]
        });

        Assert(doc.Clips.Count == 1, "expected one valid clip");
        Assert(doc.Clips[0].RecordingId == "r2", "clip should bind to second recording");
        Assert(doc.Clips[0].SourceFileMapping[0].SourcePath == "new.mp4", "clip should use matching recording file");
    }

    private static void CrossRecordingInvalid()
    {
        var events = new List<RoomEvent>
        {
            new() { EventType = "room_start", Utc = BaseUtc.AddSeconds(9), Room = "a", MapSid = "map" },
            new() { EventType = "transition", Utc = BaseUtc.AddSeconds(11), Room = "a", NextRoom = "b", MapSid = "map" }
        };
        var doc = Generate(events, new SessionManifest
        {
            SessionId = "s",
            Recordings =
            [
                Recording("r1", BaseUtc, "old.mp4", 0, 10_000),
                Recording("r2", BaseUtc.AddSeconds(10), "new.mp4", 0, 10_000)
            ]
        });

        Assert(doc.Clips.Count == 0, "cross recording clip should not be kept");
        Assert(doc.InvalidClips.Any(c => c.Reasons.Contains("cross_recording_clip")), "cross recording reason missing");
    }

    private static void SplitFile()
    {
        var manifest = new SessionManifest
        {
            SessionId = "s",
            Recordings =
            [
                Recording("r", BaseUtc, [new RecordingFileManifest { Path = "part1.mp4", StartDurationMs = 0, EndDurationMs = 4_000 }, new RecordingFileManifest { Path = "part2.mp4", StartDurationMs = 4_000, EndDurationMs = 10_000 }])
            ]
        };
        var doc = Generate(StandardRoomEvents(BaseUtc.AddSeconds(3), endOffsetSeconds: 3), manifest);
        Assert(doc.Clips.Single().SourceFileMapping.Count == 2, "clip should be split across OBS files");
        Assert(doc.Clips.Single().SourceFileMapping[1].SourceStartDurationMs == 0, "second split file should use file-relative inpoint");
    }

    private static void PauseOverlap()
    {
        var recording = Recording("r", BaseUtc, "run.mp4", 0, 10_000);
        recording = recording with { Pauses = [new PauseInterval { StartDurationMs = 3_500, EndDurationMs = 4_500 }] };
        var doc = Generate(StandardRoomEvents(BaseUtc.AddSeconds(2), endOffsetSeconds: 4), new SessionManifest { SessionId = "s", Recordings = [recording] });
        Assert(doc.Clips.Count == 2, "pause should split into two valid sub-clips");
        Assert(doc.Clips.All(c => c.PausePolicy == "supported_interval_subtraction"), "pause policy should be explicit");
    }

    private static void AdjacentRoomOverlapTrim()
    {
        var start = BaseUtc.AddSeconds(2);
        var events = new List<RoomEvent>
        {
            new() { EventType = "room_enter", Utc = start, Room = "a", MapSid = "Celeste/1-ForsakenCity" },
            new() { EventType = "transition", Utc = start.AddSeconds(2), Room = "a", NextRoom = "b", MapSid = "Celeste/1-ForsakenCity" },
            new() { EventType = "room_enter", Utc = start.AddSeconds(2), Room = "b", MapSid = "Celeste/1-ForsakenCity" },
            new() { EventType = "transition", Utc = start.AddSeconds(4), Room = "b", NextRoom = "c", MapSid = "Celeste/1-ForsakenCity" }
        };

        var doc = Generate(events, new SessionManifest { SessionId = "s", Recordings = [Recording("r", BaseUtc, "run.mp4", 0, 10_000)] }, new IntervalGenerationOptions
        {
            PreRollMs = 250,
            PostRollMs = 500,
            MaxAllowedAnchorGapMs = 1_500
        });

        Assert(doc.Clips.Count == 2, "expected both rooms to remain valid");
        Assert(doc.Clips[0].EndOutputDurationMs == doc.Clips[1].StartOutputDurationMs, "adjacent room clips should be seam-joined without overlap");
        Assert(doc.Clips[1].Reasons.Contains("adjacent_room_overlap_trimmed"), "second room should record overlap trim reason");
    }

    private static void LoadLevelResetsAttempt()
    {
        var start = BaseUtc.AddSeconds(2);
        var events = new List<RoomEvent>
        {
            new() { EventType = "room_enter", Utc = start, Room = "a", MapSid = "map" },
            new() { EventType = "death", Utc = start.AddMilliseconds(750), Room = "a", MapSid = "map" },
            new() { EventType = "load_level", Utc = start.AddSeconds(1), Room = "a", MapSid = "map" },
            new() { EventType = "transition", Utc = start.AddSeconds(2), Room = "a", NextRoom = "b", MapSid = "map" }
        };
        var doc = Generate(events, new SessionManifest { SessionId = "s", Recordings = [Recording("r", BaseUtc, "run.mp4", 0, 10_000)] }, new IntervalGenerationOptions
        {
            PreRollMs = 0,
            PostRollMs = 0,
            MaxAllowedAnchorGapMs = 1_500
        });

        var successfulAttempt = doc.Clips.Single(c => c.Reasons.Contains("final_successful_attempt"));
        Assert(successfulAttempt.StartUtc == start.AddSeconds(1), "attempt should restart from load_level, not death");
    }

    private static void FirstRoomKeepsIntroBeforeClear()
    {
        var start = BaseUtc.AddSeconds(2);
        var introLoad = start.AddMilliseconds(400);
        var clear = start.AddSeconds(2);
        var events = new List<RoomEvent>
        {
            new() { EventType = "room_enter", Utc = start, Room = "a", MapSid = "map" },
            new() { EventType = "load_level", Utc = introLoad, Room = "a", MapSid = "map", Notes = new Dictionary<string, object?> { ["playerIntro"] = "Transition" } },
            new() { EventType = "transition", Utc = clear, Room = "a", NextRoom = "b", MapSid = "map" }
        };

        var doc = Generate(events, new SessionManifest { SessionId = "s", Recordings = [Recording("r", BaseUtc, "run.mp4", 0, 10_000)] }, new IntervalGenerationOptions
        {
            PreRollMs = 0,
            PostRollMs = 0,
            MaxAllowedAnchorGapMs = 1_500
        });

        Assert(doc.Clips.Count == 2, "expected intro clip plus clear clip for first room");
        Assert(doc.Clips[0].Reasons.Contains("room_entry_intro_first_room"), "first clip should be first-room intro");
        Assert(doc.Clips[0].StartUtc == start && doc.Clips[0].EndUtc == introLoad, "first-room intro boundaries mismatch");
        Assert(doc.Clips[1].Reasons.Contains("final_successful_attempt"), "second clip should be the successful attempt");
        Assert(doc.Clips[1].StartUtc == introLoad && doc.Clips[1].EndUtc == clear, "successful attempt should start at first load_level");
    }

    private static void DeathRoomKeepsIntroBeforeClear()
    {
        var start = BaseUtc.AddSeconds(2);
        var roomBEnter = start.AddSeconds(2);
        var introLoad = roomBEnter.AddMilliseconds(300);
        var respawnLoad = roomBEnter.AddSeconds(1);
        var clear = roomBEnter.AddSeconds(2);
        var events = new List<RoomEvent>
        {
            new() { EventType = "room_enter", Utc = start, Room = "a", MapSid = "map" },
            new() { EventType = "load_level", Utc = start.AddMilliseconds(250), Room = "a", MapSid = "map", Notes = new Dictionary<string, object?> { ["playerIntro"] = "Transition" } },
            new() { EventType = "transition", Utc = roomBEnter, Room = "a", NextRoom = "b", MapSid = "map" },
            new() { EventType = "room_enter", Utc = roomBEnter, Room = "b", MapSid = "map" },
            new() { EventType = "load_level", Utc = introLoad, Room = "b", MapSid = "map", Notes = new Dictionary<string, object?> { ["playerIntro"] = "Transition" } },
            new() { EventType = "death", Utc = roomBEnter.AddMilliseconds(700), Room = "b", MapSid = "map" },
            new() { EventType = "load_level", Utc = respawnLoad, Room = "b", MapSid = "map", Notes = new Dictionary<string, object?> { ["playerIntro"] = "Respawn" } },
            new() { EventType = "transition", Utc = clear, Room = "b", NextRoom = "c", MapSid = "map" }
        };

        var doc = Generate(events, new SessionManifest { SessionId = "s", Recordings = [Recording("r", BaseUtc, "run.mp4", 0, 10_000)] }, new IntervalGenerationOptions
        {
            PreRollMs = 0,
            PostRollMs = 0,
            MaxAllowedAnchorGapMs = 1_500
        });

        var roomBClips = doc.Clips.Where(c => c.Room == "b").OrderBy(c => c.StartUtc).ToList();
        Assert(roomBClips.Count == 2, "expected intro clip plus successful attempt after death in room b");
        Assert(roomBClips[0].Reasons.Contains("room_entry_intro_before_clear"), "death room should preserve intro before clear");
        Assert(roomBClips[0].StartUtc == roomBEnter && roomBClips[0].EndUtc == introLoad, "death-room intro boundaries mismatch");
        Assert(roomBClips[1].Reasons.Contains("final_successful_attempt"), "second room-b clip should be the successful attempt");
        Assert(roomBClips[1].StartUtc == respawnLoad && roomBClips[1].EndUtc == clear, "successful attempt should restart from respawn load_level");
    }

    private static void UnfinishedRoomKeepsIntroAtEnd()
    {
        var start = BaseUtc.AddSeconds(2);
        var introLoad = start.AddMilliseconds(400);
        var events = new List<RoomEvent>
        {
            new() { EventType = "room_enter", Utc = start, Room = "a", MapSid = "map" },
            new() { EventType = "load_level", Utc = introLoad, Room = "a", MapSid = "map", Notes = new Dictionary<string, object?> { ["playerIntro"] = "Transition" } },
            new() { EventType = "exit", Utc = start.AddSeconds(1), Room = "a", MapSid = "map" }
        };

        var doc = Generate(events, new SessionManifest { SessionId = "s", Recordings = [Recording("r", BaseUtc, "run.mp4", 0, 10_000)] }, new IntervalGenerationOptions
        {
            PreRollMs = 0,
            PostRollMs = 0,
            MaxAllowedAnchorGapMs = 1_500
        });

        Assert(doc.Clips.Count == 1, "expected only trailing intro clip for unfinished room");
        Assert(doc.Clips[0].Reasons.Contains("room_entry_intro_at_end"), "unfinished room should emit trailing intro clip");
        Assert(doc.Clips[0].StartUtc == start && doc.Clips[0].EndUtc == introLoad, "unfinished intro boundaries mismatch");
    }

    private static void MissingRecordingFileMapping()
    {
        var recording = Recording("r", BaseUtc, [new RecordingFileManifest { Path = "too-short.mp4", StartDurationMs = 0, EndDurationMs = 2_000 }]);
        var doc = Generate(StandardRoomEvents(BaseUtc.AddSeconds(1), endOffsetSeconds: 3), new SessionManifest { SessionId = "s", Recordings = [recording] });
        Assert(doc.Clips.Count == 0, "clip should not be valid with file mapping gap");
        Assert(doc.InvalidClips.Any(c => c.Reasons.Contains("source_file_mapping_gap")), "mapping gap reason missing");
    }

    private static void InvalidAnchorGap()
    {
        var recording = new RecordingManifest
        {
            RecordingId = "r",
            Files = [new RecordingFileManifest { Path = "run.mp4", StartDurationMs = 0, EndDurationMs = 20_000 }],
            Anchors =
            [
                new ObsAnchor { Utc = BaseUtc, OutputDurationMs = 0 },
                new ObsAnchor { Utc = BaseUtc.AddSeconds(20), OutputDurationMs = 20_000 }
            ]
        };
        var doc = Generate(StandardRoomEvents(BaseUtc.AddSeconds(5), endOffsetSeconds: 5), new SessionManifest { SessionId = "s", Recordings = [recording] }, new IntervalGenerationOptions { MaxAllowedAnchorGapMs = 1_000 });
        Assert(doc.Clips.Count == 0, "wide anchor gap should invalidate");
        Assert(doc.InvalidClips.Any(c => c.Reasons.Any(r => r.Contains("anchor_gap_exceeds_max"))), "anchor gap reason missing");
    }

    private static void ManifestBuilderReconstructsRecording()
    {
        var events = new List<ObsEvent>
        {
            new() { EventType = "record_state", Utc = BaseUtc, OutputActive = true, OutputPaused = false, OutputDurationMs = 0, OutputPath = "part1.mp4" },
            new() { EventType = "record_status_sample", Utc = BaseUtc.AddSeconds(2), OutputActive = true, OutputPaused = false, OutputDurationMs = 2_000, OutputPath = "part1.mp4" },
            new() { EventType = "record_file_changed", Utc = BaseUtc.AddSeconds(4), OutputActive = true, OutputPaused = false, OutputDurationMs = 4_000, OutputPath = "part1.mp4", NewOutputPath = "part2.mp4" },
            new() { EventType = "record_status_sample", Utc = BaseUtc.AddSeconds(6), OutputActive = true, OutputPaused = false, OutputDurationMs = 6_000, OutputPath = "part2.mp4" },
            new() { EventType = "stop_record", Utc = BaseUtc.AddSeconds(8), OutputActive = false, OutputPaused = false, OutputDurationMs = 8_000, OutputPath = "part2.mp4" }
        };

        var manifest = new ManifestBuilder().Build(events, new ManifestBuildOptions { SessionId = "s" });
        Assert(manifest.Recordings.Count == 1, "expected one recording");
        Assert(manifest.Recordings[0].Files.Count == 2, "expected split files");
        Assert(manifest.Recordings[0].Files[0].EndDurationMs == 4_000, "first file should end at split duration");
        Assert(manifest.Recordings[0].Files[1].Path == "part2.mp4", "second file path mismatch");
        Assert(manifest.Recordings[0].Anchors.Count >= 4, "anchors should be preserved");
    }

    private static void ManifestBuilderUsesNewOutputPath()
    {
        var events = new List<ObsEvent>
        {
            new() { EventType = "record_state", Utc = BaseUtc, OutputActive = true, OutputPaused = false, OutputDurationMs = 0, OutputPath = "part1.mp4" },
            new() { EventType = "record_file_changed", Utc = BaseUtc.AddSeconds(4), OutputActive = true, OutputPaused = false, OutputDurationMs = 4_000, OutputPath = "part1.mp4", NewOutputPath = "part2.mp4" },
            new() { EventType = "record_status_sample", Utc = BaseUtc.AddSeconds(6), OutputActive = true, OutputPaused = false, OutputDurationMs = 6_000, OutputPath = "part2.mp4" },
            new() { EventType = "stop_record", Utc = BaseUtc.AddSeconds(8), OutputActive = false, OutputPaused = false, OutputDurationMs = 8_000, OutputPath = "part2.mp4" }
        };

        var manifest = new ManifestBuilder().Build(events, new ManifestBuildOptions { SessionId = "s" });
        Assert(manifest.Recordings.Count == 1, "expected one recording");
        Assert(manifest.Recordings[0].Files.Count == 2, "expected split files from file-changed event");
        Assert(manifest.Recordings[0].Files[1].Path == "part2.mp4", "newOutputPath should become the second file path");
    }

    private static void ManifestBuilderWaitsForTerminalStopPath()
    {
        var events = new List<ObsEvent>
        {
            new() { EventType = "record_status_sample", Utc = BaseUtc, OutputActive = true, OutputPaused = false, OutputDurationMs = 5_000 },
            new()
            {
                EventType = "record_state",
                Utc = BaseUtc.AddSeconds(1),
                OutputActive = false,
                OutputPaused = false,
                OutputDurationMs = 5_000,
                Raw = new Dictionary<string, object?> { ["outputState"] = "OBS_WEBSOCKET_OUTPUT_STOPPING" }
            },
            new() { EventType = "record_state", Utc = BaseUtc.AddSeconds(2), OutputActive = false, OutputPaused = false, OutputDurationMs = 5_000, OutputPath = "final.mkv" },
            new() { EventType = "record_status_sample", Utc = BaseUtc.AddSeconds(3), OutputActive = false, OutputPaused = false, OutputDurationMs = 0, OutputPath = "final.mkv" }
        };

        var manifest = new ManifestBuilder().Build(events, new ManifestBuildOptions { SessionId = "s" });
        Assert(manifest.Recordings.Count == 1, "expected one recording");
        Assert(manifest.Recordings[0].Files.Count == 1, "expected one finalized file");
        Assert(manifest.Recordings[0].Files[0].Path == "final.mkv", "terminal output path should win over placeholder path");
        Assert(manifest.Recordings[0].EndUtc == BaseUtc.AddSeconds(2), "recording should finalize at first terminal stop event, not stopping transition");
    }

    private static void CaseInsensitiveJsonRead()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "obs-clip-sidecar-selftest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var path = Path.Combine(tempDir, "room_events.jsonl");
            File.WriteAllText(path, "{\"SchemaVersion\":1,\"EventType\":\"room_enter\",\"Utc\":\"2026-05-18T00:00:00Z\",\"RunId\":\"r\",\"Room\":\"a\"}" + Environment.NewLine, Encoding.UTF8);
            var parsed = AppendOnlyJsonl.ReadAll<RoomEvent>(path);
            Assert(parsed.Count == 1, "expected one parsed room event");
            Assert(parsed[0].EventType == "room_enter", "event type should parse from PascalCase JSON");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    private static void AssemblyDefaultsToPrecise()
    {
        var doc = Generate(StandardRoomEvents(BaseUtc.AddSeconds(2), endOffsetSeconds: 2), new SessionManifest { SessionId = "s", Recordings = [Recording("r", BaseUtc, "run.mp4", 0, 10_000)] });
        var plan = new AssemblyPlanner().CreatePlan(doc, new AssemblyOptions { DryRun = true });
        Assert(plan.PrecisionMode == "precise_reencode", "default assembly must represent precise final-output semantics");
        Assert(plan.FfconcatText.Contains("ffconcat version 1.0"), "dry-run should still emit ffconcat preview plan");
        Assert(plan.SegmentConcatText?.Contains("precise_segments/segment_0000.mp4") == true, "precise segment concat should be emitted");
    }

    private static readonly DateTimeOffset BaseUtc = new(2026, 5, 18, 0, 0, 0, TimeSpan.Zero);

    private static List<RoomEvent> StandardRoomEvents(DateTimeOffset start, int endOffsetSeconds = 2) =>
    [
        new RoomEvent { EventType = "room_start", Utc = start, Room = "a", MapSid = "map" },
        new RoomEvent { EventType = "death", Utc = start.AddMilliseconds(750), Room = "a", MapSid = "map" },
        new RoomEvent { EventType = "respawn", Utc = start.AddSeconds(1), Room = "a", MapSid = "map" },
        new RoomEvent { EventType = "transition", Utc = start.AddSeconds(endOffsetSeconds), Room = "a", NextRoom = "b", MapSid = "map" }
    ];

    private static RecordingManifest Recording(string id, DateTimeOffset startUtc, string path, long startMs, long endMs) =>
        Recording(id, startUtc, [new RecordingFileManifest { Path = path, StartDurationMs = startMs, EndDurationMs = endMs }]);

    private static RecordingManifest Recording(string id, DateTimeOffset startUtc, List<RecordingFileManifest> files) => new()
    {
        RecordingId = id,
        StartUtc = startUtc,
        EndUtc = startUtc.AddSeconds(10),
        Files = files,
        Anchors = Enumerable.Range(0, 11)
            .Select(i => new ObsAnchor { Utc = startUtc.AddSeconds(i), OutputDurationMs = i * 1000L, OutputPath = files.LastOrDefault(f => i * 1000L >= f.StartDurationMs && i * 1000L <= f.EndDurationMs)?.Path })
            .ToList()
    };

    private static ClipIntervalsDocument Generate(List<RoomEvent> events, SessionManifest manifest, IntervalGenerationOptions? options = null) =>
        new IntervalGenerator().Generate(events, manifest, options ?? new IntervalGenerationOptions { MaxAllowedAnchorGapMs = 1_500 });

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
