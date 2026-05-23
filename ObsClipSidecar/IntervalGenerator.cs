using System.Globalization;
using System.Text.Json;

namespace ObsClipSidecar;

public sealed class IntervalGenerator
{
    private static readonly TimeSpan RoomEntryDeathReloadWindow = TimeSpan.FromSeconds(5);

    public ClipIntervalsDocument Generate(IReadOnlyList<RoomEvent> roomEvents, SessionManifest manifest, IntervalGenerationOptions? options = null)
    {
        options ??= new IntervalGenerationOptions();
        var warnings = new List<string>();
        if (!manifest.Capabilities.SupportsOutputDurationAnchors)
        {
            warnings.Add("output_duration_anchors_unsupported");
        }

        if (!manifest.Capabilities.SupportsRecordFileChanged && manifest.Recordings.Any(r => r.Files.Count > 1))
        {
            warnings.Add("split_file_unsupported");
        }

        var sortedEvents = roomEvents
            .Select((e, i) => (Event: e, OriginalIndex: i))
            .OrderBy(x => x.Event.Utc)
            .ThenBy(x => x.Event.GameFrame ?? long.MaxValue)
            .ThenBy(x => x.OriginalIndex)
            .Select(x => x.Event)
            .ToList();

        var candidates = MergeAdjacentKeepCandidates(BuildLinearKeepCandidates(sortedEvents, warnings))
            .Select((c, index) => c with { Index = index })
            .ToList();
        var prepared = new List<PreparedClip>();
        var valid = new List<ClipInterval>();
        var invalid = new List<ClipInterval>();

        foreach (var candidate in candidates)
        {
            var timedCandidate = candidate;
            var usesDynamicRoomEntryEnd = false;
            var usesDynamicLoadLevelStart = false;
            var usesDynamicLoadLevelEnd = false;
            if (IsStandaloneRoomEntryLoad(candidate))
            {
                var hasSample = TryFindRoomEntryLoadPlayerPositionEnd(candidate, sortedEvents, out var roomEntryDynamicEnd, out _);
                if (hasSample)
                {
                    timedCandidate = candidate with { End = roomEntryDynamicEnd };
                    usesDynamicRoomEntryEnd = true;
                }
                else if (TryFindRoomEntryDeathReloadEnd(sortedEvents, candidate.End, out var reloadEnd))
                {
                    timedCandidate = candidate with { End = reloadEnd, BaseValidReason = "room_entry_load_death_reload" };
                }
            }

            if (IsLoadLevelStart(timedCandidate) &&
                TryFindLoadLevelPlayerPositionBoundary(timedCandidate.Start, sortedEvents, out var dynamicStart))
            {
                timedCandidate = timedCandidate with { Start = dynamicStart };
                usesDynamicLoadLevelStart = true;
            }

            if (!usesDynamicRoomEntryEnd &&
                IsLoadLevelEnd(timedCandidate) &&
                TryFindLoadLevelPlayerPositionBoundary(timedCandidate.End, sortedEvents, out var dynamicEnd))
            {
                timedCandidate = timedCandidate with { End = dynamicEnd };
                usesDynamicLoadLevelEnd = true;
            }

            var startEstimate = EstimateBoundary(timedCandidate.Start.Utc, manifest, options.MaxAllowedAnchorGapMs);
            var endEstimate = EstimateBoundary(timedCandidate.End.Utc, manifest, options.MaxAllowedAnchorGapMs);
            var baseReasons = new List<string>();
            var annotations = new List<string>();
            if (usesDynamicRoomEntryEnd)
            {
                annotations.Add("room_entry_load_player_position_end");
            }
            if (usesDynamicLoadLevelStart)
            {
                annotations.Add("load_level_player_position_start");
            }
            if (usesDynamicLoadLevelEnd)
            {
                annotations.Add("load_level_player_position_end");
            }

            if (!startEstimate.IsValid)
            {
                if (TryClampStartToRecordingStart(candidate.Start.Utc, startEstimate, endEstimate, manifest, out var clampedStartEstimate))
                {
                    startEstimate = clampedStartEstimate;
                    annotations.Add("start_clamped_to_recording_start");
                }
                else
                {
                    baseReasons.AddRange(startEstimate.Reasons.Select(r => "start_" + r));
                }
            }

            if (!endEstimate.IsValid)
            {
                baseReasons.AddRange(endEstimate.Reasons.Select(r => "end_" + r));
            }

            if (startEstimate.RecordingId != endEstimate.RecordingId)
            {
                baseReasons.Add("cross_recording_clip");
            }

            if (manifest.Recordings.FirstOrDefault(r => r.RecordingId == startEstimate.RecordingId) is not { } recording)
            {
                baseReasons.Add("missing_recording");
                AddInvalid(candidate, startEstimate, endEstimate, baseReasons, invalid);
                continue;
            }

            var preRollMs = usesDynamicLoadLevelStart || timedCandidate.IsCheckpointIntro || StartsAtRespawnLoadLevel(timedCandidate) ? 0 : options.PreRollMs;
            var addsRoomEntryLoadDelay = IsStandaloneRoomEntryLoad(timedCandidate) && !usesDynamicRoomEntryEnd;
            var postRollMs = timedCandidate.IsCheckpointIntro && !addsRoomEntryLoadDelay ? 0 : options.PostRollMs;
            var startMs = Math.Max(0, startEstimate.EstimatedOutputDurationMs - preRollMs);
            var endMs = endEstimate.EstimatedOutputDurationMs + postRollMs;
            var recordingEndMs = recording.Files.Count == 0 ? long.MaxValue : recording.Files.Max(f => f.EndDurationMs);
            if (postRollMs > 0 && endEstimate.EstimatedOutputDurationMs <= recordingEndMs && endMs > recordingEndMs)
            {
                endMs = recordingEndMs;
                annotations.Add("end_postroll_clamped_to_recording_end");
            }
            var preparedClip = new PreparedClip(timedCandidate, startEstimate, endEstimate, recording, startMs, endMs, baseReasons);
            preparedClip.Annotations.AddRange(annotations);
            if (addsRoomEntryLoadDelay && postRollMs > 0)
            {
                preparedClip.Annotations.Add("room_entry_load_delay");
            }
            prepared.Add(preparedClip);
        }

        TrimAdjacentRoomOverlaps(prepared);

        foreach (var clip in prepared.OrderBy(c => c.Candidate.Index))
        {
            var reasons = new List<string>(clip.BlockingReasons);
            if (clip.EndMs <= clip.StartMs)
            {
                reasons.Add("non_positive_clip_duration");
                AddInvalid(clip.Candidate, clip.StartEstimate, clip.EndEstimate, reasons, invalid, clip.StartMs, clip.EndMs);
                continue;
            }

            if (reasons.Count > 0)
            {
                AddInvalid(clip.Candidate, clip.StartEstimate, clip.EndEstimate, reasons, invalid, clip.StartMs, clip.EndMs);
                continue;
            }

            var mediaRanges = ApplyPausePolicy(clip.StartMs, clip.EndMs, clip.Recording, options, out var pausePolicy, out var pauseReasons);
            if (pauseReasons.Count > 0)
            {
                reasons.AddRange(pauseReasons);
            }

            if (mediaRanges.Count == 0 || reasons.Count > 0)
            {
                AddInvalid(clip.Candidate, clip.StartEstimate, clip.EndEstimate, reasons.Count == 0 ? ["empty_after_pause_split"] : reasons, invalid, clip.StartMs, clip.EndMs, pausePolicy);
                continue;
            }

            var splitIndex = 0;
            foreach (var (rangeStart, rangeEnd) in mediaRanges)
            {
                var mapping = MapToFiles(rangeStart, rangeEnd, clip.Recording, options.RequireExistingFiles, out var mapReasons);
                if (mapReasons.Count > 0)
                {
                    AddInvalid(clip.Candidate, clip.StartEstimate, clip.EndEstimate, mapReasons, invalid, rangeStart, rangeEnd, pausePolicy);
                    continue;
                }

                valid.Add(new ClipInterval
                {
                    ClipId = $"{clip.Candidate.RoomKey}_{clip.Candidate.Index:D4}_{splitIndex:D2}".Replace(' ', '_').Replace('/', '_').Replace('\\', '_'),
                    Room = clip.Candidate.Room,
                    MapSid = clip.Candidate.MapSid,
                    RecordingId = clip.Recording.RecordingId,
                    StartUtc = clip.Candidate.Start.Utc,
                    EndUtc = clip.Candidate.End.Utc,
                    StartOutputDurationMs = rangeStart,
                    EndOutputDurationMs = rangeEnd,
                    AlignmentMethod = "obs_output_duration_bracket",
                    AnchorBefore = clip.StartEstimate.AnchorBefore,
                    AnchorAfter = clip.EndEstimate.AnchorAfter,
                    ErrorBoundMs = Math.Max(clip.StartEstimate.ErrorBoundMs, clip.EndEstimate.ErrorBoundMs),
                    PausePolicy = pausePolicy,
                    SourceFileMapping = mapping,
                    Reasons = BuildValidReasons(splitIndex, clip)
                });
                splitIndex++;
            }
        }

        return new ClipIntervalsDocument
        {
            Options = options,
            Clips = valid,
            InvalidClips = invalid,
            Warnings = warnings
        };
    }

    private static List<ClipCandidate> BuildLinearKeepCandidates(IReadOnlyList<RoomEvent> events, List<string> warnings)
    {
        events = AddSyntheticRoomEnterForLoadPositionFallback(events);
        var result = new List<ClipCandidate>();
        var index = 0;
        var i = 0;

        while (i < events.Count)
        {
            var e = events[i];
            if (IsSessionStart(e))
            {
                var sessionStart = e;
                i++;
                var foundRoomEnter = false;
                while (i < events.Count)
                {
                    var next = events[i];
                    if (IsRoomEntry(next))
                    {
                        AddCandidate(result, ref index, sessionStart, next, next.Room ?? sessionStart.Room ?? "room", next.MapSid ?? sessionStart.MapSid, "session_intro", true);
                        foundRoomEnter = true;
                        break;
                    }

                    if (IsSessionBoundary(next))
                    {
                        warnings.Add(DiscardWarning("linear_discard_missing_room_enter_after_session", sessionStart));
                        break;
                    }

                    warnings.Add(DiscardWarning("linear_discard_unhandled_event_before_first_room_enter", next));
                    i++;
                }

                if (!foundRoomEnter)
                {
                    continue;
                }
            }
            else if (!IsRoomEntry(e))
            {
                if (IsExitLike(e) || IsSessionEnd(e) || IsPlayerPositionSample(e))
                {
                    i++;
                    continue;
                }

                warnings.Add(DiscardWarning("linear_discard_unhandled_event", e));
                i++;
                continue;
            }

            while (i < events.Count && !IsSessionStart(events[i]))
            {
                if (IsExitLike(events[i]) || IsSessionEnd(events[i]))
                {
                    i++;
                    break;
                }

                if (IsPlayerPositionSample(events[i]))
                {
                    i++;
                    continue;
                }

                if (!IsRoomEntry(events[i]))
                {
                    warnings.Add(DiscardWarning("linear_discard_unhandled_event", events[i]));
                    i++;
                    continue;
                }

                var roomEnter = events[i];
                i++;

                if (!TryReadRoomEntryLoad(events, ref i, roomEnter, warnings, result, ref index, out var firstLoad))
                {
                    continue;
                }

                var level = LevelIdentity.From(firstLoad);
                var roomEntryIntro = new RoomIntro(roomEnter, firstLoad);
                RoomEvent? activeLoad = firstLoad;
                RoomEvent? checkpointDeathLoad = null;
                RoomEvent? checkpointDeathSuccessStart = null;
                RoomEvent? pendingCheckpointDeath = null;
                var sessionDone = false;

                void FlushPendingCheckpointDeathWarning()
                {
                    if (pendingCheckpointDeath is not null)
                    {
                        warnings.Add(DiscardWarning("linear_discard_death_before_success", pendingCheckpointDeath));
                    }

                    pendingCheckpointDeath = null;
                    checkpointDeathLoad = null;
                    checkpointDeathSuccessStart = null;
                }

                while (i < events.Count)
                {
                    var current = events[i];
                    if (IsPlayerPositionSample(current))
                    {
                        i++;
                        continue;
                    }

                    if (IsSessionStart(current))
                    {
                        FlushPendingCheckpointDeathWarning();
                        EnsureRoomEntryIntroCandidate(result, ref index, roomEntryIntro);
                        sessionDone = true;
                        break;
                    }

                    if (IsSessionEnd(current))
                    {
                        FlushPendingCheckpointDeathWarning();
                        EnsureRoomEntryIntroCandidate(result, ref index, roomEntryIntro);
                        i++;
                        sessionDone = true;
                        break;
                    }

                    if (IsRoomEntry(current))
                    {
                        FlushPendingCheckpointDeathWarning();
                        EnsureRoomEntryIntroCandidate(result, ref index, roomEntryIntro);
                        warnings.Add(DiscardWarning("linear_discard_missing_success_before_room_enter", current));
                        break;
                    }

                    if (IsExitLike(current))
                    {
                        FlushPendingCheckpointDeathWarning();
                        EnsureRoomEntryIntroCandidate(result, ref index, roomEntryIntro);
                        if (activeLoad is not null)
                        {
                            warnings.Add(DiscardWarning("linear_discard_load_to_exit", current));
                        }

                        i++;
                        sessionDone = true;
                        break;
                    }

                    if (current.EventType is "death")
                    {
                        if (activeLoad is not null && level.Matches(current))
                        {
                            checkpointDeathLoad = activeLoad;
                            pendingCheckpointDeath = current;
                        }
                        else
                        {
                            FlushPendingCheckpointDeathWarning();
                            warnings.Add(DiscardWarning("linear_discard_death_before_success", current));
                        }

                        activeLoad = null;
                        checkpointDeathSuccessStart = null;
                        i++;
                        continue;
                    }

                    if (current.EventType is "load_end")
                    {
                        FlushPendingCheckpointDeathWarning();
                        activeLoad = null;
                        warnings.Add(DiscardWarning("linear_discard_death_before_success", current));
                        i++;
                        continue;
                    }

                    if (current.EventType is "load_level")
                    {
                        if (level.Matches(current))
                        {
                            if (checkpointDeathLoad is not null && RespawnPointChanged(checkpointDeathLoad, current))
                            {
                                checkpointDeathSuccessStart = checkpointDeathLoad;
                                pendingCheckpointDeath = null;
                            }
                            else
                            {
                                FlushPendingCheckpointDeathWarning();
                            }

                            checkpointDeathLoad = null;
                            activeLoad = current;
                        }
                        else
                        {
                            FlushPendingCheckpointDeathWarning();
                            warnings.Add(DiscardWarning("linear_discard_level_mismatch", current));
                        }

                        i++;
                        continue;
                    }

                    if (current.EventType is "transition")
                    {
                        if (activeLoad is not null && level.Matches(current))
                        {
                            var successStart = activeLoad;
                            var reason = checkpointDeathSuccessStart is null ? "final_successful_attempt" : "checkpoint_death_successful_attempt";
                            AddCandidate(result, ref index, successStart, current, activeLoad.Room ?? current.Room ?? "room", activeLoad.MapSid ?? current.MapSid, reason, false);
                            pendingCheckpointDeath = null;
                            i++;
                            KeepTransitionTail(events, ref i, current, warnings, result, ref index);
                            break;
                        }

                        FlushPendingCheckpointDeathWarning();
                        warnings.Add(DiscardWarning("linear_discard_transition_without_live_load", current));
                        i++;
                        continue;
                    }

                    if (IsStrawberryCollect(current))
                    {
                        if (activeLoad is not null && level.Matches(current))
                        {
                            AddCandidate(result, ref index, activeLoad, current, activeLoad.Room ?? current.Room ?? "room", activeLoad.MapSid ?? current.MapSid, "strawberry_collect_success", false);
                            i++;
                            var endedSession = KeepStrawberryTail(events, ref i, current, warnings, result, ref index);
                            if (endedSession)
                            {
                                sessionDone = true;
                            }

                            break;
                        }

                        FlushPendingCheckpointDeathWarning();
                        warnings.Add(DiscardWarning("linear_discard_strawberry_without_live_load", current));
                        i++;
                        continue;
                    }

                    if (current.EventType is "level_complete")
                    {
                        if (activeLoad is not null && level.Matches(current))
                        {
                            AddCandidate(result, ref index, activeLoad, current, activeLoad.Room ?? current.Room ?? "room", activeLoad.MapSid ?? current.MapSid, "final_successful_attempt", false);
                            i++;
                            KeepLevelCompleteTail(events, ref i, current, warnings, result, ref index);
                        }
                        else
                        {
                            FlushPendingCheckpointDeathWarning();
                            warnings.Add(DiscardWarning("linear_discard_level_complete_without_live_load", current));
                            i++;
                        }

                        sessionDone = true;
                        break;
                    }

                    warnings.Add(DiscardWarning("linear_discard_unhandled_event", current));
                    i++;
                }

                // OBS recording can be stopped while the game is still inside the
                // room, so no exit/session-end event may arrive. Keep the final
                // matched room_enter -> load_level intro as a real clip instead
                // of requiring a terminal exit signal.
                EnsureRoomEntryIntroCandidate(result, ref index, roomEntryIntro);

                if (sessionDone)
                {
                    break;
                }
            }
        }

        return result;
    }

    private static List<ClipCandidate> MergeAdjacentKeepCandidates(IReadOnlyList<ClipCandidate> candidates)
    {
        if (candidates.Count <= 1)
        {
            return candidates.ToList();
        }

        var result = new List<ClipCandidate>();
        foreach (var candidate in candidates.OrderBy(c => c.Start.Utc).ThenBy(c => c.End.Utc).ThenBy(c => c.Index))
        {
            if (result.Count == 0)
            {
                result.Add(candidate);
                continue;
            }

            var previous = result[^1];
            if (CanMergeAdjacent(previous, candidate))
            {
                result[^1] = previous with
                {
                    End = candidate.End,
                    Room = string.IsNullOrWhiteSpace(previous.Room) ? candidate.Room : previous.Room,
                    MapSid = previous.MapSid ?? candidate.MapSid,
                    BaseValidReason = BuildMergedReason(previous.BaseValidReason, candidate.BaseValidReason),
                    IsCheckpointIntro = previous.IsCheckpointIntro && candidate.IsCheckpointIntro
                };
                continue;
            }

            result.Add(candidate);
        }

        return result;
    }

    private static bool CanMergeAdjacent(ClipCandidate previous, ClipCandidate current)
        => previous.End.Utc == current.Start.Utc &&
           string.Equals(previous.MapSid ?? string.Empty, current.MapSid ?? string.Empty, StringComparison.Ordinal) &&
           !string.Equals(previous.BaseValidReason, "room_entry_load_death_reload", StringComparison.Ordinal);

    private static string BuildMergedReason(string previous, string current)
    {
        if (string.Equals(previous, current, StringComparison.Ordinal))
        {
            return previous;
        }

        if (string.Equals(previous, "merged_linear_interval", StringComparison.Ordinal) ||
            string.Equals(current, "merged_linear_interval", StringComparison.Ordinal))
        {
            return "merged_linear_interval";
        }

        return "merged_linear_interval";
    }

    private static bool TryReadRoomEntryLoad(
        IReadOnlyList<RoomEvent> events,
        ref int i,
        RoomEvent roomEnter,
        List<string> warnings,
        List<ClipCandidate> result,
        ref int index,
        out RoomEvent firstLoad)
    {
        while (i < events.Count)
        {
            var current = events[i];
            if (current.EventType is "load_level")
            {
                if (SameMapAndRoom(roomEnter, current))
                {
                    firstLoad = current;
                    AddCandidate(result, ref index, roomEnter, firstLoad, roomEnter.Room ?? firstLoad.Room ?? "room", roomEnter.MapSid ?? firstLoad.MapSid, "room_entry_load", true);
                    i++;
                    return true;
                }

                warnings.Add(DiscardWarning("linear_discard_level_mismatch_before_entry_load", current));
                i++;
                continue;
            }

            if (IsPlayerPositionSample(current))
            {
                if (IsSyntheticLoadPositionRoomEnter(roomEnter) && SameMapAndRoom(roomEnter, current))
                {
                    firstLoad = current;
                    AddCandidate(result, ref index, roomEnter, firstLoad, roomEnter.Room ?? firstLoad.Room ?? "room", roomEnter.MapSid ?? firstLoad.MapSid, "room_entry_load", true);
                    i++;
                    return true;
                }

                i++;
                continue;
            }

            if (IsSessionStart(current) || IsSessionEnd(current) || IsExitLike(current) || IsRoomEntry(current))
            {
                warnings.Add(DiscardWarning("linear_discard_missing_load_level_after_room_enter", roomEnter));
                firstLoad = roomEnter;
                return false;
            }

            warnings.Add(DiscardWarning("linear_discard_unhandled_event_before_entry_load", current));
            i++;
        }

        warnings.Add(DiscardWarning("linear_discard_missing_load_level_after_room_enter", roomEnter));
        firstLoad = roomEnter;
        return false;
    }

    private static bool TryFindRoomEntryDeathReloadEnd(
        IReadOnlyList<RoomEvent> events,
        RoomEvent firstLoad,
        out RoomEvent reloadLoad)
    {
        reloadLoad = firstLoad;
        var searchStart = -1;
        for (var i = 0; i < events.Count; i++)
        {
            if (ReferenceEquals(events[i], firstLoad))
            {
                searchStart = i + 1;
                break;
            }
        }

        if (searchStart < 0)
        {
            return false;
        }

        RoomEvent? death = null;
        var windowEnd = firstLoad.Utc + RoomEntryDeathReloadWindow;
        for (var i = searchStart; i < events.Count; i++)
        {
            var current = events[i];
            if (IsPlayerPositionSample(current))
            {
                continue;
            }

            if (death is null)
            {
                if (current.EventType is "death" &&
                    current.Utc <= windowEnd &&
                    SameMapAndRoom(firstLoad, current))
                {
                    death = current;
                    continue;
                }

                if (IsPostLoadSemanticBoundary(current))
                {
                    return false;
                }

                continue;
            }

            if (current.EventType is "load_level" && SameMapAndRoom(firstLoad, current))
            {
                reloadLoad = current;
                return true;
            }

            if (IsPostLoadSemanticBoundary(current))
            {
                return false;
            }
        }

        return false;
    }

    private static void KeepTransitionTail(
        IReadOnlyList<RoomEvent> events,
        ref int i,
        RoomEvent transition,
        List<string> warnings,
        List<ClipCandidate> result,
        ref int index)
    {
        while (i < events.Count)
        {
            var current = events[i];
            if (IsPlayerPositionSample(current))
            {
                i++;
                continue;
            }

            if (IsRoomEntry(current))
            {
                AddCandidate(result, ref index, transition, current, current.Room ?? transition.NextRoom ?? transition.Room ?? "room", current.MapSid ?? transition.MapSid, "transition_to_room_enter", true);
                return;
            }

            if (IsSessionStart(current))
            {
                warnings.Add(DiscardWarning("linear_discard_missing_room_enter_after_transition", transition));
                return;
            }

            if (IsExitLike(current) || IsSessionEnd(current))
            {
                warnings.Add(DiscardWarning("linear_discard_missing_room_enter_after_transition", transition));
                i++;
                return;
            }

            warnings.Add(DiscardWarning("linear_discard_unhandled_event_after_transition", current));
            i++;
        }

        warnings.Add(DiscardWarning("linear_discard_missing_room_enter_after_transition", transition));
    }

    private static bool KeepStrawberryTail(
        IReadOnlyList<RoomEvent> events,
        ref int i,
        RoomEvent strawberry,
        List<string> warnings,
        List<ClipCandidate> result,
        ref int index)
    {
        while (i < events.Count)
        {
            var current = events[i];
            if (IsPlayerPositionSample(current))
            {
                i++;
                continue;
            }

            if (IsRoomEntry(current))
            {
                AddCandidate(result, ref index, strawberry, current, current.Room ?? strawberry.Room ?? "room", current.MapSid ?? strawberry.MapSid, "strawberry_collect_to_room_enter", true);
                return false;
            }

            if (IsExitLike(current) || IsSessionEnd(current))
            {
                AddCandidate(result, ref index, strawberry, current, strawberry.Room ?? current.Room ?? "room", strawberry.MapSid ?? current.MapSid, "strawberry_collect_to_exit", true);
                i++;
                return true;
            }

            if (IsSessionStart(current))
            {
                warnings.Add(DiscardWarning("linear_discard_missing_room_enter_or_exit_after_strawberry", strawberry));
                return true;
            }

            i++;
        }

        warnings.Add(DiscardWarning("linear_discard_missing_room_enter_or_exit_after_strawberry", strawberry));
        return true;
    }

    private static void KeepLevelCompleteTail(
        IReadOnlyList<RoomEvent> events,
        ref int i,
        RoomEvent levelComplete,
        List<string> warnings,
        List<ClipCandidate> result,
        ref int index)
    {
        while (i < events.Count)
        {
            var current = events[i];
            if (IsPlayerPositionSample(current))
            {
                i++;
                continue;
            }

            if (IsExitLike(current))
            {
                AddCandidate(result, ref index, levelComplete, current, levelComplete.Room ?? current.Room ?? "room", levelComplete.MapSid ?? current.MapSid, "level_complete_to_exit", true);
                i++;
                return;
            }

            if (IsSessionStart(current) || IsSessionEnd(current))
            {
                warnings.Add(DiscardWarning("linear_discard_missing_exit_after_level_complete", levelComplete));
                return;
            }

            if (IsRoomEntry(current))
            {
                warnings.Add(DiscardWarning("linear_discard_room_enter_after_level_complete_before_exit", current));
                return;
            }

            i++;
        }

        warnings.Add(DiscardWarning("linear_discard_missing_exit_after_level_complete", levelComplete));
    }

    private static void AddCandidate(List<ClipCandidate> result, ref int index, RoomEvent start, RoomEvent end, string room, string? mapSid, string reason, bool isCheckpointIntro)
    {
        result.Add(new ClipCandidate(index++, start, end, room, mapSid, reason, isCheckpointIntro));
    }

    private static IReadOnlyList<RoomEvent> AddSyntheticRoomEnterForLoadPositionFallback(IReadOnlyList<RoomEvent> events)
    {
        if (events.Any(IsRoomEntry))
        {
            return events;
        }

        var fallbackIndex = -1;
        for (var i = 0; i < events.Count; i++)
        {
            if (IsLoadPositionFallbackStart(events[i]))
            {
                fallbackIndex = i;
                break;
            }
        }

        if (fallbackIndex < 0)
        {
            return events;
        }

        var fallback = events[fallbackIndex];
        var syntheticRoomEnter = fallback with
        {
            EventType = "room_enter",
            Utc = fallback.Utc,
            EventId = "synthetic-room-enter-" + fallback.EventId,
            Notes = AddSyntheticLoadPositionFallbackNote(fallback.Notes)
        };

        var normalized = events.ToList();
        normalized.Insert(fallbackIndex, syntheticRoomEnter);
        return normalized;
    }

    private static bool IsLoadPositionFallbackStart(RoomEvent e)
        => string.Equals(e.EventType, "load_level", StringComparison.Ordinal) ||
           IsPlayerPositionSample(e);

    private static Dictionary<string, object?> AddSyntheticLoadPositionFallbackNote(Dictionary<string, object?>? notes)
    {
        var result = notes is null
            ? new Dictionary<string, object?>()
            : new Dictionary<string, object?>(notes);
        result["syntheticRoomEnter"] = "load_position_fallback";
        return result;
    }

    private static bool IsSyntheticLoadPositionRoomEnter(RoomEvent e)
        => string.Equals(GetNoteString(e, "syntheticRoomEnter"), "load_position_fallback", StringComparison.Ordinal);

    private static void EnsureRoomEntryIntroCandidate(List<ClipCandidate> result, ref int index, RoomIntro intro)
    {
        if (result.Any(candidate =>
                ReferenceEquals(candidate.Start, intro.Entry) &&
                ReferenceEquals(candidate.End, intro.InitialCheckpoint) &&
                string.Equals(candidate.BaseValidReason, "room_entry_load", StringComparison.Ordinal)))
        {
            return;
        }

        AddCandidate(
            result,
            ref index,
            intro.Entry,
            intro.InitialCheckpoint,
            intro.Entry.Room ?? intro.InitialCheckpoint.Room ?? "room",
            intro.Entry.MapSid ?? intro.InitialCheckpoint.MapSid,
            "room_entry_load",
            isCheckpointIntro: true);
    }

    private static bool IsSessionStart(RoomEvent e)
        => e.EventType is "session_start";

    private static bool IsSessionEnd(RoomEvent e)
        => e.EventType is "session_end";

    private static bool IsSessionBoundary(RoomEvent e)
        => IsSessionStart(e) || IsSessionEnd(e) || IsExitLike(e);

    private static bool IsRoomEntry(RoomEvent e)
        => e.EventType is "room_enter" or "room_start";

    private static bool IsExitLike(RoomEvent e)
        => e.EventType is "exit" or "on_exit";

    private static string DiscardWarning(string code, RoomEvent e)
        => $"{code}:{e.MapSid ?? ""}:{e.Room ?? ""}:{e.Utc:O}";

    private static List<ClipCandidate> BuildSuccessfulAttemptCandidates(IReadOnlyList<RoomEvent> events, IReadOnlySet<RoomEvent> suppressedTransitions)
    {
        var result = new List<ClipCandidate>();
        RoomEvent? currentRoomStart = null;
        RoomEvent? attemptStart = null;
        var attemptFailed = false;
        var currentVisitSawStrawberry = false;
        var attemptCollectedStrawberry = false;
        var index = 0;

        foreach (var e in events)
        {
            if (e.IsRoomStart)
            {
                currentRoomStart = e;
                attemptStart = e;
                attemptFailed = false;
                currentVisitSawStrawberry = false;
                attemptCollectedStrawberry = false;
                continue;
            }

            if (e.EventType is "transition")
            {
                if (currentRoomStart is not null &&
                    attemptStart is not null &&
                    !suppressedTransitions.Contains(e) &&
                    (!currentVisitSawStrawberry || attemptCollectedStrawberry))
                {
                    result.Add(new ClipCandidate(index++, attemptStart, e, currentRoomStart.Room ?? e.Room ?? "room", currentRoomStart.MapSid ?? e.MapSid, "final_successful_attempt", false));
                }

                currentRoomStart = e with { EventType = "room_start", Room = e.NextRoom ?? e.Room };
                attemptStart = currentRoomStart;
                attemptFailed = false;
                currentVisitSawStrawberry = false;
                attemptCollectedStrawberry = false;
                continue;
            }

            if (e.EventType is "level_complete")
            {
                if (currentRoomStart is not null &&
                    attemptStart is not null &&
                    (!currentVisitSawStrawberry || attemptCollectedStrawberry))
                {
                    result.Add(new ClipCandidate(index++, attemptStart, e, currentRoomStart.Room ?? e.Room ?? "room", currentRoomStart.MapSid ?? e.MapSid, "final_successful_attempt", false));
                }
                currentRoomStart = null;
                attemptStart = null;
                currentVisitSawStrawberry = false;
                attemptCollectedStrawberry = false;
                continue;
            }

            if (e.EventType is "on_exit" or "exit")
            {
                if (currentRoomStart is not null && attemptStart is not null)
                {
                    // Explicit exit is not a successful room boundary, so emit no kept candidate.
                    currentRoomStart = null;
                    attemptStart = null;
                }
                currentVisitSawStrawberry = false;
                attemptCollectedStrawberry = false;
                continue;
            }

            if (e.EventType is "death" or "load_end")
            {
                attemptFailed = true;
                attemptStart = null;
                attemptCollectedStrawberry = false;
                continue;
            }

            if (e.EventType is "respawn")
            {
                attemptFailed = true;
                attemptStart = e;
                attemptCollectedStrawberry = false;
                continue;
            }

            if (e.EventType is "load_level")
            {
                if (attemptFailed || IsRespawnLoadLevel(e))
                {
                    attemptStart = e;
                    attemptCollectedStrawberry = false;
                }

                attemptFailed = false;
            }

            if (IsStrawberryCollect(e) && currentRoomStart is not null && SameRoom(currentRoomStart, e))
            {
                currentVisitSawStrawberry = true;
                if (attemptStart is not null)
                {
                    result.Add(new ClipCandidate(index++, attemptStart, e, currentRoomStart.Room ?? e.Room ?? "room", currentRoomStart.MapSid ?? e.MapSid, "strawberry_collect_success", false));
                }

                attemptStart = null;
                attemptCollectedStrawberry = false;
            }
        }

        return result;
    }

    private static List<ClipCandidate> BuildCheckpointIntroCandidates(
        IReadOnlyList<RoomEvent> events,
        IReadOnlySet<RoomEvent> suppressedTransitions,
        IReadOnlyList<ClipCandidate> successfulAttemptCandidates)
    {
        var result = new List<ClipCandidate>();
        var firstIntrosByRoom = new Dictionary<RoomIdentity, RoomIntro>();
        var roomsWithDeaths = new HashSet<RoomIdentity>();
        var emittedIntros = new HashSet<IntroIdentity>();
        RoomLifecycle? current = null;
        var index = 0;

        foreach (var e in events)
        {
            if (e.EventType is "room_enter")
            {
                current = new RoomLifecycle(e);
                continue;
            }

            if (current is null)
            {
                continue;
            }

            if (e.EventType is "load_level" && current.InitialCheckpoint is null && IsInitialCheckpointLoadLevel(current.Entry, e))
            {
                current.InitialCheckpoint = e;
                var identity = RoomIdentity.From(current.Entry);
                firstIntrosByRoom.TryAdd(identity, new RoomIntro(current.Entry, current.InitialCheckpoint));
                continue;
            }

            if (e.EventType is "death" && SameRoom(current.Entry, e))
            {
                current.HadDeath = true;
                roomsWithDeaths.Add(RoomIdentity.From(current.Entry));
                continue;
            }

            if (e.EventType is "transition" or "level_complete")
            {
                if (current.InitialCheckpoint is not null &&
                    SameRoom(current.Entry, e) &&
                    current.HadDeath &&
                    !suppressedTransitions.Contains(e))
                {
                    AddIntroCandidate(result, emittedIntros, ref index, current.Entry, current.InitialCheckpoint, current.Entry.Room ?? e.Room ?? "room", current.Entry.MapSid ?? e.MapSid, "room_entry_intro_before_clear");
                }

                current = null;
                continue;
            }

            if (e.EventType is "on_exit" or "exit" or "session_end")
            {
                if (current.InitialCheckpoint is not null)
                {
                    AddIntroCandidate(result, emittedIntros, ref index, current.Entry, current.InitialCheckpoint, current.Entry.Room ?? e.Room ?? "room", current.Entry.MapSid ?? e.MapSid, "room_entry_intro_at_end");
                }

                current = null;
            }
        }

        foreach (var room in roomsWithDeaths)
        {
            if (!firstIntrosByRoom.TryGetValue(room, out var intro))
            {
                continue;
            }

            if (SuccessfulAttemptAlreadyCoversIntro(successfulAttemptCandidates, intro))
            {
                continue;
            }

            AddIntroCandidate(result, emittedIntros, ref index, intro.Entry, intro.InitialCheckpoint, intro.Entry.Room ?? "room", intro.Entry.MapSid, "room_entry_intro_before_clear");
        }

        return result;
    }

    private static HashSet<RoomEvent> FindAccidentalBacktrackTransitions(IReadOnlyList<RoomEvent> events)
    {
        var suppressed = new HashSet<RoomEvent>(ReferenceEqualityComparer.Instance);
        var transitions = events
            .Where(e => e.EventType is "transition" && !string.IsNullOrWhiteSpace(e.Room) && !string.IsNullOrWhiteSpace(e.NextRoom))
            .ToList();

        for (var i = 0; i + 2 < transitions.Count; i++)
        {
            var forward = transitions[i];
            var reversed = transitions[i + 1];
            var returnForward = transitions[i + 2];
            if (IsReverseTransition(forward, reversed) && IsSameDirectedTransition(forward, returnForward))
            {
                suppressed.Add(reversed);
                suppressed.Add(returnForward);
            }
        }

        return suppressed;
    }

    private static void TrimAdjacentRoomOverlaps(List<PreparedClip> clips)
    {
        PreparedClip? previous = null;
        foreach (var clip in clips
                     .OrderBy(c => c.Recording.RecordingId, StringComparer.Ordinal)
                     .ThenBy(c => c.StartMs)
                     .ThenBy(c => c.EndMs)
                     .ThenBy(c => c.Candidate.Index))
        {
            if (clip.EndMs <= clip.StartMs || clip.BlockingReasons.Count > 0)
            {
                continue;
            }

            if (previous is not null &&
                string.Equals(previous.Recording.RecordingId, clip.Recording.RecordingId, StringComparison.Ordinal) &&
                clip.StartMs < previous.EndMs)
            {
                var seamMs = ResolveAdjacentSeamMs(previous, clip);
                previous.EndMs = seamMs;
                clip.StartMs = seamMs;
                previous.Annotations.Add("adjacent_room_overlap_trimmed");
                clip.Annotations.Add("adjacent_room_overlap_trimmed");
            }

            previous = clip;
        }
    }

    private static long ResolveAdjacentSeamMs(PreparedClip previous, PreparedClip current)
    {
        if (previous.Candidate.End.Utc <= current.Candidate.Start.Utc)
        {
            var previousBoundaryMs = previous.EndEstimate.EstimatedOutputDurationMs;
            var currentBoundaryMs = current.StartEstimate.EstimatedOutputDurationMs;
            var seamMs = Math.Max(previousBoundaryMs, currentBoundaryMs);
            seamMs = Math.Min(seamMs, previous.EndMs);
            seamMs = Math.Min(seamMs, current.EndMs);
            seamMs = Math.Max(seamMs, previous.StartMs);
            return seamMs;
        }

        return Math.Min(previous.EndMs, Math.Max(previous.StartMs, current.StartMs));
    }

    private static List<string> BuildValidReasons(int splitIndex, PreparedClip clip)
    {
        var reasons = new List<string> { clip.Candidate.BaseValidReason };
        if (splitIndex > 0)
        {
            reasons.Add("pause_split_continuation");
        }

        foreach (var reason in clip.Annotations)
        {
            if (!reasons.Contains(reason, StringComparer.Ordinal))
            {
                reasons.Add(reason);
            }
        }

        return reasons;
    }

    private static bool StartsAtRespawnLoadLevel(ClipCandidate candidate)
        => string.Equals(candidate.Start.EventType, "load_level", StringComparison.Ordinal) &&
           IsRespawnLoadLevel(candidate.Start);

    private static bool IsLoadLevelStart(ClipCandidate candidate)
        => string.Equals(candidate.Start.EventType, "load_level", StringComparison.Ordinal);

    private static bool IsLoadLevelEnd(ClipCandidate candidate)
        => string.Equals(candidate.End.EventType, "load_level", StringComparison.Ordinal);

    private static bool IsStandaloneRoomEntryLoad(ClipCandidate candidate)
        => string.Equals(candidate.BaseValidReason, "room_entry_load", StringComparison.Ordinal);

    private static bool TryFindLoadLevelPlayerPositionBoundary(RoomEvent loadLevel, IReadOnlyList<RoomEvent> events, out RoomEvent dynamicStart)
    {
        dynamicStart = loadLevel;
        var loadIndex = -1;
        for (var i = 0; i < events.Count; i++)
        {
            if (ReferenceEquals(events[i], loadLevel))
            {
                loadIndex = i;
                break;
            }
        }

        if (loadIndex < 0)
        {
            return false;
        }

        TryGetSpawnPoint(loadLevel, out var spawnPoint);
        RoomEvent? best = null;
        var bestDistanceSquared = double.MaxValue;
        for (var i = loadIndex + 1; i < events.Count; i++)
        {
            var current = events[i];
            if (IsPlayerPositionSample(current))
            {
                if (!SameMapAndRoom(loadLevel, current) ||
                    !SampleBelongsToLoad(current, loadLevel) ||
                    !TryGetPlayerPosition(current, out var position))
                {
                    continue;
                }

                if (!TryGetSpawnPoint(loadLevel, out spawnPoint))
                {
                    dynamicStart = current;
                    return true;
                }

                var distanceSquared = DistanceSquared(spawnPoint, position);
                if (distanceSquared < bestDistanceSquared)
                {
                    best = current;
                    bestDistanceSquared = distanceSquared;
                }

                continue;
            }

            if (IsPostLoadSemanticBoundary(current))
            {
                break;
            }
        }

        if (best is null)
        {
            return false;
        }

        dynamicStart = best;
        return true;
    }

    private static bool TryFindRoomEntryLoadPlayerPositionEnd(
        ClipCandidate candidate,
        IReadOnlyList<RoomEvent> events,
        out RoomEvent dynamicEnd,
        out bool dynamicEndIsStationary)
    {
        dynamicEnd = candidate.End;
        dynamicEndIsStationary = false;
        var hasSpawnPoint = TryGetSpawnPoint(candidate.End, out var spawnPoint);

        var loadIndex = -1;
        for (var i = 0; i < events.Count; i++)
        {
            if (ReferenceEquals(events[i], candidate.End))
            {
                loadIndex = i;
                break;
            }
        }

        if (loadIndex < 0)
        {
            return false;
        }

        RoomEvent? latestStationary = null;
        RoomEvent? bestByDistance = null;
        var bestDistanceSquared = double.MaxValue;
        for (var i = loadIndex + 1; i < events.Count; i++)
        {
            var current = events[i];
            if (IsPlayerPositionSample(current))
            {
                if (!SameMapAndRoom(candidate.End, current) ||
                    !SampleBelongsToLoad(current, candidate.End))
                {
                    continue;
                }

                if (!hasSpawnPoint)
                {
                    dynamicEnd = current;
                    dynamicEndIsStationary = IsStationaryPlayerPositionSample(current);
                    return true;
                }

                if (!TryGetPlayerPosition(current, out var position))
                {
                    continue;
                }

                var distanceSquared = DistanceSquared(spawnPoint, position);
                if (IsStationaryPlayerPositionSample(current) &&
                    (latestStationary is null || current.Utc > latestStationary.Utc))
                {
                    latestStationary = current;
                }

                if (distanceSquared < bestDistanceSquared)
                {
                    bestByDistance = current;
                    bestDistanceSquared = distanceSquared;
                }

                continue;
            }

            if (IsPostLoadSemanticBoundary(current))
            {
                break;
            }
        }

        if (latestStationary is not null)
        {
            dynamicEnd = latestStationary;
            dynamicEndIsStationary = true;
            return true;
        }

        if (bestByDistance is null)
        {
            return false;
        }

        dynamicEnd = bestByDistance;
        return true;
    }

    private static bool IsStationaryPlayerPositionSample(RoomEvent sample)
        => bool.TryParse(GetNoteString(sample, "stationary"), out var stationary) && stationary;

    private static bool SampleBelongsToLoad(RoomEvent sample, RoomEvent loadLevel)
    {
        var sampleLoadEventId = GetNoteString(sample, "loadEventId");
        return string.IsNullOrWhiteSpace(sampleLoadEventId) ||
               string.Equals(sampleLoadEventId, loadLevel.EventId, StringComparison.Ordinal);
    }

    private static bool IsPostLoadSemanticBoundary(RoomEvent e)
        => e.EventType is "session_start" or "session_end" or "room_enter" or "room_start" or
           "load_level" or "death" or "load_end" or "transition" or "level_complete" or
           "exit" or "on_exit" or "strawberry_collect" or "berry_collect";

    private static double DistanceSquared(RespawnPoint left, RespawnPoint right)
    {
        var dx = left.X - right.X;
        var dy = left.Y - right.Y;
        return (dx * dx) + (dy * dy);
    }

    private static BoundaryEstimate EstimateBoundary(DateTimeOffset utc, SessionManifest manifest, long maxAllowedAnchorGapMs)
    {
        var reasons = new List<string>();
        RecordingManifest? selectedRecording = null;
        ObsAnchor? before = null;
        ObsAnchor? after = null;

        foreach (var recording in manifest.Recordings)
        {
            var anchors = recording.Anchors.OrderBy(a => a.Utc).ToList();
            var b = anchors.LastOrDefault(a => a.Utc <= utc);
            var a = anchors.FirstOrDefault(a => a.Utc >= utc);
            if (b is not null && a is not null)
            {
                selectedRecording = recording;
                before = b;
                after = a;
                break;
            }
        }

        if (selectedRecording is null || before is null || after is null)
        {
            return new BoundaryEstimate { Utc = utc, IsValid = false, Reasons = ["missing_bracket_anchors"] };
        }

        var wallGapMs = (long)Math.Ceiling((after.Utc - before.Utc).Duration().TotalMilliseconds);
        if (wallGapMs > maxAllowedAnchorGapMs)
        {
            reasons.Add("anchor_gap_exceeds_max");
        }

        var estimated = InterpolateDuration(utc, before, after);
        if (before.OutputPaused || after.OutputPaused)
        {
            reasons.Add("boundary_bracket_touches_pause");
        }

        return new BoundaryEstimate
        {
            Utc = utc,
            RecordingId = selectedRecording.RecordingId,
            AnchorBefore = before,
            AnchorAfter = after,
            EstimatedOutputDurationMs = estimated,
            ErrorBoundMs = wallGapMs,
            IsValid = reasons.Count == 0,
            Reasons = reasons
        };
    }

    private static bool TryClampStartToRecordingStart(
        DateTimeOffset originalStartUtc,
        BoundaryEstimate originalStartEstimate,
        BoundaryEstimate endEstimate,
        SessionManifest manifest,
        out BoundaryEstimate clampedStartEstimate)
    {
        clampedStartEstimate = originalStartEstimate;
        if (!originalStartEstimate.Reasons.Contains("missing_bracket_anchors") ||
            !endEstimate.IsValid ||
            string.IsNullOrWhiteSpace(endEstimate.RecordingId))
        {
            return false;
        }

        var recording = manifest.Recordings.FirstOrDefault(r => string.Equals(r.RecordingId, endEstimate.RecordingId, StringComparison.Ordinal));
        var firstAnchor = recording?.Anchors.OrderBy(a => a.Utc).FirstOrDefault();
        if (recording is null || firstAnchor is null || originalStartUtc > firstAnchor.Utc || endEstimate.Utc < firstAnchor.Utc)
        {
            return false;
        }

        clampedStartEstimate = new BoundaryEstimate
        {
            Utc = firstAnchor.Utc,
            RecordingId = recording.RecordingId,
            AnchorBefore = firstAnchor,
            AnchorAfter = firstAnchor,
            EstimatedOutputDurationMs = firstAnchor.OutputDurationMs,
            ErrorBoundMs = 0,
            IsValid = true
        };
        return true;
    }

    private static long InterpolateDuration(DateTimeOffset utc, ObsAnchor before, ObsAnchor after)
    {
        var wallSpan = (after.Utc - before.Utc).TotalMilliseconds;
        if (wallSpan <= 0)
        {
            return before.OutputDurationMs;
        }

        var position = (utc - before.Utc).TotalMilliseconds / wallSpan;
        return (long)Math.Round(before.OutputDurationMs + ((after.OutputDurationMs - before.OutputDurationMs) * position));
    }

    private static List<(long Start, long End)> ApplyPausePolicy(long startMs, long endMs, RecordingManifest recording, IntervalGenerationOptions options, out string pausePolicy, out List<string> reasons)
    {
        reasons = [];
        var pauses = recording.Pauses.Where(p => p.EndDurationMs > startMs && p.StartDurationMs < endMs).OrderBy(p => p.StartDurationMs).ToList();
        if (pauses.Count == 0)
        {
            pausePolicy = "no_pause_seen";
            return [(startMs, endMs)];
        }

        if (!options.SplitOnPause)
        {
            pausePolicy = "invalidate_on_overlap";
            reasons.Add("pause_overlap");
            return [];
        }

        pausePolicy = "supported_interval_subtraction";
        var ranges = new List<(long Start, long End)>();
        var cursor = startMs;
        foreach (var pause in pauses)
        {
            if (pause.EndDurationMs <= pause.StartDurationMs)
            {
                reasons.Add("invalid_pause_boundary");
                continue;
            }

            if (pause.StartDurationMs > cursor)
            {
                ranges.Add((cursor, Math.Min(pause.StartDurationMs, endMs)));
            }
            cursor = Math.Max(cursor, pause.EndDurationMs);
        }

        if (cursor < endMs)
        {
            ranges.Add((cursor, endMs));
        }

        return ranges.Where(r => r.End > r.Start).ToList();
    }

    private static List<ClipFileSlice> MapToFiles(long startMs, long endMs, RecordingManifest recording, bool requireExistingFiles, out List<string> reasons)
    {
        reasons = [];
        var slices = new List<ClipFileSlice>();
        var files = recording.Files.OrderBy(f => f.StartDurationMs).ToList();
        if (files.Count == 0)
        {
            reasons.Add("missing_recording_files");
            return slices;
        }

        var cursor = startMs;
        foreach (var file in files)
        {
            if (file.EndDurationMs <= cursor || file.StartDurationMs >= endMs)
            {
                continue;
            }

            if (requireExistingFiles && !File.Exists(file.Path))
            {
                reasons.Add("source_file_missing:" + file.Path);
                return [];
            }

            var sliceStart = Math.Max(cursor, file.StartDurationMs);
            var sliceEnd = Math.Min(endMs, file.EndDurationMs);
            slices.Add(new ClipFileSlice
            {
                SourcePath = file.Path,
                SourceStartDurationMs = sliceStart - file.StartDurationMs,
                SourceEndDurationMs = sliceEnd - file.StartDurationMs
            });
            cursor = sliceEnd;
            if (cursor >= endMs)
            {
                break;
            }
        }

        if (cursor < endMs)
        {
            reasons.Add("source_file_mapping_gap");
        }

        return slices;
    }

    private static bool IsInitialCheckpointLoadLevel(RoomEvent entry, RoomEvent loadLevel)
    {
        if (!SameRoom(entry, loadLevel))
        {
            return false;
        }

        if (!string.Equals(loadLevel.EventType, "load_level", StringComparison.Ordinal))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(entry.AttemptId) &&
            string.Equals(entry.AttemptId, loadLevel.AttemptId, StringComparison.Ordinal))
        {
            return true;
        }

        var playerIntro = GetNoteString(loadLevel, "playerIntro");
        return !string.Equals(playerIntro, "Respawn", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRespawnLoadLevel(RoomEvent loadLevel)
        => string.Equals(GetNoteString(loadLevel, "playerIntro"), "Respawn", StringComparison.OrdinalIgnoreCase);

    private static bool RespawnPointChanged(RoomEvent previousLoad, RoomEvent nextLoad)
        => TryGetRespawnPoint(previousLoad, out var previous) &&
           TryGetRespawnPoint(nextLoad, out var next) &&
           !SameRespawnPoint(previous, next);

    private static bool TryGetSpawnPoint(RoomEvent loadLevel, out RespawnPoint point)
        => TryGetPoint(loadLevel, "spawnPointX", "spawnPointY", out point) ||
           TryGetRespawnPoint(loadLevel, out point);

    private static bool SameRespawnPoint(RespawnPoint left, RespawnPoint right)
        => Math.Abs(left.X - right.X) < 0.001 &&
           Math.Abs(left.Y - right.Y) < 0.001;

    private static bool TryGetRespawnPoint(RoomEvent loadLevel, out RespawnPoint point)
        => TryGetPoint(loadLevel, "respawnPointX", "respawnPointY", out point);

    private static bool TryGetPlayerPosition(RoomEvent sample, out RespawnPoint point)
        => TryGetPoint(sample, "x", "y", out point);

    private static bool TryGetPoint(RoomEvent e, string xKey, string yKey, out RespawnPoint point)
    {
        point = default;
        if (!TryGetNoteDouble(e, xKey, out var x) ||
            !TryGetNoteDouble(e, yKey, out var y))
        {
            return false;
        }

        point = new RespawnPoint(x, y);
        return true;
    }

    private static bool IsStrawberryCollect(RoomEvent e)
        => string.Equals(e.EventType, "strawberry_collect", StringComparison.Ordinal) ||
           string.Equals(e.EventType, "berry_collect", StringComparison.Ordinal);

    private static bool IsPlayerPositionSample(RoomEvent e)
        => string.Equals(e.EventType, "player_position_sample", StringComparison.Ordinal);

    private static void AddIntroCandidate(
        List<ClipCandidate> result,
        HashSet<IntroIdentity> emittedIntros,
        ref int index,
        RoomEvent start,
        RoomEvent end,
        string room,
        string? mapSid,
        string reason)
    {
        if (emittedIntros.Add(new IntroIdentity(mapSid ?? string.Empty, room, start.Utc, end.Utc, reason)))
        {
            result.Add(new ClipCandidate(index++, start, end, room, mapSid, reason, true));
        }
    }

    private static bool SuccessfulAttemptAlreadyCoversIntro(IReadOnlyList<ClipCandidate> successfulAttemptCandidates, RoomIntro intro)
        => successfulAttemptCandidates.Any(c =>
            !c.IsCheckpointIntro &&
            SameMapAndRoom(c.Start, intro.Entry) &&
            c.Start.Utc <= intro.Entry.Utc &&
            c.End.Utc >= intro.InitialCheckpoint.Utc);

    private static bool IsReverseTransition(RoomEvent forward, RoomEvent reversed)
        => SameMap(forward, reversed) &&
           string.Equals(forward.Room ?? string.Empty, reversed.NextRoom ?? string.Empty, StringComparison.Ordinal) &&
           string.Equals(forward.NextRoom ?? string.Empty, reversed.Room ?? string.Empty, StringComparison.Ordinal);

    private static bool IsSameDirectedTransition(RoomEvent left, RoomEvent right)
        => SameMap(left, right) &&
           string.Equals(left.Room ?? string.Empty, right.Room ?? string.Empty, StringComparison.Ordinal) &&
           string.Equals(left.NextRoom ?? string.Empty, right.NextRoom ?? string.Empty, StringComparison.Ordinal);

    private static bool SameMap(RoomEvent left, RoomEvent right)
        => string.Equals(left.MapSid ?? string.Empty, right.MapSid ?? string.Empty, StringComparison.Ordinal);

    private static bool SameRoom(RoomEvent left, RoomEvent right)
        => string.Equals(left.Room ?? string.Empty, right.Room ?? string.Empty, StringComparison.Ordinal);

    private static bool SameMapAndRoom(RoomEvent left, RoomEvent right)
        => SameMap(left, right) && SameRoom(left, right);

    private static string? GetNoteString(RoomEvent e, string key)
    {
        if (e.Notes is null || !e.Notes.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        return value.ToString();
    }

    private static bool TryGetNoteDouble(RoomEvent e, string key, out double value)
    {
        value = default;
        if (e.Notes is null || !e.Notes.TryGetValue(key, out var raw) || raw is null)
        {
            return false;
        }

        switch (raw)
        {
            case double d:
                value = d;
                return true;
            case float f:
                value = f;
                return true;
            case int i:
                value = i;
                return true;
            case long l:
                value = l;
                return true;
            case JsonElement json when json.ValueKind == JsonValueKind.Number && json.TryGetDouble(out var d):
                value = d;
                return true;
            case JsonElement json when json.ValueKind == JsonValueKind.String:
                return double.TryParse(json.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
            default:
                return double.TryParse(raw.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }
    }

    private static void AddInvalid(ClipCandidate candidate, BoundaryEstimate start, BoundaryEstimate end, List<string> reasons, List<ClipInterval> invalid, long? startOverride = null, long? endOverride = null, string pausePolicy = "no_pause_seen")
    {
        invalid.Add(new ClipInterval
        {
            ClipId = $"invalid_{candidate.RoomKey}_{candidate.Index:D4}_{invalid.Count:D2}".Replace(' ', '_').Replace('/', '_').Replace('\\', '_'),
            Room = candidate.Room,
            MapSid = candidate.MapSid,
            RecordingId = start.RecordingId,
            StartUtc = candidate.Start.Utc,
            EndUtc = candidate.End.Utc,
            StartOutputDurationMs = startOverride ?? start.EstimatedOutputDurationMs,
            EndOutputDurationMs = endOverride ?? end.EstimatedOutputDurationMs,
            AnchorBefore = start.AnchorBefore,
            AnchorAfter = end.AnchorAfter,
            ErrorBoundMs = Math.Max(start.ErrorBoundMs, end.ErrorBoundMs),
            PausePolicy = pausePolicy,
            IsValid = false,
            Status = "invalid",
            Reasons = reasons.Distinct().ToList()
        });
    }

    private sealed record ClipCandidate(int Index, RoomEvent Start, RoomEvent End, string Room, string? MapSid, string BaseValidReason, bool IsCheckpointIntro)
    {
        public string RoomKey => string.IsNullOrWhiteSpace(Room) ? "room" : Room;
    }

    private sealed class RoomLifecycle
    {
        public RoomLifecycle(RoomEvent entry)
        {
            Entry = entry;
        }

        public RoomEvent Entry { get; }
        public RoomEvent? InitialCheckpoint { get; set; }
        public bool HadDeath { get; set; }
    }

    private readonly record struct RoomIdentity(string MapSid, string Room)
    {
        public static RoomIdentity From(RoomEvent e)
            => new(e.MapSid ?? string.Empty, e.Room ?? string.Empty);
    }

    private readonly record struct LevelIdentity(string MapSid, string Room)
    {
        public static LevelIdentity From(RoomEvent e)
            => new(e.MapSid ?? string.Empty, e.Room ?? string.Empty);

        public bool Matches(RoomEvent e)
            => string.Equals(MapSid, e.MapSid ?? string.Empty, StringComparison.Ordinal) &&
               string.Equals(Room, e.Room ?? string.Empty, StringComparison.Ordinal);
    }

    private readonly record struct RoomIntro(RoomEvent Entry, RoomEvent InitialCheckpoint);

    private readonly record struct IntroIdentity(string MapSid, string Room, DateTimeOffset StartUtc, DateTimeOffset EndUtc, string Reason);

    private readonly record struct RespawnPoint(double X, double Y);

    private sealed class PreparedClip
    {
        public PreparedClip(ClipCandidate candidate, BoundaryEstimate startEstimate, BoundaryEstimate endEstimate, RecordingManifest recording, long startMs, long endMs, List<string> blockingReasons)
        {
            Candidate = candidate;
            StartEstimate = startEstimate;
            EndEstimate = endEstimate;
            Recording = recording;
            StartMs = startMs;
            EndMs = endMs;
            BlockingReasons = blockingReasons;
        }

        public ClipCandidate Candidate { get; }
        public BoundaryEstimate StartEstimate { get; }
        public BoundaryEstimate EndEstimate { get; }
        public RecordingManifest Recording { get; }
        public long StartMs { get; set; }
        public long EndMs { get; set; }
        public List<string> BlockingReasons { get; }
        public List<string> Annotations { get; } = [];
    }
}
