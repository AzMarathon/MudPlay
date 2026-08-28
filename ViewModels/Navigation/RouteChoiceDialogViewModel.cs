using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Game.Map;
using MudPlay.Services;

namespace MudPlay.ViewModels.Navigation;

// Which route the user picked in the RouteChoiceDialog. Cancel returns null
// (walk nothing) rather than a member of this enum.
public enum RouteChoiceResult
{
    Free,           // the longer gate-free route
    Gated,          // the shorter gated route — acquire the missing items first
    GatedNoAcquire, // the shorter gated route — "send it": cross as-is, no acquisition
}

// Route picker, shown when RouteChoicePlanner found a fork worth a user decision.
// Two flavors share this VM: the item-gate fork (a shorter direct route crosses
// an acquirable gate) and the teleport fork (a shorter route teleports where a
// walking route also exists). Clicking a route selects it and previews its line
// on the map (no walk yet); the Go button commits the selected route. The
// item-gate direct route splits in two when a gate-free detour exists: "acquire
// then go" arms the acquisition pipeline for the missing items, while "send it"
// crosses the gates as-is on the user's say-so. The teleport fork is a plain
// two-way choice — walk it (safe, longer) or teleport (shorter, maybe lethal),
// no send-it split. Cancel / X walks nothing.
public sealed partial class RouteChoiceDialogViewModel
    : ObservableObject, IDialogViewModel<RouteChoiceResult?>
{
    public event Action<RouteChoiceResult?>? CloseRequested;

    // Raised when the user selects a route to preview (before committing), so the
    // caller can draw that route's line on the map. Null clears the preview. The
    // picker never draws the map itself — it has no map knowledge; the prompt
    // maps the selected route to its FreePath / GatedPath and pushes it.
    public event Action<RouteChoiceResult?>? PreviewRequested;

    public string Heading { get; }
    public string FreeSummary { get; }
    public string GatedSummary { get; }
    public string SendItSummary { get; }
    public string RequirementSummary { get; }

    // The caveat shown under the shorter route in a teleport choice — names the
    // teleport's landing room and warns the shortcut can be lethal. Empty for the
    // item-gate choice, which shows RequirementSummary instead.
    public string TeleportCaveat { get; }

    // The caveat shown under the shorter route in a trap-avoid choice — that the
    // shortcut crosses a trap the walker disarms at step time, which can fail. Empty
    // for the other forks.
    public string TrapCaveat { get; }

    // The sub-line under the shorter route's card: the item requirements for an
    // item-gate choice, the teleport caveat for a teleport choice, the trap caveat
    // for a trap-avoid choice.
    public string GatedDetail =>
        IsTeleportChoice ? TeleportCaveat :
        IsTrapAvoidChoice ? TrapCaveat :
        RequirementSummary;

    // The footnote under the cards, explaining the fork's options — different
    // wording for the teleport choice (no acquire / send-it split there).
    public string Footnote { get; }

    // True when this is the walk-vs-teleport fork: the shorter route takes a
    // teleport the walking route avoids. Reworders the cards (Walk / Teleport) and
    // hides the acquire/send-it split (there's nothing to acquire).
    public bool IsTeleportChoice { get; }

    // True when this is the trap-avoid fork: the shortest route crosses a trap and a
    // trap-free route exists. A plain two-way choice (avoid / cross), no acquire /
    // send-it split. The trap-free route is pre-selected so the safe route is the
    // default (the user can still pick the shortcut).
    public bool IsTrapAvoidChoice { get; }

    // False when there's no gate-free route — the direct (hazard-crossing) route
    // is the only way there. The Free card renders as a disabled "why you can't
    // just walk it" note; only the direct route is selectable.
    public bool HasFreeRoute { get; }

    // The "direct — send it" card (cross the gates as-is, no acquisition) is only
    // offered when a gate-free detour also exists — i.e. the true two-route fork,
    // where skipping acquisition is a meaningful third choice. When the gated
    // route is the ONLY way there, the send-it/acquire split collapses to the
    // single acquire card (chunk-4 flag logic governs that case, not the picker).
    // A teleport choice has no acquisition, so it never shows the send-it card.
    // A sole hazard route with an obtainable counter also shows it — as the
    // "cross unprotected (take the damage)" escape opposite "obtain then cross".
    // A trap-avoid choice is a plain two-way fork — no send-it card there either.
    public bool ShowSendItCard =>
        (HasFreeRoute || HazardObtain) && !IsTeleportChoice && !IsTrapAvoidChoice;

    // True when this is a sole hazard-only route the caller resolved an obtainable
    // counter for: Go fetches it then crosses (vs. "cross unprotected"). Drives the
    // obtain wording + the send-it card in the sole-hazard case.
    public bool HazardObtain { get; }

    // Which route the user has selected to preview. Null until they click one —
    // Go stays disabled until then, forcing the click-to-preview-then-Go flow.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFreeSelected))]
    [NotifyPropertyChangedFor(nameof(IsGatedSelected))]
    [NotifyPropertyChangedFor(nameof(IsSendItSelected))]
    [NotifyPropertyChangedFor(nameof(CurrentStepRows))]
    [NotifyPropertyChangedFor(nameof(HasStepRows))]
    [NotifyPropertyChangedFor(nameof(CanShowSteps))]
    [NotifyCanExecuteChangedFor(nameof(GoCommand))]
    private RouteChoiceResult? _selectedRoute;

    // The full start-to-finish command sequence for each route (moves, lever/winch/
    // door detours, and acquire steps), surfaced by the Show-steps flyout. Free
    // traces the gate-free line; the two gated choices share the same physical route.
    private readonly IReadOnlyList<RouteStepRow> _freeSteps;
    private readonly IReadOnlyList<RouteStepRow> _gatedSteps;

    public IReadOnlyList<RouteStepRow> CurrentStepRows =>
        SelectedRoute == RouteChoiceResult.Free ? _freeSteps : _gatedSteps;

    public bool HasStepRows => CurrentStepRows.Count > 0;

    // The Show-steps button lights up once a route is picked and there's a sequence
    // to show — clicking it opens the flyout listing that route's full step plan.
    public bool CanShowSteps => SelectedRoute is not null && HasStepRows;

    public bool IsFreeSelected => SelectedRoute == RouteChoiceResult.Free;
    public bool IsGatedSelected => SelectedRoute == RouteChoiceResult.Gated;
    public bool IsSendItSelected => SelectedRoute == RouteChoiceResult.GatedNoAcquire;

    public RouteChoiceDialogViewModel(
        RouteChoice choice,
        string destinationLabel,
        Func<int, string?> itemName,
        Func<int, string?>? giveNameForItem = null,
        Func<int, string?>? shopNameForItem = null,
        Func<int, string?>? dropNameForItem = null,
        TimeSpan freeEta = default,
        TimeSpan gatedEta = default,
        string? hazardCounterSource = null,
        IReadOnlyList<RouteStepRow>? freeSteps = null,
        IReadOnlyList<RouteStepRow>? gatedSteps = null)
    {
        ArgumentNullException.ThrowIfNull(choice);
        ArgumentNullException.ThrowIfNull(itemName);

        _freeSteps = freeSteps ?? Array.Empty<RouteStepRow>();
        _gatedSteps = gatedSteps ?? Array.Empty<RouteStepRow>();

        IsTeleportChoice = choice.Kind == RouteChoiceKind.Teleport;
        IsTrapAvoidChoice = choice.Kind == RouteChoiceKind.TrapAvoid;
        HasFreeRoute = choice.HasFreeRoute;

        // A fully-blocked route: no way through at all, but the destination is
        // physically reachable up to an obstacle. Offer to walk as far as possible
        // and stop at the block, naming it so the user knows what to clear by hand.
        if (choice.Kind == RouteChoiceKind.Blocked)
        {
            HazardObtain = false;
            string reason = choice.BlockedReason ?? "a blocked exit";
            Heading = $"Only route to {destinationLabel} is blocked";
            FreeSummary = $"No open route — blocked by {reason}";
            GatedSummary = $"Run to the blocked room anyway — {StepsEta(choice.GatedStepCount, gatedEta)}";
            SendItSummary = string.Empty;
            RequirementSummary = string.Empty;
            TeleportCaveat = string.Empty;
            TrapCaveat = string.Empty;
            Footnote = "Click the route to preview it on the map, then Go to walk as far as you "
                + $"can toward {destinationLabel} and stop at the block — clear it by hand to continue.";
            return;
        }

        // A sole hazard-only route the caller resolved an obtainable counter for:
        // Go fetches it then crosses, and a "cross unprotected" card is offered as
        // the take-the-damage escape.
        bool soleHazardOnly = !HasFreeRoute && !IsTeleportChoice
            && choice.Requirements.All(r => r.Kind == RouteRequirementKind.HazardProtection);
        HazardObtain = soleHazardOnly && !string.IsNullOrEmpty(hazardCounterSource);

        SendItSummary = HazardObtain
            ? $"Cross unprotected — take the damage — {StepsEta(choice.GatedStepCount, gatedEta)}"
            : $"Direct — send it — {StepsEta(choice.GatedStepCount, gatedEta)}";

        if (IsTeleportChoice)
        {
            Heading = $"Walk or teleport to {destinationLabel}?";
            FreeSummary = $"Walk it — {StepsEta(choice.FreeStepCount, freeEta)}, no teleport";
            GatedSummary = $"Teleport — {StepsEta(choice.GatedStepCount, gatedEta)} — much shorter";
            TeleportCaveat =
                $"Teleports via {choice.TeleportLanding ?? "an unknown room"} — a teleport can drop "
                + "you somewhere deadly (a damaging plane, water with no boat). Whether you survive "
                + "depends on your character, so the call is yours.";
            RequirementSummary = string.Empty;
            TrapCaveat = string.Empty;
            Footnote = "Click a route to preview it on the map, then Go to walk it. "
                + "The teleport is much shorter but can be lethal — take the walk if you're unsure.";
        }
        else if (IsTrapAvoidChoice)
        {
            int freeTraps = choice.FreeTrapCount;
            int gatedTraps = choice.GatedTrapCount;
            Heading = $"Avoid traps to {destinationLabel}?";
            // The fewest-traps route isn't always fully clean — it may still cross an
            // unavoidable trap — so state the real counts instead of claiming "trap-free".
            FreeSummary = freeTraps == 0
                ? $"Avoid traps — {StepsEta(choice.FreeStepCount, freeEta)}, trap-free"
                : $"Fewest traps — {StepsEta(choice.FreeStepCount, freeEta)}, crosses {TrapWord(freeTraps)}";
            GatedSummary = $"Shortest — {StepsEta(choice.GatedStepCount, gatedEta)}, crosses {TrapWord(gatedTraps)}";
            TrapCaveat = freeTraps == 0
                ? "The shortest route crosses a trap the walker would try to disarm as it steps — "
                    + "a disarm can fail (no lockpicks, no party disarmer) and springs the trap, so the "
                    + "trap-free route is the safer bet."
                : $"The shortest route crosses {TrapWord(gatedTraps)}; the safer route can't avoid "
                    + $"{TrapWord(freeTraps)} (no way around it), but dodges the rest — and a step-time "
                    + "disarm can fail, so fewer traps is the safer bet.";
            RequirementSummary = string.Empty;
            TeleportCaveat = string.Empty;
            Footnote = "Click a route to preview it on the map, then Go to walk it. "
                + "The fewest-traps route is pre-selected — \"shortest\" is quicker but crosses more "
                + "traps (disarmed en route).";
            // Default to the safer route so a plain Go dodges what it can; the user
            // can still click the shortcut. Previewed on open via RaiseSelectionPreview.
            SelectedRoute = RouteChoiceResult.Free;
        }
        else
        {
            // A sole route (no gate-free detour) reaching the picker is either a
            // hazard-only crossing (carry / buy / use a counter) or a gate the
            // client can't auto-source — a door key, or an unflagged item. The
            // wording branches on which: a locked door isn't a "hazard you must
            // counter", it's a gate you clear by hand, so don't mislabel it.
            if (HasFreeRoute)
            {
                Heading = $"Two routes to {destinationLabel}";
                FreeSummary = $"Free route — {StepsEta(choice.FreeStepCount, freeEta)}, no items needed";
                GatedSummary = $"Direct — acquire then go — {StepsEta(choice.GatedStepCount, gatedEta)}";
                Footnote = "Click a route to preview it on the map, then Go to walk it. "
                    + "\"Acquire then go\" sources any missing gate items first; \"send it\" walks "
                    + "straight through without them.";
            }
            else if (soleHazardOnly)
            {
                Heading = $"Only route to {destinationLabel} crosses a hazard";
                FreeSummary = "No hazard-free route — every path there crosses a hazard you must counter";
                if (HazardObtain)
                {
                    GatedSummary = $"Obtain, then cross — {StepsEta(choice.GatedStepCount, gatedEta)}";
                    Footnote = "Click a route to preview it on the map, then Go to walk it. "
                        + $"Go fetches a counter ({hazardCounterSource}) then crosses; "
                        + "\"cross unprotected\" walks straight through and takes the damage.";
                }
                else
                {
                    GatedSummary = $"Route — {StepsEta(choice.GatedStepCount, gatedEta)}";
                    Footnote = "Click the route to preview it on the map, then Go to walk it. "
                        + "This is the only way there — Go walks it and stops at the hazard; "
                        + "carry, buy, or use a counter to cross.";
                }
            }
            else
            {
                Heading = $"Only route to {destinationLabel} is gated";
                FreeSummary = "No open detour — the only way there crosses a gate you must clear yourself";
                GatedSummary = $"Route — {StepsEta(choice.GatedStepCount, gatedEta)}";
                Footnote = "Click the route to preview it on the map, then Go to walk it. "
                    + "This is the only way there — Go walks it and stops at the gate; "
                    + "clear what's shown to continue.";
            }

            RequirementSummary = "Requires "
                + DescribeRequirements(
                    choice.Requirements, itemName, giveNameForItem, shopNameForItem, dropNameForItem);
            TeleportCaveat = string.Empty;
            TrapCaveat = string.Empty;
        }
    }

    // Re-fire the current selection's preview so a pre-selected route (trap-avoid
    // defaults to the trap-free line) draws on the map when the picker opens. The
    // prompt calls this after subscribing to PreviewRequested.
    public void RaiseSelectionPreview()
    {
        if (SelectedRoute is not null) PreviewRequested?.Invoke(SelectedRoute);
    }

    // "6 steps (~35s)" — the hop count with an approximate arrival ETA when one
    // is known (realm-aware per-hop travel plus a lair-fight dwell for each lair
    // on the route). Falls back to a bare step count when no ETA is supplied
    // (default TimeSpan.Zero — e.g. the empty free-route sentinel).
    private static string StepsEta(int n, TimeSpan eta)
    {
        string steps = n == 1 ? "1 step" : $"{n} steps";
        return eta > TimeSpan.Zero ? $"{steps} (~{RouteEtaEstimator.FormatCompact(eta)})" : steps;
    }

    // "1 trap" / "3 traps" — the trap count on a route, for the trap-avoid cards.
    private static string TrapWord(int n) => n == 1 ? "1 trap" : $"{n} traps";

    // "a raft (buy at General Store); the iron key; a waterskin (dropped by a
    // sand nomad)" — each requirement is one clause; a hazard's any-of counters
    // join with " or ". An Item / Ticket gate, or a SINGLE-counter hazard, whose
    // item the walk will auto-source gets a tail naming where: "(ask <giver>)"
    // when a deterministic textblock give hands it over free, else "(buy at
    // <shop>)" when a shop sells it, else "(dropped by <monster>)" when a flagged
    // dropper is reachable. Keys and any-of hazard counters never get a tail — a
    // key isn't sourced and an any-of hazard group posts no single auto-obtain
    // path-item need. The order mirrors the routers' precedence (free give >
    // shop buy > drop hunt) so the tail names exactly what the run will do —
    // the name helpers return null when a higher-priority router preempts.
    private static string DescribeRequirements(
        IReadOnlyList<RouteRequirement> reqs,
        Func<int, string?> itemName,
        Func<int, string?>? giveNameForItem,
        Func<int, string?>? shopNameForItem,
        Func<int, string?>? dropNameForItem)
    {
        IEnumerable<string> clauses = reqs.Select(r =>
        {
            string items = string.Join(" or ", r.ItemIds.Select(id => itemName(id) ?? $"item #{id}"));
            bool autoSourced = r.Kind is RouteRequirementKind.CarryItem or RouteRequirementKind.Ticket
                || (r.Kind is RouteRequirementKind.HazardProtection && r.ItemIds.Count == 1);
            if (!autoSourced || r.ItemIds.Count != 1)
                return items;
            if (giveNameForItem?.Invoke(r.ItemIds[0]) is { Length: > 0 } giver)
                return $"{items} (ask {giver})";
            if (shopNameForItem?.Invoke(r.ItemIds[0]) is { Length: > 0 } shop)
                return $"{items} (buy at {shop})";
            if (dropNameForItem?.Invoke(r.ItemIds[0]) is { Length: > 0 } monster)
                return $"{items} (dropped by {monster})";
            return items;
        });
        return string.Join("; ", clauses);
    }

    [RelayCommand]
    private void SelectFree()
    {
        if (!HasFreeRoute) return;   // no gate-free route to pick
        SelectedRoute = RouteChoiceResult.Free;
        PreviewRequested?.Invoke(RouteChoiceResult.Free);
    }

    [RelayCommand]
    private void SelectGated()
    {
        SelectedRoute = RouteChoiceResult.Gated;
        PreviewRequested?.Invoke(RouteChoiceResult.Gated);
    }

    [RelayCommand]
    private void SelectSendIt()
    {
        if (!ShowSendItCard) return;   // no send-it card in the sole-route case
        SelectedRoute = RouteChoiceResult.GatedNoAcquire;
        // Same physical route as the gated acquire choice — preview its line.
        PreviewRequested?.Invoke(RouteChoiceResult.GatedNoAcquire);
    }

    private bool CanGo => SelectedRoute is not null;

    [RelayCommand(CanExecute = nameof(CanGo))]
    private void Go() => CloseRequested?.Invoke(SelectedRoute);

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(null);
}
