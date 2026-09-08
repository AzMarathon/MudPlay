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
    SearchEnRoute,  // walk toward the hazard searching each room; cross if a counter
                    // turns up (the floor collector grabs it), else halt at the edge.
    AvoidOverrideAlt, // the extra "route through rooms you marked Avoid" card offered
                      // alongside a hazard/gate route that respects the avoids — the
                      // avoid-crossing way needs no counter, so it's a real alternative.
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

    // Set once by the constructor (Heading) or by Populate (the rest). Populate is
    // a method (not the ctor), so these are settable rather than init-only; the ""
    // default keeps them non-null before Populate assigns the fork's wording.
    public string Heading { get; private set; } = "";
    public string FreeSummary { get; private set; } = "";
    public string GatedSummary { get; private set; } = "";
    public string SendItSummary { get; private set; } = "";
    public string RequirementSummary { get; private set; } = "";

    // The caveat shown under the shorter route in a teleport choice — names the
    // teleport's landing room and warns the shortcut can be lethal. Empty for the
    // item-gate choice, which shows RequirementSummary instead.
    public string TeleportCaveat { get; private set; } = "";

    // The caveat shown under the shorter route in a trap-avoid choice — that the
    // shortcut crosses a trap the walker disarms at step time, which can fail. Empty
    // for the other forks.
    public string TrapCaveat { get; private set; } = "";

    // The caveat shown under the override route in an avoid-override choice — that it
    // routes through room(s) the user marked "avoid". Empty for the other forks.
    public string AvoidCaveat { get; private set; } = "";

    // The sub-line under the shorter route's card: the item requirements for an
    // item-gate choice, the teleport caveat for a teleport choice, the trap caveat
    // for a trap-avoid choice, the avoided-rooms caveat for an avoid-override choice.
    public string GatedDetail =>
        IsTeleportChoice ? TeleportCaveat :
        IsTrapAvoidChoice ? TrapCaveat :
        IsAvoidOverrideChoice ? AvoidCaveat :
        RequirementSummary;

    // The footnote under the cards. One uniform line across every fork — the
    // per-case guidance lived in the card summaries anyway, and a plain "click a
    // route / Details…" line reads cleaner than a paragraph restating them (user
    // request, same spirit as the uniform heading).
    public string Footnote => PickFootnote;

    private const string PickFootnote =
        "Click a route to preview it on the map or click Details… to show routing information.";

    // True when this is the walk-vs-teleport fork: the shorter route takes a
    // teleport the walking route avoids. Reworders the cards (Walk / Teleport) and
    // hides the acquire/send-it split (there's nothing to acquire).
    public bool IsTeleportChoice { get; private set; }

    // True when this is the trap-avoid fork: the shortest route crosses a trap and a
    // trap-free route exists. A plain two-way choice (avoid / cross), no acquire /
    // send-it split. The trap-free route is pre-selected so the safe route is the
    // default (the user can still pick the shortcut).
    public bool IsTrapAvoidChoice { get; private set; }

    // True when this is the avoid-override fork: the destination is reachable only
    // through a room the user marked "avoid" (sole), or a much shorter route runs
    // through one (two-route). A plain two-way choice (respect avoids / override), no
    // acquire / send-it split. In the two-route case the avoid-honouring route is
    // pre-selected so respecting the user's own avoid is the default.
    public bool IsAvoidOverrideChoice { get; private set; }

    // False when there's no gate-free route — the direct (hazard-crossing) route
    // is the only way there. The Free card renders as a disabled "why you can't
    // just walk it" note; only the direct route is selectable.
    public bool HasFreeRoute { get; private set; }

    // The "cross unprotected / send it" card. Two flavours share it:
    //   • item-gate two-route fork (HasFreeRoute): "send it" through the gates as-is
    //     rather than acquiring — a meaningful third choice only when a free detour
    //     also exists.
    //   • SURVIVABLE-damage hazard crossing (sole OR mixed): "cross unprotected —
    //     take the damage", offered whether or not a counter can be sourced (it's the
    //     user's call to eat a river / heat crossing). NEVER offered for a GRAVE
    //     hazard (a drown / freeze death, a forced teleport) — a counter is the only
    //     way past those, so walking in unprotected is not a choice we hand the user.
    // A teleport / trap-avoid choice has no send-it split.
    public bool ShowSendItCard =>
        (HasFreeRoute || _crossesSurvivableHazard)
        && !IsTeleportChoice && !IsTrapAvoidChoice && !IsAvoidOverrideChoice;

    // The primary route / "obtain then cross" / "walk to the hazard and stop" card.
    // Hidden only in the one case where it would duplicate the cross-unprotected
    // card: a SOLE survivable hazard with no sourceable counter, where "cross
    // unprotected" is the sole action. A MIXED route (hazard + a hard gate) always
    // keeps it (obtain-then-cross, or walk to the hazard's edge and stop); so does an
    // item / key gate, a grave hazard, the item-gate fork, teleport, trap-avoid, and
    // blocked.
    public bool ShowGatedCard => !_crossesSurvivableHazard || _mixedHazard || HazardObtain;

    // True when the caller resolved an obtainable counter for the hazard: Go fetches
    // it then crosses (vs. "cross unprotected"). Drives the obtain wording + the
    // send-it card, for both a sole hazard and a mixed (hazard + hard gate) route.
    public bool HazardObtain { get; private set; }

    // The route crosses a survivable hazard the player can't currently pass, whether
    // that's the only gate (_soleHazardOnly) or there's also a hard gate past it
    // (_mixedHazard). Both gate the card-visibility rules above.
    private bool _soleHazardOnly;
    private bool _crossesSurvivableHazard;
    private bool _mixedHazard;
    // The route needs a hazard counter the player lacks — so "search en route" is a
    // valid alternative (find one free by searching each room on the way).
    private bool _hazardCounterNeeded;

    // The "search en route" card: walk toward the hazard searching each room, and
    // cross if a counter turns up (the obtain pipeline's floor collector grabs it),
    // else halt at the edge. Offered whenever a hazard counter is needed — a way to
    // source it free instead of (or before) buying / detouring. Not on a teleport /
    // trap-avoid / avoid-override / blocked fork.
    public bool ShowSearchCard =>
        _hazardCounterNeeded && !IsTeleportChoice && !IsTrapAvoidChoice && !IsAvoidOverrideChoice;

    public string SearchSummary { get; private set; } = "";
    public string SearchDetail =>
        "Searches each room on the way; if a counter turns up it's grabbed and you cross, "
        + "otherwise you stop at the hazard's edge. Turn up nothing, lose nothing.";

    // The muted sub-line under the send-it card — reframed for the hazard flavour
    // (take the damage) vs the item-gate flavour (carry the gate items yourself).
    public string SendItDetail => _crossesSurvivableHazard
        ? "Walks straight through the hazard and takes the damage — no counter fetched."
        : "Crosses the gates as-is — nothing acquired; you must already carry what's needed.";

    // The extra "route through your avoided rooms" card, offered beside a hazard/gate
    // route that respects the avoids — the avoid-crossing way needs no counter, so
    // it's a genuine alternative. Path kept for preview/commit; summary built in ctor.
    private IReadOnlyList<RoomKey>? _avoidAltPath;
    public bool ShowAvoidAltCard => _avoidAltPath is { Count: > 0 };
    public string AvoidAltSummary { get; private set; } = "";
    public string AvoidAltDetail =>
        "Skips the counter and plows through rooms you marked Avoid. Your avoid list stays "
        + "set — only this one walk crosses them.";
    public bool IsAvoidAltSelected => SelectedRoute == RouteChoiceResult.AvoidOverrideAlt;

    // The Free card is a real, selectable route only when a gate-free route exists.
    // On a sole route it used to render as a disabled "why you can't walk it" note;
    // that's now dropped (the heading + option cards carry it), so hide it entirely
    // when there's no free route to pick.
    public bool ShowFreeCard => HasFreeRoute;

    // Danger tint (a soft-red card) on the routes that skip the safe prep: the
    // avoid-crossing cards (the AvoidOverride fork's own override card, and the extra
    // avoid-alt card) and the "cross unprotected / send it" card. Makes the risky
    // option read as risky at a glance; the green/amber selection still layers on top.
    public bool GatedIsDanger => IsAvoidOverrideChoice;   // Gated card = "route through avoided"
    public bool SendItIsDanger => true;                   // "cross unprotected" / "send it direct"
    public bool AvoidAltIsDanger => true;                 // always crosses avoided rooms

    // True while the picker is up but its options are still being computed — the
    // route planning (BFS) is running on a background thread. The window shows the
    // From/To heading and a centered "Calculating…" line with no cards; when
    // planning returns, Populate fills the cards and flips this false. Only the
    // off-thread (idle) path opens in this state; if a walk is already in progress
    // planning stays on the UI thread and the picker is built fully before showing.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowOptions))]
    private bool _isCalculating;

    public bool ShowOptions => !IsCalculating;

    // Which route the user has selected to preview. Null until they click one —
    // Go stays disabled until then, forcing the click-to-preview-then-Go flow.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFreeSelected))]
    [NotifyPropertyChangedFor(nameof(IsGatedSelected))]
    [NotifyPropertyChangedFor(nameof(IsSendItSelected))]
    [NotifyPropertyChangedFor(nameof(IsSearchSelected))]
    [NotifyPropertyChangedFor(nameof(IsAvoidAltSelected))]
    [NotifyCanExecuteChangedFor(nameof(GoCommand))]
    [NotifyCanExecuteChangedFor(nameof(ShowDetailsCommand))]
    private RouteChoiceResult? _selectedRoute;

    public bool IsFreeSelected => SelectedRoute == RouteChoiceResult.Free;
    public bool IsGatedSelected => SelectedRoute == RouteChoiceResult.Gated;
    public bool IsSendItSelected => SelectedRoute == RouteChoiceResult.GatedNoAcquire;
    public bool IsSearchSelected => SelectedRoute == RouteChoiceResult.SearchEnRoute;

    // Full construction: compute the heading from the labels, then Populate the card
    // data from the resolved choice straight away. Used by every fork whose options
    // are ready at construction time (all but the buy path).
    public RouteChoiceDialogViewModel(
        RouteChoice choice,
        string destinationLabel,
        Func<int, string?> itemName,
        Func<int, string?>? giveNameForItem = null,
        Func<int, string?>? shopBuyPhraseForItem = null,
        Func<int, string?>? dropNameForItem = null,
        TimeSpan freeEta = default,
        TimeSpan gatedEta = default,
        string? hazardCounterSource = null,
        bool hazardSurvivable = false,
        Func<RouteRequirement, (int ItemId, string Source)?>? resolvedHazardCounter = null,
        // Pick-time economy read for a buy: where the money is (own bank / party) or
        // that a party member holds the item. Appended under the obtain card so a
        // broke leader sees whether it's payable and from where before committing.
        string? economyNote = null,
        // The room the walk starts from, for the "From X to Y" title. Optional (defaults
        // empty → a plain "Route to Y") so the picker's many unit tests, which don't
        // exercise the heading, construct the VM without it.
        string sourceLabel = "")
    {
        Heading = ComposeHeading(destinationLabel, sourceLabel);
        Populate(
            choice, itemName, giveNameForItem, shopBuyPhraseForItem,
            dropNameForItem, freeEta, gatedEta, hazardCounterSource, hazardSurvivable,
            resolvedHazardCounter, economyNote);
    }

    // "Calculating…" construction: the idle (off-thread) path opens the picker with
    // only the From/To heading while route planning runs on a background thread, then
    // calls Populate once it returns. Leaves every card empty and IsCalculating true,
    // so ShowOptions hides the (empty) card list until Populate flips it.
    public RouteChoiceDialogViewModel(string destinationLabel, string sourceLabel)
    {
        Heading = ComposeHeading(destinationLabel, sourceLabel);
        IsCalculating = true;
    }

    // One uniform title across every fork — the per-case wording lived in the card
    // summaries anyway, and a plain "From X to Y" over the option list reads cleaner
    // than a heading that restated the sole card (user request).
    private static string ComposeHeading(string destinationLabel, string sourceLabel) =>
        string.IsNullOrEmpty(sourceLabel)
            ? $"Route to {destinationLabel}"
            : $"From {sourceLabel} to {destinationLabel}";

    // Fill the card data from the resolved choice + the caller's name/economy helpers,
    // then reveal the options (IsCalculating → false, refreshing every card binding).
    // Called synchronously from the full constructor, or from the idle path once its
    // off-thread planning returns and the "Calculating…" window is already up.
    public void Populate(
        RouteChoice choice,
        Func<int, string?> itemName,
        Func<int, string?>? giveNameForItem = null,
        Func<int, string?>? shopBuyPhraseForItem = null,
        Func<int, string?>? dropNameForItem = null,
        TimeSpan freeEta = default,
        TimeSpan gatedEta = default,
        string? hazardCounterSource = null,
        bool hazardSurvivable = false,
        Func<RouteRequirement, (int ItemId, string Source)?>? resolvedHazardCounter = null,
        string? economyNote = null)
    {
        ArgumentNullException.ThrowIfNull(choice);
        ArgumentNullException.ThrowIfNull(itemName);

        IsTeleportChoice = choice.Kind == RouteChoiceKind.Teleport;
        IsTrapAvoidChoice = choice.Kind == RouteChoiceKind.TrapAvoid;
        IsAvoidOverrideChoice = choice.Kind == RouteChoiceKind.AvoidOverride;
        HasFreeRoute = choice.HasFreeRoute;

        // The extra avoid-crossing card (offered beside a hazard/gate route that
        // respects the avoids): the ignore-avoids route needs no counter.
        _avoidAltPath = choice.AvoidAlternativePath;
        int avoidAltCount = choice.AvoidAlternativeCount;
        AvoidAltSummary = _avoidAltPath is { Count: > 0 }
            ? $"Route through {(avoidAltCount == 1 ? "1 room" : $"{avoidAltCount} rooms")} you marked Avoid — "
                + $"{StepsEta(Math.Max(0, _avoidAltPath.Count - 1), default)}, no counter needed"
            : string.Empty;

        // A fully-blocked route: no way through at all, but the destination is
        // physically reachable up to an obstacle. Offer to walk as far as possible
        // and stop at the block, naming it so the user knows what to clear by hand.
        if (choice.Kind == RouteChoiceKind.Blocked)
        {
            HazardObtain = false;
            _hazardCounterNeeded = false;
            SearchSummary = string.Empty;
            string reason = choice.BlockedReason ?? "a blocked exit";
            FreeSummary = $"No open route — blocked by {reason}";
            GatedSummary = $"Run to the blocked room anyway — {StepsEta(choice.GatedStepCount, gatedEta)}";
            SendItSummary = string.Empty;
            RequirementSummary = string.Empty;
            TeleportCaveat = string.Empty;
            TrapCaveat = string.Empty;
            AvoidCaveat = string.Empty;
            return;
        }

        // A route that crosses a survivable hazard: Go crosses it, optionally fetching
        // a counter first (when sourceable), and "cross unprotected" is the take-the-
        // damage escape. `hazardSurvivable` (the caller's crossesSurvivableHazard) is
        // true for both a SOLE hazard route and a MIXED one (hazard + a hard gate past
        // it); the mixed case also stops at that gate.
        bool soleHazardOnly = !HasFreeRoute && !IsTeleportChoice && !IsAvoidOverrideChoice
            && choice.Requirements.Count > 0
            && choice.Requirements.All(r => r.Kind == RouteRequirementKind.HazardProtection);
        _soleHazardOnly = soleHazardOnly;
        _crossesSurvivableHazard = hazardSurvivable;
        _mixedHazard = hazardSurvivable && !soleHazardOnly;
        _hazardCounterNeeded = choice.Requirements.Any(r => r.Kind == RouteRequirementKind.HazardProtection);
        SearchSummary = _hazardCounterNeeded
            ? $"Search en route — {StepsEta(choice.GatedStepCount, gatedEta)}"
            : string.Empty;
        // A resolved counter source means "obtain then cross" is offerable — for ANY
        // hazard (a grave hazard's only safe crossing is obtaining the counter).
        HazardObtain = !string.IsNullOrEmpty(hazardCounterSource);

        // "Cross unprotected — take the damage" for any survivable-hazard crossing;
        // "Direct — send it" for the item-gate two-route fork.
        SendItSummary = hazardSurvivable
            ? $"Cross unprotected — take the damage — {StepsEta(choice.GatedStepCount, gatedEta)}"
            : $"Direct — send it — {StepsEta(choice.GatedStepCount, gatedEta)}";

        if (IsTeleportChoice)
        {
            FreeSummary = $"Walk it — {StepsEta(choice.FreeStepCount, freeEta)}, no teleport";
            GatedSummary = $"Teleport — {StepsEta(choice.GatedStepCount, gatedEta)} — much shorter";
            TeleportCaveat =
                $"Teleports via {choice.TeleportLanding ?? "an unknown room"} — a teleport can drop "
                + "you somewhere deadly (a damaging plane, water with no boat). Whether you survive "
                + "depends on your character, so the call is yours.";
            RequirementSummary = string.Empty;
            TrapCaveat = string.Empty;
            AvoidCaveat = string.Empty;
        }
        else if (IsTrapAvoidChoice)
        {
            int freeTraps = choice.FreeTrapCount;
            int gatedTraps = choice.GatedTrapCount;
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
            AvoidCaveat = string.Empty;
            // Default to the safer route so a plain Go dodges what it can; the user
            // can still click the shortcut. Previewed on open via RaiseSelectionPreview.
            SelectedRoute = RouteChoiceResult.Free;
        }
        else if (IsAvoidOverrideChoice)
        {
            string avoidedWord = choice.AvoidedRoomCount == 1
                ? "1 room you marked Avoid"
                : $"{choice.AvoidedRoomCount} rooms you marked Avoid";
            if (HasFreeRoute)
            {
                // TWO-ROUTE: an avoid-honouring route also exists, but a shorter one
                // runs through avoided rooms.
                FreeSummary = $"Respect your avoids — {StepsEta(choice.FreeStepCount, freeEta)}";
                GatedSummary = $"Shorter — through {avoidedWord} — {StepsEta(choice.GatedStepCount, gatedEta)}";
                // Default to respecting the user's own avoid list; they can still override.
                SelectedRoute = RouteChoiceResult.Free;
            }
            else
            {
                // SOLE: the only way there crosses an avoided room.
                FreeSummary = $"No route that respects your avoids — every path there crosses {avoidedWord}";
                GatedSummary = $"Route through {avoidedWord} — {StepsEta(choice.GatedStepCount, gatedEta)}";
            }
            AvoidCaveat = $"Routes through {avoidedWord}. Your avoid list stays set — only this "
                + "walk crosses them; unmark the room(s) if you want it gone for good.";
            RequirementSummary = string.Empty;
            TeleportCaveat = string.Empty;
            TrapCaveat = string.Empty;
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
                FreeSummary = $"Free route — {StepsEta(choice.FreeStepCount, freeEta)}, no items needed";
                GatedSummary = $"Direct — acquire then go — {StepsEta(choice.GatedStepCount, gatedEta)}";
            }
            else if (soleHazardOnly)
            {
                FreeSummary = "No hazard-free route — every path there crosses a hazard you must counter";
                if (HazardObtain)
                {
                    GatedSummary = $"Obtain, then cross — {StepsEta(choice.GatedStepCount, gatedEta)}";
                }
                else if (hazardSurvivable)
                {
                    // No sourceable counter, but the hazard is survivable damage — the
                    // only card shown is "cross unprotected" (the Gated card is hidden
                    // by ShowGatedCard, so its summary is unused).
                    GatedSummary = string.Empty;
                }
                else
                {
                    GatedSummary = $"Route — {StepsEta(choice.GatedStepCount, gatedEta)}";
                }
            }
            else if (_mixedHazard)
            {
                // The route crosses a survivable hazard AND a hard gate past it (a
                // keyed door): offer to obtain-then-cross / cross-unprotected, but
                // note the walk still stops at the gate you must clear yourself.
                FreeSummary = "No hazard-free route — every path there crosses a hazard, "
                    + "then a gate you must clear yourself";
                if (HazardObtain)
                {
                    GatedSummary = $"Obtain, then cross — {StepsEta(choice.GatedStepCount, gatedEta)}";
                }
                else
                {
                    GatedSummary = $"Walk to the hazard and stop — {StepsEta(choice.GatedStepCount, gatedEta)}";
                }
            }
            else
            {
                FreeSummary = "No open detour — the only way there crosses a gate you must clear yourself";
                GatedSummary = $"Route — {StepsEta(choice.GatedStepCount, gatedEta)}";
            }

            RequirementSummary = "Requires "
                + DescribeRequirements(
                    choice.Requirements, itemName, giveNameForItem, shopBuyPhraseForItem,
                    dropNameForItem, resolvedHazardCounter)
                + (string.IsNullOrEmpty(economyNote) ? "" : $" — {economyNote}");
            TeleportCaveat = string.Empty;
            TrapCaveat = string.Empty;
            AvoidCaveat = string.Empty;
        }

        // Options are ready: drop the "Calculating…" state and refresh every card
        // binding at once (a blank name signals "all properties changed"). The
        // full-constructor path runs this before the window exists — a harmless no-op
        // there. (The Blocked branch returns above; it's only ever built via the full
        // constructor, so it never lingers in the calculating state.)
        IsCalculating = false;
        OnPropertyChanged(string.Empty);
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
        Func<int, string?>? shopBuyPhraseForItem,
        Func<int, string?>? dropNameForItem,
        Func<RouteRequirement, (int ItemId, string Source)?>? resolvedHazardCounter = null)
    {
        IEnumerable<string> clauses = reqs.Select(r =>
        {
            // A resolved hazard counter names the SPECIFIC item the run will obtain +
            // how ("log raft (buy at Pier)"), instead of the whole any-of set — the
            // picker already chose the cheapest reachable one.
            if (r.Kind is RouteRequirementKind.HazardProtection
                && resolvedHazardCounter?.Invoke(r) is { } rc)
                return $"{itemName(rc.ItemId) ?? $"item #{rc.ItemId}"} ({rc.Source})";

            string items = string.Join(" or ", r.ItemIds.Select(id => itemName(id) ?? $"item #{id}"));
            bool autoSourced = r.Kind is RouteRequirementKind.CarryItem or RouteRequirementKind.Ticket
                || (r.Kind is RouteRequirementKind.HazardProtection && r.ItemIds.Count == 1);
            if (!autoSourced || r.ItemIds.Count != 1)
                return items;
            if (giveNameForItem?.Invoke(r.ItemIds[0]) is { Length: > 0 } giver)
                return $"{items} (ask {giver})";
            // The shop helper returns the full "buy at <shop>" clause (with any
            // withdraw-at-bank / shortfall note already folded in), so wrap it as-is.
            if (shopBuyPhraseForItem?.Invoke(r.ItemIds[0]) is { Length: > 0 } buyPhrase)
                return $"{items} ({buyPhrase})";
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

    [RelayCommand]
    private void SelectSearch()
    {
        if (!ShowSearchCard) return;
        SelectedRoute = RouteChoiceResult.SearchEnRoute;
        // Same physical route as the gated crossing — preview its line.
        PreviewRequested?.Invoke(RouteChoiceResult.SearchEnRoute);
    }

    [RelayCommand]
    private void SelectAvoidAlt()
    {
        if (!ShowAvoidAltCard) return;
        SelectedRoute = RouteChoiceResult.AvoidOverrideAlt;
        PreviewRequested?.Invoke(RouteChoiceResult.AvoidOverrideAlt);
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

    // Dismiss the picker without a pick — used when it was opened in the
    // "Calculating…" state up front but off-thread planning found no fork to show (a
    // plain / auto-obtain walk), so there are no cards to reveal. Same as a Cancel
    // (walk nothing here; the caller commits the walk itself).
    public void Close() => CloseRequested?.Invoke(null);
}
