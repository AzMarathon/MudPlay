using MudPlay.Game.Map;
using MudPlay.Terminal;

namespace MudPlay.Services.Api;

// Builds the read payloads. Every method here runs ON THE UI THREAD — the
// server marshals before calling, because RoomTracker, the engines and the
// view-model state are all UI-thread-confined. Keep the work here to reading
// fields and shaping records; anything slow blocks the render loop.
//
// The curated Snapshot is deliberately small and hand-picked rather than
// generated. It answers the question that kept being unanswerable from a bug
// report: "what does the client believe right now, and what is holding it?" —
// in particular the gate history, which records WHO asserted a pause and WHEN.
// GET /state/full delegates to BugReportBuilder for the exhaustive version, so
// there is one collector for deep state, not two that drift.
public static class LocalApiState
{
    public static object Snapshot(AppServices svc)
    {
        ArgumentNullException.ThrowIfNull(svc);
        RoomState tracker = svc.RoomTracker.State;
        return new
        {
            capturedAt = DateTimeOffset.Now,
            version = AppInfo.Version,
            character = svc.PlayerStats.Name,
            realm = svc.GameData.ActiveRealm.ToString(),
            player = new
            {
                hp = svc.PlayerState.Hp,
                maxHp = svc.PlayerState.MaxHp,
                ma = svc.PlayerState.Ma,
                maxMa = svc.PlayerState.MaxMa,
                position = svc.PlayerState.Position,
                encumbrance = svc.PlayerState.Encumbrance,
                inCombat = svc.PlayerState.InCombat,
                mortallyWounded = svc.PlayerState.IsMortallyWounded,
                hidden = svc.PlayerState.IsHidden,
                sneaking = svc.PlayerState.IsSneaking,
                hasPromptData = svc.PlayerState.HasPromptData,
                level = svc.PlayerStats.Level,
                lives = svc.PlayerStats.Lives,
            },
            room = new
            {
                key = tracker.CurrentRoom?.Key.ToString(),
                name = tracker.CurrentRoom?.Name,
                confidence = tracker.Confidence,
                exits = tracker.ObservedExitDirections?.Select(d => d.ToString()).ToArray(),
                dark = svc.RoomTracker.IsInDarkRoom,
            },
            movement = new
            {
                paused = svc.MovementCoordinator.IsPaused,
                // The field that was missing when a two-hour pause had to be
                // explained: the gate NAMES, and below them who set them.
                assertedGates = svc.MovementCoordinator.AssertedGates.ToArray(),
                walker = svc.Walker.State.ToString(),
                loop = svc.LoopRunner.State.ToString(),
                autoLairPhase = svc.AutoLair.Phase.ToString(),
                autoLairActive = svc.AutoLair.IsActive,
                recoveryTier = svc.Recovery.CurrentTier.ToString(),
            },
            combat = new
            {
                target = svc.Combat.CurrentTarget,
                hostilesPresent = svc.CombatTracker.HasHostileMonster,
                engageable = svc.CombatTracker.HasEngageableHostiles,
            },
            log = new
            {
                // The cursor to hand back to GET /log?since= — included here so a
                // consumer can snapshot state and start tailing with no gap.
                newestSeq = svc.Log.TotalAppended,
                retained = svc.Log.Count,
            },
        };
    }

    // Recent gate transitions, newest last, straight from the coordinator's own
    // 200-entry history. This is the answer to "who paused movement, and when" —
    // which no bug report could previously produce.
    public static object Gates(AppServices svc)
    {
        ArgumentNullException.ThrowIfNull(svc);
        return new
        {
            asserted = svc.MovementCoordinator.AssertedGates.ToArray(),
            history = svc.MovementCoordinator.History.Select(h => new
            {
                at = h.Timestamp,
                gate = h.Gate,
                action = h.Asserted ? "assert" : "clear",
                asserter = h.Asserter,
                reason = h.Reason,
            }).ToArray(),
        };
    }

    // severities: set membership, NOT a minimum. LogSeverity puts Combat after
    // Error, so a ">= Warn" threshold would silently pull in every combat trace —
    // which is why the enum itself says consumers must not compare numerically.
    // Empty set means no severity filtering.
    public static object Log(AppServices svc, long since, int limit,
        IReadOnlySet<LogSeverity> severities, string? source)
    {
        ArgumentNullException.ThrowIfNull(svc);

        // Deliberately UNLIMITED at the ring, then limited after filtering. The
        // ring holds 2000 entries, so pulling the whole window is cheap — and
        // limiting first would apply the cap to raw entries, making a filtered
        // query return whatever happened to fall inside the first N. Asking for
        // `?source=LocalApi&limit=50` really would come back empty while the
        // entries sat further along.
        LogEntry[] entries = svc.Log.SnapshotAfter(since, out long newestSeq, out _);

        // Sequences are derived rather than stored per entry: the batch is
        // contiguous and ends at newestSeq. Numbering BEFORE filtering is what
        // lets the cursor skip past entries the caller filtered out instead of
        // rescanning them forever.
        long firstSeq = newestSeq - entries.Length + 1;
        var matched = entries
            .Select((e, i) => new
            {
                seq = firstSeq + i,
                at = e.Timestamp,
                severity = e.Severity,
                source = e.Source,
                message = e.Message,
                context = e.Context,
            })
            .Where(r => severities.Count == 0 || severities.Contains(r.severity))
            .Where(r => source is null || r.source.Equals(source, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        // Two different intents, so two paging rules:
        //  - no `since` — an ad-hoc look, so give the most RECENT matches (a tail);
        //  - with `since` — a poller making forward progress, so give the OLDEST
        //    matches after the cursor and never skip any.
        bool tail = since <= 0;
        var page = tail
            ? matched[Math.Max(0, matched.Length - limit)..]
            : matched[..Math.Min(limit, matched.Length)];

        // nextSince is the cursor to send next time. When the page was truncated
        // it must stop at the last entry actually returned, or the untruncated
        // remainder would be skipped; otherwise it advances to the newest entry
        // examined, so filtered-out entries aren't rescanned.
        long nextSince = !tail && page.Length < matched.Length && page.Length > 0
            ? page[^1].seq
            : newestSeq;

        return new
        {
            newestSeq,
            nextSince,
            retained = svc.Log.Count,
            matched = matched.Length,
            count = page.Length,
            entries = page,
        };
    }

    public static object Scrollback(AppServices svc, TerminalEmulator emulator, int lines)
    {
        ArgumentNullException.ThrowIfNull(svc);
        ArgumentNullException.ThrowIfNull(emulator);
        // Timestamps come through as their own field rather than baked into the
        // text: a consumer correlating a screen line with a log entry needs to
        // compare them, not parse them back out of a rendered string.
        var rows = TranscriptSnapshot.Tail(emulator, lines)
            .Select(l => new { at = l.Timestamp, text = l.Text })
            .ToArray();
        return new { requested = lines, count = rows.Length, lines = rows };
    }

    // The exhaustive dump. BugReportBuilder already assembles every section a bug
    // report carries, each defensively so one broken subsystem can't take the
    // whole capture down — exactly the property this endpoint wants, since it's
    // called when something is already wrong.
    public static object Full(AppServices svc, TerminalEmulator emulator)
    {
        ArgumentNullException.ThrowIfNull(svc);
        ArgumentNullException.ThrowIfNull(emulator);
        BugReportBuilder.BugReportCapture capture = BugReportBuilder.Capture(svc, emulator);
        return new
        {
            capturedAt = capture.CapturedAt,
            realm = capture.Realm.ToString(),
            sections = capture.Sections.ToDictionary(s => s.Heading, s => s.Body),
        };
    }

    public static string FullMarkdown(AppServices svc, TerminalEmulator emulator)
        => BugReportBuilder.RenderStateOnly(BugReportBuilder.Capture(svc, emulator));
}
