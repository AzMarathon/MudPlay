using System.Text;
using System.Text.Json;
using MudPlay.Game;
using MudPlay.Game.Inventory;
using MudPlay.Game.Map;
using MudPlay.Terminal;

namespace MudPlay.Services;

// Snapshots the live client state into a self-contained Markdown bug report.
// Capture freezes everything time-sensitive (recent scrollback, the program
// log tail, all gameplay settings, engine + player state) at the instant the
// user clicks "Bug report", so the report reflects the moment of the problem
// rather than whenever the user finishes typing their description. Render then
// folds the user's description in and produces the final document; FileName
// derives the Desktop file name (realm-timestamp.md).
//
// The two-phase split (capture → render) keeps the capture pure data: the
// description arrives from a dialog that opens after the click, and the
// scrollback / log keep growing while the user types. Rendering per-section
// Markdown at capture time is deliberate — it freezes each subsystem's view
// without holding live references that could mutate underneath us.
public static class BugReportBuilder
{
    // How many trailing transcript lines (scrollback + live screen) to include.
    private const int ScrollbackLines = 750;

    // How many trailing program-log entries to include.
    private const int LogLines = 750;

    // One captured section of the report — a heading and its pre-rendered Markdown body.
    public readonly record struct Section(string Heading, string Body);

    // Frozen point-in-time capture produced by Capture. Holds the realm +
    // timestamp used for the file name and every pre-rendered section. The
    // user's issue description is folded in later by Render.
    public sealed record BugReportCapture(
        DateTimeOffset CapturedAt,
        RealmType Realm,
        IReadOnlyList<Section> Sections);

    // Freeze the current client state into a BugReportCapture. Every section is
    // built defensively — a failure reading one subsystem is surfaced inline in
    // that section rather than aborting the whole report, because a bug report
    // is most needed exactly when something is in a bad state.
    public static BugReportCapture Capture(AppServices svc, TerminalEmulator emulator)
    {
        ArgumentNullException.ThrowIfNull(svc);
        ArgumentNullException.ThrowIfNull(emulator);

        DateTimeOffset now = DateTimeOffset.Now;
        RealmType realm = Guard(() => svc.GameData.ActiveRealm, RealmType.Stock);

        List<Section> sections =
        [
            new("Session", SafeSection(() => BuildSession(svc, realm, now))),
            new("Player state", SafeSection(() => BuildPlayerState(svc))),
            new("Statline", SafeSection(() => BuildStatline(svc))),
            new("Party", SafeSection(() => BuildParty(svc))),
            new("Inventory", SafeSection(() => BuildInventory(svc))),
            new("Player Workshop", SafeSection(() => BuildWorkshop(svc))),
            new("Movement engine", SafeSection(() => BuildMovement(svc))),
            new("Navigation engines", SafeSection(() => BuildNavigationEngines(svc))),
            new("Exp/Hr estimator", SafeSection(() => BuildExpEstimator(svc))),
            new("Loop simulator", SafeSection(() => BuildSimulator(svc))),
            new("Special room markers", SafeSection(() => BuildRoomMarkers(svc))),
            new("Auto-mode", SafeSection(() => BuildAutoMode(svc))),
            new("Keybindings", SafeSection(() => BuildKeybindings(svc))),
            new("Live engine state", SafeSection(() => BuildEngineState(svc))),
            new("Room combat assessment", SafeSection(() => BuildRoomCombatAssessment(svc))),
            new("Combat rounds (last 10)", SafeSection(() => BuildCombatRounds(svc))),
            new("Tick timing (last 400 events)", SafeSection(() =>
                $"HP regen expected per gain: {svc.HpRegenExpected.Current?.ToString() ?? "(no `stat` read yet — gains judged on timing alone)"}"
                + (svc.HpRegenExpected.Current is { } expected ? $"; a gain above +{expected.Largest} is a heal." : string.Empty)
                // The length projected rounds step by, and whether the last one could
                // be placed: a cast refused as "already cast this round" out of a
                // fight is a projection running ahead of the game's round.
                + $"\n\nRound length: {svc.Tick.RoundLength.TotalSeconds:F3} s"
                + (svc.Tick.RoundLengthMeasured ? " (measured)" : " (nominal: no regen pass has measured it yet)")
                + $"; last round tick {(svc.Tick.LastCombatTickWasDamageDriven ? "seen on the wire" : svc.Tick.LastCombatTickWasPlaced ? "projected from a recent sighting" : "projected with nothing seen lately: it freed no cast slot")}."
                + "\n\n" + svc.TickTiming.Render())),
            new("Session combat stats", SafeSection(() => BuildSessionCombat(svc))),
            new("Session activity", SafeSection(() => BuildSessionActivity(svc))),
            new("Monster HP estimates", SafeSection(() => BuildMonsterHpEstimates(svc))),
            new("Spell resolution", SafeSection(() => BuildSpellResolution(svc))),
            new("Combat profiles", SafeSection(() => BuildCombatProfiles(svc))),
            new("Monster overrides", SafeSection(() => BuildMonsterOverrides(svc))),
            new("Monster observations (this character)", SafeSection(() => BuildMonsterObservations(svc))),
            new("Item overrides", SafeSection(() => BuildItemOverrides(svc))),
            new("Effective settings (resolved)", SafeSection(() => BuildEffectiveSettings(svc))),
            new("Settings overrides (deltas, excluding BBS + Display)", SafeSection(() => BuildSettings(svc))),
            new("Program log", SafeSection(() => BuildLog(svc))),
            new("Scrollback", SafeSection(() => BuildScrollback(emulator, svc.InGameCapture))),
        ];

        // Wire Inspector capture — only when the user has those panes up (they're
        // large; irrelevant to most reports). The raw ANSI + the recognizer's read
        // of each combat line are exactly what a combat-recognition bug needs.
        WireInspectorVisibility wire = Guard(() => svc.WireInspectorVisibility, new());
        if (wire.RawVisible)
            sections.Add(new("Wire — raw (last 750 lines)",
                SafeSection(() => BuildRawWire(svc))));
        if (wire.ClassifiedVisible)
            sections.Add(new("Wire — classified combat (last 750 lines)",
                SafeSection(() => BuildClassifiedWire(svc))));

        return new BugReportCapture(now, realm, sections);
    }

    // Compose the final Markdown document from a capture and the user's
    // issueDescription. The description is placed at the top so a triager reads
    // the "what went wrong" before the state dump.
    public static string Render(BugReportCapture capture, string issueDescription)
    {
        ArgumentNullException.ThrowIfNull(capture);

        StringBuilder sb = new(capacity: 16 * 1024);
        sb.Append("# MudPlay bug report\n\n");
        sb.Append("_Captured ").Append(capture.CapturedAt.ToString("yyyy-MM-dd HH:mm:ss zzz"))
          .Append("  •  realm ").Append(RealmLabel(capture.Realm)).Append("_\n\n");

        sb.Append("## Issue\n\n");
        sb.Append(string.IsNullOrWhiteSpace(issueDescription) ? "_(none provided)_" : issueDescription.Trim());
        sb.Append("\n\n");

        AppendSections(sb, capture);
        return sb.ToString();
    }

    // State-only variant for the crash reporter: the section dump with no
    // bug-report title and no user-description block, so a crash document can
    // embed the same live-state snapshot under its own headings.
    public static string RenderStateOnly(BugReportCapture capture)
    {
        ArgumentNullException.ThrowIfNull(capture);

        StringBuilder sb = new(capacity: 16 * 1024);
        AppendSections(sb, capture);
        return sb.ToString();
    }

    private static void AppendSections(StringBuilder sb, BugReportCapture capture)
    {
        foreach (Section section in capture.Sections)
        {
            sb.Append("## ").Append(section.Heading).Append("\n\n");
            sb.Append(section.Body.TrimEnd()).Append("\n\n");
        }
    }

    // Desktop file name for a capture: realm-yyyyMMdd-HHmmss.md, e.g.
    // paradigm-20260703-142530.md. Uses the click timestamp so the name matches
    // when the problem was seen, not when the file was written.
    public static string FileName(BugReportCapture capture)
    {
        ArgumentNullException.ThrowIfNull(capture);
        return $"{RealmLabel(capture.Realm)}-{capture.CapturedAt:yyyyMMdd-HHmmss}.md";
    }

    // ----- Section builders ----------------------------------------------

    private static string BuildSession(AppServices svc, RealmType realm, DateTimeOffset now)
    {
        StringBuilder sb = new();
        Kv(sb, "Version", AppInfo.Version);
        Kv(sb, "Captured at", now.ToString("yyyy-MM-dd HH:mm:ss zzz"));
        Kv(sb, "Realm", $"{RealmLabel(realm)} ({realm})");
        Kv(sb, "Active game-data set", svc.GameData.ActiveSet ?? "(none)");
        Kv(sb, "Game data: long text damaged by an old import",
            svc.ActiveSetDamagedCells == 0 ? "no" : $"{svc.ActiveSetDamagedCells} cell(s) - import the MDB again");
        Kv(sb, "Game data: items with room-command sources", svc.ItemSources.RoomCommandItemCount.ToString());
        Kv(sb, "Character", svc.Profile.CurrentProfileName ?? "(none loaded)");
        Kv(sb, "BBS", svc.Profile.CurrentBbsName ?? "(none)");
        if (svc.Profile.Current is { StateUnverified: true })
            Kv(sb, "Copied profile", "state not yet read off this character (stat + inventory pending)");
        Kv(sb, "Our alignment", $"{svc.Alignment.SelfAlignment ?? "(unknown)"}"
            + (svc.Alignment.EvilPoints is { } ep ? $" · EPs {ep:0.##}" : "")
            + (svc.Alignment.MinEvilPoints is { } floor ? $" · min EPs {floor:0.##}" : "")
            + (svc.Alignment.IsStale ? " · stale (dark cloud since the last check)" : "")
            + (svc.Alignment.SelfEvilPoints(svc.GameData.ActiveRealm) is { } range
                ? $" · evil points {range} (evil-only gear gate)" : ""));
        Kv(sb, "BBS realm", svc.ResolveActiveRealm() is { } bbsRealm
            ? $"{bbsRealm.Realm.Name} (game data {bbsRealm.Realm.ActiveGameDataSet ?? "global default"}; "
              + $"{bbsRealm.Bbs.Realms.Count} realm(s) on the BBS)"
            : "(none)");
        // PvP: whether the realm is marked for it, and how many players carry a
        // relationship other than the Neutral everyone starts with.
        Kv(sb, "Realm PvP", (svc.ResolveActiveRealm()?.Realm.PvpEnabled == true ? "enabled" : "off")
            + $"; {svc.Players.Players.Count(p => p.Relationship == Models.GameData.PlayerRelationship.Friend)} friend(s), "
            + $"{svc.Players.Players.Count(p => p.Relationship == Models.GameData.PlayerRelationship.Enemy)} enemy(ies)");
        // What the board takes for a hang-up in a fight, as the user recorded it
        // for this realm. It explains HP or items missing after a reconnect.
        Kv(sb, "Realm hang-up penalty",
            Game.Health.HangupPenaltyNotice.Describe(svc.ResolveActiveRealm()?.Realm));
        Kv(sb, "PvP room", svc.PvpRoom.Describe()
            + (svc.PvpRoom.RoomAttackHeldBy() is { } heldBy ? $"; our room attacks held: {heldBy}" : "")
            + (svc.PvpLeaveRoomReason() is { } leave ? $"; walking on: {leave}" : ""));
        Kv(sb, "PvP response", svc.PvpResponse.LastResponse
            + (svc.PvpFlee.IsActive ? "; fleeing to the flee room now" : ""));
        Kv(sb, "PvP fight", $"{svc.PvpFight.Describe()}; last: {svc.PvpFight.LastReport}");
        Kv(sb, "PvP attacks on us", svc.PvpAttacks.Recent.Count == 0
            ? "(none this session)"
            : string.Join("; ", svc.PvpAttacks.Recent.Select(a =>
                $"{a.At.ToLocalTime():HH:mm:ss} {a.Player} ({a.Kind}; {a.Relationship}{(a.MarkedEnemy ? ", marked by this" : "")})")));
        // Retry/reconnect config for the active BBS. A "won't stop redialing" or
        // "never reconnected" report hinges on whether InfiniteRetries is on (which
        // overrides the count+pause to unlimited @ 3s) and which triggers are armed.
        var bbs = svc.ResolveActiveBbs();
        Kv(sb, "Retry behaviour", bbs is null
            ? "(no active BBS)"
            : (bbs.InfiniteRetries ? "infinite @ 3s" : $"{bbs.MaxRedials} redials @ {bbs.RedialPauseSeconds}s")
              + "; triggers:"
              + (bbs.ReconnectOnFailedConnect ? " failed-connect" : "")
              + (bbs.ReconnectOnCarrierLost ? " carrier-lost" : "")
              + (bbs.ReconnectOnNoResponse ? " no-response" : "")
              + (bbs.ReconnectOnFailedConnect || bbs.ReconnectOnCarrierLost || bbs.ReconnectOnNoResponse ? "" : " none")
              + $"; no-response idle {(bbs.NoResponseTimeoutSeconds > 0 ? bbs.NoResponseTimeoutSeconds + "s" : "off")}");
        // Startup profile-load setting — diagnoses "it didn't reopen my profile".
        Kv(sb, "Auto-load last profile", svc.Settings.Current.AutoLoadLastProfile ? "on" : "off");
        Kv(sb, "Last-used profile", svc.Settings.Current.LastUsedProfile is { } lp
            ? $"{lp.Name} on {lp.Bbs}" : "(none)");
        // Update check — the toggle, the last verdict, and when the next automatic
        // check is due, so a "no update notice showed" report can tell whether the
        // check ran, what it found, and whether one was even scheduled.
        Kv(sb, "Automatic update check", svc.Settings.Current.AutoCheckForUpdates ? "on" : "off");
        Kv(sb, "Map other floors", $"{svc.Settings.Current.MapOtherFloors}, {svc.Settings.Current.MapOtherFloorsLevels} floor(s), overlap limit {svc.Settings.Current.MapOtherFloorsMaxOverlapPercent}%");
        Kv(sb, "Map loop lines", svc.Profile.Current?.NavLoopLinesMode.ToString() ?? "(no profile)");
        // What a colour in a screenshot or a "the red text" in a report really is.
        Kv(sb, "Terminal colours", svc.DescribeTerminalColors());
        Kv(sb, "Last update check", DescribeUpdate(svc));
        Kv(sb, "Next update check", svc.Update.NextAutoCheck is { } due
            ? due.ToString("yyyy-MM-dd HH:mm") : "(not scheduled)");
        // Quest-flag completion sync — the opt-in toggle + the last run's result, so a
        // "my quest didn't auto-complete" report shows whether the sync ran and what it found.
        Kv(sb, "Quest flag sync on login", svc.QuestFlagSync.EnabledForCurrentProfile ? "on" : "off");
        Kv(sb, "Last quest flag sync", svc.QuestFlagSync.LastResult ?? "(not run this session)");
        // The once-per-day gate's stored date — so a "didn't run at login" report shows
        // it was skipped because it already ran earlier today, not that it's broken.
        Kv(sb, "Quest flag sync last ran (date)",
            svc.QuestFlagSync.LastSyncDate?.ToString("yyyy-MM-dd") ?? "(never)");

        // Paradigm transport-token charges read this session (from each held token's
        // `look`) — so a "token wasn't offered / said no charges" report shows what
        // the tracker actually knew.
        IReadOnlyList<Game.Tokens.TokenTracker.TokenCharge> tokenCharges = svc.Tokens.KnownCharges();
        Kv(sb, "Token charges read",
            tokenCharges.Count == 0
                ? "(none read this session)"
                : string.Join(", ", tokenCharges.Select(c => $"{c.Place}={c.Remaining}")));
        // Carried limited-use item charges (what @uses / Character Info would show) —
        // Paradigm look counts or stock counted-uses, "?" when a charged item's count
        // isn't read yet. So a "charges wrong / never filled in" report shows the state.
        IReadOnlyList<Game.Inventory.CarriedChargeReadout.ChargedItem> itemCharges = svc.CarriedCharges.AllCharged();
        Kv(sb, "Item charges",
            itemCharges.Count == 0
                ? "(no limited-use items carried)"
                : string.Join(", ", itemCharges.Select(c => $"{c.Name}={(c.Remaining is { } n ? n.ToString() : "?")}")));
        // Diagnostic-channel state gates whether the Program-log tail carries any
        // decision trail: both flags default off, and every _log?.Debug/Combat
        // site is skipped at generation time when off, so a report captured with
        // them off has Info-only logs. Surface the state so a triager knows why.
        Kv(sb, "Debug diagnostics", (svc.Log.Diagnostics?.DebugDiagnostics ?? false) ? "on" : "off");
        Kv(sb, "Combat diagnostics", (svc.Log.Diagnostics?.CombatDiagnostics ?? false) ? "on" : "off");
        // Whether a session-statistics file exists to ask for alongside the report.
        Kv(sb, "Session statistics log", svc.SessionStatsLog.Summary);
        // Local control API. Worth recording because something may have been
        // driving this client over it, which changes how a report should be read.
        // The TOKEN IS NEVER INCLUDED — a bug report gets attached to public
        // issues, and the token grants control of the character.
        Kv(sb, "Local API", DescribeLocalApi(svc));
        // Direct-input (character) mode: on while a trainer / character-creation
        // stat box owns the keyboard, so arrow keys pass to the wire instead of
        // recalling command history. An "arrows don't move between stat fields"
        // report hinges on whether this flipped on entering the box.
        Kv(sb, "Direct-input mode", svc.TrainerMenu.MenuOwnsKeyboard ? "on (trainer/creation box)" : "off");
        // Death-pile auto-recovery: the toggles + the most recent death record's
        // state, so a "corpse didn't auto-recover" report shows whether it was
        // armed and how the last pile resolved (Recovered / Partial / Missing).
        Kv(sb, "Auto-recover deathpiles", svc.DeathRecovery.AutoRecover ? "on" : "off");
        Kv(sb, "Auto-equip on recovery", svc.DeathRecovery.AutoEquip ? "on" : "off");
        // Whether a rest-interrupting fight swaps to the Default set (then back on
        // room-clear if still gated). Off = the pre-rest loadout is kept through the
        // fight. Answers a "why did/didn't my gear swap when a mob showed up" report.
        Kv(sb, "Swap to Default on combat",
            (svc.Profile.Current?.Equipment?.SwapToDefaultOnCombat ?? false) ? "on" : "off");
        // The loadout the client believes is on — a gear report turns on whether that
        // matches the worn list under Inventory.
        // What a gear swap is holding on because it counters the room's hazard.
        IReadOnlyCollection<string> roomCounters = svc.WornRoomHazardCounters();
        Kv(sb, "Worn hazard counters a gear swap leaves on (this room or the next)",
            roomCounters.Count == 0 ? "(none)" : string.Join(", ", roomCounters));
        Kv(sb, "Gear set last applied",
            svc.Equipment.CurrentSetId is { } currentSetId
                ? svc.Profile.Current?.Equipment?.Sets.FirstOrDefault(s => s.Id == currentSetId)?.Name
                    ?? "(a set this profile no longer has)"
                : "(none this session)");
        // A set picked from the Equip menu turns every automatic gear swap off — the
        // first thing to rule out in a "my gear stopped swapping" report.
        Kv(sb, "Gear set held from the Equip menu",
            svc.AutoEquip.HeldSet is { } heldSet ? $"{heldSet} (automatic swaps off)"
            : svc.AutoEquip.ReleasedSetAwaitingDefault is { } owed ? $"(none; '{owed}' deselected mid-fight, Default owed once combat clears)"
            : "(none)");
        Kv(sb, "Keep Bossing set between bosses",
            ((svc.Profile.Current?.Equipment?.KeepBossingBetweenBosses ?? false) ? "on" : "off")
            + (svc.AutoEquip.IsKeepingBossing ? ", kept on right now" : string.Empty)
            + $"; last leader answer: {svc.LeaderBossTravel.LastOutcome}");
        // Non-zero while an in-combat recovery is still pacing its re-equip across
        // rounds — shows a "recovered but not fully re-equipped" report mid-burst.
        if (svc.DeathRecovery.PendingReequipCount > 0)
            Kv(sb, "Re-equip pieces pending", svc.DeathRecovery.PendingReequipCount.ToString());
        if (svc.DeathRecovery.HeldReequipCount > 0)
            Kv(sb, "Re-equip pieces held for Auto-All", svc.DeathRecovery.HeldReequipCount.ToString());
        var lastDeath = svc.DeathRecovery.Records.Count > 0 ? svc.DeathRecovery.Records[^1] : null;
        Kv(sb, "Latest deathpile", lastDeath is null
            ? "(none)"
            : $"{lastDeath.Status} @ {lastDeath.RoomKeyText}"
              + (lastDeath.RecoveryMessage is { Length: > 0 } msg ? $" — {msg}" : ""));
        // The Stock spill sweep: what the pile is still waiting on, where the sweep
        // is (or how the last one ended), and the rooms it tries in order — a "it
        // walked off and found nothing" or "it never looked there" report needs all
        // three, plus what this death said was gone and the trail it kept.
        if (lastDeath is not null)
        {
            Kv(sb, "Latest deathpile still missing",
                lastDeath.UnrecoveredItems is { Count: > 0 } missing ? string.Join(", ", missing) : "(nothing)");
            if (lastDeath.ReturnedItems is { Count: > 0 } returned)
                Kv(sb, "Latest death: returned to their rightful place", string.Join(", ", returned));
            if (lastDeath.Trail is { Count: > 0 } trail)
                Kv(sb, "Latest death: rooms walked up to it (newest first)",
                    string.Join(", ", trail.Select(r => $"{r.Map}/{r.Room}")));
        }
        Kv(sb, "Stock spill sweep", svc.DeathRecovery.SpillSweepState);
        if (svc.DeathRecovery.SpillSweepPlan is { Length: > 0 } plan)
            Kv(sb, "Stock spill sweep rooms, in order", plan);
        return sb.ToString();
    }

    // Party roster snapshot — who's grouped, their roles, and the pending-invite
    // flags. Party-relevant bugs (self-cast family-name targeting, @join-nag
    // chasing an [Invited] row) hinge on exactly this state, which the `par`
    // echo in scrollback only shows indirectly.
    private static string BuildParty(AppServices svc)
    {
        PartyState party = svc.PartyState;
        StringBuilder sb = new();
        Kv(sb, "In party", party.IsInParty.ToString());
        Kv(sb, "Self is leader", party.SelfIsLeader.ToString());
        Kv(sb, "Leader", party.LeaderName ?? "(none)");
        // Blind means a follow move prints no room for the map to confirm against.
        Kv(sb, "Follow mode (set follow)", svc.FollowModes.Mode switch
        {
            Game.FollowMode.Blind => "Blind",
            Game.FollowMode.Normal => "Normal",
            _ => "(not seen — no `pro` sheet or `set follow` reply this session)",
        });
        // What puts `par` on the wire. A "member's HP was stale / the heal came
        // late" report turns on whether anything was polling at all.
        Kv(sb, "par is sent", svc.PartyPoller.ParTriggerSummary);
        // Board-specific disconnect line, if the active BBS defines one — the
        // config a "party sprinted off after a member dropped" report needs to
        // confirm the custom logoff line was actually taught to the client.
        Kv(sb, "BBS disconnect pattern", svc.ResolveActiveBbs()?.DisconnectPattern ?? "(built-in lines only)");
        // Follower reconnect-rejoin state — the leader we'd @comeback on the
        // next reconnect (crash-survivable). A "didn't auto-rejoin after a drop"
        // report hinges on whether the leader was remembered at all.
        Kv(sb, "Reconnect rejoin leader", svc.PartyRejoin.RememberedLeader ?? "(none remembered)");
        if (svc.PartyRejoin.WaitingForRoomToRejoin is { } waitLeader)
            Kv(sb, "Reconnect rejoin — waiting on our room", $"@comeback to {waitLeader} once our room confirms");
        // Leader-side reconnect reform state — the followers we snapshotted at the
        // last drop and will wait for on reconnect. A "leader sprinted off / didn't
        // wait after a nightly-cleanup reconnect" report hinges on whether they
        // were captured at all.
        IReadOnlyList<string> pendingReform = svc.PartyReform.PendingReform;
        Kv(sb, "Reconnect reform followers",
            pendingReform.Count > 0 ? string.Join(", ", pendingReform) : "(none pending)");
        // A split-teleport regroup in flight: a "leader walked on alone after a
        // teleport" report needs whom it held for and whether the jump had landed.
        Kv(sb, "Split-teleport reform", svc.AutoParty.ReformSummary);
        // "Only auto-invite while navigation is running": who a run started here invites.
        IReadOnlyCollection<string> heldInvites = svc.AutoParty.SeenWhileIdle;
        Kv(sb, "Auto-invite held until navigation starts",
            heldInvites.Count > 0 ? string.Join(", ", heldInvites) : "(nobody)");
        // Leader-side recovery state — who (if anyone) we're currently walking to
        // re-collect, and the reach cap that gates it. A "leader never came back
        // for me" report needs both.
        Kv(sb, "Too heavy to move (waiting on weight)", svc.TooHeavyWait.IsTooHeavy.ToString());
        Kv(sb, "Our @wait reasons held (no @ok until all clear)", svc.PartyRest.HeldReasons.Count == 0
            ? "(none)"
            : string.Join(", ", svc.PartyRest.HeldReasons));
        Kv(sb, "Party @wait holding for", svc.PartyEssentials.WaitingMembers.Count == 0
            ? "(nobody)"
            : string.Join(", ", svc.PartyEssentials.WaitingMembers.Select(m =>
                svc.PartyEssentials.OkDistrusted.Contains(m) ? $"{m} (full window, @ok ignored)" : m)));
        Kv(sb, "Reconnect hold for", svc.PartyDisconnectMovement.PendingMembers.Count == 0
            ? "(nobody)" : string.Join(", ", svc.PartyDisconnectMovement.PendingMembers));
        Kv(sb, "Recovering member", svc.PartyComeback.RecoveringMember is { } rec
            ? (svc.PartyComeback.RecoveringLeftBehind ? $"{rec} (left behind by our move)" : rec)
              + $" — {svc.PartyComeback.RecoveryPhase}"
            : "(none in flight)");
        Kv(sb, "Recovery reach (rooms)", svc.PartyComeback.ReturnDistanceRooms.ToString());
        Kv(sb, "Recovery given up, resume kept for", svc.PartyComeback.ParkedResumeSummary ?? "(none)");
        // Members we gave up chasing (return route un-crossable) — a "leader keeps
        // abandoning me" report should show the give-up was deliberate.
        var givenUp = svc.PartyComeback.GivenUpMembers;
        Kv(sb, "Recovery given up on", givenUp.Count == 0
            ? "(none)"
            : string.Join(", ", givenUp.Select(kv => $"{kv.Key} ({kv.Value} fails)")));
        Kv(sb, "Probe stats on partying (@level/@version)", svc.PartyProbe.Enabled ? "on" : "off");
        // Telepath pacing: a queue that won't drain, or a climbing given-up count,
        // is the "my telepath never arrived" report.
        Kv(sb, "Telepaths queued / awaiting ack", $"{svc.Telepaths.Queued} / {svc.Telepaths.InFlight}");
        Kv(sb, "Telepaths resent / given up (throttle)", $"{svc.Telepaths.Resends} / {svc.Telepaths.GivenUp}");

        // Party auto-train. "The party never went to train" / "we left someone
        // behind" hinges on each side's own report, what the leader heard, and why
        // the quorum decided what it did.
        Game.Train.PartyTrainCoordinator ptrain = svc.PartyTrain;
        Kv(sb, "Auto-train party", svc.TrainerWalk.CurrentSettings.AutoTrainParty ? "on" : "off");
        Kv(sb, "Party train — own report", ptrain.OwnStatus().Encode());
        Kv(sb, "Party train — trip", ptrain.TripRunning
            ? "running" + (svc.TrainerWalk.PartyTripActive ? " (engine paused)" : "")
            : "idle");
        Kv(sb, "Party train — last decision", ptrain.LastDecision.Length > 0 ? ptrain.LastDecision : "(none)");
        Kv(sb, "Party train — members needed ready", svc.TrainerWalk.CurrentSettings.PartyMinReady.ToString());
        Kv(sb, "Party train — cooldown until", ptrain.CooldownUntil is { } cd
            ? cd.ToLocalTime().ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)
            : "(none)");
        Kv(sb, "Party train — done owed to", ptrain.PendingDoneFor ?? "(none)");
        Kv(sb, "Party train — partners (MudPlay, feature on)", ptrain.HasPartners ? "yes — party trips" : "none — solo settings when leading");
        Kv(sb, "Party train — asked this membership", ptrain.AskedMembers.Count == 0
            ? "(none)" : string.Join(", ", ptrain.AskedMembers));
        Kv(sb, "Party train — reporting to", ptrain.ReportingTo ?? "(no leader has asked)");
        var reports = ptrain.Reports;
        Kv(sb, "Party train — member reports", reports.Count == 0
            ? "(none)"
            : string.Join("; ", reports.Select(r =>
                $"{r.Name} @{r.At.ToLocalTime().ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)}: {r.Status.Encode()}")));

        sb.Append("\n**Members** (").Append(party.Members.Count).Append(")\n\n");
        if (party.Members.Count == 0) { sb.Append("_(none)_\n"); return sb.ToString(); }

        foreach (PartyMember m in party.Members)
        {
            sb.Append("- ").Append(string.IsNullOrWhiteSpace(m.Name) ? "(unnamed)" : m.Name);
            if (!string.IsNullOrWhiteSpace(m.Class)) sb.Append(" (").Append(m.Class).Append(')');

            List<string> tags = new();
            if (m.IsSelf) tags.Add("self");
            if (m.IsLeader) tags.Add("leader");
            if (m.IsInvited) tags.Add("invited");
            tags.Add(m.Rank.ToString().ToLowerInvariant() + "rank");
            tags.Add(m.Position.ToString());
            if (m.IsWaiting) tags.Add("WAIT");
            foreach (string flag in AilmentFlags(m)) tags.Add(flag);
            sb.Append(" — ").Append(string.Join(", ", tags));

            // Invited rows carry no health round-trip yet, so their percents are
            // meaningless — skip the H/M readout for them.
            if (!m.IsInvited) sb.Append("  [").Append(m.HpRichDisplay).Append(' ').Append(m.MaRichDisplay).Append(']');
            // Between-polls HP: the running estimate (damage / heals seen since the
            // game last stated this member's HP) against that last statement, so a
            // "party heal fired late / early" report shows what the heal picker read.
            if (!m.IsSelf && !m.IsInvited) sb.Append("  {hp: ").Append(HpEstimateNote(svc, m)).Append('}');
            // Level source drives the party level-gate routing; surface each
            // member's known level (exact + staleness, else title band) so a
            // "party routed the wrong way around a gate" report shows what the
            // gate check actually saw.
            if (!m.IsSelf && !string.IsNullOrWhiteSpace(m.Name))
            {
                sb.Append("  {lvl: ").Append(MemberLevelNote(svc, m.Name)).Append('}');
                // Client version recorded by the party stats probe (@version), when known.
                if (svc.Players.Find(m.Name)?.Version is { Length: > 0 } ver)
                    sb.Append("  {ver: ").Append(ver).Append('}');
            }
            sb.Append('\n');
        }

        // The most-constraining (Low, High) window the level gate routes on, or
        // "(n/a)" when not leading / nobody's level is known.
        Kv(sb, "HP estimate heal reader", svc.PartyHp.ReaderSummary);

        (int Low, int High)? window = svc.PartyLevel.Bounds();
        Kv(sb, "Party level window",
            window is { } w ? $"{w.Low}–{w.High}" : "(n/a — solo, following, or no levels known)");
        return sb.ToString();
    }

    // One member's HP estimate against the last HP the game stated for them.
    private static string HpEstimateNote(AppServices svc, PartyMember m)
    {
        (int? estimate, PartyHpReading? last) = svc.Party.HpEstimateOf(m);
        string stated = last is { } r
            ? $"last {r.Source} {r.Percent}% {FormatAgeSeconds(DateTimeOffset.UtcNow - r.At)}"
            : "no par / @health reading yet";
        if (m.BaselineHp <= 0) return $"not estimated (max HP unknown); {stated}";
        return estimate is { } est
            ? $"estimate ~{est}/{m.BaselineHp} ({m.HpPercent}%); {stated}"
            : $"no estimate in play; {stated}";
    }

    private static string FormatAgeSeconds(TimeSpan age)
    {
        if (age < TimeSpan.Zero) age = TimeSpan.Zero;
        return age.TotalSeconds < 120 ? $"{age.TotalSeconds:0}s ago" : $"{age.TotalMinutes:0}m ago";
    }

    // One member's level as the party level-gate check sees it: the exact level
    // (with how long ago it was learned, since a reading not from the current day
    // is re-probed on a level-gated route) when known, else the title-derived
    // band, else unknown.
    private static string MemberLevelNote(AppServices svc, string name)
    {
        Models.GameData.PlayerRecord? rec = svc.Players.Find(name);
        if (rec?.Level is { } exact)
        {
            string age = rec.LevelAt is { } at
                ? $", {FormatAgeHours(DateTime.UtcNow - at)}"
                : ", age unknown";
            return $"{exact} exact{age}";
        }
        if (Game.GameData.ClassTitleTable.LookupLevelRange(rec?.Title) is { } band)
            return $"{band.MinLevel}–{band.MaxLevel} (from title \"{rec!.Title}\")";
        return "unknown";
    }

    private static string FormatAgeHours(TimeSpan age)
    {
        if (age < TimeSpan.Zero) age = TimeSpan.Zero;
        double hours = age.TotalHours;
        return hours < 1
            ? $"{age.TotalMinutes:0}m ago"
            : $"{hours:0.#}h ago{(hours > 24 ? " STALE" : "")}";
    }

    private static IEnumerable<string> AilmentFlags(PartyMember m)
    {
        if (m.Resting) yield return "resting";
        if (m.Meditating) yield return "meditating";
        if (m.Blinded) yield return "blind";
        if (m.Poisoned) yield return "poison";
        if (m.Diseased) yield return "disease";
        if (m.Confused) yield return "confuse";
        if (m.Held) yield return "held";
    }

    // In-flight automation FSM state that the log lines only hint at: the @join
    // nag table (which invitees we're chasing and how far along) and the combat
    // weapon-swap shadow (what we believe is equipped, without re-parsing `inv`).
    // These are the exact internals a triager otherwise has to reconstruct from
    // code + log timestamps.
    // Compact, names over numbers, unset fields left out.
    private static readonly System.Text.Json.JsonSerializerOptions EventDumpJson = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private static string BuildEngineState(AppServices svc)
    {
        StringBuilder sb = new();

        // Every event exactly as saved (all its set fields — trigger, action, stop
        // rules, Then), with what the engine makes of it now: auto-disabled, next fire,
        // whether a When event's conditions hold against the readings it uses. An
        // "event misbehaved" report hinges on what was set; a JSON dump keeps new
        // fields in it without this code changing.
        Game.Events.EventConditionEvaluator.Readings now = svc.ReadEventReadings();
        IReadOnlyList<Models.GameData.ScheduledEvent> events = svc.Events.Events;
        sb.Append("**Events** (").Append(events.Count).Append(") · disable all=")
          .Append(svc.Profile.Current?.EventsGloballyDisabled == true)
          .Append(", in game=").Append(svc.EventScheduler.IsInGame)
          .Append(" · readings: money=")
          .Append(now.Copper?.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) ?? "unknown")
          .Append(" copper, encumbrance=").Append(now.EncumbrancePercent is { } ep ? $"{ep}%" : "unknown")
          .Append(", exp=").Append(now.Experience?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown")
          .Append(", level=").Append(now.Level?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown")
          .Append("\n\n");
        sb.Append("- **Running:** ").Append(svc.Events.RunSummary).Append('\n');
        if (events.Count == 0) sb.Append("- _(no events)_\n");
        else foreach (Models.GameData.ScheduledEvent e in events)
        {
            sb.Append("- ").Append(string.IsNullOrWhiteSpace(e.Name) ? "(unnamed)" : e.Name)
              .Append(e.Disabled ? (svc.Events.IsAutoDisabled(e) ? " [auto-disabled: target missing]" : " [disabled]") : string.Empty);
            if (svc.EventScheduler.GetNextFire(e) is { } next)
                sb.Append(" · next ").Append(next.ToString("MM-dd HH:mm:ss"));
            if (e.TriggerType == Models.GameData.EventTriggerType.State)
                sb.Append(" · holds now: ").Append(Game.Events.EventConditionEvaluator.AllHold(e.Conditions, now));
            sb.Append("\n  `").Append(System.Text.Json.JsonSerializer.Serialize(e, EventDumpJson)).Append("`\n");
        }
        sb.Append('\n');

        IReadOnlyList<AutoPartyManager.NagSnapshot> nags = svc.AutoParty.ActiveNagSnapshot();
        sb.Append("**@join nags** (").Append(nags.Count).Append(")\n\n");
        if (nags.Count == 0) sb.Append("_(none active)_\n");
        else foreach (AutoPartyManager.NagSnapshot n in nags)
        {
            sb.Append("- ").Append(n.Given)
              .Append(": invited ").Append(n.InvitedAt.ToLocalTime().ToString("HH:mm:ss"))
              .Append(", sends=").Append(n.JoinSends)
              .Append(", lastJoin=").Append(n.LastJoinAt?.ToLocalTime().ToString("HH:mm:ss") ?? "(none)")
              .Append(", acknowledged=").Append(n.Acknowledged).Append('\n');
        }

        // Headline count so confirming candidates are pending is a grep of this
        // report, not a scroll through the program-log tail for Warn rows.
        int pendingCandidates = 0;
        foreach (Models.GameData.MessageCandidateRecord c in svc.MessageCandidates.Candidates)
            if (!c.Dismissed) pendingCandidates++;
        sb.Append("\n**Message candidates** (unresolved / total): ")
          .Append(pendingCandidates).Append(" / ").Append(svc.MessageCandidates.Candidates.Count).Append('\n');

        Game.Combat.CombatManager.DebugState combat = svc.Combat.Snapshot();
        // The believed-worn weapon is no longer shadowed in the combat engine —
        // EquipmentManager diffs against live inventory, so the report reads the
        // worn weapon / off-hand straight from the snapshot.
        Game.Inventory.InventorySnapshot inv = svc.Inventory.Snapshot;
        sb.Append("\n**Combat weapon state**\n\n");
        Kv(sb, "Current target", combat.CurrentTarget ?? "(none)");
        // A guarded priority we're chasing through the "moves to protect" redirect —
        // explains re-attacks aimed at a monster that isn't our live target.
        Kv(sb, "Guard-blocked priority", combat.GuardBlockedTarget ?? "(none)");
        // Passive neutrals the user hand-attacked that the engine has taken over killing —
        // explains why auto-combat is (or isn't) fighting a neutral the user engaged.
        Kv(sb, "User-engaged (manual + self-defense)", combat.UserEngagedInstances.Count > 0
            ? string.Join(", ", combat.UserEngagedInstances)
            : "(none)");
        // Attack Order mode + whether we're currently holding our pick for a party
        // announce — the key tells for an "attack-last / not-last not respected" report.
        Kv(sb, "Attack Order", combat.AttackTiming);
        Kv(sb, "Attack-order hold", combat.AwaitingAttackOrderHold ?? "(committed / none)");
        Kv(sb, "Worn weapon", WornSlot(inv, "Weapon Hand") ?? "(none)");
        Kv(sb, "Worn off-hand", WornSlot(inv, "Off-Hand") ?? "(none)");
        Kv(sb, "Using alternate weapon", combat.UsingAlternateWeapon.ToString());
        // The round's committed spell action + the cast-code the server is repeating —
        // "DrainSpell" here means the drain override is currently taking the round.
        Kv(sb, "Round spell action", combat.LastCastAction ?? "(weapon / idle)");
        Kv(sb, "Announced spell", combat.AnnouncedSpell ?? "(none)");
        Kv(sb, "Spell casts counted (this room / target)", combat.SpellCastTally);
        Kv(sb, "Attack casts seen landing (session)", combat.ConfirmedAttackCasts.ToString());
        // The attack-spell cascade's own latch, surfaced separately — it can go
        // stale relative to CurrentTarget/AnnouncedSpell above (report
        // paradigm-20260824-012300). A CastingSpellTarget the current room doesn't
        // hold means the resume is waiting on a *Combat Off* that will never come,
        // so the attack stays stranded. (SpellAttackOwed no longer gates between-round
        // casting — heals/cures/buffs fire independently of the attack — but it still
        // flags this stuck-resume shape alongside a stale CastingSpellTarget.)
        Kv(sb, "Casting spell target", combat.CastingSpellTarget ?? "(none)");
        Kv(sb, "Spell attack owed", combat.SpellAttackOwed.ToString());
        // True here alongside a live CastingSpellTarget/CurrentTarget means the
        // spell-mode heartbeat is gated shut and nothing will ever retry the attack
        // (report paradigm-20260824-215802: an engage whose cast lost the round to a
        // recast-interval block left this stuck, and the character never attacked
        // again for the rest of the fight).
        Kv(sb, "Combat off (stuck?)", svc.Combat.CombatOff.ToString());
        // While true a confusion fumble re-sends no attack: the game answered the
        // last one with *Combat Engaged* and is repeating it itself.
        Kv(sb, "Engaged since last attack", svc.Combat.EngagedSinceLastAttack.ToString());
        // True when Auto-Combat is off but a room hostile is blocking a needed rest
        // (HP still above the flee trigger) — the engine is force-engaging to clear it
        // so recovery can proceed (report paradigm-20260901-093301).
        Kv(sb, "Engaging to clear a rest-blocker", svc.Health.ForceClearForRest.ToString());
        Kv(sb, "Clearing a see-hidden room (combat off)", svc.CombatTracker.SeeHiddenClearActive.ToString());
        Kv(sb, "Sneak broken by a see-hidden monster, not sneaking again yet", svc.CombatTracker.SneakBrokenBySeeHidden.ToString());
        Kv(sb, "Clearing after a failed sneak (combat off)", svc.CombatTracker.SneakFailClearActive.ToString());
        // Alternating action-order phase — pairs with the resolved Combat "ActionOrder"
        // setting below to explain why an alternate-order character is casting or
        // swinging this round (even rounds open on the mode's first phase).
        Kv(sb, "Alternation round", combat.AlternationRound.ToString());
        // A spent opener explains a normal swing where a backstab was expected; a held
        // attack explains a monster left unattacked while a send hold (the train-stats
        // screen, a password prompt) was up.
        Kv(sb, "Backstab opener spent", svc.Combat.BackstabOpenerSpent.ToString());
        Kv(sb, "Attack held by send gate", svc.Combat.AttackHeldBySendGate.ToString());
        Kv(sb, "Awaiting backstab resolution", combat.AwaitingBackstabResolution
            ? $"yes (target={combat.PendingBackstabSpecies ?? "(none)"})"
            : "no");
        Kv(sb, "Next pick held for a summon-on-death re-display", svc.Combat.AwaitingSummonRescan ? "yes" : "no");
        // ShadowRest hold explains a stealthed character resting instead of
        // engaging a monster in the room (combat stands down while true).
        Kv(sb, "ShadowRest holding", svc.Health.ShadowRestHolding.ToString());
        Kv(sb, "Sneak-cooldown hold", svc.Stealth.IsHoldingForSneakCooldown.ToString());
        Game.Stealth.CarriedStealthPenalty.Verdict carried = svc.CarriedStealth.Current();
        Kv(sb, "Auto-Sneak stood down for a carried item", svc.Stealth.StoodDownFor ?? "no");
        Kv(sb, "Carried Stealth penalty", carried.Modifier >= 0
            ? "none"
            : $"{carried.Items}: estimated sn chance {carried.Chance}% (stands down under {svc.CarriedStealth.HopelessChance}%, Settings → Other)");
        // Sneak keeping: what automation is waiting so as not to end a sneak.
        Game.Stealth.SneakHold hold = svc.SneakGuard.Current;
        Kv(sb, "Sneak keeping", hold == Game.Stealth.SneakHold.None
            ? "nothing held"
            : $"{hold} — {Game.Stealth.SneakGuard.Describe(hold)}");
        if (svc.SneakGuard.Queued.Count > 0)
            Kv(sb, "Sneak keeping — queued", string.Join(" · ", svc.SneakGuard.Queued.Select(q => $"'{q.Command}'")));
        if (svc.Equipment.HeldGearKinds.Count > 0)
            Kv(sb, "Sneak keeping — gear held", string.Join(", ", svc.Equipment.HeldGearKinds));
        // A cast held on the way stops the walk in the next NPC-free room.
        Kv(sb, "Sneak keeping — cast held", svc.CastDirector.HasSneakHeldCast
            ? (svc.Stealth.IsHoldingForCast ? "yes — the step waits here while it goes out" : "yes — stops in the next NPC-free room")
            : "no");
        Kv(sb, "Party cures backing off", svc.CastDirector.DescribePartyCureBackoff() is { Count: > 0 } cures
            ? string.Join(" · ", cures)
            : "(none)");
        if (svc.Health.IsGateFleeing)
            Kv(sb, "Sneak keeping — gate flee", svc.CastDirector.IsEmergencyHealDue
                ? "emergency heal due; it goes out, the re-sneak waits for it"
                : "no emergency heal due; re-sneak free");
        Kv(sb, "Hit and run", svc.Health.HitAndRunRuns > 0
            ? $"{svc.Health.HitAndRunRuns} of {Math.Max(1, svc.Resolver.Resolve<Models.Profile.CombatSettings>("Combat").HitAndRunMaxRuns)} run(s) since the last backstab"
            : "no runs since the last backstab");

        return sb.ToString();
    }

    // Per-monster game-data overlays the user has customized in the active set —
    // the deltas written via the Game Data Browser's Monster edit dialog (per-
    // monster attack command / attack spell / pre-attack spell, relationship,
    // priority, flags), shown as the EFFECTIVE overlay (realm seed + tier
    // overrides merged) with the tier that owns each record. A "won't attack this
    // monster" report hinges on whether a per-monster attack override is wired
    // (e.g. a physical-immune mob whose only kill means is a configured attack
    // spell) — that state lived nowhere in the capture before. Only records the
    // user actually overrode appear (the tier side-files hold deltas only), so
    // this stays a short list, not the whole realm seed.
    private static string BuildMonsterOverrides(AppServices svc)
    {
        StringBuilder sb = new();

        List<(int Number, string Id)> records = new();
        foreach (string id in svc.Resolver.GameDataOverrideIds("Monsters"))
            if (int.TryParse(id, out int n) && n > 0) records.Add((n, id));
        records.Sort((a, b) => a.Number.CompareTo(b.Number));

        sb.Append("Per-monster overlay deltas in the active game-data set — effective overlay (realm seed + tier overrides merged), tagged with the tier that owns each record (")
          .Append(records.Count).Append(")\n\n");
        if (records.Count == 0) { sb.Append("_(none)_\n"); return sb.ToString(); }

        foreach ((int n, string id) in records)
        {
            Models.GameData.MonsterOverlay o = svc.Resolver.ResolveGameData<Models.GameData.MonsterOverlay>(
                "Monsters", id, svc.MonsterOverlaySeed.GetOverlay(n));
            SettingsTier tier = svc.Resolver.GetGameDataSourceTier("Monsters", id);
            string name = svc.GameData.FindNameByNumber("Monsters", n) ?? "(unknown)";

            List<string> parts = new();
            if (!string.IsNullOrWhiteSpace(o.Name)) parts.Add($"name \"{o.Name}\"");
            if (o.Relationship is { } rel) parts.Add($"relationship {rel}");
            if (o.Priority is { } prio) parts.Add($"priority {prio}");
            if (o.OverridePreAttackSpellId is { } pre and > 0)
                parts.Add($"debuff {SpellLabel(svc, pre)}{CountSuffix(o.OverridePreAttackCount)}{ManaSuffix(o.OverridePreAttackMinMana)}");
            if (o.OverrideAttackSpellId is { } atk and > 0)
                parts.Add($"normal-spell {SpellLabel(svc, atk)}{CountSuffix(o.OverrideAttackCount)}{ManaSuffix(o.OverrideAttackMinMana)}");
            if (o.OverrideAltAttackSpellId is { } alt and > 0)
                parts.Add($"alt-spell {SpellLabel(svc, alt)}{CountSuffix(o.OverrideAltAttackCount)}{ManaSuffix(o.OverrideAltAttackMinMana)}");
            if (!string.IsNullOrWhiteSpace(o.OverridePhysicalCommand))
                parts.Add($"physical-cmd \"{o.OverridePhysicalCommand}\"");
            if (o.DontBackstab == true) parts.Add("dontBackstab");
            if (o.KillOnSight == true) parts.Add("killOnSight");
            if (parts.Count == 0) parts.Add("(no live fields)");

            sb.Append("- #").Append(n).Append(' ').Append(name)
              .Append(" [").Append(tier).Append("] — ")
              .Append(string.Join(", ", parts)).Append('\n');
        }

        return sb.ToString();
    }

    // Per-character combat outcomes actually seen against a monster (Monster
    // Intel's "Your Observations") — the personal counterpart to the MDB-sourced
    // monster overrides above, useful when a report is about "why won't it hit /
    // cast on this thing".
    private static string BuildMonsterObservations(AppServices svc)
    {
        StringBuilder sb = new();
        List<Models.Profile.MonsterObservation> rows = svc.MonsterObservations.Snapshot()
            .OrderByDescending(o => o.LastObservedAt).ToList();

        sb.Append("Combat outcomes THIS character has observed per monster — landed-hit damage, hit rate, and confirmed physical/spell no-effect discoveries (")
          .Append(rows.Count).Append(")\n\n");
        if (rows.Count == 0) { sb.Append("_(none)_\n"); return sb.ToString(); }

        foreach (Models.Profile.MonsterObservation o in rows)
        {
            string name = svc.GameData.FindNameByNumber("Monsters", o.MonsterNumber) ?? "(unknown)";
            List<string> parts = new();
            if (o.HitCount > 0)
                parts.Add($"hits {o.HitCount} (dmg {o.HitDamageMin}-{o.HitDamageMax}, avg {o.AvgHitDamage:0.#})");
            if (o.SwingCount > 0)
                parts.Add($"hit-rate {o.HitRatePercent:0}% ({o.HitCount}/{o.SwingCount})");
            if (o.PhysicalNoEffectCount > 0) parts.Add($"physical-no-effect x{o.PhysicalNoEffectCount}");
            if (o.SpellNoEffectCount > 0) parts.Add($"spell-no-effect x{o.SpellNoEffectCount}");
            if (parts.Count == 0) parts.Add("(no outcomes recorded)");

            sb.Append("- #").Append(o.MonsterNumber).Append(' ').Append(name)
              .Append(" — ").Append(string.Join(", ", parts)).Append('\n');
        }

        return sb.ToString();
    }

    // "151 (disrupt)" — an override stores a Spell.Number; annotate it with the
    // Spells-table display name so a triager needn't cross-reference the id.
    private static string SpellLabel(AppServices svc, int spellNumber)
    {
        string? name = svc.GameData.FindNameByNumber("Spells", spellNumber);
        return string.IsNullOrWhiteSpace(name) ? $"{spellNumber}" : $"{spellNumber} ({name})";
    }

    // " x20" for a positive per-room cast cap; blank for null/0 (unlimited).
    private static string CountSuffix(int? count) => count is > 0 ? $" x{count}" : string.Empty;
    private static string ManaSuffix(int? mana) => mana is > 0 ? $" m{mana}" : string.Empty;

    // The last rounds' damage ledgers, oldest first: who the client credited with
    // what damage, for a report that the round totals or Session Stats' per-round
    // damage look wrong.
    // Session Stats' Player Statistics as the window shows them, plus the spells whose
    // lines it can recognise — the basis for a "counted as a swing" / "proc missing" report.
    private static string BuildSessionCombat(AppServices svc)
    {
        Game.Combat.CombatSessionStats c = svc.CombatSession.Snapshot();
        StringBuilder sb = new();
        Kv(sb, "Attacks", $"{c.TotalSwings} (hit {c.Hits}, crit {c.Crits}, miss {c.Misses}; hit {c.HitPercent:F0}%, crit {c.CritPercent:F0}%)");
        Kv(sb, "Backstabs", $"{c.BackstabAttempts} (landed {c.Backstabs}, failed {c.BackstabFails}; {c.BackstabPercent:F0}%)");
        Kv(sb, "Swing damage", $"{c.PhysicalMinDamage}-{c.PhysicalMaxDamage}, avg {c.PhysicalAvgDamage:F0}");
        Kv(sb, "Procs", $"{c.ProcHits} ({c.ProcMinDamage}-{c.ProcMaxDamage}, total {c.ProcTotalDamage})");
        foreach (Game.Combat.SpellCombatStat sp in c.Spells)
            Kv(sb, $"Spell {sp.Name}", $"{sp.Landed} landed, {sp.Misses} resisted ({sp.RangeText}, total {sp.TotalDamage})");
        Kv(sb, "Hit by", $"{c.MobHits} ({c.HitTakenMinDamage}-{c.HitTakenMaxDamage}, avg {c.HitTakenAvgDamage:F0}); avoided {c.AvoidedAttacks} of {c.IncomingAttacks}");
        Kv(sb, "Rounds with damage", $"{c.RoundsWithDamage} ({c.RoundMinDamage}-{c.RoundMaxDamage}, avg {c.RoundAvgDamage:F0})");
        IReadOnlyList<string> spells = svc.CombatSession.RecognisedSpells;
        Kv(sb, "Recognised spells", spells.Count == 0 ? "(none)" : string.Join(", ", spells));
        return sb.ToString();
    }

    // Session Stats' Session Statistics as the window shows them, in raw units — the
    // basis for a "Walk / Sneak / Exp needed / copper-items figure looks wrong" report.
    private static string BuildSessionActivity(AppServices svc)
    {
        Game.Combat.SessionActivityStats a = svc.SessionActivity.Snapshot();
        (Game.Calculators.TimeToLevelEstimator.Result est, TimeSpan? remaining) = svc.SelfTimeToLevel();
        StringBuilder sb = new();
        Kv(sb, "Rate window", $"{a.TimeOnline.TotalHours:F2} h");
        Kv(sb, "Kills", $"{a.MonstersKilled} ({a.KillsPerHour:F1}/hr)");
        Kv(sb, "Experience", $"{a.ExperienceEarned} ({a.ExperiencePerHour:F0}/hr)");
        Kv(sb, "Exp needed", est.TargetLevel > 0 ? $"{est.ExpNeeded} for L{est.TargetLevel} (banked {est.BankableLevels})" : "(unresolved)");
        Kv(sb, "Will level in", remaining is { } eta ? eta.ToString() : "(rate unknown)");
        Kv(sb, "Collected", $"{a.CurrencyCollected} copper in {a.CoinsCollected} coins, {a.ItemsCollected} items ({a.CurrencyPerHour:F0} copper/hr, {a.CoinsPerHour:F0} coins/hr)");
        Kv(sb, "Deposit/Sold", $"{a.CurrencyDeposited} copper, {a.ItemsSold} items sold");
        Kv(sb, "Stashed", $"{a.CurrencyStashed} copper in {a.CoinsStashed} coins, {a.ItemsStashed} items");
        Kv(sb, "Sneak entries", $"{a.SneakHeld} held of {a.SneakEntries}");
        Kv(sb, "Trap disarms", $"{a.TrapsDisarmed} disarmed of {a.DisarmAttempts} attempts");
        Kv(sb, "Walk steps", a.AverageStep is { } step ? $"{a.Steps} timed, avg {step.TotalSeconds:F2}s" : "(none timed)");
        return sb.ToString();
    }

    // The running HP estimate for each monster in the room — what `look` sharpens the
    // wound band with.
    private static string BuildMonsterHpEstimates(AppServices svc)
    {
        IReadOnlyList<string> rows = svc.MonsterHpEstimates.Describe();
        return rows.Count == 0 ? "_(no monsters tracked in the room)_" : string.Concat(rows.Select(r => $"- {r}\n"));
    }

    private static string BuildCombatRounds(AppServices svc)
    {
        IReadOnlyList<Game.Combat.RoundSummary> recent = svc.RoundDamage.Recent;
        if (recent.Count == 0) return "_(no combat rounds this session)_";
        StringBuilder sb = new();
        foreach (Game.Combat.RoundSummary r in recent.Skip(Math.Max(0, recent.Count - 10)))
        {
            (string dealt, string taken) = Game.Combat.RoundTotalsFormatter.Format(r);
            sb.Append("- ").Append(r.EndedAt.ToString("HH:mm:ss")).Append(' ')
              .Append(dealt).Append(' ').Append(taken).Append('\n');
        }
        return sb.ToString();
    }

    // The engine's live engageability verdict for every monster seen in the
    // current room — the reasoning behind a "skip un-actionable … Unkillable"
    // decision, frozen so a "won't attack this monster" report is self-diagnosing
    // (no dependency on combat logging being on or the scrollback still holding
    // the line). Per hostile: the Magical level (weapon-hit gate), SpellImmu
    // (attack-spell gate), each configured weapon's HitMagic, and the resolved
    // CanAct / StuckOnMana / Unkillable assessment with its reason.
    private static string BuildRoomCombatAssessment(AppServices svc)
    {
        StringBuilder sb = new();
        var rows = svc.Combat.SnapshotRoomEngage();

        // The observed-failure fail-sets first: they override game data (a species
        // named here is skipped even when HitMagic says the weapon should land), and
        // they survive the monster leaving the room view, so they matter even when
        // the per-monster rows below are empty.
        (IReadOnlyList<string> failNormal, IReadOnlyList<string> failAlt) = svc.Combat.SnapshotWeaponFailSets();
        static string FailSet(IReadOnlyList<string> s) => s.Count == 0 ? "(none)" : string.Join(", ", s);
        Kv(sb, "Weapon-no-effect this room — normal", FailSet(failNormal));
        Kv(sb, "Weapon-no-effect this room — alternate", FailSet(failAlt));
        // What the hit-magic gate weighs besides the weapon: the class's and race's
        // own hit magic, and whether an attack command is a strike that doesn't use
        // the weapon at all.
        (int innate, string? normalCommand, string? alternateCommand) = svc.Combat.SnapshotHitMagicInputs();
        static string AttackKind(string? command) =>
            Game.Combat.MartialArtsCommand.IsStrike(command) ? "martial-arts strike, weapon not used" : "weapon attack";
        Kv(sb, "Character's own hit magic (class + race)", innate.ToString());
        Kv(sb, "Normal attack", $"`{normalCommand}` — {AttackKind(normalCommand)}");
        Kv(sb, "Alternate attack", $"`{alternateCommand}` — {AttackKind(alternateCommand)}");
        sb.Append('\n');

        sb.Append("Engine engageability of monsters known in the current room (weapon/spell magic gates + verdict) (")
          .Append(rows.Count).Append(")\n\n");
        if (rows.Count == 0) { sb.Append("_(no monsters tracked in the current room)_\n"); return sb.ToString(); }

        // -1 from the magic indexes means "unknown → fail open" — spell it out so a
        // triager doesn't read the sentinel as a real level.
        static string Lvl(int v) => v < 0 ? "?" : v.ToString();

        foreach (var r in rows)
        {
            string name = svc.GameData.FindNameByNumber("Monsters", r.MonsterNumber) ?? r.Species;
            sb.Append("- #").Append(r.MonsterNumber).Append(' ').Append(name)
              .Append(" — **").Append(r.Assessment).Append("**")
              .Append(", Magical ").Append(Lvl(r.Magical))
              .Append(", SpellImmu ").Append(Lvl(r.SpellImmu))
              .Append(", attack hit magic normal=").Append(Lvl(r.NormalWeaponHit))
              .Append(" alt=").Append(Lvl(r.AltWeaponHit));
            if (!string.IsNullOrWhiteSpace(r.UnengageableReason))
                sb.Append(" — ").Append(r.UnengageableReason);
            sb.Append('\n');
        }
        return sb.ToString();
    }

    // Every configured spell slot resolved against the active game-data set:
    // cast-code → Spell.Number, name, learned?, ReqLevel, EnergyCost, mana cost.
    // A spell whose ReqLevel / EnergyCost / learned flag reads wrong here explains
    // a mis-cast or a "spell never fires / looks blocked" report at a glance (the
    // duplicate-short-code ReqLevel corruption would have jumped straight out).
    private static string BuildSpellResolution(AppServices svc)
    {
        var combat = svc.Resolver.Resolve<Models.Profile.CombatSettings>("Combat");
        var spells = svc.Resolver.Resolve<Models.Profile.SpellsSettings>("Spells");
        var party = svc.Resolver.Resolve<Models.Profile.PartySettings>("Party");

        StringBuilder sb = new();
        Kv(sb, "Current mana", $"{svc.PlayerState.Ma}/{svc.PlayerState.MaxMa}");

        // What the Spell Book believes the character can cast and has learned. The
        // Buff Watchdog's pickers are built from exactly this, so a "my spell isn't
        // offered" report needs the gate it was built under and what fell outside it.
        Game.Spells.SpellbookState book = svc.Spellbook;
        string alignGate = book.CharAlign switch
        {
            0 => "unknown (nothing filtered)",
            1 => "Good",
            2 => "Neutral",
            3 => "Evil",
            4 => "evil, short of Outlaw (no evil-only spells)",
            _ => book.CharAlign.ToString(),
        };
        Kv(sb, "Spell book", $"class #{book.ClassNumber}, level {book.Level}, alignment gate {alignGate}; "
            + $"{book.ClassSpells.Count} spell(s) in the class list, {book.Available.Count} usable under that gate, "
            + $"{book.ObtainedCount} learned");
        HashSet<string> slottedCodes = new(
            (svc.Profile.Current?.PartyBuffs?.Slots ?? new List<Models.Profile.BuffSlot>())
                .Where(slot => !string.IsNullOrWhiteSpace(slot.Spell)).Select(slot => slot.Spell!.Trim()),
            StringComparer.OrdinalIgnoreCase);
        List<string> unslotted = book.Available
            .Where(spell => Game.Spells.BuffClassifier.IsAnyBuff(spell) && book.IsObtained(spell.Number)
                && !slottedCodes.Contains(spell.Short.Trim()))
            .Select(spell => $"{spell.Short.Trim()} ({spell.Name}, level {spell.ReqLevel})")
            .ToList();
        Kv(sb, "Learned buffs with no Buff Watchdog slot",
            unslotted.Count == 0 ? "(none)" : string.Join(", ", unslotted));

        // Buff-duration timers CastingDirector believes are still running, straight
        // from its own tracking (not re-derived) — a timer surviving past a real
        // death is the direct symptom of report paradigm-20260824-012300 (the
        // server clears every buff on death, but nothing told CastingDirector, so
        // it declines to recast a buff that's actually long gone).
        IReadOnlyList<Game.Spells.ActiveBuffTimer> buffs = svc.CastDirector.SnapshotActiveBuffs();
        sb.Append("\n**Active buff timers (CastingDirector)**\n\n");
        if (buffs.Count == 0)
        {
            sb.Append("(none)\n");
        }
        else
        {
            foreach (Game.Spells.ActiveBuffTimer b in buffs)
            {
                string target = string.IsNullOrEmpty(b.Target) ? "self" : b.Target;
                System.TimeSpan remaining = b.Until - System.DateTime.UtcNow;
                string what = b.Outcome is { Length: > 0 } drawn ? $"{b.Short} ({drawn})" : b.Short;
                sb.Append($"- {what} on {target}: {(remaining > System.TimeSpan.Zero ? $"{remaining.TotalSeconds:F0}s remaining" : "expired, not yet cleared")} (of {b.TotalSec}s)\n");
            }
        }
        sb.Append('\n');

        // Buffs a configured winner PERMANENTLY removes one-directionally (Paradigm continuous
        // removal) — never maintained, shown "covered by" in the Watchdog. Surfaced so a
        // "why isn't <buff> casting / holding a timer" report shows it's a deliberate skip.
        IReadOnlyDictionary<string, string> suppressed = svc.CastDirector.CurrentSuppressedBuffs();
        sb.Append("**Suppressed buffs (permanently removed by a configured buff)**\n\n");
        if (suppressed.Count == 0)
        {
            sb.Append("(none)\n");
        }
        else
        {
            foreach (KeyValuePair<string, string> kv in suppressed)
                sb.Append($"- {kv.Key}: not maintained — covered by {kv.Value}\n");
        }
        sb.Append('\n');

        // Stock counterpart: one-directional losers KEPT by casting the remover first
        // (loser cast-code → remover cast-code). Surfaced so a "both won't hold on stock"
        // report shows the ordering the director is applying. Empty off stock.
        IReadOnlyDictionary<string, string> collisionKept = svc.CastDirector.CurrentCollisionOrder();
        sb.Append("**Collision-ordered buffs (stock — kept by casting the remover first)**\n\n");
        if (collisionKept.Count == 0)
        {
            sb.Append("(none)\n");
        }
        else
        {
            foreach (KeyValuePair<string, string> kv in collisionKept)
                sb.Append($"- {kv.Key}: kept — cast after its remover {kv.Value}\n");
        }
        sb.Append('\n');

        // Mana-regen reroll engine state — so a "flux stuck at a bad value" report
        // (paradigm-20260830-110918) shows the roll quality it judges from and its
        // cycle, not just the configured threshold in the buff plan above.
        Game.Spells.ManaRegenReroller reroll = svc.ManaRegen;
        string rerollSignal = svc.GameData.ActiveRealm == Game.RealmType.ParaMud
            ? "abil 145 spells value" : "roll read back off the natural mana tick";
        sb.Append("**Mana-regen reroll**\n\n");
        sb.Append($"- Roll signal: {rerollSignal}\n");
        sb.Append($"- Cycle active: {reroll.CycleActive}; rerolls used this cycle: {reroll.RerollsUsed}\n");
        sb.Append($"- Waiting for mana to resume: {reroll.WaitingForMana}\n");
        sb.Append($"- Waiting for the fight to end: {reroll.WaitingForCombat}\n");
        sb.Append($"- Last observed roll: {reroll.LastObservedText ?? "(none judged yet)"}\n");
        if (svc.GameData.ActiveRealm != Game.RealmType.ParaMud)
            sb.Append($"- Stock tick inputs: {svc.DescribeStockManaRollContext()}\n");
        sb.Append('\n');

        int shown = 0;
        void Group(string title, IEnumerable<(string Label, string? Code)> slots)
        {
            List<string> lines = new();
            foreach ((string label, string? code) in slots)
            {
                if (string.IsNullOrWhiteSpace(code)) continue;
                lines.Add("- " + SpellResolutionLine(svc, label, code));
                shown++;
            }
            if (lines.Count == 0) return;
            sb.Append("**").Append(title).Append("**\n\n");
            foreach (string l in lines) sb.Append(l).Append('\n');
            sb.Append('\n');
        }

        static string CureLabel(string ailment, bool afterCombat) =>
            afterCombat ? ailment + " (after combat)" : ailment;

        Group("Combat", new (string, string?)[]
        {
            ("normal-attack", combat.NormalAttackSpell.SpellName),
            ("alternate-attack", combat.AlternateAttackSpell.SpellName),
            ("multi-attack", combat.MultiAttackSpell.SpellName),
            (combat.MultiAttack2Enabled ? "multi-attack-2" : "multi-attack-2 (disabled)",
             combat.MultiAttack2Spell.SpellName),
            ("area-debuff", combat.AreaDebuffSpell.SpellName),
            ("single-debuff", combat.SingleTargetDebuffSpell.SpellName),
            ("drain", combat.DrainSpell.SpellName),
        });
        Group("Heal & regen", new (string, string?)[]
        {
            ("minor-heal", spells.MinorHealSpell),
            ("major-heal", spells.MajorHealSpell),
            ("emergency-heal", spells.EmergencyHealSpell),
            ("hp-regen", spells.HpRegenSpell),
            ("ma-regen", spells.MaRegenSpell),
        });
        Group("Cures", new (string, string?)[]
        {
            (CureLabel("holds", spells.CureHoldsAfterCombat), spells.CureHoldsSpell),
            (CureLabel("poison", spells.CurePoisonAfterCombat), spells.CurePoisonSpell),
            (CureLabel("disease", spells.CureDiseaseAfterCombat), spells.CureDiseaseSpell),
            (CureLabel("blindness", spells.CureBlindnessAfterCombat), spells.CureBlindnessSpell),
        });
        Group("Party heal", new (string, string?)[]
        {
            ("minor-party-heal", party.MinorPartyHealSpell),
            ("minor-party-heal-aoe", party.MinorPartyHealAoeSpell),
            ("major-party-heal", party.MajorPartyHealSpell),
            ("major-party-heal-aoe", party.MajorPartyHealAoeSpell),
        });

        // The one unified buff list (self bless + when-full + party buffs). Each slot's
        // label carries its targeting + any per-slot condition so a "buff didn't fire"
        // report shows exactly what was configured.
        int buffNo = 0;
        if (svc.Profile.Current?.PartyBuffs is { } unifiedBuffs)
        {
            // Note the layout + cast-priority mode: the list is shown in display order,
            // but the engine casts by category unless priority is top→bottom AND the
            // rows were hand-arranged — so a "wrong buff fired first" report needs both.
            string heading = unifiedBuffs.ManualOrder || unifiedBuffs.PriorityTopDown
                ? $"Buffs (layout: {(unifiedBuffs.ManualOrder ? "manual" : "auto")}; priority: {(unifiedBuffs.PriorityTopDown ? "top→bottom" : "default")})"
                : "Buffs";
            Group(heading, unifiedBuffs.Slots.Select(s => ($"buff {++buffNo} [{BuffScope(svc, s)}]", s.Spell)));
        }

        if (shown == 0) sb.Append("_(no spells configured)_\n");
        return sb.ToString();
    }

    // "code → #num name [learned] ReqLevel=X Energy=Y Mana=Z" for one slot's cast-
    // code, resolved against the active set. Unknowns render as "?" (fail-open in
    // the engine) rather than a sentinel number.
    private static string SpellResolutionLine(AppServices svc, string label, string code)
    {
        // The character's own spell for the code first: several spells can share one
        // (a priest's three word spells), and the set-wide index knows only the first.
        int? number = svc.Spellbook.FindByCastCode(code)?.Number ?? svc.SpellShort.NumberByShort(code);
        string head = $"{label}: `{code}`";
        if (number is not { } n)
            return $"{head} → (no Spells row with this short-code)";

        string name = svc.GameData.FindNameByNumber("Spells", n) ?? "(unnamed)";
        bool learned = svc.Spellbook.IsObtained(n);
        int req = svc.SpellReqLevel.ReqLevel(code);
        int? energy = svc.SpellCatalog.GetFormulaByNumber(n)?.EnergyCost;
        int? mana = svc.Spellbook.ManaCostOf(code);
        return $"{head} → #{n} {name} [{(learned ? "learned" : "NOT learned")}]"
             + $" ReqLevel={(req < 0 ? "?" : req.ToString())}"
             + $" Energy={(energy is { } e ? e.ToString() : "?")}"
             + $" Mana={(mana is { } m ? m.ToString() : "?")}";
    }

    // A unified buff slot's targeting + condition summary for the report label —
    // e.g. "self", "all", "Bob,Sue", "party-wide+solo", with "+hp-full" /
    // "+ma-full" when a downtime condition is set. Whole-party scope is resolved from
    // the same live spellbook data as the UI so the report exposes both master + option.
    private static string BuffScope(AppServices svc, Models.Profile.BuffSlot s)
    {
        List<string> who = new();
        string code = s.Spell?.Trim() ?? string.Empty;
        bool wholeParty = Game.Spells.ItemCastToken.IsToken(code)
            ? svc.Spellbook.IsTokenWholeParty(code)
            : svc.Spellbook.FindByCastCode(code) is { } spell
              && Game.Spells.BuffClassifier.IsWholeParty(spell.Targets);
        if (wholeParty)
        {
            if (s.WholePartyOn)
            {
                who.Add("party-wide");
                who.Add(s.CastSolo ? "solo" : "party-only");
            }
            else
            {
                who.Add("off");
            }
        }
        else
        {
            if (s.CastOnSelf) who.Add("self");
            if (s.AllMembers) who.Add("all");
            else if (s.Targets.Count > 0) who.Add(string.Join(",", s.Targets));
        }
        string scope = who.Count > 0 ? string.Join("+", who) : "unset";
        if (s.OnlyWhenHpFull) scope += " +hp-full";
        if (s.OnlyWhenMaFull) scope += " +ma-full";
        if (s.OnlyWhenDark) scope += " +only-dark";
        if (s.CastBeforeRestingForMana) scope += " +pre-rest";
        scope += $" +ma>={s.BlessIfAboveMa}";
        if (s.BlessWhileResting) scope += " +while-resting";
        if (s.BlessDuringCombat) scope += " +in-combat";
        if (s.RerollInfinite || s.RerollCount > 0)
            scope += $" +reroll<{s.RerollThreshold?.ToString() ?? "-"}x{(s.RerollInfinite ? "∞" : s.RerollCount.ToString())}";
        return scope;
    }

    // Per-item overlay deltas the user set in the active set — the loot-automation
    // flags written via the Game Data Browser's Item edit dialog. Symmetric to the
    // Monster overrides section; targets "why didn't it collect / sell / stash this
    // item" reports, which were blind to per-item flags before.
    private static string BuildItemOverrides(AppServices svc)
    {
        StringBuilder sb = new();

        List<(int Number, string Id)> records = new();
        foreach (string id in svc.Resolver.GameDataOverrideIds("Items"))
            if (int.TryParse(id, out int n) && n > 0) records.Add((n, id));
        records.Sort((a, b) => a.Number.CompareTo(b.Number));

        sb.Append("Per-item overlay deltas in the active game-data set — effective overlay (seed + tier overrides merged), tagged with the owning tier (")
          .Append(records.Count).Append(")\n\n");
        if (records.Count == 0) { sb.Append("_(none)_\n"); return sb.ToString(); }

        foreach ((int n, string id) in records)
        {
            Models.GameData.ItemOverlay o = svc.Resolver.ResolveGameData<Models.GameData.ItemOverlay>(
                "Items", id, svc.ItemOverlaySeed.GetOverlay(n));
            SettingsTier tier = svc.Resolver.GetGameDataSourceTier("Items", id);
            string name = svc.GameData.FindNameByNumber("Items", n) ?? "(unknown)";

            List<string> parts = new();
            if (!string.IsNullOrWhiteSpace(o.Name)) parts.Add($"name \"{o.Name}\"");
            Flag(parts, "autoCollect", o.AutoCollect);
            Flag(parts, "autoDiscard", o.AutoDiscard);
            Flag(parts, "autoFind", o.AutoFind);
            Flag(parts, "autoOpen", o.AutoOpen);
            Flag(parts, "autoBuy", o.AutoBuy);
            Flag(parts, "autoSell", o.AutoSell);
            Flag(parts, "sellDetour", o.SellDetour);
            if (!string.IsNullOrWhiteSpace(o.SellDetourAbove)) parts.Add($"sellDetourAbove {o.SellDetourAbove}");
            if (!string.IsNullOrWhiteSpace(o.SellShops)) parts.Add($"sellShops {o.SellShops}");
            Flag(parts, "autoStash", o.AutoStash);
            Flag(parts, "cannotBeTaken", o.CannotBeTaken);
            Flag(parts, "mustHaveMinimum", o.MustHaveMinimum);
            Flag(parts, "loyalItem", o.LoyalItem);
            Flag(parts, "autoObtainForPath", o.AutoObtainForPath);
            if (!string.IsNullOrWhiteSpace(o.MinToKeep)) parts.Add($"minToKeep {o.MinToKeep}");
            if (!string.IsNullOrWhiteSpace(o.MaxToGet)) parts.Add($"maxToGet {o.MaxToGet}");
            if (parts.Count == 0) parts.Add("(no live fields)");

            sb.Append("- #").Append(n).Append(' ').Append(name)
              .Append(" [").Append(tier).Append("] — ")
              .Append(string.Join(", ", parts)).Append('\n');
        }
        return sb.ToString();
    }

    // Append "flag" (on) / "!flag" (off) for a set tri-state bool; skip when null
    // (not overridden at any tier).
    private static void Flag(List<string> parts, string name, bool? value)
    {
        if (value is { } v) parts.Add(v ? name : "!" + name);
    }

    private static string BuildPlayerState(AppServices svc)
    {
        StringBuilder sb = new();
        sb.Append("**Live vitals (PlayerState)**\n\n");
        sb.Append(Json(svc.PlayerState)).Append('\n');
        // Self ailment flags (ConditionTracker) — the authoritative self view that
        // gates poison-sensitive behavior (e.g. the downtime-rest paths skip a
        // poisoned character). Distinct from the party self-row's broadcast flags.
        Kv(sb, "Active conditions (self)", svc.Conditions.ActiveFlags.ToString());
        sb.Append('\n');
        sb.Append("**Stat screen (PlayerStats)**\n\n");
        sb.Append(Json(svc.PlayerStats));
        return sb.ToString();
    }

    // Editor statline vs what the game actually prints. A prompt the parser can't
    // read leaves HP at 0 and stalls every HP-gated engine with no other symptom
    // (report stock-20260929-111956), so this answers "is HP being read at all?".
    private static string BuildStatline(AppServices svc)
    {
        StringBuilder sb = new();
        StatlineReconciler r = svc.StatlineReconcile;
        string? editor = r.DesiredCommand;
        Kv(sb, "Editor command", StatlineSyntax.IsDefault(editor)
            ? "full (Default)"
            : StatlineSyntax.NormalizeForWire(editor!));
        Kv(sb, "Latest prompt matches Settings -> Statline", r.LastPromptMatched switch
        {
            true  => "yes",
            false => "NO",
            null  => "(no prompt seen)",
        });
        Kv(sb, "Last unmatched prompt", r.LastUnmatchedPrompt is { } p ? $"`{p}`" : "(none)");
        Kv(sb, "Reconciler", $"armed={r.IsArmed}, in game={r.IsInGame}, synced={r.IsSynced}, "
            + $"unmatched in a row={r.ConsecutiveMismatches}/{r.MismatchThreshold}, "
            + $"resends={r.Retries}/{r.MaxRetries}, gave up={r.HasGivenUp}");
        Kv(sb, "Mismatch warning", r.IsFlagged ? "SHOWING" : "off");
        return sb.ToString();
    }

    // Enabled / listening state, subscriber count, and whether anything has been
    // rejected. Never the token.
    private static string DescribeLocalApi(AppServices svc)
    {
        if (!svc.Settings.Current.LocalApiEnabled) return "off";
        Api.LocalApiServer api = svc.LocalApi;
        string state = api.IsListening
            ? $"listening on 127.0.0.1:{api.Port}"
            : $"ENABLED BUT NOT LISTENING ({api.LastStartError ?? "reason unknown"})";
        // Whether an automated caller could have suicided / disconnected this
        // character materially changes how the rest of the report reads.
        string destructive = svc.Settings.Current.LocalApiAllowDestructive
            ? ", DESTRUCTIVE COMMANDS ALLOWED"
            : ", destructive commands blocked";
        string subs = $"{destructive}, {api.Events.SubscriberCount} event subscriber(s)";
        string failed = api.Auth.LastAuthFailureUtc is { } at
            ? $", last rejected request {at:u}"
            : string.Empty;
        return state + subs + failed;
    }

    // The last update-check verdict (never checked / up to date / a newer build /
    // no-asset / an error). Answers "why didn't I get an update notice" — was the
    // check even run, and what did GitHub say.
    private static string DescribeUpdate(AppServices svc)
    {
        if (svc.Update.Last is not { } r) return "(not checked this session)";
        return r.State switch
        {
            Update.UpdateAvailability.UpToDate         => $"up to date ({r.CurrentVersion})",
            Update.UpdateAvailability.UpdateAvailable  => $"{r.CurrentVersion} → {r.LatestVersion} available",
            Update.UpdateAvailability.NoAssetForPlatform => $"{r.LatestVersion} available, no asset for this platform",
            _                                          => $"check failed ({r.Error ?? "reason unknown"})",
        };
    }

    private static string BuildInventory(AppServices svc)
    {
        InventorySnapshot snapshot = svc.Inventory.Snapshot;
        return Json(snapshot);
    }

    // The Character Workshop's persisted, per-character artifacts — the gear
    // sets, the CP-allocation plan, and the quest log. These live as top-level
    // CharacterProfile properties (not in the settings-tab dictionary), so the
    // settings dump wouldn't otherwise carry them. The rest of the Workshop is
    // a read-only view over stats / inventory already captured above.
    private static string BuildWorkshop(AppServices svc)
    {
        var profile = svc.Profile.Current;
        if (profile is null) return "_(no character loaded)_";

        StringBuilder sb = new();

        sb.Append("**Gear sets (Equipment)**\n\n");
        sb.Append(profile.Equipment is { } equip ? Json(equip) : "_(none)_\n");

        // Unwearable-slot blocks — items a set can't currently equip (alignment /
        // level / class, or a game refusal). Skipped on apply until addressed, so
        // a "set won't equip X" report is answered right here.
        var blocks = svc.Equipment.BlockedSlotsSnapshot();
        sb.Append("\n**Equipment slot blocks** (").Append(blocks.Count).Append(")\n\n");
        if (blocks.Count == 0)
            sb.Append("_(none)_\n");
        else
            foreach ((string setId, var slot, string item, bool refused) in blocks)
            {
                string setName = profile.Equipment?.Sets
                    .FirstOrDefault(s => string.Equals(s.Id, setId, StringComparison.Ordinal))?.Name ?? setId;
                sb.Append("- ").Append(setName).Append(" / ").Append(slot)
                  .Append(": ").Append(item)
                  .Append(refused ? " (game refused)" : " (restricted)").Append('\n');
            }

        // Location auto-equip (Settings → Other): items currently worn under a
        // matched-area rule. The rules themselves ride the resolved OtherSettings
        // JSON below; this is the live "what's owned right now" state, so a "mask
        // didn't equip / didn't revert" report is answered from the capture.
        var locActive = svc.LocationEquip.ActiveItemsSnapshot();
        sb.Append("\n**Location auto-equip (active)** (").Append(locActive.Count).Append(")\n\n");
        if (locActive.Count == 0)
            sb.Append("_(none — not in a matched area, or the item isn't carried)_\n");
        else
            foreach (string item in locActive)
                sb.Append("- ").Append(item).Append('\n');

        // Chest Offload list (ChestOpenTracker): what's still listed after the opens,
        // capped at what's carried, and whether an open is mid-flight.
        var chestLoot = svc.ChestOpens.Loot(svc.Inventory.Snapshot.CarriedItems);
        sb.Append("\n**Chest Offload list** (").Append(chestLoot.Count)
          .Append(svc.ChestOpens.IsOpening ? ", an open in progress" : "")
          .Append(", coin ").Append(svc.ChestOpens.Coin.TotalCopperValue).Append("c")
          .Append(svc.ChestOpens.SayLootToRoom ? "; said to the room" : "; not said to the room")
          .Append(svc.ChestSellTour.IsRunning ? "; sell tour running" : "")
          .Append(svc.ChestSellTour.Status.Length > 0 ? $" — {svc.ChestSellTour.Status}" : "")
          .Append(")\n\n");
        if (chestLoot.Count == 0)
            sb.Append("_(empty)_\n");
        else
            foreach ((string name, int count) in chestLoot)
                sb.Append("- ").Append(count).Append(' ').Append(name).Append('\n');

        var plan = profile.CharacterPlan;
        sb.Append("\n**CP allocation plan (CharacterPlan)** (").Append(plan?.Count ?? 0).Append(")\n\n");
        sb.Append(plan is { Count: > 0 } ? Json(plan) : "_(none)_\n");
        AppendPlanBaseline(sb, svc);

        var quests = profile.QuestLog;
        sb.Append("\n**Quest log (QuestLog)** (").Append(quests?.Count ?? 0).Append(")\n\n");
        sb.Append(quests is { Count: > 0 } ? Json(quests) : "_(none)_\n");

        // Count only — the Players Seen log can hold many rows; a count tells an
        // "empty / not recording" report apart from a working one without bloating
        // the capture with the whole roster.
        var seen = profile.PlayersSeen;
        sb.Append("\n**Players seen (PlayersSeen)**: ").Append(seen?.Count ?? 0).Append('\n');

        return sb.ToString();
    }

    // The baseline the CP plan is measured from, and how it was reached: what the
    // last `stat` screen marked as modified, what came off each stat for gear and
    // for the effects that screen listed, and what couldn't be accounted for.
    private static void AppendPlanBaseline(StringBuilder sb, AppServices svc)
    {
        var ctx = Game.Calculators.CharacterPlanContext.Resolve(
            svc.PlayerStats, svc.GameData, svc.Inventory, svc.ListedEffects);
        sb.Append("\n**CP plan baseline (worked back from the last `stat` screen)**\n\n");
        if (!ctx.HasCharacter)
        {
            sb.Append("_(no character / race resolved)_\n");
            return;
        }
        var r = ctx.Reading;
        Game.PlayerStats s = svc.PlayerStats;
        static string Row(int[] v) => $"STR {v[0]} INT {v[1]} WIL {v[2]} AGL {v[3]} HEA {v[4]} CHM {v[5]}";
        sb.Append("- reading: ").Append(r.State)
          .Append(s.ModifiedMarksRead ? " (modified marks read)" : " (no modified marks on record)").Append('\n');
        sb.Append("- marked modified: ").Append(r.Modified).Append('\n');
        sb.Append("- shown: ").Append(Row(new[] { s.Strength, s.Intellect, s.Willpower, s.Agility, s.Health, s.Charm })).Append('\n');
        sb.Append("- baseline may be acted on (rows clamped / pruned by it): ").Append(ctx.BaselineTrusted).Append('\n');
        sb.Append("- taken off for worn gear: ").Append(Row(r.Equipment)).Append('\n');
        sb.Append("- taken off for listed effects: ").Append(Row(r.Effects)).Append('\n');
        sb.Append("- unmodified (before the race floor): ").Append(Row(r.Base)).Append('\n');
        sb.Append("- not accounted for: ").Append(r.Unexplained).Append('\n');
        foreach (string note in r.EffectNotes)
            sb.Append("- effect: ").Append(note).Append('\n');
        sb.Append("- effect lines on that screen: ").Append(s.ActiveEffects.Count == 0
            ? "(none)"
            : string.Join(" | ", s.ActiveEffects.Select(e => e.Timed ? e.Text + " (timed)" : e.Text))).Append('\n');
        sb.Append("- auto-train hold: ").Append(svc.AutoTrain.HoldReason ?? "(none)").Append('\n');
        sb.Append("- Stock wait at the trainer for altered stats: ")
          .Append(svc.TrainerWalk.AlteredStatsWaitSeconds).Append("s, `stat` read again every ")
          .Append(svc.TrainerWalk.AlteredStatsRereadDelay.TotalSeconds.ToString("0")).Append("s\n");
        sb.Append("- last CP pass not applied because: ").Append(svc.AutoTrain.LastApplyNote ?? "(n/a)").Append('\n');
    }

    private static string BuildMovement(AppServices svc)
    {
        StringBuilder sb = new();
        Kv(sb, "Coalesced state", svc.MovementControl.State.ToString());
        Kv(sb, "Active", svc.MovementControl.IsActive.ToString());
        Kv(sb, "Paused", svc.MovementControl.IsPaused.ToString());
        // Name the gate(s) actually holding the pause. "Paused: True" alone
        // can't tell a rest-hold (HealthRecovery) from a fight-hold (Combat) or
        // a manual stop (User) — the distinction a "walker stuck idle" report
        // needs to point at the right engine.
        var gates = svc.MovementCoordinator.AssertedGates;
        Kv(sb, "Paused by", gates.Count > 0 ? string.Join(", ", gates) : "(nothing)");
        Kv(sb, "Paused by a typed move", svc.MovementControl.PausedByTypedMove ?? "(no)");
        Kv(sb, "Loop waiting on its walk-to", svc.LoopHandoff.Pending is { } waiting
            ? $"'{waiting.Name}', starts at {svc.LoopHandoff.Entry}"
            : "(none)");
        // Why an automatic walk stopped short of a teleport, or went through one.
        Kv(sb, "Automatic walks may use", svc.Walker.AutomaticWalkTeleports is { } teleports
            ? (teleports.Count == 0 ? "no teleports" : $"{teleports.Count} teleport(s): "
                + string.Join(", ", teleports.Take(12).Select(t => $"{t.From}>{t.To}")) + (teleports.Count > 12 ? ", …" : string.Empty))
            : "any teleport (not wired)");
        // Whether the Auto-All kill switch is the one holding navigation — it
        // suspends an in-flight nav on engage and resumes it on restore.
        Kv(sb, "Auto-All suspended nav", svc.MovementControl.IsAutoAllSuspended.ToString());
        // A Stop that held an errand reads as a plain User pause above; this names it.
        Kv(sb, "Errand held by Stop", svc.MovementControl.SuspendedErrand ?? "(none)");
        var loop = svc.LoopRunner;
        Kv(sb, "Loop runner", loop.State.ToString());
        // CurrentLoop is the loop of the LIVE run; StagedLoop is the loaded-but-
        // -not-started slot. They're mutually exclusive, so report both — a
        // running loop shows up under CurrentLoop, never StagedLoop.
        Kv(sb, "Running loop",
            loop.CurrentLoop is { } running
                ? $"{running.Name} — step {loop.CurrentIndex + 1}/{loop.StepCount}"
                : "(none)");
        Kv(sb, "Loop holding for a command's replies", loop.AwaitingCommandReplies ? "yes" : "no");
        // A room command that opens an exit on a stat roll is sent again when the
        // move behind it bonks; a count stuck at the cap is a reveal that never took.
        Kv(sb, "Rolled-reveal re-sends on the step in flight (walk / loop)",
            $"{svc.Walker.RolledRevealRetries} / {loop.RolledRevealRetries} of "
            + Game.Map.SpecialExitDispatch.RolledRevealRetryCap);
        if (loop.CurrentLoop is { } curLoop)
        {
            Kv(sb, "Loop approach target",
                loop.ApproachTarget is { } appr ? $"{appr.Map}/{appr.Room}" : "(none)");
            Kv(sb, "Loop circle start",
                loop.CircleStartRoom is { } start ? $"{start.Map}/{start.Room}" : "(none)");
            // Loop combat-suppression state — answers "why didn't it fight
            // here?" for a do-not-attack / only-attack-in-lair report.
            Kv(sb, "Loop only-attack-in-lair", curLoop.OnlyAttackInLairRooms.ToString());
            Kv(sb, "Loop wait-to-debuff before lairs", curLoop.LairEntryDebuff.ToString());
            Kv(sb, "Loop do-not-attack waypoints",
                curLoop.Waypoints.Count(w => w.DoNotAttack).ToString());
            Kv(sb, "Loop do-not-rest waypoints",
                curLoop.Waypoints.Count(w => w.DoNotRest).ToString());
            Kv(sb, "Loop rest-up-here waypoints (HP / mana)",
                $"{curLoop.Waypoints.Count(w => w.RestHereHp)} / {curLoop.Waypoints.Count(w => w.RestHereMana)}");
            // The verdict the engage gates act on, and which room it judged — the
            // room an in-flight loop move is entering, or the tracker's current room.
            (Game.Map.RoomKey? judged, bool suppressedNow, bool entering) = svc.CombatSuppressionVerdict();
            Kv(sb, "Combat suppressed in current room",
                judged is { } jr
                    ? $"{suppressedNow} (judged at {jr}, {(entering ? "room the in-flight loop move is entering" : "tracker's current room")})"
                    : "(unknown room)");
            Kv(sb, "Loop step in flight", svc.LoopRunner.IsStepInFlight.ToString());
            Kv(sb, "Loop waiting on a trap disarm", svc.LoopRunner.IsAwaitingTrapDisarm.ToString());
            Kv(sb, "Loop command held for an empty room", svc.LoopRunner.AwaitingEmptyRoom ? "yes — clearing the room first" : "no");
        }
        // A loop set aside by a dropped link restarts on the first prompt back in the
        // game, held until the party reform has seen the room when one is pending: a
        // "walked off without the party after a relog" report needs which it was.
        Kv(sb, "Loop restart after reconnect", svc.LoopRunner.PendingReconnectResumeName is { } pendingLoop
            ? $"'{pendingLoop}' — on the next in-game prompt"
            : svc.LoopRunner.ReconnectResumeHeldForReform
                ? "restarted — held until the party reform has seen the room"
                : "(none pending)");
        // Settings → Cash + Items "No combat during an auto-sell detour / auto-deposit trip".
        Kv(sb, "Auto-Combat held off for a detour", svc.DetourCombat.HeldFor ?? "(no)");
        Kv(sb, "Staged loop", loop.StagedLoop?.Name ?? "(none)");
        // Whether the map is in a build mode, and what its loop builder holds: a
        // running loop drawn as the builder's line was reported with nothing in the
        // capture to say how the window got there (report paradigm-20261008-225930).
        Kv(sb, "Navigation window mode", svc.NavigationModeProvider?.Invoke() ?? "(window closed)");
        // Last loop / auto-lair run this session, retained past a stop/death —
        // what @path reports when idle so a party member can help the player
        // resume the circuit they were on.
        Kv(sb, "Last run loop", loop.LastRunLoopName ?? "(none)");
        Kv(sb, "Last run auto-lair", svc.AutoLair.LastRunLairName ?? "(none)");
        Kv(sb, "Auto-Lair phase", svc.AutoLair.Phase.ToString());
        Kv(sb, "Auto-Lair active", svc.AutoLair.IsActive.ToString());
        Kv(sb, "Auto-Lair paused", svc.AutoLair.IsPaused.ToString());
        Kv(sb, "Auto-Lair target",
            svc.AutoLair.CurrentTarget is { } lair ? $"{lair.Map}/{lair.Room}" : "(none)");
        // Which anchor the target's respawn clock runs from (Stock: the room's last
        // kill, else the entry; Paradigm: the entry) — a "Auto-Lair walked in early /
        // waited too long" report needs both stamps and the ready-time they gave.
        if (svc.AutoLair.CurrentTarget is { } clockLair)
        {
            Game.Map.LairTimerStore t = svc.LairTimers;
            int? overrideSec = svc.AutoLair.GetOverride(clockLair);
            static string At(DateTimeOffset? v, string none) => v?.ToLocalTime().ToString("HH:mm:ss") ?? none;
            Kv(sb, "Auto-Lair target clock",
                $"entered {At(t.LastEntered(clockLair), "—")}, last kill {At(t.LastKilled(clockLair), "—")}, "
                + $"respawn {(overrideSec ?? t.DefaultRespawnSeconds(clockLair))?.ToString() ?? "?"} s, "
                + $"ready {At(t.NextReadyAt(clockLair, overrideSec), "now")}");
        }
        // The live-resolved travel-cost model + its current per-hop figure — a
        // "walk-to ETA / lair ranking looks wrong" report needs to know which
        // model got wired (realm-aware Auto vs Flat vs bucketed) and what it
        // predicts for one hop at the moment of capture (live enc% / quickness
        // on Paradigm, the encumbrance bucket on stock).
        Kv(sb, "Travel cost model", svc.AutoLair.TravelCostModel.GetType().Name);
        Kv(sb, "Travel per-hop estimate",
            $"{svc.AutoLair.TravelCostModel.EstimateTravel(1).TotalSeconds:0.00} s");
        Kv(sb, "Auto-deposit reroute", svc.AutoDeposit.RerouteStatus);
        Kv(sb, "Sell detour", svc.SellDetour.Status);
        Kv(sb, "Default-gear pool baseline", svc.PoolBaseline.Current is { } pb
            ? $"HP {pb.MaxHp}, pool {pb.MaxMa} at level {pb.Level} (gear +{pb.DefaultGearHp}/+{pb.DefaultGearMa}, {pb.RecordedAt:u})"
              + (svc.PoolBaseline.IsStale ? " — stale, re-reading `stat` in Default gear" : "")
            : "none yet — using the gear estimate");
        Kv(sb, "Party @ok held for gear", svc.Health.IsPartyOkHeldForGear.ToString());
        Kv(sb, "Auto-sell", svc.AutoSell.IsSelling ? "selling here" : "idle");
        Kv(sb, "Floor check after a kill", svc.AutoGetItems.IsReLookHeld ? "waiting on the room display" : "idle");
        // Roomba Mode (GhSweepManager) — a "sweep won't start / got stuck"
        // report needs the phase, lap count, and how much of the sort queue
        // is still outstanding.
        Kv(sb, "Roomba mode", svc.GhSweep.Mode.ToString());
        Kv(sb, "Roomba sweep phase", svc.GhSweep.Phase.ToString());
        Kv(sb, "Roomba recon laps done", svc.GhSweep.CompletedReconLaps.ToString());
        Kv(sb, "Roomba completed sort laps", svc.GhSweep.CompletedSortLaps.ToString());
        Kv(sb, "Roomba searches per room", svc.GhRoomLabels.SearchesPerRoom.ToString());
        Kv(sb, "Roomba labeled / actively-managed (this char) / circuit rooms",
            $"{svc.GhRoomLabels.Labels.Count} / {svc.GhManagedRooms.Count} / {svc.GhSweep.CircuitRoomCount}");
        Kv(sb, "Roomba rooms full this sweep",
            svc.GhSweep.FullRooms is { Count: > 0 } full
                ? string.Join(", ", full.Select(r => $"{r.Map}/{r.Room}"))
                : "(none)");
        Kv(sb, "Roomba moved / left / pending / carried / hidden",
            $"{svc.GhSweep.MovedSoFar.Count} / {svc.GhSweep.LeftInPlace.Count} / "
            + $"{svc.GhSweep.PendingMoveCount} / {svc.GhSweep.CarriedPendingCount} / "
            + $"{svc.GhSweep.HiddenPendingCount}");
        // Resume state — the ONLY trace of an interrupted sweep after a restart, when
        // Phase is Idle and the in-memory pending counts read 0 but a per-character
        // manifest is still on disk. A "Resume greyed out" or "it dumped my load"
        // report is undiagnosable without it. ResumableMoveCount falls back to the
        // persisted store, so it's non-zero even in a fresh session.
        Kv(sb, "Roomba resumable (can / moves)",
            $"{svc.GhSweep.CanResume} / {svc.GhSweep.ResumableMoveCount}");
        // Full-ledger carry state — a "sweep stranded everything" or "won't pick up"
        // report needs the tracked working budget, what the ledger thinks is carried,
        // the live headroom, and how many items were left as too-heavy.
        Kv(sb, "Roomba working budget / carried / headroom",
            svc.GhSweep.WorkingWeightBudget == int.MaxValue
                ? "(no weight data)"
                : $"{svc.GhSweep.WorkingWeightBudget} / {svc.GhSweep.LedgerCarriedWeightNow} / "
                    + $"{svc.GhSweep.CarryHeadroomNow}");
        Kv(sb, "Roomba left too-heavy",
            svc.GhSweep.LeftInPlace.Count(f => f.Reason == GhLeftReason.TooHeavy).ToString());

        // Default-task startup state — a "my loop / Auto-Lair didn't start on
        // login" report needs to know whether the runner deferred the start
        // behind the party-reform hold and whether that hold is still counting.
        Kv(sb, "Default task party-hold armed", svc.DefaultTaskRunner.PendingPartyRebuildHold.ToString());
        Kv(sb, "Default task holding now", svc.DefaultTaskRunner.IsHoldingForParty.ToString());

        // Recovery gate + Paradigm rm re-sync — a "walker got lost / stuck
        // mid-walk" report needs the tier the gate climbed to, the anchor it
        // last held, and whether an authoritative-position round-trip was
        // pending (Paradigm). Without these a drift report can't tell a normal
        // walk from one stalled waiting on an `rm` that never answered.
        Kv(sb, "Recovery tier", svc.Recovery.CurrentTier.ToString());
        Kv(sb, "Recovery anchor",
            svc.Recovery.Anchor is { } anc ? $"{anc.Map}/{anc.Room}" : "(none)");
        Kv(sb, "Awaiting rm resync", svc.Recovery.AwaitingAuthoritativeResync.ToString());
        var resync = svc.ParadigmResync;
        Kv(sb, "rm resync enabled", resync.Enabled.ToString());
        Kv(sb, "rm request in flight", resync.RequestInFlight.ToString());
        Kv(sb, "rm requested at",
            resync.LastRequestedAt is { } req ? req.ToLocalTime().ToString("HH:mm:ss") : "(idle)");
        Kv(sb, "rm last resolved",
            resync.LastResolved is { } res ? $"{res.Map}/{res.Room}" : "(none)");

        var roomState = svc.RoomTracker.State;
        Kv(sb, "Current room",
            roomState.CurrentRoom is { } room ? $"{room.Key.Map}/{room.Key.Room} — {room.DisplayName}" : "(unknown)");
        Kv(sb, "Room confidence", roomState.Confidence.ToString());
        // A room whose cast-on-enter Spell strips buffs suppresses auto-bless —
        // a "buffs won't cast here" report needs this to explain the silence.
        Kv(sb, "Room strips buffs",
            svc.RoomBuffStrip.StripsBuffs(roomState.CurrentRoom?.Spell ?? 0).ToString());
        // Dark rooms print no name/exits/"Also here:", so the walker infers
        // position from moves and combat from attack lines. A "stuck in the dark"
        // report needs this flag to explain why the room display looks empty.
        Kv(sb, "In dark room", svc.RoomTracker.IsInDarkRoom.ToString());
        // ...and what the dark-room reveal actually did, which is the whole
        // question behind "it never fought back": whether it revealed something
        // (and what), or held off (and why). Neither line present means the
        // watcher saw nothing to act on.
        if (svc.DarkRoomCombat is { } dark)
        {
            Kv(sb, "Dark reveal",
                dark.LastRevealName is { Length: > 0 } rn
                    ? $"{rn} at {dark.LastRevealAt:HH:mm:ss} from '{dark.LastRevealLine}'"
                    : "(none this session)");
            Kv(sb, "Dark held off", dark.LastHeldOffReason ?? "(none)");
        }
        // Suspect-strike count + the last observation's exit sets drive the
        // walker's hidden-search / lost-recovery decisions — the exact inputs a
        // "walker got lost / re-searched" report needs.
        Kv(sb, "Suspect strikes", roomState.SuspectStrikes.ToString());
        Kv(sb, "Observed exits",
            roomState.ObservedExitDirections is { Count: > 0 } obs
                ? string.Join(", ", obs) : "(none observed)");
        Kv(sb, "Open-door exits",
            roomState.OpenDoorDirections is { Count: > 0 } doors
                ? string.Join(", ", doors) : "(none)");
        // The last exit whose spell teleported us on, where the tracker put us and
        // what it went by. Its landings can look alike (the golden idol's do), so a
        // "map shows me on the wrong side" report needs the reasoning.
        Kv(sb, "Last cast-on-walk teleport", svc.RoomTracker.LastCastLanding is { } cast
            ? $"{cast.From} → {cast.Landing} at {cast.At.ToLocalTime():HH:mm:ss} ({cast.Basis})"
            : "(none this session)");
        // The landing is booked off the room passed through and the game shows it
        // next; a roster or loot read that never happened there shows as this still
        // set (it clears on that display, or on the wait for it running out).
        Kv(sb, "Cast-on-walk landing booked, its own display still to come", svc.RoomTracker.CastLandingAwaitsDisplay.ToString());
        // RoomTracker anchors its timestamps in UTC (DateTimeOffset.UtcNow); the
        // rest of the report uses local .Now. The two are the same absolute
        // instant so all the tracker's comparisons work either way, but printing
        // the raw value would show the UTC hour next to local ones — normalize.
        Kv(sb, "Last move sent", svc.RoomTracker.LastMoveSentAt?.ToLocalTime().ToString("HH:mm:ss") ?? "(never)");
        // The echo gate confirms a move's landing only once the server has echoed
        // its command; if the tracker is stuck Pending in a same-named room, this
        // tells missed-echo (no echo recorded) apart from some other hang.
        Kv(sb, "Last move-echo",
            svc.RoomTracker.LastInboundMoveEcho is { } echo
                ? $"'{echo.Command}' @ {echo.At.ToLocalTime():HH:mm:ss}"
                : "(none)");
        // Passive grid re-localiser: whether the engine-independent same-name-grid
        // recovery is mid-narrow and how ambiguous it still is. A "stuck Lost with
        // automation off" report shows here whether that path was even running.
        Kv(sb, "Passive grid locate", svc.RoomTracker.PassiveGridStatus);
        // Sysop room-status capability. A "recovery didn't work" report needs to
        // distinguish never-enabled from enabled-but-the-BBS-refused: the probe
        // turns itself off after one unanswered attempt, and that leaves no other
        // trace in the report.
        Kv(sb, "Sysop status probe",
            svc.SysStatus.Available ? "available"
            : svc.SysStatus.AutoDisabled ? "backed off after a timeout — retries shortly"
            : "off (no sysop powers set for this BBS)");
        // What the last ground-truth locate actually did. "Recovery walked me
        // backwards anyway" is unanswerable without it: the probe can be
        // available and still have declined (throttled, queued behind a move) or
        // failed (empty reply, room outside the active set).
        Kv(sb, "Sysop locate",
            svc.SysopLocate.RequestInFlight ? "in flight"
            : svc.SysopLocate.LocateDeferred ? "queued behind movement"
            : svc.SysopLocate.LastOutcome);
        // Sysop goto: whether the power is on for this BBS, how many locations the
        // table holds, and any jump still awaiting its landing resync — a "goto left
        // me lost" report needs the armed-but-uncommitted state.
        Kv(sb, "Sysop goto",
            svc.SysopGoto.Enabled ? $"on ({svc.SysopGoto.UsableNow.Count} location(s))" : "off");
        if (svc.SysopGoto.ArmedLandingSummary is { } armed)
            Kv(sb, "Sysop goto landing", $"awaiting {armed}");
        // "Sys goto wimpy instead of hanging" (Health tab) — a low-HP escape that
        // substitutes for the hangup, so a "didn't hang / didn't jump" report needs it.
        var healthCfg = svc.Resolver.Resolve<Models.Profile.HealthSettings>("Health");
        Kv(sb, "Sys goto wimpy",
            healthCfg.SysGotoWimpyInsteadOfHanging
                ? $"on → '{(string.IsNullOrWhiteSpace(healthCfg.SysGotoWimpyLocation) ? "(no location set)" : healthCfg.SysGotoWimpyLocation)}'"
                : "off");
        // @panic (party bail-out, MegaMUD parity) — send gate, receive gate, and
        // whether we're currently leading (the send only fires while leading). A
        // "@panic didn't fire / dropped me unexpectedly" report needs all three.
        var partyCfg = svc.Resolver.Resolve<Models.Profile.PartySettings>("Party");
        Kv(sb, "@panic",
            $"send while leading={(partyCfg.UsePanicWhileLeading ? "on" : "off")}, "
            + $"ignore incoming={(partyCfg.IgnorePanics ? "on" : "off")}, "
            + $"leading now={(svc.PartyState.IsInParty && svc.PartyState.SelfIsLeader ? "yes" : "no")}");

        IReadOnlyList<Game.Map.RoomKey> history = svc.RoomTracker.GetHistory();
        if (history.Count > 0)
        {
            sb.Append("\nRecent confirmed positions (newest first): ");
            sb.Append(string.Join(", ", history.Take(10).Select(k => $"{k.Map}/{k.Room}")));
            sb.Append('\n');
        }

        // Active boss timers at capture — a "boss timer / @timer looks wrong"
        // report needs the tracked set (name + time-to-full + next window).
        var bossTimers = svc.BossTimers.ActiveTimers(svc.GameData.ActiveRealm);
        Kv(sb, "Active boss timers", bossTimers.Count.ToString());
        foreach (var (def, state) in bossTimers.Take(15))
            Kv(sb, $"  {def.Name}",
                // Which monster record the timer is read from, and its length: a boss
                // whose name several records share is only right when it's its own.
                $"{(def.MonsterNumber is { } n ? $"#{n}" : "no number")}, "
                + $"{Game.GameData.BossCatalog.EffectiveRegenHours(svc.GameData, def)?.ToString() ?? "?"}h, "
                + $"full {Game.Map.BossTimerMath.FormatHours(state.FullRemaining.TotalHours)}, "
                + $"next {state.NextLabel} {Game.Map.BossTimerMath.FormatHours(state.NextRemaining.TotalHours)}");

        return sb.ToString();
    }

    // The navigation engines the Movement section only summarizes: the
    // point-to-point walk engine (absent above — Movement covers loops/lairs but
    // not a plain "go to room X" walk), the obstacle FSMs a walk stalls on
    // (door / hidden-exit / trap), and the path-item detour routers. A "walker
    // stuck / took a wrong route / stalled on a door" report needs the walk's
    // live target + progress + last stop reason and which obstacle handler is
    // mid-request — the exact internals the log only hints at.
    // Re-plan the last walk-to the user requested — from where they are NOW to
    // where they aimed — so a "blocked" / generic route card in the report can be
    // diagnosed after the fact: what the picker computed, which exit it stopped
    // at, and the raw gate fields on that exit (so a generic reason reveals its
    // real gate kind). Nothing is sent to the game; this is pure re-planning.
    private static void AppendLastRoutePlan(StringBuilder sb, AppServices svc)
    {
        if (svc.LastRequestedWalkTo is not { } target) return;
        if (svc.RoomTracker.State.CurrentRoom?.Key is not { } here) return;

        sb.Append("\n**Route plan (last walk-to)**\n\n");
        string Label(Game.Map.RoomKey k) =>
            svc.RoomGraph.GetRoom(k)?.Name is { Length: > 0 } n ? $"{k.Map}/{k.Room} ({n})" : $"{k.Map}/{k.Room}";
        Kv(sb, "From (current)", Label(here));
        Kv(sb, "To (requested)", Label(target));

        Game.Map.BfsMapper bfs = svc.Bfs;
        MovementFilter filter = svc.Movement;
        Game.Map.RoomGraphManager graph = svc.RoomGraph;

        bool direct = bfs.FindPath(here, target, filter) is { Count: > 0 };
        Kv(sb, "Direct route (gates honoured)", direct ? "reachable" : "none — blocked");
        if (direct) return;

        IReadOnlyList<Game.Map.Direction>? physical =
            bfs.FindPath(here, target, filter, ignoreExitGates: true);
        Kv(sb, "Physical route (all gates ignored)",
            physical is { Count: > 0 } ? $"{physical.Count} step(s)" : "none — graph-disconnected");

        // The gates the route card named, and what the client makes of each: whether
        // a pick of the card would fetch a door key, and any trade that yields it.
        if (RouteChoicePlanner.Evaluate(bfs, filter, graph, here, target) is { Requirements: { Count: > 0 } reqs })
        {
            IReadOnlyList<int> fetchable = svc.SourceableGateItems(reqs);
            Kv(sb, "Gate items on the route through gates", string.Join("; ", reqs.Select(r =>
            {
                string items = string.Join("/", r.ItemIds.Select(id => $"{id} {svc.ItemNames.GetName(id) ?? "?"}"));
                if (r.Carried) return $"{r.Kind} {items} (carried)";
                if (r.Kind != RouteRequirementKind.DoorKey) return $"{r.Kind} {items}";
                string source = fetchable.Contains(r.ItemIds[0])
                    ? " — a route card's pick fetches it" : " — nothing fetches it";
                string trade = svc.GiveSources.TradeNote(r.ItemIds[0]) is { } note ? $" — {note}" : string.Empty;
                return $"{r.Kind} {items}{source}{trade}";
            })));
        }

        if (RouteChoicePlanner.PlanBlocked(bfs, filter, graph, here, target) is { } b)
        {
            string reason = Game.Map.BlockedExitDescriber.Describe(
                b.StopRoom, b.BlockDir, b.BlockExit,
                k => graph.GetRoom(k)?.Name, svc.ItemNames.GetName);
            Kv(sb, "Blocked at", $"{b.StopRoom.Map}/{b.StopRoom.Room} heading {b.BlockDir}");
            Kv(sb, "Picker reason", reason);
            Game.Map.RoomExit e = b.BlockExit;
            Kv(sb, "Block exit fields",
                $"hint={e.Hint} level={e.MinLevel}-{e.MaxLevel} stat={e.StatRequirement} "
                + $"toll={e.TollGold} fare={e.FareCopper} class={e.ClassGate} race={e.RaceGate} keyItem={e.KeyItemId} "
                + $"align={e.HasAlignmentGate} filterReasons={filter.DescribeExitBlock(in e)}");
        }
        else
        {
            Kv(sb, "Blocked plan", physical is { Count: > 0 }
                ? "none — reachable only past a gate not on the walked route (e.g. a level-gated "
                  + "teleport or boat), so the picker can't point at a run-to-block exit"
                : "none — destination is graph-disconnected from here");
        }
    }

    private static string BuildNavigationEngines(AppServices svc)
    {
        StringBuilder sb = new();

        sb.Append("**Walk engine (point-to-point)**\n\n");
        Game.Map.AutoWalkManager walker = svc.Walker;
        Kv(sb, "State", walker.State.ToString());
        Kv(sb, "Destination", walker.Destination is { } dest ? $"{dest.Map}/{dest.Room}" : "(none)");
        if (walker.StepCount > 0)
            Kv(sb, "Progress", $"step {Math.Min(walker.CurrentStepIndex + 1, walker.StepCount)}/{walker.StepCount}");
        // A voyage in flight is the state most likely to hang (captain refused
        // boarding, arrival mismatch) — surface the sailing target + ETA so a
        // report captured mid-sail pins down which crossing stalled.
        if (walker.IsSailing)
            Kv(sb, "Sailing", $"to {walker.SailingDestinationName ?? "(port)"}, arriving in "
                + $"{Math.Max(0, (walker.SailingArrivalEta - DateTimeOffset.UtcNow).TotalSeconds):F0}s");
        // Where the leg under way began, not the whole trip: a detour leg starts
        // from wherever the detour took over. The trip is the "Whole trip" line.
        Kv(sb, "This leg's origin (flee anchor)",
            walker.JourneyOrigin is { } origin ? $"{origin.Map}/{origin.Room}" : "(none)");
        Kv(sb, "Next planned direction",
            walker.PeekNextPlannedDirection() is { } dir ? dir.ToString() : "(none / command step)");
        Kv(sb, "Room command held for an empty room", walker.AwaitingEmptyRoom ? "yes — clearing the room first" : "no");
        Kv(sb, "Boss Stop before / Grab All choices (this character)", svc.Profile.Current?.BossFlags is { } bossFlags
            ? (bossFlags.Count == 0 ? "none: every boss on its own default"
                : string.Join(", ", bossFlags.Select(kv =>
                    $"{kv.Key}[{(kv.Value.StopBefore is { } sb2 ? $"stop={sb2}" : "")}{(kv.Value.GrabAll is { } ga ? $" grab={ga}" : "")}]")))
            : "(not taken over from the realm's list yet)");
        Kv(sb, "Stop-before boss rooms on this walk", walker.BossRoomRuleSummary);
        Kv(sb, "Paused before a boss room", walker.HaltedBeforeBossRoom is { } bossRoom
            ? $"{bossRoom.Map}/{bossRoom.Room} ({svc.BossInRoom(bossRoom) ?? "boss"}) — Resume walks through" : "no");
        // The retained last event carries the failure/stop reason (Detail) — the
        // single most useful line for "why did the walk quit".
        Kv(sb, "Last walk event",
            walker.LastEvent is { } ev
                ? $"{ev.Kind}: {ev.Detail}" + (ev.Destination is { } d ? $" → {d.Map}/{d.Room}" : string.Empty)
                : "(none yet)");
        // The whole trip, of which the walk above is one leg: where it ends and the
        // rules every leg, re-plan and errand restart is planned by. Without it a
        // capture can't tell a route the planner chose from one the walker fell
        // back to. A journey can stand with the walker idle, between two legs.
        Kv(sb, "Whole trip (every leg keeps to this)",
            walker.Journey is not { } journey
                ? (walker.State == Game.Map.WalkState.Idle ? "(none)" : "(none — this walk is on no trip's rules)")
            : $"to {journey.Destination.Map}/{journey.Destination.Room}: {journey.Describe()}"
              + (journey.ClosedGates is { Count: > 0 } closed
                  ? $" ({string.Join(", ", closed.Select(id => $"#{id} {svc.ItemNames.GetName(id) ?? "?"}"))})" : string.Empty)
              + (walker.State == Game.Map.WalkState.Idle ? "; no leg under way (between legs)"
                  : walker.LegIsToJourneyGoal ? "; this leg goes to its destination"
                  : "; this leg is a side trip"));

        AppendLastRoutePlan(sb, svc);

        // Another player's route drawn from their @path reply is rebuilt from this
        // reply alone, so it's the input a "their route looks wrong" report needs.
        Kv(sb, "Last @path reply",
            svc.PathReply?.Last is { } p
                ? $"from {p.Sender} {(DateTimeOffset.UtcNow - p.At).TotalSeconds:F0}s ago: at {p.Report.LeaderRoom}, "
                  + (p.Report.Destination is { } pd ? $"walking to {pd}" : p.Report.LoopName is { } pl ? $"loop '{pl}'" : "no destination")
                  + $", step {p.Report.Step}/{p.Report.TotalSteps}"
                : "(none)");
        Kv(sb, "Last leader @goto reply",
            svc.PathReply?.LastGoto is { } g
                ? $"from {g.Sender} {(DateTimeOffset.UtcNow - g.At).TotalSeconds:F0}s ago: walking to {g.Destination}"
                : "(none)");

        sb.Append("\n**Obstacle handlers (door / hidden exit / trap)**\n\n");
        Game.Map.DoorOpenManager door = svc.Door;
        Kv(sb, "Door FSM", $"{door.CurrentState}"
            + (door.CurrentDirection is { } dd ? $", dir={dd}" : string.Empty)
            + (door.CurrentDoorRoom is { } dr ? $", door in {dr}" : string.Empty)
            + (door.QueueDepth > 0 ? $", queued={door.QueueDepth}" : string.Empty));
        Game.Map.HiddenExitRevealManager hidden = svc.HiddenSearch;
        Kv(sb, "Hidden-exit search", hidden.IsBusy
            ? $"searching dir={hidden.CurrentDirection ?? "(none)"}, queued={hidden.QueueDepth}"
                + (hidden.HeldForBlindness ? ", held (blind)" : string.Empty)
                + (hidden.HeldForRest ? ", held (resting)" : string.Empty)
            : hidden.QueueDepth > 0 ? $"idle, queued={hidden.QueueDepth}" : "idle");
        Kv(sb, "Winch", $"{svc.Winch.CurrentState}"
            + (svc.Winch.CurrentDirection is { } wd ? $", dir={wd}" : string.Empty));
        Game.TrapDisarmManager trap = svc.TrapDisarm;
        Kv(sb, "Trap disarm", $"{trap.CurrentState}"
            + (trap.CurrentDirection is { } td ? $", dir={td}" : string.Empty)
            + (trap.HeldForRest ? ", held (resting)" : string.Empty)
            + (trap.QueueDepth > 0 ? $", queued={trap.QueueDepth}" : string.Empty)
            + $", canDisarm={trap.CanDisarm}, trapsStat={svc.PlayerStats.Traps}"
            + $", skillFromClassRace={trap.SkillInferredFromClassOrRace}"
            + (trap.DisarmOdds is { } odds ? $", disarmSkill={odds.Skill} ({odds.Summary})" : string.Empty));
        Kv(sb, "Trap disarm, exits we disarmed (re-arm "
            + $"{trap.RearmTime.TotalMinutes:0} min)", trap.RecentDisarmsDescription());
        if (trap.LastUnansweredReply is { } unanswered)
            Kv(sb, "Trap disarm, last unanswered reply", unanswered);

        sb.Append("\n**Path-item detours**\n\n");
        Kv(sb, "Path-item search demand", svc.PathItemDemand.SearchDemandActive.ToString());
        Kv(sb, "Party path-item search demand", svc.PartyPathItemGate.SearchDemandActive.ToString());
        // "Paused by" above names the gate; this says which items' counts hold it.
        IReadOnlyList<string> counting = svc.PartyPathItemGate.HoldingWalkFor;
        Kv(sb, "Walk held for the party's count of", counting.Count == 0 ? "(nothing)" : string.Join(", ", counting));
        Kv(sb, "Party count of per-member gate items", svc.PartyGateCountSummary);
        // Why a walk did or didn't ask the party: a count it is still deciding
        // from, or a route card's count waiting for the walk that card starts.
        Kv(sb, "Party counts standing for this trip", svc.PartyPathItemGate.JourneyCountsSummary);
        Kv(sb, "Route card counts not yet taken by a walk", svc.CardCountSummary);
        Kv(sb, "Give detour active", svc.PathItemGiveRouter.DetourActive.ToString());
        Kv(sb, "Give asked for and not handed over this walk",
            svc.PathItemGiveRouter.Declined.Count == 0 ? "(none)" : string.Join(", ", svc.PathItemGiveRouter.Declined));
        // Both ride on the journey ("Route this walk keeps to" above), so with no
        // journey standing there is nothing to list.
        Game.Map.JourneyFetch? fetch = svc.Walker.Journey?.Fetch;
        Kv(sb, "Journey's items to fetch", fetch is not { HasItems: true } ? "(none)" : string.Join(", ", fetch.Items));
        Kv(sb, "Journey's trades agreed to on its route card",
            fetch is not { Trades.Count: > 0 } ? "(none)" : string.Join(", ", fetch.Trades.Select(t =>
                $"{t.Key} {svc.ItemNames.GetName(t.Key) ?? "?"} for {t.Value} {svc.ItemNames.GetName(t.Value) ?? "?"}"
                + (svc.AgreedTradeFor(t.Key) is null ? " (not in force)" : " (in force)"))));
        Kv(sb, "Shop-buy detour active", svc.PathItemShopRouter.DetourActive.ToString());
        Kv(sb, "Monster-drop hunt detour active", svc.MonsterDropRouter.DetourActive.ToString());
        Kv(sb, "Summon detour active", svc.PathItemSummonRouter.DetourActive.ToString()
            + (svc.PathItemSummonRouter.PendingMonsterName is { Length: > 0 } sm
                ? $" — waiting on {sm}"
                : string.Empty));

        IReadOnlyList<Need> needs = svc.Needs.Outstanding(NeedKind.PathItem);
        sb.Append("\nOutstanding path-item needs (").Append(needs.Count).Append(")\n\n");
        if (needs.Count == 0) sb.Append("_(none)_\n");
        else foreach (Need n in needs)
        {
            sb.Append("- ").Append(n.Descriptor).Append(" ×").Append(n.Quantity)
              .Append(" (requester: ").Append(n.Requester);
            // Which items count toward it on this route (a canoe for a raft on the river).
            if (int.TryParse(n.Descriptor, out int needId)
                && svc.PathItemSubstitutes.For(needId) is { Count: > 1 } subs)
                sb.Append("; covered by any of ").Append(string.Join(", ", subs));
            sb.Append(")\n");
        }

        // Checkspell hazard-buff provisioning for the CURRENT room — a "walked into
        // the desert / drowned without `use`ing the waterskin" report needs whether
        // the room the character stands in is a buff-gated hazard, which item raises
        // the buff, and whether one is on hand for the provisioner to `use`.
        sb.Append("\n**Room hazard (current)**\n\n");
        Game.Map.Room? here = svc.RoomTracker.State.CurrentRoom;
        RoomHazardIndex.RoomHazard? hazard = here is { Spell: > 0 }
            ? svc.RoomHazards.HazardForSpell(here.Spell) : null;
        if (hazard is null || hazard.BuffCounters.Count == 0)
            Kv(sb, "Checkspell hazard", "(none — current room needs no buff counter)");
        else foreach (RoomHazardIndex.BuffCounter bc in hazard.BuffCounters)
        {
            List<string> names = bc.SourceItems
                .Select(id => svc.ItemNames.GetName(id))
                .Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n!).ToList();
            // Approximate carried check: the dump lists carried names (sometimes
            // count-prefixed), so a substring match tolerates "3 waterskins".
            bool carried = names.Any(n => svc.Inventory.Snapshot.CarriedItems
                .Any(c => c.Contains(n, StringComparison.OrdinalIgnoreCase)));
            string label = names.Count > 0 ? string.Join(" / ", names) : "(unnamed source)";
            // LapseSpell 0 means the buff-absent damage cast wasn't derivable from
            // the checkspell chain, so the reactive re-raise (fire on the lapse
            // prompt) can't arm — only the predictive timer holds the buff.
            string lapse = bc.LapseSpell > 0
                ? bc.LapseSpell.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : "none — reactive re-raise off";
            // A held immunity guard (worn sunstone) makes the `use` a no-op — surface
            // which guards exist and whether one is held, so a report shows WHY a buff
            // was (or wasn't) raised.
            List<string> immunityNames = bc.ImmunityItems
                .Select(id => svc.ItemNames.GetName(id))
                .Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n!).ToList();
            bool immune = immunityNames.Any(n => svc.Inventory.Snapshot.CarriedItems
                .Any(c => c.Contains(n, StringComparison.OrdinalIgnoreCase)));
            string immunity = immunityNames.Count > 0
                ? $", immunity: {string.Join(" / ", immunityNames)} ({(immune ? "held — no `use` needed" : "not held")})"
                : "";
            Kv(sb, $"Buff {bc.BuffSpell}",
                $"{label} (dur ~{bc.DurationSeconds}s, carried: {(carried ? "yes" : "no")}, "
                + $"lapse spell: {lapse}{immunity})");
        }

        // Random-teleport maze solver — a "walker never reaches the asylum room /
        // spins forever teleporting" report needs whether the solver engaged, its
        // goal, which phase it's stuck in, and how many reshuffles it's burned.
        sb.Append("\n**Teleport-maze solver**\n\n");
        Game.Map.TeleportMazeSolver maze = svc.MazeSolver;
        Kv(sb, "Enabled", maze.Enabled.ToString());
        Kv(sb, "Pockets indexed", svc.MazeIndex.HasMazes.ToString());
        Kv(sb, "Active", maze.Active.ToString());
        Kv(sb, "Phase", maze.PhaseName);
        Kv(sb, "Goal", maze.Goal is { } mg ? $"{mg.Map}/{mg.Room}" : "(none)");
        Kv(sb, "Reshuffle attempts", maze.Attempts.ToString());
        Kv(sb, "Held by a pause", maze.IsHeld.ToString());

        // Great Pyramid climb solver — a "walker won't climb the pyramid / scattered
        // out" report needs whether it engaged, which floor + phase it reached, its
        // goal, how many commands it sent before halting, what it is waiting on, and
        // how often it had to recover its place.
        sb.Append("\n**Pyramid solver**\n\n");
        Game.Map.PyramidSolver pyr = svc.PyramidSolver;
        Kv(sb, "Enabled", pyr.Enabled.ToString());
        Kv(sb, "Active", pyr.Active.ToString());
        Kv(sb, "Floor", pyr.FloorName);
        Kv(sb, "Phase", pyr.PhaseName);
        Kv(sb, "Goal", pyr.Goal is { } pg ? $"{pg.Map}/{pg.Room}" : "(none)");
        Kv(sb, "Floor 1 time left", pyr.Floor1TimeLeft is { } f1 ? $"{f1:m\\:ss}" : "(not on floor 1, or the clock wasn't seen starting)");
        Kv(sb, "Rooms left on the route line", pyr.RemainingRoomKeys.Count.ToString());
        Kv(sb, "Commands sent", pyr.StepsDriven.ToString());
        Kv(sb, "Script step", $"{pyr.ScriptStep} of {pyr.ScriptSteps}"
            + (pyr.ExpectedRoom is { } er ? $" (scripted from {er.Map}/{er.Room})" : ""));
        Kv(sb, "Move in flight", pyr.MoveInFlightTo is { } mf ? $"to {mf.Map}/{mf.Room}" : "(none)");
        Kv(sb, "Holding on", pyr.HoldReasonText ?? "(nothing)");
        Kv(sb, "Running through (floors 1-2)", pyr.IsRunningThrough.ToString());
        Kv(sb, "Refused-move retries", pyr.MoveRetries.ToString());
        Kv(sb, "Gate rewinds", pyr.GateRewinds.ToString());
        Kv(sb, "Landings taken on trust", pyr.AssumedLandings.ToString());
        Kv(sb, "Door with the door manager", pyr.DoorWithManager.ToString());
        Kv(sb, "Door watched (s)", pyr.DoorWatchSeconds.ToString());
        Kv(sb, "Golden lion key", pyr.KeyStatus);
        Kv(sb, "Key respawn trips", pyr.KeyRespawns.ToString());

        return sb.ToString();
    }

    // The live Exp/Hr Estimator session, if one's active — route, tunables, and the
    // computed estimate + per-lair breakdown the user was looking at. The estimator
    // is a build-time UI tool whose state lives only on the Navigation view-model, so
    // it reaches the report through a snapshot provider the VM registers on
    // AppServices; null (provider unset or not estimating) reads as inactive. A
    // "the exp/hr number looks wrong" report needs exactly this to reproduce.
    private static string BuildExpEstimator(AppServices svc)
    {
        Game.Map.ExpEstimatorSnapshot? snap = svc.ExpEstimatorSnapshotProvider?.Invoke();
        if (snap is null) return "_(estimator not active)_";

        StringBuilder sb = new();
        Kv(sb, "Proposed loop name", snap.ProposedName);
        Kv(sb, "Estimate", $"{snap.ExpPerHour:N0} exp/hr");
        Kv(sb, "Laps", $"{snap.LapsPerHour} laps/hr · {snap.AvgLapSeconds:N1}s/lap");
        Kv(sb, "Summary", snap.Summary);
        Kv(sb, "Combat mode", snap.AreaCombat ? "area (rooming)" : "single-target");
        if (!string.IsNullOrEmpty(snap.RealmName)) Kv(sb, "Realm", snap.RealmName);
        Kv(sb, "Seconds per step", $"{snap.SecondsPerStep:0.0}");
        Kv(sb, "Rounds to kill a mob", $"{snap.RoundsPerMob:0.0}");
        Kv(sb, "Real-world multiplier", $"{snap.RealConditionsMultiplier:0.00}");

        sb.Append("\n**Route** (").Append(snap.Rooms.Count).Append(")\n\n");
        if (snap.Rooms.Count == 0) sb.Append("_(none)_\n");
        else foreach (string r in snap.Rooms) sb.Append("- ").Append(r).Append('\n');

        sb.Append("\n**Per-lair** (").Append(snap.Lairs.Count).Append(")\n\n");
        if (snap.Lairs.Count == 0) sb.Append("_(none)_\n");
        else foreach (string l in snap.Lairs) sb.Append("- ").Append(l).Append('\n');

        if (snap.Bosses.Count > 0)
        {
            sb.Append("\n**Bosses** (").Append(snap.Bosses.Count).Append(")\n\n");
            foreach (string b in snap.Bosses) sb.Append("- ").Append(b).Append('\n');
        }

        if (snap.Summons.Count > 0)
        {
            sb.Append("\n**Room summons** (").Append(snap.Summons.Count).Append(")\n\n");
            foreach (string su in snap.Summons) sb.Append("- ").Append(su).Append('\n');
        }

        return sb.ToString();
    }

    // The Simulator window's last result, live check and area ranking — what a "the
    // simulated number looks wrong" report needs beside the character sections. Its
    // state lives on the Navigation view-model, which registers the provider; null
    // (no Navigation window yet) reads as not opened.
    private static string BuildSimulator(AppServices svc)
    {
        Game.Simulation.SimulatorSnapshot? snap = svc.SimulatorSnapshotProvider?.Invoke();
        if (snap is null) return "_(simulator not opened)_";
        System.Globalization.CultureInfo inv = System.Globalization.CultureInfo.InvariantCulture;

        StringBuilder sb = new();
        Kv(sb, "Route picked", snap.Route ?? "(none)");
        Kv(sb, "Realm", snap.RealmName);
        Kv(sb, "Runs", $"{snap.Runs} × {snap.Hours.ToString("0.#", inv)} h");

        sb.Append("\n**Character simulation**\n\n");
        if (snap.Simulation is null) sb.Append("_(not run)_\n");
        else
        {
            sb.Append("- Route: ").Append(snap.SimulatedRoute).Append('\n');
            sb.Append("- Walk pace: ").Append(snap.WalkSeconds.ToString("0.00", inv)).Append(" s/room\n");
            foreach (string line in snap.Simulation) sb.Append("- ").Append(line).Append('\n');
        }

        if (snap.LiveCheck is not null)
        {
            sb.Append("\n**Simulator vs your play**\n\n");
            foreach (string line in snap.LiveCheck) sb.Append("- ").Append(line).Append('\n');
        }

        if (snap.AreaRanking is not null)
        {
            sb.Append("\n**Area ranking**\n\n");
            foreach (string line in snap.AreaRanking) sb.Append("- ").Append(line).Append('\n');
        }

        return sb.ToString();
    }

    private static string BuildRoomMarkers(AppServices svc)
    {
        StringBuilder sb = new();

        // The character's own avoid list, which routes go around and the route
        // picker asks about. It used to be missing here, with the realm's blacklist
        // printed under its name (report paradigm-20261007-215302).
        var avoided = svc.Movement.Avoided;
        sb.Append("**Avoid rooms (this character)** (").Append(avoided.Count).Append(")\n\n");
        if (avoided.Count == 0) sb.Append("_(none)_\n");
        else foreach (var k in avoided) sb.Append("- ").Append(k.Map).Append('/').Append(k.Room)
            .Append(" — ").Append(svc.RoomGraph.GetRoom(k)?.Name ?? "(not in the active map)").Append('\n');

        var blacklisted = svc.RoomBlacklist.Entries;
        sb.Append("\n**Blacklisted rooms (realm)** (").Append(blacklisted.Count).Append(")\n\n");
        if (blacklisted.Count == 0) sb.Append("_(none)_\n");
        else foreach (var r in blacklisted) sb.Append("- ").Append(r.Map).Append('/').Append(r.Room)
            .Append(" — ").Append(r.Name).Append('\n');

        var profile = svc.Profile.Current;
        var stash = profile?.StashRooms;
        sb.Append("\n**Stash rooms** (").Append(stash?.Count ?? 0).Append(")\n\n");
        if (stash is not { Count: > 0 }) sb.Append("_(none)_\n");
        else foreach (var r in stash) sb.Append("- ").Append(r.Map).Append('/').Append(r.Room).Append('\n');

        // Auto-train funding. "Auto-train just doesn't go" is the report this feature
        // will produce, and answering it needs all three money stores plus whether a
        // back-off is currently holding the armed run off.
        var believed = svc.StashBalances.NonEmpty();
        sb.Append("\n**Stashed coin (believed, shared by the realm's characters)** (").Append(believed.Count).Append(")\n\n");
        sb.Append("_store: ").Append(svc.StashStore.ActiveRealmFolder is { } stashFolder
            ? Path.GetFileName(stashFolder) + "/stash-balances.json"
            : "memory only (no realm)").Append("_\n\n");
        if (believed.Count == 0) sb.Append("_(none)_\n");
        else foreach ((var room, long copper) in believed)
            sb.Append("- ").Append(room.Map).Append('/').Append(room.Room)
              .Append(" — ").Append(copper.ToString("N0", System.Globalization.CultureInfo.InvariantCulture))
              .Append(" copper\n");

        var balances = svc.BankBalance.LastKnown;
        sb.Append("\n**Bank balances** (").Append(balances.Count).Append(")\n\n");
        if (balances.Count == 0) sb.Append("_(never queried this session)_\n");
        else foreach ((string bank, long copper) in balances)
            sb.Append("- ").Append(bank).Append(" — ")
              .Append(copper.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)).Append(" copper\n");

        sb.Append('\n');
        Kv(sb, "Train funding errand", svc.TrainFunding.IsCheckingFunds ? "checking purse / bank (i, bank)"
            : svc.TrainFunding.IsBusy ? "collecting" : "idle");
        Kv(sb, "Stash transfer", svc.StashTransfer.Describe());
        Kv(sb, "Coin pickup ceiling (funding errand / stash transfer)", svc.Cash.CollectLimitCopper is { } cap
            ? $"{cap:N0} copper still to take; {svc.Cash.SurveyedCopperUnderLimit:N0} copper surveyed"
            : "(none)");
        Kv(sb, "Trainer choice (if a run started now)", svc.TrainerWalk.DescribeTrainerChoiceFromHere());
        Kv(sb, "Last train shortfall", svc.TrainerWalk.LastFundingShortfall > 0
            ? $"{svc.TrainerWalk.LastFundingShortfall:N0} copper"
            : "(none)");
        Kv(sb, "Funding retry held until", svc.TrainerWalk.FundingRetryAt is { } at
            ? at.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)
            : "(not held)");
        Kv(sb, "Auto-obtain spells from shops", svc.TrainerWalk.CurrentSettings.AutoObtainShopSpells ? "on" : "off");
        Kv(sb, "Spell trip", svc.ShopSpells.Describe());
        Kv(sb, "Spell trip (if one started now)", svc.DescribeShopSpellTripFromHere());

        // Only the starred quick-access favourites — the full GOTO list runs to
        // hundreds of entries and bloats the report without helping diagnosis.
        var favorites = svc.Favorites.StarredFavorites();
        sb.Append("\n**Starred favorites** (").Append(favorites.Count).Append(")\n\n");
        if (favorites.Count == 0) sb.Append("_(none)_\n");
        else foreach (var f in favorites)
        {
            sb.Append("- ").Append(f.Map).Append('/').Append(f.Room);
            if (!string.IsNullOrWhiteSpace(f.Label)) sb.Append(" — ").Append(f.Label);
            if (!string.IsNullOrWhiteSpace(f.Folder)) sb.Append("  (folder: ").Append(f.Folder).Append(')');
            sb.Append('\n');
        }

        // The loops and auto-lair setups are the game data's; which are favourites
        // is this character's (LoopFavoritesStore).
        sb.Append('\n');
        Kv(sb, "Favourite loops (this character)", NamesOrNone(svc.LoopFavorites.Loops));
        Kv(sb, "Favourite auto-lair setups (this character)", NamesOrNone(svc.LoopFavorites.LairSetups));

        return sb.ToString();
    }

    private static string NamesOrNone(IReadOnlyCollection<string> names) =>
        names.Count == 0 ? "(none)" : string.Join(", ", names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase));

    private static string BuildAutoMode(AppServices svc)
    {
        StringBuilder sb = new();
        Kv(sb, "Kill-switch engaged", svc.AutoModeController.KillSwitchEngaged.ToString());
        Kv(sb, "All wired engines off", svc.AutoModeController.AllWiredOff.ToString());
        sb.Append("\nPer-engine toggles live in the `General` settings block below: `AutoMode` is the live toolbar state, `AutoModeBase` the base defaults reconciled onto it at profile load / loop / auto-lair start (null = pre-split character, treated as equal to `AutoMode`).\n");
        return sb.ToString();
    }

    // Per-character built-in keybindings — the chord bound to each app action,
    // flagged where it deviates from the shipped default. A "hotkey does
    // nothing" or "my Ctrl+S rebind stopped reaching the terminal" report hinges
    // on whether the user rebound the chord, which only lives in this per-
    // character store (deltas persist to CharacterProfile.BuiltInKeybindings).
    private static string BuildKeybindings(AppServices svc)
    {
        KeybindingStore store = svc.Keybindings;
        StringBuilder sb = new();
        foreach (Models.Profile.BuiltInAction action in Enum.GetValues<Models.Profile.BuiltInAction>())
        {
            Models.Profile.KeyChord chord = store.Get(action);
            Models.Profile.KeyChord def =
                KeybindingStore.DefaultBindings.TryGetValue(action, out Models.Profile.KeyChord d)
                    ? d : Models.Profile.KeyChord.Empty;
            string bound = chord.IsEmpty ? "(unbound)" : chord.Label;
            string suffix = chord.Equals(def)
                ? string.Empty
                : $"  — changed (default: {(def.IsEmpty ? "(unbound)" : def.Label)})";
            Kv(sb, KeybindingStore.ActionLabel(action), bound + suffix);
        }
        return sb.ToString();
    }

    // Fully-resolved effective values for every gameplay / automation section,
    // merged across all four tiers. The delta-only per-tier dump below hides any
    // knob left at its default — but "what behavior should be happening" is
    // exactly those defaults (combat target order/priority, attack timing, flee
    // thresholds, etc.). A triager can't reason about a combat report without
    // seeing the effective priority even when the user never overrode it, so we
    // dump the resolved DTO for each section here regardless of override state.
    private static string BuildEffectiveSettings(AppServices svc)
    {
        StringBuilder sb = new();
        sb.Append("Merged Defaults → Global → BBS → Character values — the actual knobs the engines read, ")
          .Append("including ones left at their defaults. The per-tier override deltas are in the next section.\n\n");

        AppendResolved<Models.Profile.CombatSettings>(sb, svc, "Combat");
        AppendResolved<Models.Profile.PartySettings>(sb, svc, "Party");
        AppendResolved<Models.Profile.HealthSettings>(sb, svc, "Health");
        AppendResolved<Models.Profile.SpellsSettings>(sb, svc, "Spells");
        AppendResolved<Models.Profile.GeneralSettings>(sb, svc, "General");
        AppendResolved<Models.Profile.OtherSettings>(sb, svc, "Other");
        AppendResolved<Models.Profile.CashSettings>(sb, svc, "Cash");
        AppendResolved<Models.Profile.TalkSettings>(sb, svc, "Talk");
        AppendResolved<Models.Profile.AutoLightSettings>(sb, svc, "AutoLight");
        AppendResolved<Models.Profile.AutoLairSettings>(sb, svc, "AutoLair");
        AppendResolved<Models.Profile.AutoTrainerSettings>(sb, svc, "AutoTrainer");
        AppendResolved<Models.Profile.PvpSettings>(sb, svc, "Pvp");
        return sb.ToString();
    }

    // Resolve one tab-keyed section across the tier hierarchy and emit it as a
    // labelled JSON block. Isolated per-section so one section failing to
    // resolve leaves the rest intact.
    // Casting spell profiles: how many exist, which is active, and every profile's
    // FULL held config — each slot (empty shown as —) with its gates, plus the
    // mana-mode / drain-trigger / drains-override knobs, the active one flagged. So
    // a "wrong spells firing" report shows which profile was live, what it held, and
    // what the others hold. Spells by cast code, never full name.
    private static string BuildCombatProfiles(AppServices svc)
    {
        System.Collections.Generic.IReadOnlyList<Models.Profile.CombatSpellProfile> profiles =
            svc.CombatProfiles.Profiles;
        if (profiles.Count == 0) return "No combat spell profiles.";
        int active = svc.CombatProfiles.ActiveIndex;

        StringBuilder sb = new();
        string activeLabel = active >= 0 && active < profiles.Count
            ? (string.IsNullOrWhiteSpace(profiles[active].Name)
                ? $"profile {active + 1}"
                : $"profile {active + 1} ({profiles[active].Name.Trim()})")
            : "(none)";
        sb.Append(profiles.Count).Append(profiles.Count == 1 ? " profile" : " profiles")
          .Append(". Active: ").Append(activeLabel).Append(".\n");
        System.Collections.Generic.IReadOnlyList<Models.Profile.CombatProfileGroup> shared =
            svc.CombatProfiles.SharedGroups;
        sb.Append("Shared by every profile (not in combat profile): ")
          .Append(shared.Count == 0 ? "none" : string.Join(", ", shared)).Append(".\n\n");

        for (int i = 0; i < profiles.Count; i++)
        {
            sb.Append(Game.Combat.CombatSpellProfileReport.DescribeConfig(profiles[i], i + 1));
            if (i == active) sb.Append("  (ACTIVE)");
            sb.Append('\n');
        }
        return sb.ToString();
    }

    private static void AppendResolved<T>(StringBuilder sb, AppServices svc, string tabKey)
        where T : class, new()
    {
        sb.Append("**").Append(tabKey).Append("**\n\n");
        try
        {
            sb.Append(Json(svc.Resolver.Resolve<T>(tabKey)));
        }
        catch (Exception ex)
        {
            sb.Append("_(could not resolve: ").Append(ex.Message).Append(")_\n");
        }
        sb.Append('\n');
    }

    private static string BuildSettings(AppServices svc)
    {
        StringBuilder sb = new();
        AppendTier(sb, "Global tier", svc.Settings.Current.Settings);

        string? bbsName = svc.Profile.CurrentBbsName;
        var bbsSettings = bbsName is null ? null : svc.Bbs.Get(bbsName)?.Settings;
        AppendTier(sb, "BBS tier", bbsSettings);

        AppendTier(sb, "Character tier", svc.Profile.Current?.Settings);
        return sb.ToString();
    }

    // Emit one settings tier's deltas as JSON, dropping any BBS / Display keys
    // per the "everything except BBS + Display" scope. Those live in separate
    // stores today (BbsProfileStore / DisplayConfig), so this is belt-and-
    // braces — the tab dictionary shouldn't contain them anyway.
    private static void AppendTier(StringBuilder sb, string label, Dictionary<string, JsonElement>? tier)
    {
        sb.Append("**").Append(label).Append("**\n\n");
        if (tier is not { Count: > 0 })
        {
            sb.Append("_(no overrides)_\n\n");
            return;
        }

        Dictionary<string, JsonElement> filtered = tier
            .Where(kv => !kv.Key.Equals("Bbs", StringComparison.OrdinalIgnoreCase)
                      && !kv.Key.Equals("Display", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(kv => kv.Key, kv => kv.Value);

        if (filtered.Count == 0) sb.Append("_(only BBS/Display overrides — omitted)_\n\n");
        else sb.Append(Json(filtered)).Append('\n');
    }

    private static string BuildLog(AppServices svc)
    {
        LogEntry[] entries = svc.Log.Snapshot();
        int take = Math.Min(LogLines, entries.Length);
        if (take == 0) return "_(log empty)_";

        StringBuilder sb = new();
        // Both diagnostic channels off ⇒ the tail below is Info-only; the
        // engines' Debug/Combat decision traces were never generated. Flag it so
        // a triager doesn't read the absence of a trail as the engine going quiet.
        bool debugOn = svc.Log.Diagnostics?.DebugDiagnostics ?? false;
        bool combatOn = svc.Log.Diagnostics?.CombatDiagnostics ?? false;
        if (!debugOn && !combatOn)
            sb.Append("> Debug + Combat diagnostics were off — no decision-trail entries below. ")
              .Append("Enable them in the Log pane and reproduce for a fuller capture.\n\n");
        sb.Append("Last ").Append(take).Append(" of ").Append(entries.Length).Append(" entries.\n\n```\n");
        for (int i = entries.Length - take; i < entries.Length; i++)
        {
            LogEntry e = entries[i];
            sb.Append(e.Timestamp.ToString("HH:mm:ss")).Append("  [").Append(e.Severity).Append("]  ")
              .Append(e.Source).Append(": ").Append(e.Message).Append('\n');
        }
        sb.Append("```");
        return sb.ToString();
    }

    // The raw ANSI wire, last ScrollbackLines lines (non-printables shown as the
    // Wire Inspector renders them). Only emitted when the Raw pane is visible.
    // Only the wire read since the game was entered: what came before it is the
    // board's login, which a report must not carry (InGameCapture).
    private static string BuildRawWire(AppServices svc)
    {
        if (svc.InGameCapture.WireMark is not { } enteredAt) return NotInGame;
        // The total first: bytes read between the two calls then only shorten what
        // is kept, never reach back past the mark.
        long sinceEntry = svc.Wire.TotalBytes - enteredAt;
        byte[] bytes = svc.Wire.Snapshot();
        if (sinceEntry < bytes.Length) bytes = bytes[^(int)Math.Max(0, sinceEntry)..];
        string raw = WireFormatter.RenderRaw(bytes);
        string tail = LastLines(raw, ScrollbackLines);
        if (tail.Length == 0) return "_(no wire captured)_";
        return "```\n" + tail + "\n```";
    }

    // The classified combat trace, last ScrollbackLines combat-window lines each
    // tagged with how the recognizer read it. Only emitted when the Classified pane
    // is visible.
    private static string BuildClassifiedWire(AppServices svc)
    {
        string log = svc.CombatClassifier.RenderLog(ScrollbackLines).TrimEnd('\n');
        if (log.Length == 0) return "_(no combat lines classified yet)_";
        return "```\n" + log + "\n```";
    }

    // The last n newline-delimited lines of s (trailing blank line ignored).
    private static string LastLines(string s, int n)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        string[] lines = s.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
        int skip = Math.Max(0, lines.Length - n);
        return string.Join('\n', lines.Skip(skip));
    }

    private const string NotInGame =
        "_(left out: nothing from outside the game is copied into a report, so a login screen can't ride along)_";

    // Only rows written while in the game (InGameCapture): the backscroll and the
    // terminal still show the board's login, and a report made at login would
    // otherwise carry the account name with it.
    private static string BuildScrollback(TerminalEmulator emulator, Game.InGameCapture inGame)
    {
        // Every content row carries the instant its content was written — the same
        // per-row write stamp whether it has scrolled off into the ring or is still
        // on screen, so the timestamps stay in order and line up against the program
        // log (e.g. matching a nag-cancel log line to the telepath that triggered
        // it, or a combat resume to the buff that interrupted it). Only blank
        // spacing rows have no time.
        IReadOnlyList<TranscriptSnapshot.Line> lines =
            TranscriptSnapshot.Tail(emulator, ScrollbackLines, only: inGame.Window);
        if (lines.Count == 0) return NotInGame;

        StringBuilder sb = new();
        sb.Append("Last ").Append(lines.Count)
          .Append(" line(s) written while in the game, each prefixed with its write time (blank spacing rows have none). ")
          .Append(inGame.InGame ? "In the game now." : "Not in the game now.")
          .Append("\n\n```\n");
        foreach (TranscriptSnapshot.Line line in lines)
        {
            sb.Append(line.Timestamp is { } t ? t.ToLocalTime().ToString("HH:mm:ss") : "        ")
              .Append(' ').Append(line.Text).Append('\n');
        }
        sb.Append("```");
        return sb.ToString();
    }

    // ----- Helpers -------------------------------------------------------

    private static string RealmLabel(RealmType realm) => realm switch
    {
        RealmType.ParaMud => "paradigm",
        _ => "stock",
    };

    private static void Kv(StringBuilder sb, string key, string value)
        => sb.Append("- **").Append(key).Append("**: ").Append(value).Append('\n');

    // The item worn in a given inventory slot (e.g. "Weapon Hand"), or null when
    // that slot is empty / the loadout hasn't been parsed yet.
    private static string? WornSlot(InventorySnapshot inv, string slot)
    {
        foreach (EquippedItem e in inv.EquippedItems)
            if (string.Equals(e.Slot, slot, StringComparison.OrdinalIgnoreCase))
                return e.Name;
        return null;
    }

    // Serialize value into a fenced JSON block.
    private static string Json(object? value)
    {
        try
        {
            return "```json\n" + JsonSerializer.Serialize(value, JsonStore.Options) + "\n```\n";
        }
        catch (Exception ex)
        {
            return $"_(could not serialize: {ex.Message})_\n";
        }
    }

    // Run a section builder, converting any throw into an inline note.
    private static string SafeSection(Func<string> build)
    {
        try { return build(); }
        catch (Exception ex) { return $"_(capture failed: {ex.Message})_"; }
    }

    private static T Guard<T>(Func<T> read, T fallback)
    {
        try { return read(); }
        catch { return fallback; }
    }
}
