using System.Linq;
using System.Threading.Tasks;
using MudPlay.Game.Map;
using MudPlay.Services;

namespace MudPlay.ViewModels.Navigation;

// Shared entry point for user-initiated walks that should offer a route choice.
// Automated walks (event scripts, death recovery, loops, deposits, party
// comeback, trainer routing) bypass this and call Walker.WalkTo directly — they
// default to the free-preferring, teleport-allowed route with no prompt.
//
// The flow: resolve the current room, then plan (PlanRouteChoice) — the forks in
// priority order are walk-vs-teleport, trap-avoid, avoid-override, then the
// free-vs-direct item-gate fork; failing all, a fully-blocked "run to the block"
// offer or a plain walk. A sole route whose gates are all auto-obtainable arms
// acquisition and walks with no prompt. Anything else surfaces the picker, whose
// answer commits the chosen route; cancel walks nothing.
//
// Planning runs off the UI thread when the walker is idle (so a big-graph plan
// doesn't freeze the app), and a slow plan surfaces a "Calculating…" window while
// it runs; see WalkAsync.
public static class RouteChoicePrompt
{
    // Program-log category for the route-pick decision trace. Nav lifecycle → Info
    // for the fork that actually fired (what the user sees), Debug for the plain
    // no-fork walk so an ordinary GOTO doesn't spam the log.
    private const string LogCat = "RoutePick";
    // previewSink: optional map-preview channel. When the user selects a route in
    // the picker (before committing), it's called with that route's RoomKey line
    // so the caller can draw it; called with null when the picker closes (the
    // committed walk then draws its own live path). Callers without a map (e.g.
    // the navigation-manager list) pass none and the picker just works Go-only.
    public static async Task WalkAsync(
        AppServices services,
        RoomKey destination,
        Action<IReadOnlyList<RoomKey>?>? previewSink = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Let the nav-map right-click menu that launched this walk close before we do
        // anything heavy.
        await Task.Yield();

        Room? source = services.RoomTracker.State.CurrentRoom;
        if (source is null)
        {
            // No confident source room — let the walker plan and report the
            // "no known source" failure itself rather than second-guessing here.
            services.Log.Debug(LogCat, $"route pick to {destination}: no confident source room — plain walk");
            CommitWalk(services, destination, gated: false);
            return;
        }

        RoomKey src = source.Key;

        // Plan the route (which fork, if any, to surface). The BFS passes are
        // synchronous and, on a large graph (Paradigm), can take up to ~1s. When the
        // walker is IDLE, run them on a background thread so the UI thread stays live —
        // and if planning drags past a short threshold, surface the picker window in a
        // "Calculating…" state that paints while planning finishes. When a walk is
        // already IN PROGRESS, the live walker shares the movement filter the planner
        // briefly toggles (SuspendAcquirableGates), so plan on the UI thread instead
        // (no calc window that time) to avoid racing it.
        RouteChoiceDialogViewModel? calcVm = null;
        Task<RouteChoiceResult?>? calcDialogTask = null;
        RoutePlan plan;
        if (services.Walker.State == WalkState.Idle)
        {
            Task<RoutePlan> planTask = Task.Run(() => PlanRouteChoice(services, src, destination));
            // Only pop the "Calculating…" window if planning takes long enough to
            // notice — a fast plan (most walk-tos) wins the race and never flashes a
            // window; the picker, if any, is then built fully-populated below.
            if (await Task.WhenAny(planTask, Task.Delay(RouteCalcRevealDelayMs)) != planTask)
            {
                calcVm = new RouteChoiceDialogViewModel(
                    DestinationLabel(services, destination), DestinationLabel(services, src));
                calcDialogTask = services.Dialogs
                    .OpenWindowAsync<RouteChoiceDialogViewModel, RouteChoiceResult?>(calcVm);
            }
            try
            {
                plan = await planTask;
            }
            catch (Exception ex)
            {
                // The off-thread plan reads the room graph + movement filter, which a
                // RARE concurrent UI-thread mutation (a game-data reload / reconnect, an
                // avoid-list edit) can disturb mid-read. The idle gate excludes the live
                // walker, not those. Rather than let such a race surface as a crash, log
                // it and re-plan inline on the UI thread (safe; just a one-off brief
                // freeze). Not silent — the walk still proceeds from the re-plan.
                services.Log.Warn(LogCat,
                    $"route pick {src} -> {destination}: off-thread plan faulted ({ex.GetType().Name}: {ex.Message}); re-planning on the UI thread");
                plan = PlanRouteChoice(services, src, destination);
            }
        }
        else
        {
            plan = PlanRouteChoice(services, src, destination);
        }

        // Nav lifecycle stays Info for a fork that surfaces; a plain no-fork walk is
        // Debug so an ordinary GOTO doesn't spam the log.
        if (plan.Kind == RoutePlanKind.PlainWalk)
            services.Log.Debug(LogCat, plan.LogMessage);
        else
            services.Log.Info(LogCat, plan.LogMessage);

        switch (plan.Kind)
        {
            case RoutePlanKind.PlainWalk:
                calcVm?.Close();   // no picker to show — dismiss any "Calculating…" window
                CommitWalk(services, destination, gated: false);
                return;
            case RoutePlanKind.AutoObtainSole:
                calcVm?.Close();
                // Every gate here is already flagged AutoObtainForPath, but the
                // DEMAND gate is a separate switch: with Settings → Other → "search
                // rooms if item needed" off it stays shut, so this path's own
                // "arming acquisition" promise armed nothing. Forcing the ids opens
                // it for this walk, which is what the flags asked for.
                if (plan.Choice is { } sole
                    && services.SourceableGateItems(sole.Requirements) is { Count: > 0 } soleItems)
                    services.ForcePathObtain(soleItems);
                CommitWalk(services, destination, gated: true);
                return;
            default:
                await RunPickerAsync(services, destination, src, plan.Choice!, previewSink, calcVm, calcDialogTask);
                return;
        }
    }

    // The delay before a slow plan surfaces the "Calculating…" window. A fast plan
    // (well under this) finishes first and never shows it, so an ordinary quick
    // walk-to doesn't flash a window; a plan that drags gets the feedback.
    private const int RouteCalcRevealDelayMs = 150;

    private enum RoutePlanKind { Teleport, TrapAvoid, AvoidOverride, ItemGate, Blocked, AutoObtainSole, PlainWalk }

    // The outcome of route planning: which fork (if any) to surface, the resolved
    // choice for the picker, and the ready-to-log decision line. Pure computation —
    // no UI, no logging, no network — so WalkAsync can run it off the UI thread.
    private readonly record struct RoutePlan(RoutePlanKind Kind, RouteChoice? Choice, string LogMessage);

    // Run the fork evaluations in priority order and decide the outcome. The forks
    // each need the same two full-graph pathfinds — the plain default route (gates +
    // avoids on, teleports allowed) and the avoids-lifted route — so compute each ONCE
    // behind a memoized closure (caches the result, null included) rather than
    // re-running per fork. Reads the graph / movement filter and settings only; does
    // no UI or logging, so it's safe to call from a background thread.
    private static RoutePlan PlanRouteChoice(AppServices services, RoomKey src, RoomKey destination)
    {
        IReadOnlyList<Direction>? baseCache = null; bool baseDone = false;
        IReadOnlyList<Direction>? BaseRoute()
        {
            if (!baseDone) { baseCache = services.Bfs.FindPath(src, destination, services.Movement); baseDone = true; }
            return baseCache;
        }
        IReadOnlyList<Direction>? avoidLiftedCache = null; bool avoidLiftedDone = false;
        IReadOnlyList<Direction>? AvoidLiftedRoute()
        {
            if (!avoidLiftedDone)
            {
                avoidLiftedCache = services.Bfs.FindPath(src, destination, services.Movement, ignoreAvoids: true);
                avoidLiftedDone = true;
            }
            return avoidLiftedCache;
        }

        // Walk-vs-teleport fork takes precedence over the item-gate fork: if the
        // shortest route teleports and a pure-walking route also exists, let the user
        // weigh the teleport's shortcut against its danger — a teleport can drop the
        // crosser somewhere lethal, a call the client can't make.
        RouteChoice? teleport = RouteChoicePlanner.EvaluateTeleport(
            services.Bfs, services.Movement, services.RoomGraph, src, destination, BaseRoute);
        if (teleport is not null)
            return new(RoutePlanKind.Teleport, teleport,
                $"route pick {src} -> {destination}: teleport fork — walk {teleport.FreeStepCount} "
                + $"vs teleport {teleport.GatedStepCount} hop(s) via {teleport.TeleportLanding}; showing picker");

        // Trap-avoid fork: the shortest route crosses a trap and a trap-free route to
        // the same room exists — surface the clean detour vs. trusting the step-time
        // disarm (which can fail).
        RouteChoice? trapAvoid = RouteChoicePlanner.EvaluateTrapAvoid(
            services.Bfs, services.Movement, services.RoomGraph, src, destination, BaseRoute);
        if (trapAvoid is not null)
            return new(RoutePlanKind.TrapAvoid, trapAvoid,
                $"route pick {src} -> {destination}: trap-avoid fork — fewest-traps route crosses "
                + $"{trapAvoid.FreeTrapCount} trap(s) vs {trapAvoid.GatedTrapCount} on the shortest; showing picker");

        // Avoid-override fork: the destination is reachable only through a room the user
        // marked "avoid" (sole), or a much shorter route runs through one (two-route).
        // EvaluateAvoidOverride defers (null) when suspending the acquirable gates opens
        // an avoid-respecting route — the item-gate fork below surfaces obtain / cross
        // options for that instead of asking the user to override their own avoid.
        RouteChoice? avoidOverride = RouteChoicePlanner.EvaluateAvoidOverride(
            services.Bfs, services.Movement, services.RoomGraph, src, destination, BaseRoute, AvoidLiftedRoute);
        if (avoidOverride is not null)
            return new(RoutePlanKind.AvoidOverride, avoidOverride,
                $"route pick {src} -> {destination}: avoid-override fork — "
                + $"{(avoidOverride.HasFreeRoute ? $"a shorter route saves {avoidOverride.FreeStepCount - avoidOverride.GatedStepCount} step(s)" : "the only route")} "
                + $"crosses {avoidOverride.AvoidedRoomCount} room(s) you marked Avoid; showing picker");

        RouteChoice? choice = RouteChoicePlanner.Evaluate(
            services.Bfs, services.Movement, services.RoomGraph, src, destination, BaseRoute);
        if (choice is null)
        {
            // No shorter gated route. If the route is fully blocked but the destination
            // is physically reachable up to an obstacle, offer to run to the block;
            // otherwise walk the free route (which reports its own failure if it can't).
            if (RouteChoicePlanner.PlanBlocked(
                    services.Bfs, services.Movement, services.RoomGraph, src, destination)
                is { } blocked)
                return new(RoutePlanKind.Blocked,
                    BuildBlockedChoice(services, src, destination, blocked),
                    $"route pick {src} -> {destination}: blocked — no full route; can run as far as "
                    + $"{blocked.StopRoom} ({blocked.BlockDir} is {blocked.BlockExit.Hint}); showing picker");
            return new(RoutePlanKind.PlainWalk, null,
                $"route pick {src} -> {destination}: no fork (free route needs nothing acquirable); plain walk");
        }

        // This choice's routes all respect the avoids (Evaluate never lifts them). If a
        // route that DOES cross an avoided room also exists — one that needs no counter
        // to obtain — attach it as an extra card so the user can pick "plow through my
        // avoided rooms" instead of fetching a raft (report paradigm-20260907-212758).
        string avoidAltNote = "";
        if (RouteChoicePlanner.AvoidAlternative(
                services.Bfs, services.Movement, services.RoomGraph, src, destination, AvoidLiftedRoute) is { } alt)
        {
            choice = choice with { AvoidAlternativePath = alt.Path, AvoidAlternativeCount = alt.AvoidedCount };
            avoidAltNote = $" (+avoid-crossing alt: {alt.AvoidedCount} room(s), no counter)";
        }

        string reqSummary = string.Join(", ", choice.Requirements.Select(r => $"{r.Kind}[{string.Join("/", r.ItemIds)}]"));

        // Sole route (no gate-free alternative) whose gates are item/ticket/key, not a
        // hazard. When every gate is a single item/ticket the user flagged
        // AutoObtainForPath, the acquisition pipeline sources it all: arm and cross, no
        // prompt. Otherwise a gate the client can't auto-source is on the route (a door
        // key, or an unflagged item) — surface the picker so the user sees it and
        // chooses. Hazard-only sole routes fall through to the item-gate picker below.
        if (!choice.HasFreeRoute
            && choice.Requirements.Any(r => r.Kind != RouteRequirementKind.HazardProtection))
        {
            if (services.ShouldAutoObtainSoleRoute(choice.Requirements))
                return new(RoutePlanKind.AutoObtainSole, choice,
                    $"route pick {src} -> {destination}: sole route needs {reqSummary}, all auto-obtainable "
                    + "— arming acquisition and walking, no prompt");
            return new(RoutePlanKind.ItemGate, choice,
                $"route pick {src} -> {destination}: sole route needs {reqSummary} (not auto-obtainable); showing picker{avoidAltNote}");
        }

        return new(RoutePlanKind.ItemGate, choice,
            $"route pick {src} -> {destination}: item-gate fork — "
            + $"{(choice.HasFreeRoute ? $"direct route saves {choice.FreeStepCount - choice.GatedStepCount} step(s)" : "sole route")} "
            + $"needing {reqSummary}; showing picker{avoidAltNote}");
    }

    // Build the picker, draw the previewed route while it's open, and commit the
    // chosen route. Shared by every fork that surfaces a choice (teleport,
    // trap-avoid, avoid-override, item-gate, and the fully-blocked "run to the
    // block" offer); the commit switch at the end branches on the choice kind, and
    // an item-gate choice further splits into free / acquire / send-it / search /
    // route-through-avoided.
    private static async Task RunPickerAsync(
        AppServices services,
        RoomKey destination,
        RoomKey source,
        RouteChoice choice,
        Action<IReadOnlyList<RoomKey>?>? previewSink,
        // When the idle path pre-opened a "Calculating…" window (planning ran long),
        // it's passed here to populate in place; calcDialogTask is that window's
        // close-result task. Both null when planning was fast (or a walk was in
        // progress) — then this builds the fully-populated window and shows it.
        RouteChoiceDialogViewModel? calcVm = null,
        Task<RouteChoiceResult?>? calcDialogTask = null)
    {
        // Approximate arrival ETA for each route — realm-aware per-hop travel plus,
        // when auto-combat is on, a dwell for each lair the walker will actually FIGHT
        // through (friendly / passive "lairs" walk straight past — services.LairWillBeFought).
        // The same estimate the live walk-status label surfaces, so they agree. Empty
        // free path (sole route) estimates to zero, so the picker shows a bare step
        // count there.
        TimeSpan freeEta = RouteEtaEstimator.Estimate(
            choice.FreePath, services.AutoLair.TravelCostModel,
            services.RoomGraph.GetRoom, includeLairDwell: services.IsAutoCombatEnabled,
            lairWillBeFought: services.LairWillBeFought);
        TimeSpan gatedEta = RouteEtaEstimator.Estimate(
            choice.GatedPath, services.AutoLair.TravelCostModel,
            services.RoomGraph.GetRoom, includeLairDwell: services.IsAutoCombatEnabled,
            lairWillBeFought: services.LairWillBeFought);

        // A route that crosses a SURVIVABLE hazard the player can't currently pass —
        // whether the hazard is the only gate (sole hazard) or the route ALSO has a
        // hard gate past it (a keyed door: a "mixed" route). Either way the picker
        // offers to obtain the counter / cross unprotected; the mixed case just also
        // names the hard gate it stops at. Never for a grave hazard (a drown / freeze
        // death, a forced teleport) — UnprotectedHazardsAllSurvivable gates that.
        bool crossesHazard = !choice.HasFreeRoute && choice.Kind != RouteChoiceKind.Teleport
            && choice.Requirements.Any(r => r.Kind == RouteRequirementKind.HazardProtection);
        bool crossesSurvivableHazard = crossesHazard
            && services.UnprotectedHazardsAllSurvivable(choice.GatedPath);
        bool mixedHazard = crossesSurvivableHazard
            && choice.Requirements.Any(r => r.Kind != RouteRequirementKind.HazardProtection);

        // Resolve which counter the run would obtain and how (floor grab / free give /
        // shop buy / drop hunt), so the picker can offer "obtain then cross" and Go
        // forces that counter through the acquire pipeline — an explicit pick, so no
        // AutoObtainForPath flag is needed. Run for ANY hazard (survivable OR grave —
        // a grave hazard's only safe crossing IS obtaining the counter); only the
        // HAZARD requirements are sourced this way — a hard gate (a door key) is never
        // auto-fetched.
        List<int> floorCounters = new();     // grabbed in place with a `get`
        List<int> detourCounters = new();    // sourced via the give/shop/drop pipeline
        List<string> hazardSources = new();
        // Counters the run would BUY (item + shop room) and the items to ask the
        // party about — feed the pick-time economy probe (own bank + @wealth/@have).
        List<(int ItemId, RoomKey ShopRoom)> buys = new();
        List<(int ItemId, string Name)> neededItems = new();
        // Every any-of counter id for the hazards on this route — the "search en
        // route" card force-obtains all of them so a `sea`-revealed one (whichever
        // turns up) is grabbed by the floor collector and the crossing goes safe.
        List<int> hazardCounterIds = new();
        // The specific counter resolved per hazard requirement (which item, and how) —
        // so the picker's requirement line names the exact one it'll obtain ("log raft
        // (buy at Pier)") instead of the whole any-of list.
        Dictionary<RouteRequirement, (int ItemId, string Source)> resolvedCounters = new();
        if (crossesHazard)
            foreach (RouteRequirement req in choice.Requirements)
            {
                if (req.Kind != RouteRequirementKind.HazardProtection) continue;
                foreach (int cid in req.ItemIds)
                    if (!hazardCounterIds.Contains(cid)) hazardCounterIds.Add(cid);
                // A counter already chosen for an earlier hazard that also appears in
                // THIS hazard's any-of set covers it too — both FCCO slide rooms accept
                // rope-and-grapple OR climbing harness, so one rope answers both. Skip
                // the redundant second source rather than buying a rope AND a harness.
                if (req.ItemIds.Any(id => floorCounters.Contains(id) || detourCounters.Contains(id)))
                    continue;
                if (services.ResolveHazardCounter(req.ItemIds, source, destination) is { } r)
                {
                    List<int> bucket = r.OnFloor ? floorCounters : detourCounters;
                    if (!bucket.Contains(r.ItemId)) bucket.Add(r.ItemId);
                    if (!hazardSources.Contains(r.Source)) hazardSources.Add(r.Source);
                    resolvedCounters[req] = (r.ItemId, r.Source);
                    // A shop-sourced counter is a money question — collect it (and the
                    // item name) for the pick-time economy probe below.
                    if (r.ShopRoom is { } shopRoom)
                    {
                        buys.Add((r.ItemId, shopRoom));
                        if (services.ItemNames.GetName(r.ItemId) is { Length: > 0 } bn)
                            neededItems.Add((r.ItemId, bn));
                    }
                }
            }
        string? hazardCounterSource = hazardSources.Count > 0
            ? string.Join("; ", hazardSources) : null;
        bool hazardObtain = hazardCounterSource is not null;

        // Requirement-clause name helpers shared by both build paths:
        //   • give: the NPC / room a free deterministic give would ask (preempts the
        //     shop and drop tails — the give router stands both down).
        //   • shop: the shop the run would detour to buy at when it's give-less and
        //     flagged buy-if-needed (resolved from this walk's source/destination).
        //   • drop: the lair a flagged dropper sits in, when there's no give or shop.
        //   • resolvedCounter: the specific counter the run resolved per hazard
        //     requirement (item + "buy at Pier" / "ask X" / …), so the clause names
        //     that one, not the whole any-of set.
        Func<int, string?> giveName = itemId => services.PathItemGiveName(itemId, source, destination);
        Func<int, string?> shopName = itemId => services.PathItemShopName(itemId, source, destination);
        Func<int, string?> dropName = itemId => services.PathItemDropName(itemId, source);
        Func<RouteRequirement, (int ItemId, string Source)?> resolvedCounter =
            req => resolvedCounters.TryGetValue(req, out (int ItemId, string Source) v) ? v : ((int, string)?)null;

        string destLabel = DestinationLabel(services, destination);

        // For a mixed route with no sourceable counter, the base card walks to the
        // hazard's edge and stops (the user then fetches a counter / clears the hard
        // gate themselves), rather than crossing the hazard blindly.
        RoomKey? hazardEdge = mixedHazard && !hazardObtain
            ? services.HazardApproachRoom(choice.GatedPath)
            : null;

        // The run would BUY a counter → check the money BEFORE surfacing (the user's
        // "check own bank, send @wealth to the party, then surface by availability"):
        // refresh own bank and, in a party, members' on-hand cash + whether a member
        // already holds a needed item. Drives the card note and — when the leader can't
        // pay from cash / the configured bank — redirects Go to walk to the shop and
        // pause for manual provisioning.
        Game.Map.RouteBuyEconomy? economy = buys.Count > 0
            ? await services.AssessRouteBuyAsync(buys, neededItems, source)
            : null;
        string? economyNote = economy is { } eco
            ? (eco.PartyItemHolder is { } holder
                ? $"{holder} in your party has it — will hand it over on the way"
                : eco.Affordability.Note)
            : null;
        RoomKey? buyPauseRoom = economy is { PartyItemHolder: null } e2 && !e2.AutoPayable
            ? e2.ShopRoom
            : null;

        RouteChoiceDialogViewModel vm;
        Task<RouteChoiceResult?> dialogTask;
        if (calcVm is not null && calcDialogTask is not null)
        {
            // Idle path: the "Calculating…" window is already open and painted — fill
            // its cards in place (Populate flips it out of the calculating state).
            vm = calcVm;
            dialogTask = calcDialogTask;
            vm.Populate(
                choice, services.ItemNames.GetName, giveName, shopName, dropName,
                freeEta, gatedEta, hazardCounterSource, crossesSurvivableHazard, resolvedCounter, economyNote);
        }
        else
        {
            // Fast plan / walk-in-progress: no calc window was opened — build the
            // fully-populated VM and show it (nothing to morph, no flicker).
            vm = new RouteChoiceDialogViewModel(
                choice, destLabel, services.ItemNames.GetName, giveName, shopName, dropName,
                freeEta, gatedEta, hazardCounterSource, crossesSurvivableHazard, resolvedCounter,
                economyNote, sourceLabel: DestinationLabel(services, source));
            dialogTask = services.Dialogs
                .OpenWindowAsync<RouteChoiceDialogViewModel, RouteChoiceResult?>(vm);
        }

        // Draw the selected route's line while the picker is open; clear it when
        // the picker closes so a committed walk's live path isn't double-drawn and
        // a cancel leaves no stale preview behind.
        if (previewSink is not null)
        {
            vm.PreviewRequested += r => previewSink(r switch
            {
                RouteChoiceResult.Free => choice.FreePath,
                // The direct / send-it / search choices all trace the same physical
                // gated line toward the hazard.
                RouteChoiceResult.Gated => choice.GatedPath,
                RouteChoiceResult.GatedNoAcquire => choice.GatedPath,
                RouteChoiceResult.SearchEnRoute => choice.GatedPath,
                RouteChoiceResult.AvoidOverrideAlt => choice.AvoidAlternativePath,
                _ => null,
            });
            // A pre-selected route (trap-avoid defaults to the trap-free line) draws
            // its preview on open, now that the sink is subscribed.
            vm.RaiseSelectionPreview();
        }

        // Details… opens the shared route-details browse window for the selected
        // route's polyline — the same window the CURRENT NAV panel uses, with each
        // room's monsters, hazards, and item gates linked to their records. Both
        // direct choices trace the same physical gated line.
        string detailsTitle = $"Route → {destLabel}";
        vm.ShowDetailsRequested += r => RouteDetailsLauncher.Open(
            services, detailsTitle,
            r switch
            {
                RouteChoiceResult.Free => choice.FreePath,
                RouteChoiceResult.AvoidOverrideAlt when choice.AvoidAlternativePath is { } ap => ap,
                _ => choice.GatedPath,
            });

        RouteChoiceResult? result;
        try
        {
            result = await dialogTask;
        }
        finally
        {
            previewSink?.Invoke(null);
        }

        if (choice.Kind == RouteChoiceKind.Blocked)
        {
            // Go = "run to the blocked room anyway": a plain walk to the furthest
            // reachable room, landing the walker adjacent to the obstacle. Cancel /
            // any other result walks nothing.
            if (result == RouteChoiceResult.Gated && choice.StopRoom is { } stop)
                CommitWalk(services, stop, gated: false);
            return;
        }

        if (choice.Kind == RouteChoiceKind.Teleport)
        {
            switch (result)
            {
                case RouteChoiceResult.Free:
                    // "Walk it" — refuse the teleport shortcut, plan the safe route.
                    CommitWalk(services, destination, gated: false, avoidTeleports: true);
                    break;
                case RouteChoiceResult.Gated:
                    // "Teleport" — the user explicitly chose the shortcut, so drop the
                    // prefer-walk bias and let the walker plan the teleport hop.
                    CommitWalk(services, destination, gated: false, preferTeleportFree: false);
                    break;
                // null → cancelled: walk nothing.
            }
            return;
        }

        if (choice.Kind == RouteChoiceKind.TrapAvoid)
        {
            switch (result)
            {
                case RouteChoiceResult.Free:
                    // "Avoid traps" — plan the trap-free route (refuse trapped exits).
                    CommitWalk(services, destination, gated: false, avoidTraps: true);
                    break;
                case RouteChoiceResult.Gated:
                    // "Cross traps" — the walker's default plan, disarming at step time.
                    CommitWalk(services, destination, gated: false);
                    break;
                // null → cancelled: walk nothing.
            }
            return;
        }

        if (choice.Kind == RouteChoiceKind.AvoidOverride)
        {
            switch (result)
            {
                case RouteChoiceResult.Free:
                    // "Respect my avoids" (two-route case) — the longer route that
                    // honours the avoid list, planned normally.
                    CommitWalk(services, destination, gated: false);
                    break;
                case RouteChoiceResult.Gated:
                    // "Route through avoided rooms" — override the avoid list for this
                    // one walk. Avoids stay set; only this walk crosses them.
                    CommitWalk(services, destination, gated: false, ignoreAvoids: true);
                    break;
                // null → cancelled: walk nothing.
            }
            return;
        }

        switch (result)
        {
            case RouteChoiceResult.Free:
                CommitWalk(services, destination, gated: false);
                break;
            case RouteChoiceResult.Gated when hazardEdge is { } edge:
                // Mixed route, no sourceable counter: the base card walks to the
                // hazard's edge and stops (a plain gate-free walk to the room just
                // short of the river), so the user can fetch a counter / clear the
                // hard gate by hand from there — rather than crossing blindly.
                CommitWalk(services, edge, gated: false);
                break;
            case RouteChoiceResult.Gated when buyPauseRoom is { } shopStop:
                // The counter must be bought but the leader can't pay from cash or the
                // configured bank (the money's at another bank or spread across the
                // party). Don't arm an auto-buy that would stall — walk to the shop
                // and stop, so the user withdraws / pools cash / provisions the party
                // by hand from there (the card names where the money is).
                CommitWalk(services, shopStop, gated: false);
                break;
            case RouteChoiceResult.Gated:
                // Hazard "obtain then cross". A counter already on the floor is
                // grabbed in place; the rest are forced through the acquire pipeline
                // (give/shop/drop) even when unflagged — the explicit pick is the
                // consent. The walk still plans gated (the counter isn't carried
                // yet at plan time) and crosses safely once it's in hand. A SOLE
                // route (the gate is unavoidable) also plans avoidTraps, so the
                // forced crossing takes the fewest-traps approach the planner chose.
                foreach (int id in floorCounters)
                    if (services.ItemNames.GetName(id) is { Length: > 0 } n)
                        services.SendGameCommand($"get {n}");
                if (detourCounters.Count > 0)
                    services.ForcePathObtain(detourCounters);
                // The route's ITEM gates need the same force, for the same reason:
                // the pick is the consent. Only the hazard counters were being
                // forced, so an item-gated pick armed nothing unless the global
                // search-if-needed preference happened to be on — and the walk then
                // crossed a gate it had made no arrangements for.
                if (services.SourceableGateItems(choice.Requirements) is { Count: > 0 } gateItems)
                    services.ForcePathObtain(gateItems);
                CommitWalk(services, destination, gated: true, avoidTraps: !choice.HasFreeRoute);
                break;
            case RouteChoiceResult.GatedNoAcquire:
                // "Send it": walk the gated route but don't arm acquisition — the
                // user asserts they'll clear the gates without provisioning. A sole
                // route still avoids traps, matching the planner's chosen approach.
                CommitWalk(services, destination, gated: true,
                    armAcquisition: false, avoidTraps: !choice.HasFreeRoute);
                break;
            case RouteChoiceResult.SearchEnRoute:
                // "Search en route": force-obtain every any-of counter, then walk the
                // gated route. The forced obtain both arms the shop-buy fallback (the
                // reliable acquire path — see PathItemShopRouter) and, when the master
                // auto-search toggle is on, the per-room `sea`. The floor collector
                // grabs whichever counter a search reveals first; found-first aborts the
                // buy and the walk crosses. With auto-search off (or if nothing turns up
                // en route) the shop-buy is the last resort. Auto-search is the driver:
                // toggling it off mid-route stops the `sea` and leaves the buy running.
                if (hazardCounterIds.Count > 0)
                    services.ForcePathObtain(hazardCounterIds);
                // Picking Search asserts intent to search, so turn auto-search on for
                // this leg if it's off — the card always actually searches. It flips
                // back off once the counter lands (found or bought) or the walk ends.
                services.BeginRouteSearchAutoSearch();
                CommitWalk(services, destination, gated: true, avoidTraps: !choice.HasFreeRoute);
                break;
            case RouteChoiceResult.AvoidOverrideAlt:
                // "Route through my avoided rooms" — the extra card: override the avoid
                // list for this one walk (needs no counter). Avoids stay set; only this
                // walk crosses them, same as the avoid-override fork's commit.
                CommitWalk(services, destination, gated: false, ignoreAvoids: true);
                break;
            // null → cancelled: walk nothing (and leave any manual pause intact —
            // the user backed out, so nothing changed).
        }
    }

    // Start the walk, first lifting any lingering manual pause. A user picking a
    // fresh destination is an explicit "go here now" that outranks a mid-walk
    // Pause: without clearing the UserGate the new walk would immediately re-pause
    // (AutoWalkManager.WalkToImmediate honours the coordinator's paused state), so
    // the destination changed but the walker stayed frozen. Engine waits (Combat /
    // rest / party) are left asserted and re-pause on their own if still relevant.
    // preferTeleportFree defaults TRUE for every user-picker commit: a walk the user
    // launched from the picker (or a plain walk-to) should take the pure-walking route
    // and only fall back to a teleport hop when walking is genuinely impossible — so a
    // mid-walk re-plan (e.g. after a search-en-route counter turns up) never silently
    // pivots onto a vortex the user didn't ask for. The one exception is the teleport
    // fork's explicit "Teleport" pick, which passes false to allow the shortcut.
    private static void CommitWalk(
        AppServices services, RoomKey destination, bool gated,
        bool armAcquisition = true, bool avoidTeleports = false, bool avoidTraps = false,
        bool ignoreAvoids = false, bool preferTeleportFree = true)
    {
        // Abandon a paused walk-in-progress BEFORE clearing the gate. Clearing
        // UserGate synchronously resumes a Paused walker (OnCoordinatorPauseChanged
        // → SendNextStep), which would fire one stale step toward the OLD
        // destination before we redirect. Stopping first leaves the walker Idle so
        // the gate clear has nothing to resume, and WalkTo plans the new route
        // cleanly.
        if (services.Walker.State == WalkState.Paused)
            services.Walker.Stop("superseded by new user walk-to");
        services.MovementCoordinator.ClearGate(
            MovementCoordinator.UserGate, nameof(RouteChoicePrompt));
        services.Walker.WalkTo(
            destination,
            planThroughAcquirableGates: gated,
            armItemAcquisition: armAcquisition,
            avoidTeleports: avoidTeleports,
            avoidTraps: avoidTraps,
            ignoreAvoids: ignoreAvoids,
            preferTeleportFree: preferTeleportFree);
    }

    private static string DestinationLabel(AppServices services, RoomKey destination) =>
        services.RoomGraph.GetRoom(destination)?.Name is { Length: > 0 } name
            ? $"{name} ({destination})"
            : destination.ToString();

    // Turn a "run to the blocked room" plan into a Blocked RouteChoice: the
    // reachable prefix as the previewed path, and the obstacle named via the shared
    // describer (which room, which way, the key / picklocks-strength it needs) so
    // the picker states exactly what's in the way.
    private static RouteChoice BuildBlockedChoice(
        AppServices services, RoomKey source, RoomKey destination, BlockedRoutePlan plan)
    {
        string reason = BlockedExitDescriber.Describe(
            plan.StopRoom, plan.BlockDir, plan.BlockExit,
            key => services.RoomGraph.GetRoom(key)?.Name,
            services.ItemNames.GetName);
        return new RouteChoice(
            FreeStepCount: 0,
            GatedStepCount: plan.Preview.Count > 0 ? plan.Preview.Count - 1 : 0,
            Requirements: Array.Empty<RouteRequirement>(),
            FreePath: Array.Empty<RoomKey>(),
            GatedPath: plan.Preview,
            Kind: RouteChoiceKind.Blocked,
            StopRoom: plan.StopRoom,
            BlockedReason: reason);
    }
}
