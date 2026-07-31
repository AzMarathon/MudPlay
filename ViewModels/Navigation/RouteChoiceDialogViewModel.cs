using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FujinTerm.Game.Map;
using FujinTerm.Services;

namespace FujinTerm.ViewModels.Navigation;

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

    // The sub-line under the shorter route's card: the item requirements for an
    // item-gate choice, the teleport caveat for a teleport choice.
    public string GatedDetail => IsTeleportChoice ? TeleportCaveat : RequirementSummary;

    // The footnote under the cards, explaining the fork's options — different
    // wording for the teleport choice (no acquire / send-it split there).
    public string Footnote { get; }

    // True when this is the walk-vs-teleport fork: the shorter route takes a
    // teleport the walking route avoids. Reworders the cards (Walk / Teleport) and
    // hides the acquire/send-it split (there's nothing to acquire).
    public bool IsTeleportChoice { get; }

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
    public bool ShowSendItCard => HasFreeRoute && !IsTeleportChoice;

    // Which route the user has selected to preview. Null until they click one —
    // Go stays disabled until then, forcing the click-to-preview-then-Go flow.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFreeSelected))]
    [NotifyPropertyChangedFor(nameof(IsGatedSelected))]
    [NotifyPropertyChangedFor(nameof(IsSendItSelected))]
    [NotifyCanExecuteChangedFor(nameof(GoCommand))]
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
        TimeSpan gatedEta = default)
    {
        ArgumentNullException.ThrowIfNull(choice);
        ArgumentNullException.ThrowIfNull(itemName);

        IsTeleportChoice = choice.Kind == RouteChoiceKind.Teleport;
        HasFreeRoute = choice.HasFreeRoute;
        SendItSummary = $"Direct — send it — {StepsEta(choice.GatedStepCount, gatedEta)}";

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
            Footnote = "Click a route to preview it on the map, then Go to walk it. "
                + "The teleport is much shorter but can be lethal — take the walk if you're unsure.";
        }
        else
        {
            // A sole route (no gate-free detour) reaching the picker is either a
            // hazard-only crossing (carry / buy / use a counter) or a gate the
            // client can't auto-source — a door key, or an unflagged item. The
            // wording branches on which: a locked door isn't a "hazard you must
            // counter", it's a gate you clear by hand, so don't mislabel it.
            bool soleHazardOnly = !HasFreeRoute
                && choice.Requirements.All(r => r.Kind == RouteRequirementKind.HazardProtection);

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
                GatedSummary = $"Route — {StepsEta(choice.GatedStepCount, gatedEta)}";
                Footnote = "Click the route to preview it on the map, then Go to walk it. "
                    + "This is the only way there — Go walks it and stops at the hazard; "
                    + "carry, buy, or use a counter to cross.";
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
        }
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
