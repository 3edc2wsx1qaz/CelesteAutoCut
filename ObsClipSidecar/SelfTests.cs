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
            ("first room transition load level does not cut off intro", FirstRoomTransitionLoadLevelDoesNotCutOffIntro),
            ("death room keeps room-entry intro before successful attempt", DeathRoomKeepsIntroBeforeClear),
            ("revisited branching room gets separate death entry clips", RevisitedBranchingRoomGetsSeparateDeathEntryClips),
            ("death on revisit still keeps earliest uncovered room intro", DeathOnRevisitKeepsEarliestUncoveredRoomIntro),
            ("strawberry room keeps collect-and-exit attempt after death", StrawberryRoomKeepsCollectedAttemptAfterDeath),
            ("strawberry before death does not authorize later exit", StrawberryBeforeDeathDoesNotAuthorizeLaterExit),
            ("strawberry room without death keeps entry through exit", StrawberryRoomWithoutDeathKeepsEntryThroughExit),
            ("accidental backtrack to cleared previous room is cut", AccidentalBacktrackToClearedPreviousRoomIsCut),
            ("branch return to hub room is kept", BranchReturnToHubRoomIsKept),
            ("unfinished room keeps room-entry intro at end", UnfinishedRoomKeepsIntroAtEnd),
            ("missing recording files invalidates clip", MissingRecordingFileMapping),
            ("invalid anchor gaps are reported", InvalidAnchorGap),
            ("manifest builder reconstructs recording and split files", ManifestBuilderReconstructsRecording),
            ("manifest builder honors record_file_changed newOutputPath", ManifestBuilderUsesNewOutputPath),
            ("manifest builder waits for terminal stop path after stopping transition", ManifestBuilderWaitsForTerminalStopPath),
            ("json reader is case insensitive for mod output", CaseInsensitiveJsonRead),
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
        Assert(doc.Clips[0].EndOutputDurationMs == 4_000, "adjacent room seam should stay on the observed transition boundary");
        Assert(doc.Clips[0].Reasons.Contains("adjacent_room_overlap_trimmed"), "first room should record overlap trim reason");
        Assert(doc.Clips[1].Reasons.Contains("adjacent_room_overlap_trimmed"), "second room should record overlap trim reason");
    }

    private static void AdjacentRoomTransitionBoundaryTrim()
    {
        var start = BaseUtc.AddSeconds(2);
        var events = new List<RoomEvent>
        {
            new() { EventType = "room_enter", Utc = start, Room = "a", MapSid = "map" },
            new() { EventType = "transition", Utc = start.AddSeconds(2), Room = "a", NextRoom = "b", MapSid = "map" },
            new() { EventType = "room_enter", Utc = start.AddSeconds(2), Room = "b", MapSid = "map" },
            new() { EventType = "transition", Utc = start.AddSeconds(4), Room = "b", NextRoom = "c", MapSid = "map" }
        };

        var doc = Generate(events, new SessionManifest { SessionId = "s", Recordings = [Recording("r", BaseUtc, "run.mp4", 0, 10_000)] }, new IntervalGenerationOptions
        {
            PreRollMs = 1_000,
            PostRollMs = 1_000,
            MaxAllowedAnchorGapMs = 1_500
        });

        Assert(doc.Clips.Count == 2, "expected both adjacent rooms to remain valid");
        Assert(doc.Clips[0].EndOutputDurationMs == 4_000, "first room should end at the transition boundary, not include the next room preroll");
        Assert(doc.Clips[1].StartOutputDurationMs == 4_000, "second room should start at the transition boundary, not duplicate first-room postroll");
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

        Assert(doc.Clips.Count == 1, "first room should produce one non-overlapping valid clip");
        Assert(doc.Clips[0].Reasons.Contains("final_successful_attempt"), "first room should be kept as the successful attempt");
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

        var roomBClips = doc.Clips.Where(c => c.Room == "b").OrderBy(c => c.StartUtc).ToList();
        Assert(roomBClips.Count == 2, "expected intro clip plus successful attempt after death in room b");
        Assert(roomBClips[0].Reasons.Contains("room_entry_intro_before_clear"), "death room should preserve entry through initial load_level");
        Assert(roomBClips[0].StartUtc == roomBEnter && roomBClips[0].EndUtc == introLoad, "death-room intro boundaries mismatch");
        Assert(roomBClips[1].Reasons.Contains("final_successful_attempt"), "second room-b clip should be the successful attempt");
        Assert(roomBClips[1].StartUtc == respawnLoad && roomBClips[1].EndUtc == clear, "successful attempt should restart from respawn load_level");
    }

    private static void RevisitedBranchingRoomGetsSeparateDeathEntryClips()
    {
        var start = BaseUtc.AddSeconds(2);
        var events = new List<RoomEvent>
        {
            new() { EventType = "room_enter", Utc = start, Room = "a", MapSid = "map" },
            new() { EventType = "transition", Utc = start.AddSeconds(1), Room = "a", NextRoom = "b", MapSid = "map" },
            new() { EventType = "room_enter", Utc = start.AddSeconds(1), Room = "b", MapSid = "map" },
            new() { EventType = "load_level", Utc = start.AddMilliseconds(1_010), Room = "b", MapSid = "map", Notes = new Dictionary<string, object?> { ["playerIntro"] = "Transition" } },
            new() { EventType = "death", Utc = start.AddMilliseconds(1_500), Room = "b", MapSid = "map" },
            new() { EventType = "load_level", Utc = start.AddSeconds(2), Room = "b", MapSid = "map", Notes = new Dictionary<string, object?> { ["playerIntro"] = "Respawn" } },
            new() { EventType = "transition", Utc = start.AddSeconds(3), Room = "b", NextRoom = "c", MapSid = "map" },
            new() { EventType = "room_enter", Utc = start.AddSeconds(3), Room = "c", MapSid = "map" },
            new() { EventType = "transition", Utc = start.AddSeconds(4), Room = "c", NextRoom = "b", MapSid = "map" },
            new() { EventType = "room_enter", Utc = start.AddSeconds(4), Room = "b", MapSid = "map" },
            new() { EventType = "load_level", Utc = start.AddMilliseconds(4_010), Room = "b", MapSid = "map", Notes = new Dictionary<string, object?> { ["playerIntro"] = "Transition" } },
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

        var roomBClips = doc.Clips.Where(c => c.Room == "b").OrderBy(c => c.StartUtc).ToList();
        Assert(roomBClips.Count == 4, "each visit to branching room b should keep entry-to-load-level plus final success");
        Assert(roomBClips.Count(c => c.Reasons.Contains("room_entry_intro_before_clear")) == 2, "both b visits should keep their own entry-load intro clip");
        Assert(roomBClips.Where(c => c.Reasons.Contains("final_successful_attempt")).All(c => c.StartUtc != start.AddMilliseconds(1_500) && c.StartUtc != start.AddMilliseconds(4_500)), "death timestamps must not become successful clip starts");
        Assert(roomBClips[0].StartUtc == start.AddSeconds(1) && roomBClips[0].EndUtc == start.AddMilliseconds(1_010), "first b visit entry-load boundaries mismatch");
        Assert(roomBClips[2].StartUtc == start.AddSeconds(4) && roomBClips[2].EndUtc == start.AddMilliseconds(4_010), "second b visit entry-load boundaries mismatch");
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
            new() { EventType = "transition", Utc = firstEnter, Room = "a", NextRoom = "b", MapSid = "map" },
            new() { EventType = "room_enter", Utc = firstEnter, Room = "b", MapSid = "map" },
            new() { EventType = "load_level", Utc = firstLoad, Room = "b", MapSid = "map", Notes = new Dictionary<string, object?> { ["playerIntro"] = "Transition" } },
            new() { EventType = "transition", Utc = start.AddSeconds(2), Room = "b", NextRoom = "a", MapSid = "map" },
            new() { EventType = "room_enter", Utc = start.AddSeconds(2), Room = "a", MapSid = "map" },
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

        var roomBIntros = doc.Clips.Where(c => c.Room == "b" && c.Reasons.Contains("room_entry_intro_before_clear")).OrderBy(c => c.StartUtc).ToList();
        Assert(roomBIntros.Count == 2, "death room should keep earliest uncovered entry-load intro and the later death-visit intro");
        Assert(roomBIntros[0].StartUtc == firstEnter && roomBIntros[0].EndUtc == firstLoad, "earliest room-b intro should not be lost after revisit death");
        Assert(roomBIntros[1].StartUtc == secondEnter && roomBIntros[1].EndUtc == secondLoad, "death-visit room-b intro should remain separate");
    }

    private static void StrawberryRoomKeepsCollectedAttemptAfterDeath()
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

        var success = doc.Clips.Single(c => c.Room == "berry" && c.Reasons.Contains("final_successful_attempt"));
        Assert(success.StartUtc == respawnLoad && success.EndUtc == clear, "strawberry success after death should start from respawn/load_level and include collect-to-exit");
        Assert(doc.Clips.Any(c => c.Room == "berry" && c.Reasons.Contains("room_entry_intro_before_clear")), "death strawberry room should still keep entry-load intro");
    }

    private static void StrawberryBeforeDeathDoesNotAuthorizeLaterExit()
    {
        var start = BaseUtc.AddSeconds(2);
        var events = new List<RoomEvent>
        {
            new() { EventType = "room_enter", Utc = start, Room = "berry", MapSid = "map" },
            new() { EventType = "load_level", Utc = start.AddMilliseconds(200), Room = "berry", MapSid = "map", Notes = new Dictionary<string, object?> { ["playerIntro"] = "Transition" } },
            new() { EventType = "strawberry_collect", Utc = start.AddMilliseconds(500), Room = "berry", MapSid = "map" },
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

        Assert(!doc.Clips.Any(c => c.Room == "berry" && c.Reasons.Contains("final_successful_attempt")), "strawberry collected before death must not make a later no-collect exit successful");
        Assert(doc.Clips.Single(c => c.Room == "berry").Reasons.Contains("room_entry_intro_before_clear"), "only the death-room intro should remain");
    }

    private static void StrawberryRoomWithoutDeathKeepsEntryThroughExit()
    {
        var start = BaseUtc.AddSeconds(2);
        var clear = start.AddSeconds(2);
        var events = new List<RoomEvent>
        {
            new() { EventType = "room_enter", Utc = start, Room = "berry", MapSid = "map" },
            new() { EventType = "load_level", Utc = start.AddMilliseconds(200), Room = "berry", MapSid = "map", Notes = new Dictionary<string, object?> { ["playerIntro"] = "Transition" } },
            new() { EventType = "strawberry_collect", Utc = start.AddSeconds(1), Room = "berry", MapSid = "map" },
            new() { EventType = "transition", Utc = clear, Room = "berry", NextRoom = "next", MapSid = "map" }
        };

        var doc = Generate(events, new SessionManifest { SessionId = "s", Recordings = [Recording("r", BaseUtc, "run.mp4", 0, 10_000)] }, new IntervalGenerationOptions
        {
            PreRollMs = 0,
            PostRollMs = 0,
            MaxAllowedAnchorGapMs = 1_500
        });

        var success = doc.Clips.Single(c => c.Room == "berry" && c.Reasons.Contains("final_successful_attempt"));
        Assert(success.StartUtc == start && success.EndUtc == clear, "strawberry room without death should keep entry -> collect -> exit");
    }

    private static void AccidentalBacktrackToClearedPreviousRoomIsCut()
    {
        var start = BaseUtc.AddSeconds(2);
        var events = new List<RoomEvent>
        {
            new() { EventType = "room_enter", Utc = start, Room = "a", MapSid = "map" },
            new() { EventType = "transition", Utc = start.AddSeconds(1), Room = "a", NextRoom = "b", MapSid = "map" },
            new() { EventType = "room_enter", Utc = start.AddSeconds(1), Room = "b", MapSid = "map" },
            new() { EventType = "transition", Utc = start.AddSeconds(2), Room = "b", NextRoom = "a", MapSid = "map" },
            new() { EventType = "room_enter", Utc = start.AddSeconds(2), Room = "a", MapSid = "map" },
            new() { EventType = "transition", Utc = start.AddSeconds(3), Room = "a", NextRoom = "b", MapSid = "map" },
            new() { EventType = "room_enter", Utc = start.AddSeconds(3), Room = "b", MapSid = "map" },
            new() { EventType = "transition", Utc = start.AddSeconds(4), Room = "b", NextRoom = "c", MapSid = "map" }
        };

        var doc = Generate(events, new SessionManifest { SessionId = "s", Recordings = [Recording("r", BaseUtc, "run.mp4", 0, 10_000)] }, new IntervalGenerationOptions
        {
            PreRollMs = 0,
            PostRollMs = 0,
            MaxAllowedAnchorGapMs = 1_500
        });

        var clips = doc.Clips.OrderBy(c => c.StartUtc).ToList();
        Assert(clips.Count == 2, "backtrack bounce should keep only the original cleared room and the later forward clear");
        Assert(clips[0].Room == "a" && clips[0].StartUtc == start && clips[0].EndUtc == start.AddSeconds(1), "initial room a clear should stay");
        Assert(clips[1].Room == "b" && clips[1].StartUtc == start.AddSeconds(3) && clips[1].EndUtc == start.AddSeconds(4), "room b should restart after returning from accidental backtrack");
        Assert(!doc.Clips.Any(c => c.Room == "a" && c.StartUtc == start.AddSeconds(2)), "already-cleared room revisit should be cut");
        Assert(!doc.Clips.Any(c => c.Room == "b" && c.EndUtc == start.AddSeconds(2)), "failed transition back to previous cleared room should be cut");
    }

    private static void BranchReturnToHubRoomIsKept()
    {
        var start = BaseUtc.AddSeconds(2);
        var events = new List<RoomEvent>
        {
            new() { EventType = "room_enter", Utc = start, Room = "hub", MapSid = "map" },
            new() { EventType = "transition", Utc = start.AddSeconds(1), Room = "hub", NextRoom = "side", MapSid = "map" },
            new() { EventType = "room_enter", Utc = start.AddSeconds(1), Room = "side", MapSid = "map" },
            new() { EventType = "transition", Utc = start.AddSeconds(2), Room = "side", NextRoom = "hub", MapSid = "map" },
            new() { EventType = "room_enter", Utc = start.AddSeconds(2), Room = "hub", MapSid = "map" },
            new() { EventType = "transition", Utc = start.AddSeconds(3), Room = "hub", NextRoom = "exit", MapSid = "map" }
        };

        var doc = Generate(events, new SessionManifest { SessionId = "s", Recordings = [Recording("r", BaseUtc, "run.mp4", 0, 10_000)] }, new IntervalGenerationOptions
        {
            PreRollMs = 0,
            PostRollMs = 0,
            MaxAllowedAnchorGapMs = 1_500
        });

        var clips = doc.Clips.OrderBy(c => c.StartUtc).ToList();
        Assert(clips.Count == 3, "branch return should keep hub entry, side branch, and returned hub exit clips");
        Assert(clips[0].Room == "hub" && clips[0].StartUtc == start && clips[0].EndUtc == start.AddSeconds(1), "initial hub branch entry should stay");
        Assert(clips[1].Room == "side" && clips[1].StartUtc == start.AddSeconds(1) && clips[1].EndUtc == start.AddSeconds(2), "side branch return should stay");
        Assert(clips[2].Room == "hub" && clips[2].StartUtc == start.AddSeconds(2) && clips[2].EndUtc == start.AddSeconds(3), "returned hub forward exit should stay");
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
