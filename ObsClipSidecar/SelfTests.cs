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
            ("adjacent rooms cut exactly at transition boundary", AdjacentRoomTransitionBoundaryTrim),
            ("checkpoint lobby entered before recording still yields clip", CheckpointLobbyEnteredBeforeRecording),
            ("load level becomes attempt reset after failure", LoadLevelResetsAttemptAfterFailure),
            ("respawn load clips do not include death preroll", RespawnLoadClipsDoNotIncludeDeathPreroll),
            ("respawn load clips start at spawn sample", RespawnLoadClipsStartAtSpawnSample),
            ("late checkpoint respawn transition counts as success", LateCheckpointRespawnTransitionCountsAsSuccess),
            ("changed respawn point keeps checkpoint death route", ChangedRespawnPointKeepsCheckpointDeathRoute),
            ("same respawn point does not keep death route", SameRespawnPointDoesNotKeepDeathRoute),
            ("respawn attempt with another death is discarded", RespawnAttemptWithAnotherDeathIsDiscarded),
            ("room entry load ends at closest spawn sample", RoomEntryLoadEndsAtClosestSpawnSample),
            ("standalone room entry load gets delay", StandaloneRoomEntryLoadGetsDelay),
            ("first room transition load level does not cut off intro", FirstRoomTransitionLoadLevelDoesNotCutOffIntro),
            ("death room keeps room-entry intro before successful attempt", DeathRoomKeepsIntroBeforeClear),
            ("revisited branching room gets separate death entry clips", RevisitedBranchingRoomGetsSeparateDeathEntryClips),
            ("death on revisit still keeps earliest uncovered room intro", DeathOnRevisitKeepsEarliestUncoveredRoomIntro),
            ("strawberry room succeeds at collect after death", StrawberryRoomSucceedsAtCollectAfterDeath),
            ("strawberry before death is kept at collect only", StrawberryBeforeDeathIsKeptAtCollectOnly),
            ("strawberry room without death ends at collect", StrawberryRoomWithoutDeathEndsAtCollect),
            ("strawberry tail keeps death until exit", StrawberryTailKeepsDeathUntilExit),
            ("level complete keeps final load interval", LevelCompleteKeepsFinalLoadInterval),
            ("backtrack bounce is not suppressed", BacktrackBounceIsNotSuppressed),
            ("branch return to hub room is kept", BranchReturnToHubRoomIsKept),
            ("unfinished room keeps room-entry intro at end", UnfinishedRoomKeepsIntroAtEnd),
            ("postroll at recording end is clamped", PostrollAtRecordingEndIsClamped),
            ("missing recording files invalidates clip", MissingRecordingFileMapping),
            ("invalid anchor gaps are reported", InvalidAnchorGap),
            ("manifest builder reconstructs recording and split files", ManifestBuilderReconstructsRecording),
            ("manifest builder honors record_file_changed newOutputPath", ManifestBuilderUsesNewOutputPath),
            ("manifest builder waits for terminal stop path after stopping transition", ManifestBuilderWaitsForTerminalStopPath),
            ("json reader is case insensitive for mod output", CaseInsensitiveJsonRead),
            ("jsonl room events tolerate shared read write access", JsonlRoomEventsUseSharedAccess),
            ("assembly plan defaults to precise mode", AssemblyDefaultsToPrecise),
            ("assembly groups one recording into per-map outputs", AssemblyGroupsOneRecordingIntoPerMapOutputs),
            ("assembly disambiguates colliding map outputs", AssemblyDisambiguatesCollidingMapOutputs)
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

        Assert(doc.Clips.Count >= 1, "expected at least one valid clip");
        Assert(doc.Clips.All(c => c.RecordingId == "r2"), "clips should bind to second recording");
        Assert(doc.Clips.All(c => c.SourceFileMapping[0].SourcePath == "new.mp4"), "clips should use matching recording file");
    }

    private static void CrossRecordingInvalid()
    {
        var events = new List<RoomEvent>
        {
            new() { EventType = "room_enter", Utc = BaseUtc.AddSeconds(9), Room = "a", MapSid = "map" },
            new() { EventType = "load_level", Utc = BaseUtc.AddSeconds(9), Room = "a", MapSid = "map", Notes = new Dictionary<string, object?> { ["playerIntro"] = "Transition" } },
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
        var start = BaseUtc.AddMilliseconds(3_500);
        var events = new List<RoomEvent>
        {
            new() { EventType = "room_enter", Utc = start, Room = "a", MapSid = "map" },
            new() { EventType = "load_level", Utc = start.AddMilliseconds(200), Room = "a", MapSid = "map", Notes = new Dictionary<string, object?> { ["playerIntro"] = "Transition" } },
            new() { EventType = "transition", Utc = start.AddMilliseconds(1_000), Room = "a", NextRoom = "b", MapSid = "map" }
        };
        var doc = Generate(events, manifest);
        Assert(doc.Clips.Single().SourceFileMapping.Count == 2, "clip should be split across OBS files");
        Assert(doc.Clips.Single().SourceFileMapping[1].SourceStartDurationMs == 0, "second split file should use file-relative inpoint");
    }

    private static void PauseOverlap()
    {
        var recording = Recording("r", BaseUtc, "run.mp4", 0, 10_000);
        recording = recording with { Pauses = [new PauseInterval { StartDurationMs = 3_500, EndDurationMs = 4_500 }] };
        var doc = Generate(StandardRoomEvents(BaseUtc.AddSeconds(2), endOffsetSeconds: 4), new SessionManifest { SessionId = "s", Recordings = [recording] });
        Assert(doc.Clips.Count >= 2, "pause should split overlapping clips into valid sub-clips");
        Assert(doc.Clips.Where(c => c.PausePolicy == "supported_interval_subtraction").Count() >= 2, "pause policy should be explicit for split clips");
    }

    private static void AdjacentRoomOverlapTrim()
    {
        var start = BaseUtc.AddSeconds(2);
        var events = new List<RoomEvent>
        {
            new() { EventType = "room_enter", Utc = start, Room = "a", MapSid = "Celeste/1-ForsakenCity" },
            new() { EventType = "load_level", Utc = start, Room = "a", MapSid = "Celeste/1-ForsakenCity", Notes = new Dictionary<string, object?> { ["playerIntro"] = "Transition" } },
            new() { EventType = "transition", Utc = start.AddSeconds(2), Room = "a", NextRoom = "b", MapSid = "Celeste/1-ForsakenCity" },
            new() { EventType = "room_enter", Utc = start.AddSeconds(2), Room = "b", MapSid = "Celeste/1-ForsakenCity" },
            new() { EventType = "load_level", Utc = start.AddSeconds(2), Room = "b", MapSid = "Celeste/1-ForsakenCity", Notes = new Dictionary<string, object?> { ["playerIntro"] = "Transition" } },
            new() { EventType = "transition", Utc = start.AddSeconds(4), Room = "b", NextRoom = "c", MapSid = "Celeste/1-ForsakenCity" }
        };

        var doc = Generate(events, new SessionManifest { SessionId = "s", Recordings = [Recording("r", BaseUtc, "run.mp4", 0, 10_000)] }, new IntervalGenerationOptions
        {
            PreRollMs = 250,
            PostRollMs = 500,
            MaxAllowedAnchorGapMs = 1_500
        });

        Assert(doc.Clips.Count == 1, "time-connected adjacent room candidates should merge into one valid interval");
        Assert(doc.Clips[0].StartUtc == start && doc.Clips[0].EndUtc == start.AddSeconds(4), "merged adjacent room interval boundaries mismatch");
        Assert(doc.Clips[0].Reasons.Contains("merged_linear_interval"), "merged adjacent room interval should explain the merge");
    }

    private static void AdjacentRoomTransitionBoundaryTrim()
    {
        var start = BaseUtc.AddSeconds(2);
        var events = new List<RoomEvent>
        {
            new() { EventType = "room_enter", Utc = start, Room = "a", MapSid = "map" },
            new() { EventType = "load_level", Utc = start, Room = "a", MapSid = "map", Notes = new Dictionary<string, object?> { ["playerIntro"] = "Transition" } },
            new() { EventType = "transition", Utc = start.AddSeconds(2), Room = "a", NextRoom = "b", MapSid = "map" },
            new() { EventType = "room_enter", Utc = start.AddSeconds(2), Room = "b", MapSid = "map" },
            new() { EventType = "load_level", Utc = start.AddSeconds(2), Room = "b", MapSid = "map", Notes = new Dictionary<string, object?> { ["playerIntro"] = "Transition" } },
            new() { EventType = "transition", Utc = start.AddSeconds(4), Room = "b", NextRoom = "c", MapSid = "map" }
        };

        var doc = Generate(events, new SessionManifest { SessionId = "s", Recordings = [Recording("r", BaseUtc, "run.mp4", 0, 10_000)] }, new IntervalGenerationOptions
        {
            PreRollMs = 1_000,
            PostRollMs = 1_000,
            MaxAllowedAnchorGapMs = 1_500
        });

        Assert(doc.Clips.Count == 1, "time-connected adjacent room candidates should merge before roll is applied");
        Assert(doc.Clips[0].StartUtc == start && doc.Clips[0].EndUtc == start.AddSeconds(4), "merged adjacent room interval should span the connected route");
    }

    private static void CheckpointLobbyEnteredBeforeRecording()
    {
        var events = new List<RoomEvent>
        {
            new() { EventType = "room_enter", Utc = BaseUtc.AddSeconds(-1), Room = "lobby", MapSid = "Mod/CheckpointLobby" },
            new() { EventType = "load_level", Utc = BaseUtc.AddMilliseconds(-500), Room = "lobby", MapSid = "Mod/CheckpointLobby", Notes = new Dictionary<string, object?> { ["playerIntro"] = "Transition" } },
            new() { EventType = "transition", Utc = BaseUtc.AddSeconds(2), Room = "lobby", NextRoom = "a-00", MapSid = "Mod/CheckpointLobby" }
        };

        var doc = Generate(events, new SessionManifest { SessionId = "s", Recordings = [Recording("r", BaseUtc, "mod-run.mp4", 0, 10_000)] }, new IntervalGenerationOptions
        {
            PreRollMs = 250,
            PostRollMs = 0,
            MaxAllowedAnchorGapMs = 1_500
        });

        Assert(doc.Clips.Count == 1, "checkpoint lobby should still produce a valid first clip after recording starts");
        Assert(doc.Clips[0].StartOutputDurationMs == 0, "clip should be clamped to the recording start");
        Assert(doc.Clips[0].EndOutputDurationMs == 2_000, "clip should end at the lobby transition");
        Assert(doc.Clips[0].Reasons.Contains("start_clamped_to_recording_start"), "clip should explain the recording-start clamp");
    }

    private static void LoadLevelResetsAttemptAfterFailure()
    {
        var start = BaseUtc.AddSeconds(2);
        var events = new List<RoomEvent>
        {
            new() { EventType = "room_enter", Utc = start, Room = "a", MapSid = "map" },
            new() { EventType = "load_level", Utc = start, Room = "a", MapSid = "map", Notes = new Dictionary<string, object?> { ["playerIntro"] = "Transition" } },
            new() { EventType = "death", Utc = start.AddMilliseconds(750), Room = "a", MapSid = "map" },
            new() { EventType = "load_level", Utc = start.AddSeconds(1), Room = "a", MapSid = "map", Notes = new Dictionary<string, object?> { ["playerIntro"] = "Respawn" } },
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

    private static void RespawnLoadClipsDoNotIncludeDeathPreroll()
    {
        var start = BaseUtc.AddSeconds(2);
        var respawnLoad = start.AddSeconds(1);
        var clear = start.AddSeconds(3);
        var events = new List<RoomEvent>
        {
            new() { EventType = "room_enter", Utc = start, Room = "a", MapSid = "map" },
            new() { EventType = "load_level", Utc = start.AddMilliseconds(200), Room = "a", MapSid = "map", Notes = new Dictionary<string, object?> { ["playerIntro"] = "Transition" } },
            new() { EventType = "death", Utc = start.AddMilliseconds(800), Room = "a", MapSid = "map" },
            new() { EventType = "load_level", Utc = respawnLoad, Room = "a", MapSid = "map", Notes = new Dictionary<string, object?> { ["playerIntro"] = "Respawn" } },
            new() { EventType = "transition", Utc = clear, Room = "a", NextRoom = "b", MapSid = "map" }
        };

        var doc = Generate(events, new SessionManifest { SessionId = "s", Recordings = [Recording("r", BaseUtc, "run.mp4", 0, 10_000)] }, new IntervalGenerationOptions
        {
            PreRollMs = 500,
            PostRollMs = 0,
            MaxAllowedAnchorGapMs = 1_500
        });

        var success = doc.Clips.Single(c => c.StartUtc == respawnLoad && c.EndUtc == clear);
        Assert(success.StartOutputDurationMs == 3_000, "respawn load success should start exactly at load_level without prerolling into death footage");
    }

    private static void RespawnLoadClipsStartAtSpawnSample()
    {
        var start = BaseUtc.AddSeconds(2);
        var respawnLoad = start.AddSeconds(1);
        var spawnSample = respawnLoad.AddMilliseconds(180);
        var clear = start.AddSeconds(3);
        var events = new List<RoomEvent>
        {
            new() { EventType = "room_enter", Utc = start, Room = "a", MapSid = "map" },
            new() { EventType = "load_level", Utc = start.AddMilliseconds(200), Room = "a", MapSid = "map", Notes = SpawnNotes("Transition", 10, 20) },
            new() { EventType = "death", Utc = start.AddMilliseconds(800), Room = "a", MapSid = "map" },
            new() { EventType = "load_level", EventId = "respawn-load", Utc = respawnLoad, Room = "a", MapSid = "map", Notes = SpawnNotes("Respawn", 100, 200) },
            new() { EventType = "player_position_sample", Utc = respawnLoad.AddMilliseconds(80), Room = "a", MapSid = "map", Notes = PlayerSampleNotes("respawn-load", 130, 200) },
            new() { EventType = "player_position_sample", Utc = spawnSample, Room = "a", MapSid = "map", Notes = PlayerSampleNotes("respawn-load", 100, 200) },
            new() { EventType = "transition", Utc = clear, Room = "a", NextRoom = "b", MapSid = "map" }
        };

        var doc = Generate(events, new SessionManifest { SessionId = "s", Recordings = [Recording("r", BaseUtc, "run.mp4", 0, 10_000)] }, new IntervalGenerationOptions
        {
            PreRollMs = 500,
            PostRollMs = 0,
            MaxAllowedAnchorGapMs = 1_500
        });

        var success = doc.Clips.Single(c => c.Reasons.Contains("load_level_player_position_start"));
        Assert(success.StartUtc == spawnSample, "respawn success should start at the player sample closest to spawn");
        Assert(success.StartOutputDurationMs == 3_180, "dynamic respawn start must not preroll into death transition footage");
    }

    private static void LateCheckpointRespawnTransitionCountsAsSuccess()
    {
        var start = BaseUtc.AddSeconds(2);
        var death = start.AddSeconds(1);
        var lateCheckpointRespawnLoad = start.AddMilliseconds(1_600);
        var transition = start.AddSeconds(2);
        var events = new List<RoomEvent>
        {
            new() { EventType = "room_enter", Utc = start, Room = "a", MapSid = "map" },
            new() { EventType = "load_level", Utc = start.AddMilliseconds(200), Room = "a", MapSid = "map", Notes = new Dictionary<string, object?> { ["playerIntro"] = "Transition" } },
            new() { EventType = "death", Utc = death, Room = "a", MapSid = "map" },
            new() { EventType = "load_level", Utc = lateCheckpointRespawnLoad, Room = "a", MapSid = "map", Notes = new Dictionary<string, object?> { ["playerIntro"] = "Respawn", ["source"] = "level_load_hook" } },
            new() { EventType = "transition", Utc = transition, Room = "a", NextRoom = "b", MapSid = "map" }
        };

        var doc = Generate(events, new SessionManifest { SessionId = "s", Recordings = [Recording("r", BaseUtc, "run.mp4", 0, 10_000)] }, new IntervalGenerationOptions
        {
            PreRollMs = 500,
            PostRollMs = 0,
            MaxAllowedAnchorGapMs = 1_500
        });

        var success = doc.Clips.Single(c => c.Reasons.Contains("final_successful_attempt"));
        Assert(success.StartUtc == lateCheckpointRespawnLoad && success.EndUtc == transition, "late checkpoint respawn should keep respawn load -> transition");
        Assert(success.StartUtc > death, "success clip must start after the death event");
    }

    private static void ChangedRespawnPointKeepsCheckpointDeathRoute()
    {
        var start = BaseUtc.AddSeconds(2);
        var firstLoad = start.AddMilliseconds(200);
        var death = start.AddSeconds(1);
        var respawnLoad = start.AddMilliseconds(1_600);
        var transition = start.AddSeconds(2);
        var events = new List<RoomEvent>
        {
            new() { EventType = "room_enter", Utc = start, Room = "a", MapSid = "map" },
            new() { EventType = "load_level", Utc = firstLoad, Room = "a", MapSid = "map", Notes = RespawnNotes("Transition", 10, 20) },
            new() { EventType = "death", Utc = death, Room = "a", MapSid = "map" },
            new() { EventType = "load_level", Utc = respawnLoad, Room = "a", MapSid = "map", Notes = RespawnNotes("Respawn", 80, 20) },
            new() { EventType = "transition", Utc = transition, Room = "a", NextRoom = "b", MapSid = "map" }
        };

        var doc = Generate(events, new SessionManifest { SessionId = "s", Recordings = [Recording("r", BaseUtc, "run.mp4", 0, 10_000)] }, new IntervalGenerationOptions
        {
            PreRollMs = 0,
            PostRollMs = 0,
            MaxAllowedAnchorGapMs = 1_500
        });

        Assert(!HasClipCovering(doc, firstLoad, transition), "changed respawn point should not keep death transition footage in the successful route");
        Assert(doc.Clips.Any(c => c.StartUtc == respawnLoad && c.EndUtc == transition && c.Reasons.Contains("checkpoint_death_successful_attempt")), "changed respawn point should keep the respawn load -> transition route");
        Assert(!doc.Warnings.Any(w => w.Contains("linear_discard_death_before_success", StringComparison.Ordinal)), "legal checkpoint-death route should not log the death as discarded");
    }

    private static void SameRespawnPointDoesNotKeepDeathRoute()
    {
        var start = BaseUtc.AddSeconds(2);
        var firstLoad = start.AddMilliseconds(200);
        var respawnLoad = start.AddMilliseconds(1_600);
        var transition = start.AddSeconds(2);
        var events = new List<RoomEvent>
        {
            new() { EventType = "room_enter", Utc = start, Room = "a", MapSid = "map" },
            new() { EventType = "load_level", Utc = firstLoad, Room = "a", MapSid = "map", Notes = RespawnNotes("Transition", 10, 20) },
            new() { EventType = "death", Utc = start.AddSeconds(1), Room = "a", MapSid = "map" },
            new() { EventType = "load_level", Utc = respawnLoad, Room = "a", MapSid = "map", Notes = RespawnNotes("Respawn", 10, 20) },
            new() { EventType = "transition", Utc = transition, Room = "a", NextRoom = "b", MapSid = "map" }
        };

        var doc = Generate(events, new SessionManifest { SessionId = "s", Recordings = [Recording("r", BaseUtc, "run.mp4", 0, 10_000)] }, new IntervalGenerationOptions
        {
            PreRollMs = 0,
            PostRollMs = 0,
            MaxAllowedAnchorGapMs = 1_500
        });

        Assert(!HasClipCovering(doc, firstLoad, transition), "same respawn point should not keep the failed death route");
        Assert(HasClipCovering(doc, respawnLoad, transition), "same respawn point should still keep normal respawn load -> transition");
        Assert(doc.Warnings.Any(w => w.Contains("linear_discard_death_before_success", StringComparison.Ordinal)), "same respawn point should still report the failed death route");
    }

    private static void RespawnAttemptWithAnotherDeathIsDiscarded()
    {
        var start = BaseUtc.AddSeconds(2);
        var firstRespawnLoad = start.AddSeconds(1);
        var secondRespawnLoad = start.AddSeconds(2);
        var transition = start.AddSeconds(3);
        var events = new List<RoomEvent>
        {
            new() { EventType = "room_enter", Utc = start, Room = "a", MapSid = "map" },
            new() { EventType = "load_level", Utc = start.AddMilliseconds(200), Room = "a", MapSid = "map", Notes = new Dictionary<string, object?> { ["playerIntro"] = "Transition" } },
            new() { EventType = "death", Utc = start.AddMilliseconds(800), Room = "a", MapSid = "map" },
            new() { EventType = "load_level", Utc = firstRespawnLoad, Room = "a", MapSid = "map", Notes = new Dictionary<string, object?> { ["playerIntro"] = "Respawn", ["source"] = "level_load_hook" } },
            new() { EventType = "death", Utc = start.AddMilliseconds(1_500), Room = "a", MapSid = "map" },
            new() { EventType = "load_level", Utc = secondRespawnLoad, Room = "a", MapSid = "map", Notes = new Dictionary<string, object?> { ["playerIntro"] = "Respawn", ["source"] = "level_load_hook" } },
            new() { EventType = "transition", Utc = transition, Room = "a", NextRoom = "b", MapSid = "map" }
        };

        var doc = Generate(events, new SessionManifest { SessionId = "s", Recordings = [Recording("r", BaseUtc, "run.mp4", 0, 10_000)] }, new IntervalGenerationOptions
        {
            PreRollMs = 500,
            PostRollMs = 0,
            MaxAllowedAnchorGapMs = 1_500
        });

        Assert(!doc.Clips.Any(c => c.StartUtc == firstRespawnLoad && c.EndUtc == transition), "failed respawn attempt must not be kept through the later transition");
        Assert(HasClipCovering(doc, secondRespawnLoad, transition), "later death-free respawn attempt should be kept");
    }

    private static void RoomEntryLoadEndsAtClosestSpawnSample()
    {
        var start = BaseUtc.AddSeconds(2);
        var load = start.AddMilliseconds(200);
        var closestSample = start.AddMilliseconds(420);
        var respawnLoad = start.AddSeconds(1);
        var events = new List<RoomEvent>
        {
            new() { EventType = "room_enter", Utc = start, Room = "a", MapSid = "map" },
            new() { EventType = "load_level", EventId = "load-a", Utc = load, Room = "a", MapSid = "map", Notes = SpawnNotes("Transition", 100, 100) },
            new() { EventType = "player_position_sample", Utc = start.AddMilliseconds(300), Room = "a", MapSid = "map", Notes = PlayerSampleNotes("load-a", 130, 100) },
            new() { EventType = "player_position_sample", Utc = closestSample, Room = "a", MapSid = "map", Notes = PlayerSampleNotes("load-a", 101, 100) },
            new() { EventType = "player_position_sample", Utc = start.AddMilliseconds(540), Room = "a", MapSid = "map", Notes = PlayerSampleNotes("load-a", 150, 100) },
            new() { EventType = "death", Utc = start.AddMilliseconds(800), Room = "a", MapSid = "map" },
            new() { EventType = "load_level", Utc = respawnLoad, Room = "a", MapSid = "map", Notes = new Dictionary<string, object?> { ["playerIntro"] = "Respawn" } },
            new() { EventType = "transition", Utc = start.AddSeconds(2), Room = "a", NextRoom = "b", MapSid = "map" }
        };

        var doc = Generate(events, new SessionManifest { SessionId = "s", Recordings = [Recording("r", BaseUtc, "run.mp4", 0, 10_000)] }, new IntervalGenerationOptions
        {
            PreRollMs = 0,
            PostRollMs = 500,
            MaxAllowedAnchorGapMs = 1_500
        });

        var entryLoad = doc.Clips.Single(c => c.StartUtc == start && c.Reasons.Contains("room_entry_load_player_position_end"));
        Assert(entryLoad.EndUtc == closestSample, "room entry load should end at the player sample closest to the spawn point");
        Assert(entryLoad.EndOutputDurationMs == 2_420, "dynamic room entry load end must not add fixed postroll");
        Assert(!doc.Warnings.Any(w => w.Contains("player_position_sample", StringComparison.Ordinal)), "player position samples should not create discard warnings");
    }

    private static void StandaloneRoomEntryLoadGetsDelay()
    {
        var start = BaseUtc.AddSeconds(2);
        var load = start.AddMilliseconds(200);
        var events = new List<RoomEvent>
        {
            new() { EventType = "room_enter", Utc = start, Room = "a", MapSid = "map" },
            new() { EventType = "load_level", Utc = load, Room = "a", MapSid = "map", Notes = new Dictionary<string, object?> { ["playerIntro"] = "Transition" } },
            new() { EventType = "death", Utc = start.AddMilliseconds(800), Room = "a", MapSid = "map" },
            new() { EventType = "load_level", Utc = start.AddSeconds(1), Room = "a", MapSid = "map", Notes = new Dictionary<string, object?> { ["playerIntro"] = "Respawn" } },
            new() { EventType = "transition", Utc = start.AddSeconds(2), Room = "a", NextRoom = "b", MapSid = "map" }
        };

        var doc = Generate(events, new SessionManifest { SessionId = "s", Recordings = [Recording("r", BaseUtc, "run.mp4", 0, 10_000)] }, new IntervalGenerationOptions
        {
            PreRollMs = 0,
            PostRollMs = 500,
            MaxAllowedAnchorGapMs = 1_500
        });

        var entryLoad = doc.Clips.Single(c => c.StartUtc == start && c.EndUtc == load);
        Assert(entryLoad.StartOutputDurationMs == 2_000, "room entry load delay must not add preroll");
        Assert(entryLoad.EndOutputDurationMs == 2_700, "standalone room_enter -> load_level should keep a post-load delay");
        Assert(entryLoad.Reasons.Contains("room_entry_load_delay"), "standalone room entry load delay should be annotated");
    }

    private static void FirstRoomTransitionLoadLevelDoesNotCutOffIntro()
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

        Assert(doc.Clips.Count == 1, "first room connected entry and success segments should merge into one clip");
        Assert(doc.Clips[0].Reasons.Contains("merged_linear_interval"), "first room merged clip should record merge reason");
        Assert(doc.Clips[0].StartUtc == start && doc.Clips[0].EndUtc == clear, "initial transition load_level must not cut off first-room intro");
        Assert(doc.InvalidClips.Count == 0, "first room should not create an overlapping intro candidate");
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

        Assert(HasClipCovering(doc, roomBEnter, introLoad), "death room should preserve entry through initial load_level");
        Assert(HasClipCovering(doc, respawnLoad, clear), "successful attempt should restart from respawn load_level");
    }

    private static void RevisitedBranchingRoomGetsSeparateDeathEntryClips()
    {
        var start = BaseUtc.AddSeconds(2);
        var events = new List<RoomEvent>
        {
            new() { EventType = "room_enter", Utc = start, Room = "a", MapSid = "map" },
            new() { EventType = "load_level", Utc = start, Room = "a", MapSid = "map", Notes = new Dictionary<string, object?> { ["playerIntro"] = "Transition" } },
            new() { EventType = "transition", Utc = start.AddSeconds(1), Room = "a", NextRoom = "b", MapSid = "map" },
            new() { EventType = "room_enter", Utc = start.AddSeconds(1), Room = "b", MapSid = "map" },
            new() { EventType = "load_level", Utc = start.AddMilliseconds(1_100), Room = "b", MapSid = "map", Notes = new Dictionary<string, object?> { ["playerIntro"] = "Transition" } },
            new() { EventType = "death", Utc = start.AddMilliseconds(1_500), Room = "b", MapSid = "map" },
            new() { EventType = "load_level", Utc = start.AddSeconds(2), Room = "b", MapSid = "map", Notes = new Dictionary<string, object?> { ["playerIntro"] = "Respawn" } },
            new() { EventType = "transition", Utc = start.AddSeconds(3), Room = "b", NextRoom = "c", MapSid = "map" },
            new() { EventType = "room_enter", Utc = start.AddSeconds(3), Room = "c", MapSid = "map" },
            new() { EventType = "load_level", Utc = start.AddSeconds(3), Room = "c", MapSid = "map", Notes = new Dictionary<string, object?> { ["playerIntro"] = "Transition" } },
            new() { EventType = "transition", Utc = start.AddSeconds(4), Room = "c", NextRoom = "b", MapSid = "map" },
            new() { EventType = "room_enter", Utc = start.AddSeconds(4), Room = "b", MapSid = "map" },
            new() { EventType = "load_level", Utc = start.AddMilliseconds(4_100), Room = "b", MapSid = "map", Notes = new Dictionary<string, object?> { ["playerIntro"] = "Transition" } },
            new() { EventType = "death", Utc = start.AddMilliseconds(4_500), Room = "b", MapSid = "map" },
            new() { EventType = "load_level", Utc = start.AddSeconds(5), Room = "b", MapSid = "map", Notes = new Dictionary<string, object?> { ["playerIntro"] = "Respawn" } },
            new() { EventType = "transition", Utc = start.AddSeconds(6), Room = "b", NextRoom = "d", MapSid = "map" }
        };

        var doc = Generate(events, new SessionManifest { SessionId = "s", Recordings = [Recording("r", BaseUtc, "run.mp4", 0, 10_000)] }, new IntervalGenerationOptions
        {
            PreRollMs = 0,
            PostRollMs = 0,
            MaxAllowedAnchorGapMs = 1_500
        });

        Assert(HasClipCovering(doc, start.AddSeconds(1), start.AddMilliseconds(1_100)), "first b visit entry-load boundaries mismatch");
        Assert(HasClipCovering(doc, start.AddSeconds(2), start.AddSeconds(3)), "first b visit successful attempt should restart at respawn load");
        Assert(HasClipCovering(doc, start.AddSeconds(4), start.AddMilliseconds(4_100)), "second b visit entry-load boundaries mismatch");
        Assert(HasClipCovering(doc, start.AddSeconds(5), start.AddSeconds(6)), "second b visit successful attempt should restart at respawn load");
        Assert(doc.Clips.All(c => c.StartUtc != start.AddMilliseconds(1_500) && c.StartUtc != start.AddMilliseconds(4_500)), "death timestamps must not become clip starts");
    }

    private static void DeathOnRevisitKeepsEarliestUncoveredRoomIntro()
    {
        var start = BaseUtc.AddSeconds(2);
        var firstEnter = start.AddSeconds(1);
        var firstLoad = start.AddMilliseconds(1_100);
        var secondEnter = start.AddSeconds(3);
        var secondLoad = start.AddMilliseconds(3_100);
        var respawnLoad = start.AddSeconds(4);
        var events = new List<RoomEvent>
        {
            new() { EventType = "room_enter", Utc = start, Room = "a", MapSid = "map" },
            new() { EventType = "load_level", Utc = start, Room = "a", MapSid = "map", Notes = new Dictionary<string, object?> { ["playerIntro"] = "Transition" } },
            new() { EventType = "transition", Utc = firstEnter, Room = "a", NextRoom = "b", MapSid = "map" },
            new() { EventType = "room_enter", Utc = firstEnter, Room = "b", MapSid = "map" },
            new() { EventType = "load_level", Utc = firstLoad, Room = "b", MapSid = "map", Notes = new Dictionary<string, object?> { ["playerIntro"] = "Transition" } },
            new() { EventType = "transition", Utc = start.AddSeconds(2), Room = "b", NextRoom = "a", MapSid = "map" },
            new() { EventType = "room_enter", Utc = start.AddSeconds(2), Room = "a", MapSid = "map" },
            new() { EventType = "load_level", Utc = start.AddSeconds(2), Room = "a", MapSid = "map", Notes = new Dictionary<string, object?> { ["playerIntro"] = "Transition" } },
            new() { EventType = "transition", Utc = secondEnter, Room = "a", NextRoom = "b", MapSid = "map" },
            new() { EventType = "room_enter", Utc = secondEnter, Room = "b", MapSid = "map" },
            new() { EventType = "load_level", Utc = secondLoad, Room = "b", MapSid = "map", Notes = new Dictionary<string, object?> { ["playerIntro"] = "Transition" } },
            new() { EventType = "death", Utc = start.AddMilliseconds(3_500), Room = "b", MapSid = "map" },
            new() { EventType = "load_level", Utc = respawnLoad, Room = "b", MapSid = "map", Notes = new Dictionary<string, object?> { ["playerIntro"] = "Respawn" } },
            new() { EventType = "transition", Utc = start.AddSeconds(5), Room = "b", NextRoom = "c", MapSid = "map" }
        };

        var doc = Generate(events, new SessionManifest { SessionId = "s", Recordings = [Recording("r", BaseUtc, "run.mp4", 0, 10_000)] }, new IntervalGenerationOptions
        {
            PreRollMs = 0,
            PostRollMs = 0,
            MaxAllowedAnchorGapMs = 1_500
        });

        Assert(HasClipCovering(doc, firstEnter, firstLoad), "earliest room-b intro should not be lost after revisit death");
        Assert(HasClipCovering(doc, secondEnter, secondLoad), "death-visit room-b intro should remain separate");
    }

    private static void StrawberryRoomSucceedsAtCollectAfterDeath()
    {
        var start = BaseUtc.AddSeconds(2);
        var respawnLoad = start.AddSeconds(1);
        var collect = start.AddMilliseconds(1_500);
        var clear = start.AddSeconds(2);
        var events = new List<RoomEvent>
        {
            new() { EventType = "room_enter", Utc = start, Room = "berry", MapSid = "map" },
            new() { EventType = "load_level", Utc = start.AddMilliseconds(200), Room = "berry", MapSid = "map", Notes = new Dictionary<string, object?> { ["playerIntro"] = "Transition" } },
            new() { EventType = "death", Utc = start.AddMilliseconds(700), Room = "berry", MapSid = "map" },
            new() { EventType = "load_level", Utc = respawnLoad, Room = "berry", MapSid = "map", Notes = new Dictionary<string, object?> { ["playerIntro"] = "Respawn" } },
            new() { EventType = "strawberry_collect", Utc = collect, Room = "berry", MapSid = "map" },
            new() { EventType = "transition", Utc = clear, Room = "berry", NextRoom = "next", MapSid = "map" }
        };

        var doc = Generate(events, new SessionManifest { SessionId = "s", Recordings = [Recording("r", BaseUtc, "run.mp4", 0, 10_000)] }, new IntervalGenerationOptions
        {
            PreRollMs = 0,
            PostRollMs = 0,
            MaxAllowedAnchorGapMs = 1_500
        });

        var success = doc.Clips.Single(c => c.Room == "berry" && c.Reasons.Contains("strawberry_collect_success"));
        Assert(success.StartUtc == respawnLoad && success.EndUtc == collect, "strawberry success after death should start from respawn/load_level and end at collect");
        Assert(!doc.Clips.Any(c => c.Room == "berry" && c.EndUtc == clear), "strawberry transition after collect should not create a second success clip");
        Assert(doc.Clips.Any(c => c.Room == "berry" && c.Reasons.Contains("room_entry_load")), "death strawberry room should still keep entry-load intro");
    }

    private static void StrawberryBeforeDeathIsKeptAtCollectOnly()
    {
        var start = BaseUtc.AddSeconds(2);
        var collect = start.AddMilliseconds(500);
        var events = new List<RoomEvent>
        {
            new() { EventType = "room_enter", Utc = start, Room = "berry", MapSid = "map" },
            new() { EventType = "load_level", Utc = start.AddMilliseconds(200), Room = "berry", MapSid = "map", Notes = new Dictionary<string, object?> { ["playerIntro"] = "Transition" } },
            new() { EventType = "strawberry_collect", Utc = collect, Room = "berry", MapSid = "map" },
            new() { EventType = "death", Utc = start.AddMilliseconds(800), Room = "berry", MapSid = "map" },
            new() { EventType = "load_level", Utc = start.AddSeconds(1), Room = "berry", MapSid = "map", Notes = new Dictionary<string, object?> { ["playerIntro"] = "Respawn" } },
            new() { EventType = "transition", Utc = start.AddSeconds(2), Room = "berry", NextRoom = "next", MapSid = "map" }
        };

        var doc = Generate(events, new SessionManifest { SessionId = "s", Recordings = [Recording("r", BaseUtc, "run.mp4", 0, 10_000)] }, new IntervalGenerationOptions
        {
            PreRollMs = 0,
            PostRollMs = 0,
            MaxAllowedAnchorGapMs = 1_500
        });

        Assert(HasClipCovering(doc, start.AddMilliseconds(200), collect), "strawberry collected before death should keep load_level -> collect");
        Assert(!doc.Clips.Any(c => c.Room == "berry" && c.EndUtc == start.AddSeconds(2)), "later no-collect exit after death must not create another success clip");
        Assert(HasClipCovering(doc, start, start.AddMilliseconds(200)), "room entry -> load segment should cover the first room entry even if death happens later");
    }

    private static void StrawberryRoomWithoutDeathEndsAtCollect()
    {
        var start = BaseUtc.AddSeconds(2);
        var collect = start.AddSeconds(1);
        var clear = start.AddSeconds(2);
        var events = new List<RoomEvent>
        {
            new() { EventType = "room_enter", Utc = start, Room = "berry", MapSid = "map" },
            new() { EventType = "load_level", Utc = start.AddMilliseconds(200), Room = "berry", MapSid = "map", Notes = new Dictionary<string, object?> { ["playerIntro"] = "Transition" } },
            new() { EventType = "strawberry_collect", Utc = collect, Room = "berry", MapSid = "map" },
            new() { EventType = "transition", Utc = clear, Room = "berry", NextRoom = "next", MapSid = "map" }
        };

        var doc = Generate(events, new SessionManifest { SessionId = "s", Recordings = [Recording("r", BaseUtc, "run.mp4", 0, 10_000)] }, new IntervalGenerationOptions
        {
            PreRollMs = 0,
            PostRollMs = 0,
            MaxAllowedAnchorGapMs = 1_500
        });

        Assert(HasClipCovering(doc, start.AddMilliseconds(200), collect), "strawberry room without death should keep load_level -> collect");
        Assert(!doc.Clips.Any(c => c.Room == "berry" && c.EndUtc == clear), "strawberry room should not wait for exit once collect succeeded");
    }

    private static void StrawberryTailKeepsDeathUntilExit()
    {
        var start = BaseUtc.AddSeconds(2);
        var collect = start.AddMilliseconds(500);
        var exit = start.AddSeconds(2);
        var events = new List<RoomEvent>
        {
            new() { EventType = "room_enter", Utc = start, Room = "berry", MapSid = "map" },
            new() { EventType = "load_level", Utc = start.AddMilliseconds(200), Room = "berry", MapSid = "map", Notes = new Dictionary<string, object?> { ["playerIntro"] = "Transition" } },
            new() { EventType = "strawberry_collect", Utc = collect, Room = "berry", MapSid = "map" },
            new() { EventType = "death", Utc = start.AddMilliseconds(800), Room = "berry", MapSid = "map" },
            new() { EventType = "load_level", Utc = start.AddSeconds(1), Room = "berry", MapSid = "map", Notes = new Dictionary<string, object?> { ["playerIntro"] = "Respawn" } },
            new() { EventType = "exit", Utc = exit, Room = "berry", MapSid = "map" }
        };

        var doc = Generate(events, new SessionManifest { SessionId = "s", Recordings = [Recording("r", BaseUtc, "run.mp4", 0, 10_000)] }, new IntervalGenerationOptions
        {
            PreRollMs = 0,
            PostRollMs = 0,
            MaxAllowedAnchorGapMs = 1_500
        });

        Assert(HasClipCovering(doc, collect, exit), "strawberry tail should keep collect -> first exit even with death in between");
    }

    private static void LevelCompleteKeepsFinalLoadInterval()
    {
        var start = BaseUtc.AddSeconds(2);
        var load = start.AddMilliseconds(300);
        var complete = start.AddSeconds(2);
        var events = new List<RoomEvent>
        {
            new() { EventType = "room_enter", Utc = start, Room = "end", MapSid = "map" },
            new() { EventType = "load_level", Utc = load, Room = "end", MapSid = "map", Notes = new Dictionary<string, object?> { ["playerIntro"] = "Transition" } },
            new() { EventType = "level_complete", Utc = complete, Room = "end", MapSid = "map" },
            new() { EventType = "exit", Utc = complete.AddMilliseconds(500), Room = "end", MapSid = "map" }
        };

        var doc = Generate(events, new SessionManifest { SessionId = "s", Recordings = [Recording("r", BaseUtc, "run.mp4", 0, 10_000)] }, new IntervalGenerationOptions
        {
            PreRollMs = 0,
            PostRollMs = 0,
            MaxAllowedAnchorGapMs = 1_500
        });

        Assert(HasClipCovering(doc, load, complete), "level_complete should keep load_level -> level_complete");
        Assert(HasClipCovering(doc, complete, complete.AddMilliseconds(500)), "level_complete -> exit should be kept");
    }

    private static void BacktrackBounceIsNotSuppressed()
    {
        var start = BaseUtc.AddSeconds(2);
        var events = new List<RoomEvent>
        {
            new() { EventType = "room_enter", Utc = start, Room = "a", MapSid = "map" },
            new() { EventType = "load_level", Utc = start, Room = "a", MapSid = "map", Notes = new Dictionary<string, object?> { ["playerIntro"] = "Transition" } },
            new() { EventType = "transition", Utc = start.AddSeconds(1), Room = "a", NextRoom = "b", MapSid = "map" },
            new() { EventType = "room_enter", Utc = start.AddSeconds(1), Room = "b", MapSid = "map" },
            new() { EventType = "load_level", Utc = start.AddSeconds(1), Room = "b", MapSid = "map", Notes = new Dictionary<string, object?> { ["playerIntro"] = "Transition" } },
            new() { EventType = "transition", Utc = start.AddSeconds(2), Room = "b", NextRoom = "a", MapSid = "map" },
            new() { EventType = "room_enter", Utc = start.AddSeconds(2), Room = "a", MapSid = "map" },
            new() { EventType = "load_level", Utc = start.AddSeconds(2), Room = "a", MapSid = "map", Notes = new Dictionary<string, object?> { ["playerIntro"] = "Transition" } },
            new() { EventType = "transition", Utc = start.AddSeconds(3), Room = "a", NextRoom = "b", MapSid = "map" },
            new() { EventType = "room_enter", Utc = start.AddSeconds(3), Room = "b", MapSid = "map" },
            new() { EventType = "load_level", Utc = start.AddSeconds(3), Room = "b", MapSid = "map", Notes = new Dictionary<string, object?> { ["playerIntro"] = "Transition" } },
            new() { EventType = "transition", Utc = start.AddSeconds(4), Room = "b", NextRoom = "c", MapSid = "map" }
        };

        var doc = Generate(events, new SessionManifest { SessionId = "s", Recordings = [Recording("r", BaseUtc, "run.mp4", 0, 10_000)] }, new IntervalGenerationOptions
        {
            PreRollMs = 0,
            PostRollMs = 0,
            MaxAllowedAnchorGapMs = 1_500
        });

        var clips = doc.Clips.OrderBy(c => c.StartUtc).ToList();
        Assert(clips.Count == 1, "backtrack bounce suppression is disabled and connected intervals should merge");
        Assert(clips[0].StartUtc == start && clips[0].EndUtc == start.AddSeconds(4), "merged backtrack route should keep the whole connected route");
    }

    private static void BranchReturnToHubRoomIsKept()
    {
        var start = BaseUtc.AddSeconds(2);
        var events = new List<RoomEvent>
        {
            new() { EventType = "room_enter", Utc = start, Room = "hub", MapSid = "map" },
            new() { EventType = "load_level", Utc = start, Room = "hub", MapSid = "map", Notes = new Dictionary<string, object?> { ["playerIntro"] = "Transition" } },
            new() { EventType = "transition", Utc = start.AddSeconds(1), Room = "hub", NextRoom = "side", MapSid = "map" },
            new() { EventType = "room_enter", Utc = start.AddSeconds(1), Room = "side", MapSid = "map" },
            new() { EventType = "load_level", Utc = start.AddSeconds(1), Room = "side", MapSid = "map", Notes = new Dictionary<string, object?> { ["playerIntro"] = "Transition" } },
            new() { EventType = "transition", Utc = start.AddSeconds(2), Room = "side", NextRoom = "hub", MapSid = "map" },
            new() { EventType = "room_enter", Utc = start.AddSeconds(2), Room = "hub", MapSid = "map" },
            new() { EventType = "load_level", Utc = start.AddSeconds(2), Room = "hub", MapSid = "map", Notes = new Dictionary<string, object?> { ["playerIntro"] = "Transition" } },
            new() { EventType = "transition", Utc = start.AddSeconds(3), Room = "hub", NextRoom = "exit", MapSid = "map" }
        };

        var doc = Generate(events, new SessionManifest { SessionId = "s", Recordings = [Recording("r", BaseUtc, "run.mp4", 0, 10_000)] }, new IntervalGenerationOptions
        {
            PreRollMs = 0,
            PostRollMs = 0,
            MaxAllowedAnchorGapMs = 1_500
        });

        var clips = doc.Clips.OrderBy(c => c.StartUtc).ToList();
        Assert(clips.Count == 1, "branch return connected intervals should merge into one route clip");
        Assert(clips[0].StartUtc == start && clips[0].EndUtc == start.AddSeconds(3), "merged branch route boundaries mismatch");
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
        Assert(doc.Clips[0].Reasons.Contains("room_entry_load"), "unfinished room should emit room_entry_load clip");
        Assert(doc.Clips[0].StartUtc == start && doc.Clips[0].EndUtc == introLoad, "unfinished intro boundaries mismatch");
    }

    private static void MissingRecordingFileMapping()
    {
        var recording = Recording("r", BaseUtc, [new RecordingFileManifest { Path = "too-short.mp4", StartDurationMs = 0, EndDurationMs = 2_000 }]);
        var doc = Generate(StandardRoomEvents(BaseUtc.AddSeconds(1), endOffsetSeconds: 3), new SessionManifest { SessionId = "s", Recordings = [recording] });
        Assert(doc.InvalidClips.Any(c => c.Reasons.Contains("source_file_mapping_gap")), "mapping gap reason missing");
    }

    private static void PostrollAtRecordingEndIsClamped()
    {
        var recording = Recording("r", BaseUtc, "run.mp4", 0, 3_000);
        var doc = Generate(StandardRoomEvents(BaseUtc, endOffsetSeconds: 3), new SessionManifest { SessionId = "s", Recordings = [recording] }, new IntervalGenerationOptions
        {
            PreRollMs = 0,
            PostRollMs = 500,
            MaxAllowedAnchorGapMs = 1_500,
            RequireExistingFiles = false
        });

        Assert(doc.InvalidClips.Count == 0, "postroll-only overrun should not invalidate final clip");
        var finalClip = doc.Clips.SingleOrDefault(c => c.Reasons.Contains("end_postroll_clamped_to_recording_end"));
        Assert(finalClip is not null, "expected a valid clamped final clip");
        Assert(finalClip!.EndOutputDurationMs == 3_000, "clip end should clamp to recording file end");
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

    private static void JsonlRoomEventsUseSharedAccess()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "obs-clip-sidecar-selftest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var path = Path.Combine(tempDir, "room_events.jsonl");
            File.WriteAllText(path, "{\"schemaVersion\":1,\"eventType\":\"room_enter\",\"utc\":\"2026-05-18T00:00:00Z\",\"room\":\"a\"}" + Environment.NewLine, Encoding.UTF8);

            using (var writerHandle = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            {
                var parsed = AppendOnlyJsonl.ReadAll<RoomEvent>(path);
                Assert(parsed.Count == 1, "reader should not be blocked by an active shared writer");

                AppendOnlyJsonl.Clear(path);
                Assert(new FileInfo(path).Length == 0, "clear should truncate even while an active shared writer exists");
            }

            AppendOnlyJsonl.AppendAsync(path, new RoomEvent
            {
                SchemaVersion = 1,
                EventType = "room_enter",
                Utc = BaseUtc,
                Room = "b"
            }).GetAwaiter().GetResult();

            using (var readerHandle = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                AppendOnlyJsonl.AppendAsync(path, new RoomEvent
                {
                    SchemaVersion = 1,
                    EventType = "load_level",
                    Utc = BaseUtc.AddMilliseconds(1),
                    Room = "b"
                }).GetAwaiter().GetResult();
            }

            var final = AppendOnlyJsonl.ReadAll<RoomEvent>(path);
            Assert(final.Count == 2, "writer should not be blocked by an active shared reader");
            Assert(final[0].Room == "b" && final[1].EventType == "load_level", "events should remain readable after shared access operations");
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
        Assert(plan.FfmpegCommand.Contains("final concat reencode", StringComparison.Ordinal), "precise assembly should document final concat reencode to avoid copied timestamp gaps");
        Assert(plan.FfconcatText.Contains("ffconcat version 1.0"), "dry-run should still emit ffconcat preview plan");
        Assert(plan.SegmentConcatText?.Contains("precise_segments/segment_0000.mp4") == true, "precise segment concat should be emitted");
    }

    private static void AssemblyGroupsOneRecordingIntoPerMapOutputs()
    {
        var doc = new ClipIntervalsDocument
        {
            Clips =
            [
                new ClipInterval
                {
                    ClipId = "a",
                    Room = "a",
                    MapSid = "Maps/Alpha",
                    IsValid = true,
                    SourceFileMapping = [new ClipFileSlice { SourcePath = "run.mkv", SourceStartDurationMs = 0, SourceEndDurationMs = 1_000 }]
                },
                new ClipInterval
                {
                    ClipId = "b",
                    Room = "b",
                    MapSid = "Maps/Beta",
                    IsValid = true,
                    SourceFileMapping = [new ClipFileSlice { SourcePath = "run.mkv", SourceStartDurationMs = 1_000, SourceEndDurationMs = 2_000 }]
                },
                new ClipInterval
                {
                    ClipId = "c",
                    Room = "c",
                    MapSid = "Maps/Alpha",
                    IsValid = true,
                    SourceFileMapping = [new ClipFileSlice { SourcePath = "run.mkv", SourceStartDurationMs = 2_000, SourceEndDurationMs = 3_000 }]
                }
            ]
        };

        var root = Path.Combine(Path.GetTempPath(), "CelesteAutoCutSelfTests", Guid.NewGuid().ToString("N"));
        try
        {
            var outputs = new AssemblyPlanner().AssembleByMap(
                doc,
                root,
                mapSid => Path.Combine(root, (mapSid ?? "Unknown").Replace('/', '_') + ".mp4"),
                new AssemblyOptions { DryRun = true });

            Assert(outputs.Count == 2, "expected one output per map SID");
            Assert(outputs[0].MapSid == "Maps/Alpha" && outputs[0].ClipCount == 2, "alpha output should keep both alpha clips");
            Assert(outputs[1].MapSid == "Maps/Beta" && outputs[1].ClipCount == 1, "beta output should keep beta clip");
            Assert(File.Exists(Path.Combine(root, "map_outputs.json")), "map output manifest should be written");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static void AssemblyDisambiguatesCollidingMapOutputs()
    {
        var doc = new ClipIntervalsDocument
        {
            Clips =
            [
                new ClipInterval
                {
                    ClipId = "a",
                    Room = "a",
                    MapSid = "Maps/Alpha",
                    IsValid = true,
                    SourceFileMapping = [new ClipFileSlice { SourcePath = "run.mkv", SourceStartDurationMs = 0, SourceEndDurationMs = 1_000 }]
                },
                new ClipInterval
                {
                    ClipId = "b",
                    Room = "b",
                    MapSid = "Maps/Beta",
                    IsValid = true,
                    SourceFileMapping = [new ClipFileSlice { SourcePath = "run.mkv", SourceStartDurationMs = 1_000, SourceEndDurationMs = 2_000 }]
                }
            ]
        };

        var root = Path.Combine(Path.GetTempPath(), "CelesteAutoCutSelfTests", Guid.NewGuid().ToString("N"));
        try
        {
            var collidingPath = Path.Combine(root, "2026-05-19 20-31-08.mp4");
            var outputs = new AssemblyPlanner().AssembleByMap(
                doc,
                root,
                _ => collidingPath,
                new AssemblyOptions { DryRun = true });

            Assert(outputs.Count == 2, "expected both maps to be assembled");
            Assert(outputs.Select(o => Path.GetFullPath(o.FinalOutputPath)).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 2, "colliding map outputs must be disambiguated");
            Assert(outputs.All(o => o.Warnings.Contains("final_output_collision_avoided_by_map_folder")), "colliding outputs should report the map-folder adjustment");
            Assert(outputs.All(o => Path.GetDirectoryName(o.FinalOutputPath)?.StartsWith(root, StringComparison.OrdinalIgnoreCase) == true), "adjusted outputs should stay under the requested output root");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static readonly DateTimeOffset BaseUtc = new(2026, 5, 18, 0, 0, 0, TimeSpan.Zero);

    private static List<RoomEvent> StandardRoomEvents(DateTimeOffset start, int endOffsetSeconds = 2) =>
    [
        new RoomEvent { EventType = "room_enter", Utc = start, Room = "a", MapSid = "map" },
        new RoomEvent { EventType = "load_level", Utc = start, Room = "a", MapSid = "map", Notes = new Dictionary<string, object?> { ["playerIntro"] = "Transition" } },
        new RoomEvent { EventType = "death", Utc = start.AddMilliseconds(750), Room = "a", MapSid = "map" },
        new RoomEvent { EventType = "load_level", Utc = start.AddSeconds(1), Room = "a", MapSid = "map", Notes = new Dictionary<string, object?> { ["playerIntro"] = "Respawn" } },
        new RoomEvent { EventType = "transition", Utc = start.AddSeconds(endOffsetSeconds), Room = "a", NextRoom = "b", MapSid = "map" }
    ];

    private static Dictionary<string, object?> RespawnNotes(string playerIntro, double x, double y) => new()
    {
        ["playerIntro"] = playerIntro,
        ["hasRespawnPoint"] = "True",
        ["respawnPointX"] = x.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["respawnPointY"] = y.ToString(System.Globalization.CultureInfo.InvariantCulture)
    };

    private static Dictionary<string, object?> SpawnNotes(string playerIntro, double x, double y) => new()
    {
        ["playerIntro"] = playerIntro,
        ["hasSpawnPoint"] = "True",
        ["spawnPointX"] = x.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["spawnPointY"] = y.ToString(System.Globalization.CultureInfo.InvariantCulture)
    };

    private static Dictionary<string, object?> PlayerSampleNotes(string loadEventId, double x, double y) => new()
    {
        ["loadEventId"] = loadEventId,
        ["x"] = x.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["y"] = y.ToString(System.Globalization.CultureInfo.InvariantCulture)
    };

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

    private static bool HasClipCovering(ClipIntervalsDocument doc, DateTimeOffset startUtc, DateTimeOffset endUtc) =>
        doc.Clips.Any(c => c.StartUtc <= startUtc && c.EndUtc >= endUtc);

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
