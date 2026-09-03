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

    // Raised when the user clicks Details… for the selected route, so the prompt
    // can open the shared route-details browse window (the same one the CURRENT NAV
    // panel uses) for that route's polyline — the full step plan with per-room
    // monsters, hazards, and item gates. The picker has no map/graph knowledge, so
    // it just forwards which route is selected and lets the prompt resolve it.
    public event Action<RouteChoiceResult>? ShowDetailsRequested;

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

    // The "cross unprotected / send it" card. Two flavours share it:
    //   • item-gate two-route fork (HasFreeRoute): "send it" through the gates as-is
    //     rather than acquiring — a meaningful third choice only when a free detour
    //     also exists.
    //   • sole SURVIVABLE-damage hazard: "cross unprotected — take the damage",
    //     offered whether or not a counter can be sourced (it's the user's call to
    //     eat a river / heat crossing). NEVER offered for a GRAVE hazard (a drown /
    //     freeze death, a forced teleport) — a counter is the only way past those,
    //     so walking in unprotected is not a choice we hand the user.
    // A teleport / trap-avoid choice has no send-it split.
    public bool ShowSendItCard =>
        (HasFreeRoute || (_soleHazardOnly && _hazardSurvivable))
        && !IsTeleportChoice && !IsTrapAvoidChoice;

    // The primary route / "obtain then cross" card. Hidden only in the one case
    // where it would duplicate the cross-unprotected card: a sole SURVIVABLE hazard
    // with no sourceable counter, where "cross unprotected" is the sole action. Any
    // other route keeps it — an item / key gate ("walk to the gate and stop"), a
    // sourceable-counter hazard ("obtain, then cross"), a grave hazard ("walk to
    // the hazard and stop"), the item-gate fork, teleport, trap-avoid, and blocked.
    public bool ShowGatedCard => !_soleHazardOnly || HazardObtain || !_hazardSurvivable;

    // True when this is a sole hazard-only route the caller resolved an obtainable
    // counter for: Go fetches it then crosses (vs. "cross unprotected"). Drives the
    // obtain wording + the send-it card in the sole-hazard case.
    public bool HazardObtain { get; }

    // Set for a sole hazard route: whether all unprotected hazards crossed are
    // survivable damage (safe to offer "cross unprotected") + the sole-hazard flag
    // itself, both used by the card-visibility gates above.
    private readonly bool _soleHazardOnly;
    private readonly bool _hazardSurvivable;

    // The muted sub-line under the send-it card — reframed for the hazard flavour
    // (take the damage) vs the item-gate flavour (carry the gate items yourself).
    public string SendItDetail => (_soleHazardOnly && _hazardSurvivable)
        ? "Walks straight through the hazard and takes the damage — no counter fetched."
        : "Crosses the gates as-is — nothing acquired; you must already carry what's needed.";

    // Which route the user has selected to preview. Null until they click one —
    // Go stays disabled until then, forcing the click-to-preview-then-Go flow.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFreeSelected))]
    [NotifyPropertyChangedFor(nameof(IsGatedSelected))]
    [NotifyPropertyChangedFor(nameof(IsSendItSelected))]
    [NotifyCanExecuteChangedFor(nameof(GoCommand))]
    [NotifyCanExecuteChangedFor(nameof(ShowDetailsCommand))]
    private RouteChoiceResult? _selectedRoute;

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
        bool hazardSurvivable = false)
    {
        ArgumentNullException.ThrowIfNull(choice);
        ArgumentNullException.ThrowIfNull(itemName);

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

        // A sole hazard-only route: Go crosses the hazard, optionally fetching a
        // counter first (when one is sourceable), and — for a SURVIVABLE hazard —
        // "cross unprotected" is offered as the take-the-damage escape.
        bool soleHazardOnly = !HasFreeRoute && !IsTeleportChoice
            && choice.Requirements.All(r => r.Kind == RouteRequirementKind.HazardProtection);
        _soleHazardOnly = soleHazardOnly;
        _hazardSurvivable = hazardSurvivable;
        HazardObtain = soleHazardOnly && !string.IsNullOrEmpty(hazardCounterSource);

        // "Cross unprotected — take the damage" for any survivable sole hazard;
        // "Direct — send it" for the item-gate two-route fork.
        SendItSummary = (soleHazardOnly && hazardSurvivable)
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
                else if (hazardSurvivable)
                {
                    // No sourceable counter, but the hazard is survivable damage — the
                    // only card shown is "cross unprotected" (the Gated card is hidden
                    // by ShowGatedCard, so its summary is unused).
                    GatedSummary = string.Empty;
                    Footnote = "Click the route to preview it on the map, then Go to cross it. "
                        + "This is the only way there and there's no counter to fetch nearby — "
                        + "Go walks straight through and takes the damage; carry a counter yourself to avoid it.";
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

    // The Details… button lights up once a route is picked: it opens the shared
    // route-details browse window for that route (richer than the Show-steps
    // flyout — per-room monsters, hazards, and item gates, each linking its record).
    private bool CanShowDetails => SelectedRoute is not null;

    [RelayCommand(CanExecute = nameof(CanShowDetails))]
    private void ShowDetails()
    {
        if (SelectedRoute is { } r) ShowDetailsRequested?.Invoke(r);
    }

    private bool CanGo => SelectedRoute is not null;

    [RelayCommand(CanExecute = nameof(CanGo))]
    private void Go() => CloseRequested?.Invoke(SelectedRoute);

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(null);
}
