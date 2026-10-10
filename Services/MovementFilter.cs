using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using MudPlay.Game.Cash;
using MudPlay.Game.Map;
using MudPlay.Game.Recovery;
using MudPlay.Models.Profile;

namespace MudPlay.Services;

// Per-character avoided + stash room set. Implements IRoomFilter for BFS —
// the walker / loop runner / auto-lair scheduler all read IsAvoided at
// planning time so the avoided rooms are dropped from candidate paths.
//
// Scope: Char-only. Lives on CharacterProfile.AvoidedRooms +
// CharacterProfile.StashRooms, not in SettingsResolver — the avoided set is a
// personal no-go list, not a per-realm or per-BBS rule.
//
// Wiring: AppServices subscribes the filter to
// ProfileService.ProfileLoaded + ProfileService.ProfileClosed. Mutating
// methods (MarkAvoided, UnmarkAvoided, MarkStash, UnmarkStash) update the
// in-memory set, mirror the change back into the loaded profile, persist via
// ProfileService.Save, and fire AvoidedChanged / StashChanged so the map UI
// can recolour the affected cells.
public sealed class MovementFilter : IRoomFilter
{
    private readonly ProfileService _profile;
    private readonly LogService? _log;
    private readonly HashSet<RoomKey> _avoided = new();
    private readonly HashSet<RoomKey> _stash = new();

    // Supplies the player's current character level for Form-A exit
    // level-gate evaluation, or null when the level isn't known yet (no stat
    // screen parsed). Wired by AppServices to StatParser. When null,
    // IsExitBlocked never blocks — we don't refuse a walk on a gate we can't
    // yet evaluate.
    public Func<int?>? LevelProvider { get; set; }

    // Supplies the party's most-constraining (Low, High) level window when
    // this character is leading a party, or null when solo, not leading, or
    // nobody's level is known yet. Wired by AppServices to
    // Game.Remote.PartyLevelTracker. When non-null it takes precedence over
    // LevelProvider in IsExitBlocked: BFS routes the party around a gate that
    // would leave a member behind, instead of walking the leader through it.
    // The bounds already fold in the leader's own level, so the party branch
    // never waves the leader through a gate the leader can't cross either.
    public Func<(int Low, int High)?>? PartyLevelBoundsProvider { get; set; }

    // Supplies the player's current on-hand wealth in copper farthings (the
    // consolidated `Wealth:` value), or null when it isn't known yet (no
    // inventory parsed). Wired by AppServices to the live currency snapshot.
    // A (Toll: N) exit needs N*100 copper-value carried to cross (confirmed
    // mechanic — the game phrases the bar as "N gold crowns" but any coin mix
    // totalling that value passes), so IsExitBlocked routes around a toll we
    // can't afford. When null we don't gate — same rule as an unknown level.
    public Func<long?>? WealthProvider { get; set; }

    // Supplies the party's minimum on-hand wealth (copper) when this character
    // is leading a party, or null when solo, not leading, or our own wallet is
    // unknown. Wired by AppServices to
    // Game.Remote.PartyWealthTracker. When non-null it takes precedence over
    // WealthProvider in IsTollGateBlocked: BFS routes the party around a toll a
    // member can't afford, instead of walking the leader through and stranding
    // them at the gate. A toll is per-crosser, so this is a genuine second gate
    // over the self-only wallet check. Demand-driven — the tracker only probes
    // @wealth when this is invoked, i.e. only while BFS evaluates a toll exit,
    // and treats a member who hasn't reported fresh wealth as unaffordable.
    public Func<long?>? PartyWealthProvider { get; set; }

    // Supplies the player's own class Number (Classes.Number, 1-15), or null
    // when the class isn't parsed yet. Wired by AppServices to the live
    // PlayerStats.Class resolved through the Classes table. A "(Class: N OK)"
    // exit only admits class N (confirmed single-class gate, not a bitmask), so
    // IsExitBlocked routes around a class hall we can't enter. Evaluated against
    // the controlling character's own class only — a party's members may be
    // different classes, but class halls are single-class by design and a party
    // doesn't loop through one together, so there's no party-wide branch here.
    // When null we don't gate — same rule as an unknown level.
    public Func<int?>? ClassNumberProvider { get; set; }

    // The crosser's race Number (Races table), or null while it isn't known. Feeds
    // the "(Race: N OK)" gate the way ClassNumberProvider feeds the class gate.
    public Func<int?>? RaceNumberProvider { get; set; }

    // Supplies the numeric alignment of every crosser — the controlling character
    // plus, when leading a party, each follower — for "(Alignment: X to Y)" exit
    // gates. Each entry is the member's alignment value, or null when we don't know
    // it (never who'd / looked). Wired by AppServices to PlayerDatabase alignments
    // resolved through the confirmed band ladder. Alignment gates are whole-party
    // (the game stops the party if ANY member is excluded), so IsExitBlocked routes
    // around a gate a KNOWN member can't cross. An UNKNOWN member does NOT block
    // routing here — per user direction the walker walks up to the gate and halts
    // there (the alignment-refusal revert) rather than detouring on a guess. Null
    // provider → never gate, same rule as an unknown level.
    public Func<IReadOnlyList<int?>>? PartyAlignmentsProvider { get; set; }

    // Fires the party @wealth round-trip. Wired by AppServices to
    // PartyWealthTracker.Probe. Invoked from WarmForRoute (only when the
    // tolls-permitted shortest route actually crosses a toll, so an off-path
    // toll edge inside the BFS frontier never triggers a poll) and from
    // WarmForBoat (when a chosen route boards a fare-gated boat).
    public Action? WealthWarmProbe { get; set; }

    // Fires the party @level freshness round-trip. Wired by AppServices to
    // PartyLevelTracker.WarmStaleLevels. Invoked only from WarmForRoute, and
    // only when the levels-permitted shortest route actually crosses a level
    // gate — so an off-path level edge inside the BFS frontier never triggers a
    // probe. The tracker itself decides whether any member is stale/unknown
    // enough to warrant the round-trip; this just signals "a level gate is on
    // the planned route".
    public Action? LevelWarmProbe { get; set; }

    // ----- Acquirable-gate providers (item / ticket / key-door / hazard) ----
    // These four gates share a trait the level / class / toll gates don't: the
    // thing that unblocks them (a raft, a ticket, a door key, a hazard-counter
    // item) can be picked up, so BFS prefers a safe/free route by default but
    // the route picker can still plan through them (SuspendAcquirableGates).

    // Whether the crosser currently holds item id (carried or worn). Wired by
    // AppServices to IsItemCarried. Only consulted while inventory is known
    // (see InventoryReadyProbe) — an unparsed inventory never refuses a walk.
    public Func<int, bool>? ItemCarriedProbe { get; set; }

    // Whether the party is short of a gate item the leader holds: an (Item: N) or
    // (Ticket: N) exit carries across only the member holding the item, so one
    // copy in the leader's pack doesn't open the gate for the followers. Wired by
    // AppServices to what the last party count found; unset or false leaves the
    // gate to the leader's own pack, as for a solo crosser.
    public Func<int, bool>? PartyShortOfItemProbe { get; set; }

    // Whether the crosser holds an item gate's item as far as crossing goes.
    // perMember is an Item / Ticket exit, where a party short of copies doesn't.
    public bool HoldsGateItem(int itemId, bool perMember) =>
        ItemCarriedProbe?.Invoke(itemId) == true
        && !(perMember && PartyShortOfItemProbe?.Invoke(itemId) == true);

    // True once an inventory dump has parsed. Until then the item / hazard
    // gates stand down — same "don't refuse on what we can't evaluate" rule as
    // an unknown level. Wired by AppServices to Inventory.IsLoaded.
    public Func<bool>? InventoryReadyProbe { get; set; }

    // Crosser's Strength / Picklocks for locked-door achievability. null until
    // stats parse → a locked door is left to the traversal-time door FSM
    // rather than routed around on an unknown build.
    public Func<int?>? StrengthProvider { get; set; }
    public Func<int?>? PicklocksProvider { get; set; }

    // The active set's highest reachable Strength (MaxStrengthIndex) — the
    // bash ceiling DoorPolicy.IsAchievable uses to decide whether a door is
    // bashable by anyone. Wired by AppServices to MaxStrength; falls back to
    // DoorPolicy's own threshold when unset.
    public Func<int>? MaxBashableStrengthProvider { get; set; }

    // Resolves a room key to its cast-on-enter spell (Room.Spell), 0 when the
    // room is benign or not in the live graph. Wired by AppServices to
    // RoomGraph. Feeds hazard-room entry blocking.
    public Func<RoomKey, int>? RoomEntrySpellProbe { get; set; }

    // Room-entry hazard index — maps a harmful cast-on-enter spell to the
    // item(s) that make the room survivable. null disables hazard avoidance.
    public RoomHazardIndex? Hazards { get; set; }

    // While set, IsTollGateBlocked stands down (treats every toll as
    // crossable). WarmForRoute flips this on to plan the route the party WOULD
    // take if every toll were affordable, so it can tell whether a toll is
    // genuinely on the path before deciding to poll. Single-threaded planning
    // use — set and cleared inside WarmForRoute's own try/finally.
    private bool _tollGateSuspended;

    // While set, the party branch of IsLevelGateBlocked stands down (the
    // self-only level check still applies). WarmForRoute flips this on to plan
    // the route the party WOULD take if every level gate were passable, so it
    // can tell whether a level gate is genuinely on the path before deciding to
    // fire the @level freshness probe. Single-threaded planning use — set and
    // cleared inside WarmForRoute's own try/finally.
    private bool _partyLevelGateSuspended;

    // While set, the four acquirable gates (item / ticket / key-door / hazard)
    // stand down so a planning pass can compute the route the crosser WOULD
    // take with every gate item in hand — the "gated" alternative the route
    // picker compares against the free route. Level / class / toll gates stay
    // active (those aren't acquired on demand). Single-threaded planning use;
    // set and cleared through SuspendAcquirableGates' disposable scope.
    private bool _acquirableGateSuspended;

    // While gates are suspended, the gates for these item ids are kept evaluated
    // normally (null/empty = none), so a planning pass can ask "is the destination
    // still reachable if these item gates stay closed?" — the test that separates a
    // genuinely required gate from an optional shortcut an item merely unlocks.
    // Set/cleared through SuspendAcquirableGatesExcept's disposable scope.
    private IReadOnlyCollection<int>? _keepClosedGateItems;

    // While gates are suspended, hazard rooms stay closed unless countered: by what
    // is carried, or by what the walk will obtain for that room (HazardProvisionProbe).
    // Set/cleared through SuspendAcquirableGatesButUncounteredHazards' scope.
    private bool _keepUncounteredHazards;

    // While gates are suspended, a hazard room stays closed when no item makes it
    // safe to route through (HazardCounterProtects): the pass asks what could be
    // walked with every gate item in hand, and having one opens nothing there.
    // Set/cleared through SuspendAcquirableGatesButUnprotectableHazards' scope.
    private bool _keepUnprotectableHazards;

    // The hazard rooms that open anyway while uncountered hazards are kept closed:
    // the ones on a route card's path, which the user saw and agreed to walk into.
    // Set/cleared through SuspendAcquirableGatesExcept's scope.
    private IReadOnlyCollection<RoomKey>? _openHazardRooms;

    // The counter items a walk will obtain on its own for entering a hazard room.
    // Wired by AppServices to the same list the walk-start announce posts needs for.
    public Func<RoomKey, IReadOnlyList<int>>? HazardProvisionProbe { get; set; }

    // Read-only snapshot of the currently-avoided room keys.
    public IReadOnlyCollection<RoomKey> Avoided => _avoided;

    // Read-only snapshot of the currently-flagged stash-room keys.
    public IReadOnlyCollection<RoomKey> Stash => _stash;

    // Fires after every mutation to the avoided set, including profile reload.
    public event Action? AvoidedChanged;

    // Fires after every mutation to the stash set, including profile reload.
    public event Action? StashChanged;

    public MovementFilter(ProfileService profile, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _profile = profile;
        _log = log;

        _profile.ProfileLoaded  += OnProfileLoaded;
        _profile.ProfileClosed  += OnProfileClosed;

        // Pick up the already-loaded profile, if any (AppServices
        // wires this filter after ProfileService.LoadDefaultProfile fires).
        if (_profile.Current is { } current) OnProfileLoaded(current);
    }

    public bool IsAvoided(RoomKey key) => _avoided.Contains(key);

    // An exit is non-traversable for planning when its level window excludes
    // the crosser, it's a toll the crosser can't afford, it's a class gate for
    // a class we aren't, it needs an item / ticket / door-key we can't produce,
    // it's a plain door the crosser can't pick or bash, or it leads into a room
    // whose cast-on-enter hazard we can't survive. The gate kinds are
    // independent (a toll exit carries Hint=Toll; a level or class gate is a
    // plain cardinal carrying a window / allowed-class), so each is checked.
    public bool IsExitBlocked(in RoomExit exit) =>
        IsLevelGateBlocked(in exit) || IsTollGateBlocked(in exit) || IsFareGateBlocked(in exit)
        || IsClassGateBlocked(in exit) || IsRaceGateBlocked(in exit)
        || IsItemGateBlocked(in exit) || IsImpassableDoorBlocked(in exit) || IsHazardEntryBlocked(in exit)
        || IsAlignmentGateBlocked(in exit);

    // Same gate checks as IsExitBlocked, but reports which kinds fire rather
    // than a single bool — so a failed walk names the actual obstacle. A
    // key-locked door surfaces as LockedDoor (not the pack-item Item reason)
    // so the message reads "a locked door you can't open" instead of a
    // mismatched "required item". Evaluated under whatever gate-suspension is
    // live: a walk that suspended the acquirable gates and still found no path
    // is blocked by a non-acquirable gate, and this reports exactly that.
    public ExitBlockReason DescribeExitBlock(in RoomExit exit)
    {
        ExitBlockReason reasons = ExitBlockReason.None;
        if (IsLevelGateBlocked(in exit)) reasons |= ExitBlockReason.Level;
        if (IsTollGateBlocked(in exit)) reasons |= ExitBlockReason.Toll;
        if (IsFareGateBlocked(in exit)) reasons |= ExitBlockReason.Fare;
        if (IsClassGateBlocked(in exit)) reasons |= ExitBlockReason.Class;
        if (IsRaceGateBlocked(in exit)) reasons |= ExitBlockReason.Race;
        if (IsItemGateBlocked(in exit))
            reasons |= exit.Hint == RoomExitHint.KeyLocked
                ? ExitBlockReason.LockedDoor
                : ExitBlockReason.Item;
        if (IsImpassableDoorBlocked(in exit)) reasons |= ExitBlockReason.Door;
        if (IsHazardEntryBlocked(in exit)) reasons |= ExitBlockReason.Hazard;
        if (IsAlignmentGateBlocked(in exit)) reasons |= ExitBlockReason.Alignment;
        return reasons;
    }

    // Item / ticket / key-locked-door / action-item gates. Suspended for the
    // gated-route planning pass. Only evaluated once inventory is known — an
    // unparsed inventory walks unrestricted rather than routing around gates we
    // can't yet tell whether we satisfy.
    private bool IsItemGateBlocked(in RoomExit exit)
    {
        // Suspended — UNLESS this exit gates on an item we're keeping closed to probe
        // whether it's genuinely required (then fall through to the live check).
        if (_acquirableGateSuspended && !ExitGatesOnAny(in exit, _keepClosedGateItems)) return false;
        if (!InventoryKnown || ItemCarriedProbe is not { } carries) return false;

        // An exit whose spell teleports the crosser on, and to the side routes are
        // planned through only when they have an item: without it the step still
        // goes through but lands somewhere else, so the way beyond is closed. Judged
        // on our own pack — a follower is checked by the game on theirs.
        if (exit.CastGateItemId > 0 && !carries(exit.CastGateItemId)) return true;

        switch (exit.Hint)
        {
            case RoomExitHint.Item:
            case RoomExitHint.Ticket:
                // A raft / ticket / held-item exit: the item must be in hand to
                // cross — no bash or pick alternative. Route around when we
                // lack it (or acquire it, per the item's path flags). In hand for
                // every member: a party short of copies splits at the gate.
                return exit.KeyItemId > 0 && !HoldsGateItem(exit.KeyItemId, perMember: true);

            case RoomExitHint.KeyLocked:
                return IsLockedDoorImpassable(in exit, carries);

            case RoomExitHint.Teleport:
                // A synthesised teleport edge that crosses by USING a carried item
                // (an item-use teleport such as `use potion of levitation`) is
                // impassable without that item. KeyItemId is a genuine possession
                // requirement only — RoomGraphManager clears it on a teleport that
                // merely shadows a locked door (you teleport past the door, no key
                // needed) — so a set KeyItemId here always means "carry this". The
                // item is acquirable, so this gate is suspended for the gated-route
                // planning pass like the other item gates.
                return exit.KeyItemId > 0 && !carries(exit.KeyItemId);

            case RoomExitHint.MultiActionHidden:
                // A hidden exit whose unlock action needs a held item ("hold up
                // amber talisman (Item: 815)") is impassable without it — no
                // pick/bash alternative, and the item is acquirable, so this
                // gate is suspended for the gated-route planning pass like the
                // other item gates. Lever-only doors carry no item requirement,
                // so they stay passable here.
                return exit.MultiAction is { } ma && ma.RequiresUnheldItem(carries);

            default:
                return false;
        }
    }

    // A locked door is impassable only when we can neither key, pick, nor bash
    // it. The key is the extra opener the stat matrix doesn't model; everything
    // else defers to CanOpenByStat (shared with the plain-door gate).
    private bool IsLockedDoorImpassable(in RoomExit exit, Func<int, bool> carries)
    {
        if (exit.KeyItemId > 0 && carries(exit.KeyItemId)) return false;   // have the key
        // A keyed door with no stat requirement is KEY-ONLY: the door FSM sends it
        // straight to the use-key path (KeyItemId > 0 && StatRequirement <= 0) and
        // never bashes or picks. CanOpenByStat would wrongly clear it — IsAchievable
        // reads a 0 requirement as a plain "(Door)" anyone can bash/pick — so
        // without this the planner treats a key-only door as freely passable,
        // collects no key requirement, and walks the crosser into a door they can
        // only fail on (report paradigm-20260812-111920: no route-picker offer for a
        // key-only door whose key we lack). Align with the FSM: key-only ⇒
        // impassable without the key, so it surfaces as a DoorKey gate.
        if (exit.KeyItemId > 0 && exit.StatRequirement <= 0) return true;
        return !CanOpenByStat(in exit);
    }

    // A plain "(Door)" carries no key — its only openers are pick and bash,
    // both innate stats you can't pick up en route. So unlike a KeyLocked door
    // it's NOT an acquirable gate: it stays active through the route picker's
    // SuspendAcquirableGates pass (a route that assumes every gate item in hand
    // still can't muscle a door the build lacks the strength / picklocks for),
    // which is why it lives here and not in IsItemGateBlocked. Provably
    // unopenable → routed around at plan time so BFS falls through to a
    // traversable alternate instead of walking the loop into a door it can only
    // bonk on.
    private bool IsImpassableDoorBlocked(in RoomExit exit) =>
        exit.Hint == RoomExitHint.Door && !CanOpenByStat(in exit);

    // Can the crosser force this door open with innate stats alone (pick or
    // bash)? Reuses DoorPolicy.IsAchievable — the walker's own fail-fast door
    // matrix — so the filter and the door FSM never disagree on whether a door
    // opens. Stat inputs unknown → treat as openable (passable) and leave the
    // door to the traversal-time FSM rather than route around an unknown build.
    private bool CanOpenByStat(in RoomExit exit)
    {
        if (StrengthProvider?.Invoke() is not { } strength) return true;
        if (PicklocksProvider?.Invoke() is not { } picks) return true;
        int maxBash = MaxBashableStrengthProvider?.Invoke() ?? DoorPolicy.UnbashableStrengthThreshold;
        return DoorPolicy.IsAchievable(exit.StatRequirement, exit.CanBash, strength, picks, maxBash);
    }

    // A door or keyed door whose only opener for this crosser is a pick at poor odds.
    // The key in hand (or assumed in hand, on a pass planning through acquirable
    // gates) opens it for certain, a key-only door has no lock to pick, and unknown
    // stats say nothing either way: none of those is one. The same on both realms
    // (GAME_MECHANICS "Locked doors — picking, opening and bashing").
    public bool IsPoorOddsDoor(in RoomExit exit)
    {
        if (exit.Hint is not (RoomExitHint.Door or RoomExitHint.KeyLocked)) return false;
        if (exit.Hint == RoomExitHint.KeyLocked && exit.KeyItemId > 0)
        {
            if (exit.StatRequirement <= 0) return false;
            if (_acquirableGateSuspended && !ExitGatesOnAny(in exit, _keepClosedGateItems)) return false;
            if (InventoryKnown && ItemCarriedProbe?.Invoke(exit.KeyItemId) == true) return false;
        }
        if (StrengthProvider?.Invoke() is not { } strength) return false;
        if (PicklocksProvider?.Invoke() is not { } picks) return false;
        int maxBash = MaxBashableStrengthProvider?.Invoke() ?? DoorPolicy.UnbashableStrengthThreshold;
        return DoorPolicy.IsPoorOddsPick(exit.StatRequirement, exit.CanBash, strength, picks, maxBash);
    }

    public int DoorRuleStamp => HashCode.Combine(
        StrengthProvider?.Invoke(), PicklocksProvider?.Invoke(), MaxBashableStrengthProvider?.Invoke());

    public string? DescribeDoorRefusal(in RoomExit exit)
    {
        if (exit.Hint is not (RoomExitHint.Door or RoomExitHint.KeyLocked)) return null;
        bool shut = exit.Hint == RoomExitHint.Door ? IsImpassableDoorBlocked(in exit) : IsItemGateBlocked(in exit);
        if (!shut && !IsPoorOddsDoor(in exit)) return null;
        // A key-only door has no numbers to give: the key is all there is to say.
        if (exit.KeyItemId > 0 && exit.StatRequirement <= 0) return $"needs its key (item {exit.KeyItemId}), not carried";
        if (StrengthProvider?.Invoke() is not { } strength || PicklocksProvider?.Invoke() is not { } picks) return null;
        int maxBash = MaxBashableStrengthProvider?.Invoke() ?? DoorPolicy.UnbashableStrengthThreshold;
        string odds = DoorPolicy.DescribeOdds(exit.StatRequirement, exit.CanBash, strength, picks, maxBash);
        return exit.KeyItemId > 0 ? $"its key (item {exit.KeyItemId}) isn't carried; {odds}" : odds;
    }

    // Blocks stepping into a room whose cast-on-enter spell is a protectable
    // hazard we can't currently survive (no counter item held). Suspended for
    // the gated-route planning pass and skipped while inventory is unknown.
    private bool IsHazardEntryBlocked(in RoomExit exit)
    {
        if (_acquirableGateSuspended)
        {
            if (!_keepUncounteredHazards && !_keepUnprotectableHazards) return false;
            if (_openHazardRooms?.Contains(exit.Target) == true) return false;
        }
        if (Hazards is null || RoomEntrySpellProbe is not { } spellOf) return false;

        int spell = spellOf(exit.Target);
        if (spell <= 0) return false;
        RoomHazardIndex.RoomHazard? hazard = Hazards.HazardForSpell(spell);
        if (hazard is null) return false;
        // A room that teleports its counter's holders too opens to no plan: not with
        // the item carried, arranged for or assumed in hand, and not for a loop's
        // own legs either (user, 2026-10-10: nobody loops those rooms). Nothing
        // carried changes that, so it doesn't wait for the inventory to be read.
        if (!HazardCounterProtects(hazard)) return true;
        if (!InventoryKnown || ItemCarriedProbe is not { } carries) return false;
        if (!_acquirableGateSuspended) return !hazard.IsSatisfiedBy(carries);
        if (!_keepUncounteredHazards) return false;

        // Planning through gates with uncountered hazards kept: the room opens only
        // when what is carried plus what the walk will obtain covers it.
        IReadOnlyList<int> arranged = HazardProvisionProbe?.Invoke(exit.Target) ?? Array.Empty<int>();
        return !hazard.IsSatisfiedBy(id => carries(id) || arranged.Contains(id));
    }

    // Whether holding the hazard's counter makes its rooms safe to route through.
    // Not on Crystal Lake, whose sea rooms teleport a boat's holder as well
    // (GAME_MECHANICS "Crystal Lake: the sea room spells").
    public static bool HazardCounterProtects(RoomHazardIndex.RoomHazard hazard)
    {
        ArgumentNullException.ThrowIfNull(hazard);
        return !hazard.TeleportsCounterHolders;
    }

    // A room no route is planned into as things stand, though a character standing
    // in one is always planned out of it (BfsMapper.FindPath): a hazard room nothing
    // counters, unless this plan has it open (a route card's pick agreed to it, or
    // every gate is stood down to find the route that card offers).
    public bool IsClosedToRoutes(RoomKey room)
    {
        int spell = RoomEntrySpellProbe?.Invoke(room) ?? 0;
        return spell > 0
            && Hazards?.HazardForSpell(spell) is { } hazard
            && !HazardCounterProtects(hazard)
            && IsUncounteredHazardRoom(room);
    }

    // The terms on which the few walks that cross such a room do: the level from
    // which the room's own textblock treats a boat's holder apart (50 on the lake),
    // and a boat in hand. With both the lake teleports about 2 entries in 100; with
    // either missing, about 70. Judged on our own level and pack, and never met on
    // a level or pack not read yet: a crossing isn't made on a guess.
    public ClosedRoomTerms? CrossingTerms(RoomKey room)
    {
        int spell = RoomEntrySpellProbe?.Invoke(room) ?? 0;
        if (spell <= 0 || Hazards?.HazardForSpell(spell) is not { } hazard || HazardCounterProtects(hazard))
            return null;
        return TermsOf(hazard);
    }

    private ClosedRoomTerms TermsOf(RoomHazardIndex.RoomHazard hazard) => new(
        hazard.CounterHolderMinLevel,
        hazard.ProtectingItems,
        LevelMet: LevelProvider?.Invoke() is { } level && level >= hazard.CounterHolderMinLevel,
        ItemHeld: InventoryKnown && ItemCarriedProbe is { } carries && hazard.IsSatisfiedBy(carries));

    public bool MayCrossClosedRooms()
    {
        if (Hazards is null) return false;
        foreach (RoomHazardIndex.RoomHazard hazard in Hazards.Hazards)
            if (!HazardCounterProtects(hazard) && TermsOf(hazard).Met) return true;
        return false;
    }

    // Whether a room spell teleports on a roll or outright (RoomSpellTeleportIndex's
    // Sudden class). Wired by AppServices; unset, no room reads as one.
    public Func<int, bool>? SpellTeleportsAtRandomProbe { get; set; }

    public bool TeleportsOnArrival(RoomKey room) =>
        RoomEntrySpellProbe?.Invoke(room) is > 0 and int spell && SpellTeleportsAtRandomProbe?.Invoke(spell) == true;

    private bool InventoryKnown => InventoryReadyProbe?.Invoke() == true;

    // Suspends the four acquirable gates for a single planning pass so a caller
    // can compute the route the crosser WOULD take with every gate item in hand
    // (the "gated" alternative the route picker weighs against the free route).
    // Dispose to restore gating. Single-threaded planning use. Returns the
    // IRoomFilter-typed scope (boxes the struct) so a caller holding only the
    // interface can suspend without knowing the concrete filter.
    public IDisposable SuspendAcquirableGates() => new GateSuspensionScope(this, keepClosed: null);

    public IDisposable SuspendAcquirableGatesButUncounteredHazards() =>
        new GateSuspensionScope(this, keepClosed: null, keepUncounteredHazards: true);

    // The suspension for asking what the crosser could walk by obtaining something:
    // every gate stands down but a hazard room no item makes safe, which obtaining
    // its counter would not open. The route picker weighs its "acquire, then go"
    // route against the free one under this, so it never offers a boat as the way
    // across Crystal Lake.
    public IDisposable SuspendAcquirableGatesButUnprotectableHazards() =>
        new GateSuspensionScope(this, keepClosed: null, keepUnprotectableHazards: true);

    // Suspends the acquirable gates EXCEPT ones that gate on an item in `keepClosed`,
    // which stay live (blocked unless carried). A BFS in this scope answers "can the
    // crosser still reach the destination WITHOUT relying on those items?" — reachable
    // means they merely unlock an optional shortcut; unreachable means one is a genuine
    // requirement. Single-threaded planning use; dispose to restore gating.
    // keepUncounteredHazards adds SuspendAcquirableGatesButUncounteredHazards' rule,
    // for a leg of such a walk that nobody was shown; openHazardRooms are the hazard
    // rooms that rule leaves open, because the user agreed to them on a route card.
    public IDisposable SuspendAcquirableGatesExcept(
        IReadOnlyCollection<int> keepClosed, bool keepUncounteredHazards = false,
        IReadOnlyCollection<RoomKey>? openHazardRooms = null) =>
        new GateSuspensionScope(this, keepClosed, keepUncounteredHazards, openHazardRooms);

    // The same keeping closed the hazard rooms no item makes safe, for the route
    // picker's sole route.
    public IDisposable SuspendAcquirableGatesExceptUnprotectable(IReadOnlyCollection<int> keepClosed) =>
        new GateSuspensionScope(this, keepClosed, keepUnprotectableHazards: true);

    // Whether stepping into this room is refused, as things stand, for want of a
    // counter to its cast-on-enter hazard. What a route card's "cross it" asks the
    // user to agree to, room by room.
    public bool IsUncounteredHazardRoom(RoomKey room)
    {
        RoomExit into = new(room, RoomExitHint.None, RawHint: null);
        return IsHazardEntryBlocked(in into);
    }

    public readonly struct GateSuspensionScope : IDisposable
    {
        private readonly MovementFilter _filter;
        internal GateSuspensionScope(MovementFilter filter, IReadOnlyCollection<int>? keepClosed,
            bool keepUncounteredHazards = false, IReadOnlyCollection<RoomKey>? openHazardRooms = null,
            bool keepUnprotectableHazards = false)
        {
            _filter = filter;
            _filter._acquirableGateSuspended = true;
            _filter._keepClosedGateItems = keepClosed;
            _filter._keepUncounteredHazards = keepUncounteredHazards;
            _filter._openHazardRooms = openHazardRooms;
            _filter._keepUnprotectableHazards = keepUnprotectableHazards;
        }
        public void Dispose()
        {
            _filter._acquirableGateSuspended = false;
            _filter._keepClosedGateItems = null;
            _filter._keepUncounteredHazards = false;
            _filter._openHazardRooms = null;
            _filter._keepUnprotectableHazards = false;
        }
    }

    // Does this exit gate on any item in the set? Unions the carryable-item gates
    // (ExitGateItems covers Item/Ticket/Teleport/MultiActionHidden) with the
    // KeyLocked door key, which ExitGateItems deliberately omits. Empty/null → never.
    private static bool ExitGatesOnAny(in RoomExit exit, IReadOnlyCollection<int>? items)
    {
        if (items is null || items.Count == 0) return false;
        if (exit.Hint == RoomExitHint.KeyLocked && items.Contains(exit.KeyItemId)) return true;
        foreach (int gid in ExitGateItems.Of(in exit))
            if (items.Contains(gid)) return true;
        return false;
    }

    // A "(Class: N OK)" exit only admits class Number N. Gate only when our
    // own class is known — an unparsed class never refuses a walk on a gate we
    // can't yet evaluate.
    private bool IsClassGateBlocked(in RoomExit exit)
    {
        if (!exit.HasClassGate) return false;
        if (ClassNumberProvider?.Invoke() is { } myClass) return myClass != exit.ClassGate;

        // Class unknown (stat screen not parsed yet — e.g. just after login). A class
        // gate NEVER opens for the wrong class, so for a class-gated TELEPORT — a
        // discrete shortcut the router / picker OFFERS as a choice — routing through
        // one on a guess is wrong for all-but-one class; block it until the class is
        // known (report stock-20260908-103628: a bard-only barmaid transport surfaced
        // as a teleport fork to a warrior whose stat screen hadn't parsed yet, ~11s
        // into the session). A class-gated CARDINAL keeps the level-gate rule instead —
        // don't block on unknown, walk up and halt on the game's own refusal.
        return exit.Hint == RoomExitHint.Teleport;
    }

    // A "(Race: N OK)" exit only admits race Number N (GAME_MECHANICS "Class and
    // race gated exits"). Gated only when our own race is known, the same rule as
    // a class-gated cardinal: an unparsed character walks up and halts on the
    // game's own refusal rather than being routed around on a guess.
    private bool IsRaceGateBlocked(in RoomExit exit)
        => exit.HasRaceGate && RaceNumberProvider?.Invoke() is { } myRace && myRace != exit.RaceGate;

    // An "(Alignment: X to Y)" exit admits only crossers whose alignment value is
    // inside [X, Y]. Whole-party: block (route around) when ANY member whose
    // alignment we KNOW falls outside the window — the game stops the party at the
    // tightest member. A member whose alignment is UNKNOWN (null) does NOT block
    // here; the walker walks up to the gate and halts on the refusal rather than
    // detouring on a guess (user-confirmed for report paradigm-20260827-144553). No
    // provider / no known member → never gate, same rule as an unknown level.
    private bool IsAlignmentGateBlocked(in RoomExit exit)
    {
        if (exit.AlignmentGate is not { } gate) return false;
        if (PartyAlignmentsProvider?.Invoke() is not { Count: > 0 } aligns) return false;
        foreach (int? a in aligns)
            if (a is int v && !Game.Calculators.AlignmentBands.Admits(gate, v)) return true;
        return false;
    }

    private bool IsLevelGateBlocked(in RoomExit exit)
    {
        if (!exit.HasLevelGate) return false;
        return IsLevelExcluded(exit.MinLevel, exit.MaxLevel);
    }

    // Party-or-self level-window exclusion: true when the crosser's level (or,
    // when leading a party, any member's) falls outside [minLevel, maxLevel]
    // (0 in either slot = no bound on that side; the MDB's 0/999 sentinels are
    // normalised to 0 at parse time). Party branch routes around a gate that
    // would leave a member behind — the bounds fold in the leader's own level —
    // and falls through to the self level when solo / not leading / nobody's
    // level parsed, or while the party gate is suspended for the freshness-warm
    // planning pass. Unknown level never blocks. Shared by the cardinal level
    // gate and the boat sailing's minlevel floor.
    private bool IsLevelExcluded(int minLevel, int maxLevel)
    {
        if (minLevel <= 0 && maxLevel <= 0) return false;

        if (!_partyLevelGateSuspended && PartyLevelBoundsProvider?.Invoke() is { } bounds)
        {
            if (minLevel > 0 && bounds.Low < minLevel) return true;
            if (maxLevel > 0 && bounds.High > maxLevel) return true;
            return false;
        }

        if (LevelProvider?.Invoke() is not { } level) return false;  // level unknown → don't gate
        return LevelIgnoringFilter.OutsideLevelWindow(level, minLevel, maxLevel);
    }

    // A (Toll: N) exit needs N*100 copper-value on hand to cross. Only gate
    // when we know our wealth — an unknown wallet never refuses a walk on a
    // bar we can't yet evaluate.
    private bool IsTollGateBlocked(in RoomExit exit)
    {
        if (exit.Hint != RoomExitHint.Toll || exit.TollGold <= 0) return false;
        if (_tollGateSuspended && !_tollGateForcedClosed) return false;   // planning the tolls-permitted route (see WarmForRoute)
        // A toll is phrased in gold crowns but any coin mix totalling N*100
        // copper passes — so the fold-to-copper conversion is here, not in the
        // shared wallet check.
        return IsRefusedCrossing(in exit) || CannotAfford((long)exit.TollGold * 100);
    }

    // An NPC ask-transport charges its fare to every person who asks, so it gates on
    // the same party-or-self wallet check as a toll — the party only routes through it
    // when its poorest member can pay. The fare is already copper. Stands down with the
    // toll gate while WarmForRoute plans the paid-crossings-permitted route.
    private bool IsFareGateBlocked(in RoomExit exit)
    {
        if (exit.FareCopper <= 0 || (_tollGateSuspended && !_tollGateForcedClosed)) return false;
        return IsRefusedCrossing(in exit) || CannotAfford(exit.FareCopper);
    }

    // Party-or-self affordability: true when the crosser can't cover `cost`
    // copper. Party branch (PartyWealthTracker.MinWealth folds in our own wallet
    // too) routes around a cost a member can't meet rather than stranding them
    // at the gate; a member who hasn't reported fresh wealth counts as
    // unaffordable. Returns false (don't gate) when solo / not leading / own
    // wallet unknown — same "don't refuse on what we can't evaluate" rule as an
    // unknown level — except while the record is known to be wrong (PurseDoubt),
    // when nothing is affordable. Demand-driven: invoked only for a toll exit, an
    // NPC transport fare, or a boat fare, so nothing polls unless one is actually
    // in play. Shared by the toll, transport-fare, and boat-fare gates.
    private bool CannotAfford(long cost)
    {
        if (cost <= 0) return false;
        if (_tollGateForcedClosed) return true;
        // Ahead of the party's purse: it is our own record that isn't believed.
        if (_purseDoubt != PurseDoubt.None) return true;
        if (PartyWealthProvider?.Invoke() is { } partyMin) return partyMin < cost;
        if (WealthProvider?.Invoke() is not { } wealth) return false;
        return wealth - Math.Max(0, ReservedCopper) < cost;
    }

    // ----- The purse on record, when it is known to be wrong ----------------
    // The inventory record is only rebuilt by a full `i`. Until one is read, two
    // events prove what it says about coin is out of date, and an unknown purse
    // must not then fall back on "never refuses a walk": that rule is for a purse
    // nobody has read, not for one known to be short.
    private enum PurseDoubt
    {
        None,
        // A death takes the coin carried along with the pile, on both realms
        // (GAME_MECHANICS "Coins in the deathpile"), so the purse is empty.
        Death,
        // The game turned us away at a toll the record covered.
        Refused,
    }

    private PurseDoubt _purseDoubt;

    // RoomTracker.PlayerDeathObserved, with the room died in. A death in an arena
    // room takes nothing (ArenaDeathRooms), so the record stands; any other empties
    // the purse.
    public void NoteDeath(RoomKey? diedIn)
    {
        if (diedIn is { } room && ArenaDeathRooms.Contains(room))
        {
            _log?.Info("Tolls", $"Died in {room}, an arena room, where a death takes nothing: the purse on record stands.");
            return;
        }
        NotePurseLostAtDeath();
    }

    // A walk started from the graveyard on the coin carried before the death went
    // through the first toll on its way and stood at the gate (report
    // paradigm-20261010-145529).
    public void NotePurseLostAtDeath()
    {
        _purseDoubt = PurseDoubt.Death;
        _log?.Info("Tolls",
            "The coin carried went with the deathpile: no toll or fare is taken until the inventory is read again.");
    }

    // ----- Crossings the game refused ----------------------------------------
    // A refusal at a toll or fare is the game's word, whatever the record and the
    // data said: the data's price can be wrong as well as the purse. The exit stays
    // closed to routes until a full inventory read shows the price the game named.
    // A gate check is handed the exit alone, so one is known by the room it leads
    // to and what the data charges for it.
    private readonly record struct CrossingKey(RoomKey Target, int TollGold, long FareCopper);

    // Named: the price the refusal named, in copper; null when its wording gave
    // none we can value. ReadSince: a full read has come since, for a crossing
    // with no price to compare it with. Immutable and swapped whole: a route can
    // be planned off the UI thread while a refusal lands on it.
    private volatile ImmutableDictionary<CrossingKey, (long? Named, bool ReadSince)> _refusedCrossings =
        ImmutableDictionary<CrossingKey, (long? Named, bool ReadSince)>.Empty;

    private static CrossingKey KeyOf(in RoomExit exit) => new(exit.Target, exit.TollGold, exit.FareCopper);

    private static long CostOf(in RoomExit exit) =>
        exit.Hint == RoomExitHint.Toll && exit.TollGold > 0 ? (long)exit.TollGold * 100 : exit.FareCopper;

    private bool IsRefusedCrossing(in RoomExit exit) =>
        _refusedCrossings is { Count: > 0 } refused && refused.ContainsKey(KeyOf(in exit));

    // Whether a walk, loop or Auto-Lair run is under way. A crossing refused in a
    // wording with no price can only be judged on the data again, and the data is
    // what sent this trip into it: it stays closed until the trip is over.
    public Func<bool>? TripUnderWayProbe { get; set; }

    // The game refused a paid crossing. crossing is the exit, null when the move
    // refused couldn't be tied to one; namedCopper is the price its line named,
    // null when the wording gives none we can value. Returns true when that
    // contradicts the purse on record, so the caller reads the inventory again;
    // false when the record already said we were short (a toll walked into by hand
    // with an empty purse, or a price the data has too low).
    public bool NoteCrossingRefused(RoomExit? crossing, long? namedCopper)
    {
        if (crossing is { } exit && CostOf(in exit) > 0)
        {
            _refusedCrossings = _refusedCrossings.SetItem(KeyOf(in exit), (namedCopper, ReadSince: false));
            _log?.Info("Tolls",
                $"The game refused the crossing into {exit.Target}"
                + (namedCopper is { } named ? $", naming {CurrencyFormat.Full(named)}" : ", in a wording that names no price the client can value")
                + ": closed to routes until a full inventory read "
                + (namedCopper is null ? "and the end of this trip." : "shows that much."));
        }
        if (_purseDoubt != PurseDoubt.None) return false;
        if (namedCopper is { } cost && WealthProvider?.Invoke() is { } wealth && wealth < cost) return false;
        _purseDoubt = PurseDoubt.Refused;
        _log?.Info("Tolls",
            "The game refused a toll or fare the client took the purse to cover: none is taken until the inventory is read again.");
        return true;
    }

    // InventoryManager.FullInventoryParsed: the record is the game's own again.
    public void NotePurseRead()
    {
        if (_purseDoubt != PurseDoubt.None)
        {
            _purseDoubt = PurseDoubt.None;
            _log?.Info("Tolls", "Inventory read: tolls and fares go by the purse on record again.");
        }
        if (_refusedCrossings.Count == 0) return;
        long? purse = WealthProvider?.Invoke();
        bool trip = TripUnderWayProbe?.Invoke() == true;
        foreach ((CrossingKey key, (long? named, _)) in _refusedCrossings)
        {
            if (named is { } price ? purse >= price : !trip) Reopen(key, "the inventory read");
            else _refusedCrossings = _refusedCrossings.SetItem(key, (named, ReadSince: true));
        }
    }

    // MovementController going idle: the trip a crossing was refused on is over.
    public void NoteTripEnded()
    {
        foreach ((CrossingKey key, (long? Named, bool ReadSince) refused) in _refusedCrossings)
            if (refused is (null, true)) Reopen(key, "the trip it was refused on ended");
    }

    private void Reopen(CrossingKey key, string why)
    {
        _refusedCrossings = _refusedCrossings.Remove(key);
        _log?.Info("Tolls", $"The refused crossing into {key.Target} is open to routes again: {why}.");
    }

    // How the purse stands against what this exit charges, for a walk's log line
    // and for the line a walk with no route gives. Null for an exit that charges
    // nothing.
    public string? DescribePurseFor(in RoomExit exit)
    {
        long cost = CostOf(in exit);
        if (cost <= 0) return null;
        if (_purseDoubt == PurseDoubt.None && _refusedCrossings.TryGetValue(KeyOf(in exit), out (long? Named, bool ReadSince) refused))
        {
            if (refused.Named is not { } named)
                return "the game refused it in a wording that names no price the client can value, so it isn't tried again on this trip";
            string carried = WealthProvider?.Invoke() is { } purse ? $" (you carry {CurrencyFormat.Full(purse)})" : string.Empty;
            return $"the game refused it, naming {CurrencyFormat.Full(named)}: closed until a full inventory read shows that much{carried}";
        }
        return DescribePurse(cost);
    }

    // The same with nothing to pay, for the bug report, with the crossings a
    // refusal has closed.
    public string DescribePurse()
    {
        string purse = DescribePurse(cost: 0);
        if (_refusedCrossings.Count == 0) return purse;
        return purse + "; closed after a refusal: " + string.Join(", ", _refusedCrossings.Select(r =>
            $"into {r.Key.Target} ({(r.Value.Named is { } named ? "named " + CurrencyFormat.Full(named) : "no price named")})"));
    }

    private string DescribePurse(long cost)
    {
        switch (_purseDoubt)
        {
            case PurseDoubt.Death:
                return "your coin went with the deathpile and the inventory hasn't been read since (type i)";
            case PurseDoubt.Refused:
                return "the game refused a toll or fare the client took the purse to cover, so none is taken until the inventory is read again (type i)";
        }
        if (PartyWealthProvider?.Invoke() is { } partyMin)
            return $"the party's poorest known purse holds {CurrencyFormat.Full(partyMin)}{Short(cost - partyMin)}";
        if (WealthProvider?.Invoke() is not { } wealth)
            return "the purse isn't known (inventory not read), so tolls and fares aren't refused on it";
        long reserved = Math.Max(0, ReservedCopper);
        string carried = reserved > 0
            ? $"you carry {CurrencyFormat.Full(wealth)}, {CurrencyFormat.Full(reserved)} of it set aside for fees ahead"
            : $"you carry {CurrencyFormat.Full(wealth)}";
        return carried + Short(cost - (wealth - reserved));

        static string Short(long by) => by > 0 ? $", {CurrencyFormat.Full(by)} short" : string.Empty;
    }

    // Copper set aside from our own wallet for something the walk is heading to pay,
    // so a toll or fare is only taken when it's affordable on top of it: a train trip
    // reserves its fees, and walks round a toll it couldn't then pay (user,
    // 2026-09-30). 0 = nothing reserved.
    public long ReservedCopper { get; set; }

    // While set, every toll and fare reads unaffordable — HasTollFreeRoute's probe.
    private bool _tollGateForcedClosed;

    // Whether a walk from source to destination can avoid every toll and fare,
    // planning through the acquirable gates as a train or detour walk does.
    public bool HasTollFreeRoute(BfsMapper bfs, RoomKey source, RoomKey destination)
    {
        ArgumentNullException.ThrowIfNull(bfs);
        _tollGateForcedClosed = true;
        try
        {
            using IDisposable _ = SuspendAcquirableGatesButUnprotectableHazards();
            return bfs.FindPath(source, destination, this) is not null;
        }
        finally { _tollGateForcedClosed = false; }
    }

    // ----- Boat sailing gates (per-member minlevel + copper fare) -----------
    // A `secure passage` boarding gates every member individually on BOTH a
    // level floor and a copper fare (the captain leaves an under-level or
    // too-poor member at the dock), so both fold into the same party-or-self
    // checks the land gates use. RequiresCheckability is NOT gated: the client
    // can't read the quest-attunement flag, so it's the user's responsibility —
    // an un-attuned member simply never boards and the voyage fails out on the
    // arrival timeout.

    // A sailing is routable only when every crosser clears both its gates.
    public bool IsBoatPassable(in BoatPassage passage) =>
        DescribeBoatBlock(in passage) == ExitBlockReason.None;

    // Which per-member gates keep the crosser off this sailing, for a "boat not
    // taken" diagnostic. Mirrors DescribeExitBlock over the two boat gates. The
    // fare is already copper (a boat charges the base coin unit directly, unlike
    // a toll's gold*100), so it feeds the wallet check unscaled.
    public ExitBlockReason DescribeBoatBlock(in BoatPassage passage)
    {
        ExitBlockReason reasons = ExitBlockReason.None;
        if (IsLevelExcluded(passage.MinLevel, 0)) reasons |= ExitBlockReason.Level;
        if (CannotAfford(passage.FareCopper)) reasons |= ExitBlockReason.Fare;
        return reasons;
    }

    // Warm the party @wealth + @level readings for a boat the router has chosen
    // to board. A sailing gates every member individually on fare and minlevel,
    // so — like a toll on a land route — the planner probes fresh party wealth +
    // level before committing, so a stale/unknown member isn't waved onto (or
    // wrongly kept off) the boat. Unconditional (unlike WarmForRoute's per-gate
    // BFS test) because the caller invokes it only once a fare-gated boat is on
    // the chosen route. No-op when solo / not leading — a lone crosser gates on
    // its own live wallet/level, no round-trip needed.
    public void WarmForBoat()
    {
        if (WealthWarmProbe is { } wealthProbe && PartyWealthProvider?.Invoke() is not null)
            wealthProbe();
        if (LevelWarmProbe is { } levelProbe && PartyLevelBoundsProvider?.Invoke() is not null)
            levelProbe();
    }

    // Warm the party @wealth reading and @level freshness before a walk, but
    // only when each is actually needed. BFS explores off-path gate edges (any
    // toll / level exit inside the search frontier, in any direction), so
    // probing from the per-exit gate would fire a round-trip for a gate the
    // party would never walk through. Here we plan the route ONCE per gate kind
    // with that gate suspended — the path the party WOULD take if every gate of
    // that kind were passable — and probe only when that path genuinely crosses
    // one. Each half is independent: a toll on the route warms @wealth, a level
    // gate on the route warms @level; a route with neither warms nothing.
    // The tolls and fares on the route a walk WOULD take once it can pay them: the
    // toll gate stands down (an unaffordable toll is routed around only until the
    // money is in hand), and so do the acquirable gates a train or detour walk plans
    // through. Prices a trip's crossings before the money for it is fetched.
    public long TollCopperOnRoute(BfsMapper bfs, RoomKey source, RoomKey destination)
    {
        ArgumentNullException.ThrowIfNull(bfs);
        _tollGateSuspended = true;
        try
        {
            using IDisposable _ = SuspendAcquirableGatesButUnprotectableHazards();
            return bfs.RouteTollCopper(source, destination, this);
        }
        finally { _tollGateSuspended = false; }
    }

    public void WarmForRoute(BfsMapper bfs, RoomKey source, RoomKey destination)
    {
        ArgumentNullException.ThrowIfNull(bfs);

        // Toll / transport-fare wealth warm — only when a paid crossing is actually on
        // the route.
        if (WealthWarmProbe is { } tollProbe && PartyWealthProvider?.Invoke() is not null)
        {
            _tollGateSuspended = true;
            try
            {
                if (bfs.RouteUsesToll(source, destination, this))
                    tollProbe();
            }
            finally { _tollGateSuspended = false; }
        }

        // Level freshness warm — only when the party level gate is actually in
        // play (bounds known → we're leading a party with at least one member
        // level to gate on). The probe fires a fresh @level for stale / unknown
        // members so the next evaluation gates on verified levels.
        if (LevelWarmProbe is { } levelProbe && PartyLevelBoundsProvider?.Invoke() is not null)
        {
            _partyLevelGateSuspended = true;
            try
            {
                if (bfs.RouteUsesLevelGate(source, destination, this))
                    levelProbe();
            }
            finally { _partyLevelGateSuspended = false; }
        }
    }

    // True when the user has flagged this room as a stash drop-off point.
    public bool IsStash(RoomKey key) => _stash.Contains(key);

    // Add the room to the avoided set. No-op when already avoided or when no
    // profile is loaded. Persists immediately.
    public void MarkAvoided(RoomKey key)
    {
        if (_profile.Current is not { } current) return;
        if (!_avoided.Add(key)) return;

        current.AvoidedRooms ??= new List<RoomRef>();
        current.AvoidedRooms.Add(new RoomRef(key.Map, key.Room));
        _profile.Save();
        _log?.Info("MovementFilter", $"avoided {key}");
        AvoidedChanged?.Invoke();
    }

    // Remove the room from the avoided set. Persists immediately.
    public void UnmarkAvoided(RoomKey key)
    {
        if (_profile.Current is not { } current) return;
        if (!_avoided.Remove(key)) return;

        if (current.AvoidedRooms is { } list)
            list.RemoveAll(r => r.Map == key.Map && r.Room == key.Room);

        _profile.Save();
        _log?.Info("MovementFilter", $"unavoided {key}");
        AvoidedChanged?.Invoke();
    }

    // Flag the room as a stash drop-off point. Persists immediately.
    public void MarkStash(RoomKey key)
    {
        if (_profile.Current is not { } current) return;
        if (!_stash.Add(key)) return;

        current.StashRooms ??= new List<RoomRef>();
        current.StashRooms.Add(new RoomRef(key.Map, key.Room));
        _profile.Save();
        _log?.Info("MovementFilter", $"stash {key}");
        StashChanged?.Invoke();
    }

    // Clear the room's stash-room flag. Persists immediately.
    public void UnmarkStash(RoomKey key)
    {
        if (_profile.Current is not { } current) return;
        if (!_stash.Remove(key)) return;

        if (current.StashRooms is { } list)
            list.RemoveAll(r => r.Map == key.Map && r.Room == key.Room);

        _profile.Save();
        _log?.Info("MovementFilter", $"unstashed {key}");
        StashChanged?.Invoke();
    }

    // Replace both the avoided and stash sets in one shot and persist once.
    // Used by the Modify-Avoid-Rooms dialog on Save — it stages the merged,
    // type-tagged working copy locally and commits both sets together so Cancel
    // can discard cleanly (mirrors RoomBlacklistStore.ReplaceAll). No-op when no
    // profile is loaded. Fires both change events so the map recolours the
    // affected cells.
    public void ReplaceAll(IEnumerable<RoomKey> avoided, IEnumerable<RoomKey> stash)
    {
        ArgumentNullException.ThrowIfNull(avoided);
        ArgumentNullException.ThrowIfNull(stash);
        if (_profile.Current is not { } current) return;

        _avoided.Clear();
        _stash.Clear();
        foreach (RoomKey k in avoided) _avoided.Add(k);
        foreach (RoomKey k in stash) _stash.Add(k);

        current.AvoidedRooms = _avoided.Select(k => new RoomRef(k.Map, k.Room)).ToList();
        current.StashRooms   = _stash.Select(k => new RoomRef(k.Map, k.Room)).ToList();
        _profile.Save();
        _log?.Info("MovementFilter",
            $"replaced sets: avoided({_avoided.Count}) stash({_stash.Count})");
        AvoidedChanged?.Invoke();
        StashChanged?.Invoke();
    }

    private void OnProfileLoaded(CharacterProfile profile)
    {
        _avoided.Clear();
        _stash.Clear();
        // Another character's purse: its own first inventory read settles it.
        _purseDoubt = PurseDoubt.None;
        _refusedCrossings = _refusedCrossings.Clear();

        if (profile.AvoidedRooms is { } a)
            foreach (RoomRef r in a) _avoided.Add(new RoomKey(r.Map, r.Room));
        if (profile.StashRooms is { } s)
            foreach (RoomRef r in s) _stash.Add(new RoomKey(r.Map, r.Room));

        AvoidedChanged?.Invoke();
        StashChanged?.Invoke();
    }

    private void OnProfileClosed()
    {
        bool hadAvoided = _avoided.Count > 0;
        bool hadStash   = _stash.Count > 0;
        _avoided.Clear();
        _stash.Clear();
        _purseDoubt = PurseDoubt.None;
        _refusedCrossings = _refusedCrossings.Clear();
        if (hadAvoided) AvoidedChanged?.Invoke();
        if (hadStash)   StashChanged?.Invoke();
    }
}
