using MudPlay.Game.Inventory;
using MudPlay.Game.Map;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.Services.Patterns;

namespace MudPlay.Game.Light;

// The auto-light engine, split across a PROVISIONING path (keeps a covering light
// in the pack) and a READYING path (lights / puts one away):
//
//  • Provisioning — on each freshly-planned walk (AutoWalkManager.SetRouteAnnouncer)
//    it scans the path for its darkest room and asks the pure AutoLightPlanner what
//    is needed. A Buy verdict (the route crosses a room nothing carried, worn or
//    cast covers) becomes a shop detour handed to AutoLightShopRouter, which runs
//    after any path-item detour and before the walk goes on. A readied light
//    dwindling under Reorder-at is a Reorder: on a walk the same router; while a
//    loop or Auto-Lair runs, the loop restock trip (SetLoopRestock). A bank run or a
//    training trip that passes near a shop selling the light stocks up to Carry
//    hours on the way (PlanStockUp), and a light lying on the floor is picked up by
//    the auto-get path (WantsFloorLight). The route-scan's own Ready verdict is
//    never acted on: it readied for the darkest room ANYWHERE on the route and
//    over-lit the lit rooms between (the over-lit-town report).
//  • Readying — scoped to one room, two ways:
//     – Predictive lookahead (OnApproachingRoom) — the walker / loop-runner hand
//       over the room they're about to step into; if its mapped Rooms.Light reads
//       dark, the room-light buff goes up and / or a light is `use`d ahead of the
//       move on the same dispatch, so the room is lit on arrival.
//     – Reactive (the "very dark" / "pitch black" lines) — the fallback for an
//       UNMAPPED next room the graph can't score, one room late.
//    Either way an auto-readied light is `rem`d the moment we step into a room
//    seeable without it (OnRoomEntered).
//
// The room-light spell (the self buff slot flagged Only when dark) comes in two
// kinds (RoomLightSpellKind). A buff adds to a lit light, so it goes first and a
// light item covers only what it leaves short. A spell that makes a light item is
// one more light source, cast when nothing carried can light the room; the item it
// makes is lit like any other. A spell that was in effect while the room still
// printed its can't-see line is latched as not enough: it isn't cast again for a
// room as dark or darker until worn illumination rises (SpellShortFor).
//
// "Only use my room-light spell" mode readies nothing but the spell's own light and
// provisions nothing from a shop.
//
// Gating: every action sits behind the single AutoLight master toggle.
//
// A readied light lands in the snapshot only on the next i dump, so a local pending
// latch bridges the gap between sending use and the dump confirming it — a repeated
// can't-see line on the same stale snapshot won't double-send, and a newer dump
// that lands without our light retires the latch so a failed send retries.
public sealed class AutoLightProvisioner
{
    // LogService category — [AutoLight] rows per decision.
    public const string LogCategory = "AutoLight";

    // How many steps a bank or training trip may add to stop at a light shop and
    // stock up: the 25-step allowance the sell-on-bank-run detour defaults to
    // (CashSettings.SellOnBankRunWithinSteps), measured the same way the shop
    // routers pick a shop — dist(from, shop) + dist(shop, to) − dist(from, to).
    public const int StockUpMaxAddedSteps = 25;

    private readonly Func<bool> _isEnabled;
    private readonly Func<InventorySnapshot> _snapshot;
    private readonly Func<IReadOnlyList<LightItem>> _catalogue;
    private readonly Func<RoomKey, Room?> _resolveRoom;
    private readonly Func<int> _wornIllu;
    private readonly Func<RoomLightSpell?> _roomLightSpell;
    private readonly Func<string, bool> _castRoomLightSpell;
    private readonly Func<string, bool> _roomLightSpellUp;
    private readonly Func<AutoLightSettings> _settings;
    private readonly Func<Room?> _currentRoom;
    private readonly Func<bool> _inDarkRoom;
    private readonly LogService? _log;
    private readonly WireSender _wire = new();

    // Hand-off for the Buy verdict on a walk: the shop-detour router.
    private Action<AutoLightBuyRequest>? _provision;

    // Hand-off for a reorder while a loop / Auto-Lair runs: the loop restock trip.
    private Func<bool>? _engineRunning;
    private Func<AutoLightBuyRequest, LightRestockOutcome>? _loopRestock;

    // The light we last asked to ready, and the snapshot it was decided against.
    // Guards a re-send of `use` before the readied light shows up in a later `i`
    // dump; retired once a newer dump lands (confirming it or not).
    private string? _pendingReadyName;
    private DateTimeOffset _pendingSnapshotTime;

    // Whether the most recent ReadyLight call actually sent a `use` (vs. skipping it
    // on the pending-latch). Read right after by the reactive dark-room path to
    // decide whether to send the bare-CR redisplay — only a real light-up needs it.
    private bool _readyJustSentUse;

    // The readied light instance we last fired a reorder for. A reorder is
    // requested at most once per instance — the readied charge only refreshes on
    // an `i` dump, so an unlatched reorder would re-detour on every dump while the
    // light sits below the threshold, over-buying. Retired when the readied light
    // is replaced/refreshed (name change, charge climbs, or it's gone).
    private ReadiedLight? _reorderRequestedFor;

    // The name of the light THIS engine `use`d. Cleared when we `rem` it on
    // entering a seeable room (OnRoomEntered) or when it burns out. Only
    // auto-readied lights are rem'd — a light the player readied by hand is theirs.
    private string? _autoReadiedName;

    // Set when a burn-out line reports the readied light went out. The snapshot's
    // ReadiedLight only clears on the next `i` dump, so until then this treats the
    // lit strength as 0 so a carried spare re-readies. Cleared on the next dump or
    // when we ready a fresh light.
    private bool _readiedLightBurnedOut;

    // A light-making spell cast whose item hasn't shown in a dump yet: the item's
    // name and the snapshot it was cast against. The cast is followed by an `i` so
    // the item lands in the snapshot; the next newer dump retires this either way.
    private (string Name, DateTimeOffset Stamp)? _awaitingMadeItem;

    // The room-light spell proved not enough: it was in effect and the room still
    // printed its can't-see line. Cast code, the room's Light there, the room, and
    // worn illumination at the time.
    private (string Code, int Light, RoomKey Room, int Worn)? _spellShort;

    // The last provisioning decision (a planned buy, a reorder, a stock-up), for
    // the bug report.
    private string? _lastPlannedBuy;

    private readonly List<IDisposable> _subs = new();

    public AutoLightProvisioner(
        Func<bool> isEnabled,
        Func<InventorySnapshot> snapshot,
        Func<IReadOnlyList<LightItem>> catalogue,
        Func<RoomKey, Room?> resolveRoom,
        Func<int> wornIllu,
        Func<RoomLightSpell?> roomLightSpell,
        Func<string, bool> castRoomLightSpell,
        Func<string, bool> roomLightSpellUp,
        Func<AutoLightSettings> settings,
        Func<Room?>? currentRoom = null,
        Func<bool>? inDarkRoom = null,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(isEnabled);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(catalogue);
        ArgumentNullException.ThrowIfNull(resolveRoom);
        ArgumentNullException.ThrowIfNull(wornIllu);
        ArgumentNullException.ThrowIfNull(roomLightSpell);
        ArgumentNullException.ThrowIfNull(castRoomLightSpell);
        ArgumentNullException.ThrowIfNull(roomLightSpellUp);
        ArgumentNullException.ThrowIfNull(settings);
        _isEnabled = isEnabled;
        _snapshot = snapshot;
        _catalogue = catalogue;
        _resolveRoom = resolveRoom;
        _wornIllu = wornIllu;
        _roomLightSpell = roomLightSpell;
        _castRoomLightSpell = castRoomLightSpell;
        _roomLightSpellUp = roomLightSpellUp;
        _settings = settings;
        // Unbound, no room is known (the latch then never sets) and no room reads dark.
        _currentRoom = currentRoom ?? (static () => null);
        _inDarkRoom = inDarkRoom ?? (static () => false);
        _log = log;
    }

    // Bind the wire-sender — the gate-wrapped engine pipeline from
    // MainWindowViewModel.
    public void SetWireSender(Action<byte[]> sender) => _wire.Bind(sender);

    // Bind the shop-detour hand-off — AutoLightShopRouter.OnBuyRequested.
    public void SetProvisioner(Action<AutoLightBuyRequest> provision) => _provision = provision;

    // Bind the reorder hand-off used while a loop or Auto-Lair drives movement
    // (AutoDepositManager.StartLightRestock), and what says one is running.
    public void SetLoopRestock(Func<bool> engineRunning, Func<AutoLightBuyRequest, LightRestockOutcome> restock)
    {
        ArgumentNullException.ThrowIfNull(engineRunning);
        ArgumentNullException.ThrowIfNull(restock);
        _engineRunning = engineRunning;
        _loopRestock = restock;
    }

    // Subscribe the lines the engine reacts to: the two can't-see lines light the
    // room, the burn-out line drops the latches on the light that went out. Called
    // by AppServices after the inventory's own burn-out handler, so the inventory
    // has taken the light off what is held before this runs.
    public void AttachRouter(MessageRouter router)
    {
        ArgumentNullException.ThrowIfNull(router);
        foreach (IDisposable s in _subs) s.Dispose();
        _subs.Clear();
        _subs.Add(router.Subscribe(KnownPatterns.RoomPitchBlack, _ => OnDarkRoomObserved()));
        _subs.Add(router.Subscribe(KnownPatterns.RoomVeryDark, _ => OnDarkRoomObserved()));
        _subs.Add(router.Subscribe(KnownPatterns.LightBurnedOut, _ => OnReadiedLightExpired()));
    }

    // Another character, another game-data set, or a death: what the engine
    // believed about its light and spell no longer holds.
    public void Forget(string why)
    {
        bool had = _pendingReadyName is not null || _autoReadiedName is not null
                   || _spellShort is not null || _awaitingMadeItem is not null;
        _pendingReadyName = null;
        _autoReadiedName = null;
        _readiedLightBurnedOut = false;
        _awaitingMadeItem = null;
        _spellShort = null;
        _reorderRequestedFor = null;
        if (had) _log?.Info(LogCategory, $"{why} — light latches cleared");
    }

    // One line for the bug report: what is lit, what the engine is waiting on, the
    // spell's standing, and the last thing it planned to buy.
    public string DescribeState()
    {
        InventorySnapshot snap = _snapshot();
        RoomLightSpell? spell = _roomLightSpell();
        string lit = snap.ReadiedLight is { } r ? $"{r.Name} (Readied/{r.Readied})" : "nothing";
        if (_readiedLightBurnedOut) lit += ", burned out since the last `i`";
        string spellText = spell is { } s
            ? $"{s.CastCode} ({s.Name}, {s.Kind}, illu {s.Strength}{(s.IsBuff ? (_roomLightSpellUp(s.CastCode) ? ", up" : ", not up") : string.Empty)})"
            : "none";
        string shortText = _spellShort is { } l
            ? $"{l.Code} not enough at Light {l.Light} or darker (in {l.Room}, worn illu {l.Worn})"
            : "no";
        return $"lit {lit}; auto-readied {_autoReadiedName ?? "none"}; pending use {_pendingReadyName ?? "none"}; "
            + $"spell {spellText}; spell latched short: {shortText}; "
            + $"awaiting made item {(_awaitingMadeItem is { } a ? a.Name : "none")}; "
            + $"reorder latched {(_reorderRequestedFor is { } ro ? ro.Name : "no")}; "
            + $"last planned buy {_lastPlannedBuy ?? "none"}";
    }

    // Route-planned handler bound to AutoWalkManager.SetRouteAnnouncer. Scans the
    // route, runs the planner, and hands a Buy / Reorder to the shop router. A
    // no-op unless the AutoLight master toggle is on.
    public void OnRoutePlanned(IReadOnlyList<RoomKey> route)
    {
        ArgumentNullException.ThrowIfNull(route);
        if (!_isEnabled()) return;

        InventorySnapshot snap = _snapshot();
        RetirePendingReady(snap);
        RetireReorderLatch(snap.ReadiedLight);

        AutoLightPlan plan = PlanFor(route, snap, out IReadOnlyList<LightItem> catalogue);
        switch (plan.Action)
        {
            case AutoLightAction.Ready:
                // Route-wide readying stays off: a light goes on only for the room
                // about to be entered or one that reports we can't see.
                _log?.Debug(LogCategory, $"route-dark ready left to the room-by-room path: {plan.Reason}");
                break;

            case AutoLightAction.Buy:
                RequestProvision(plan.LightName!, plan.BuyCount, catalogue, plan.Reason);
                break;

            case AutoLightAction.Reorder:
                RequestReorder(plan, snap.ReadiedLight, catalogue);
                break;
        }
    }

    // Advisor for an orchestrated errand (the auto-deposit reroute owns the walk):
    // given a route the character is about to traverse, decide whether it must buy a
    // light to cover it and, if so, what to buy. Sends nothing. Null when no buy is
    // warranted or the AutoLight master toggle is off.
    public AutoLightBuyRequest? PlanRouteBuy(IReadOnlyList<RoomKey> route)
    {
        ArgumentNullException.ThrowIfNull(route);
        if (!_isEnabled()) return null;
        AutoLightPlan plan = PlanFor(route, _snapshot(), out IReadOnlyList<LightItem> catalogue);
        if (plan.Action != AutoLightAction.Buy) return null;
        AutoLightBuyRequest? buy = ResolveBuy(plan.LightName!, plan.BuyCount, catalogue);
        if (buy is { } b) NotePlannedBuy($"{b.LightName} x{b.Count} for an errand's leg ({plan.Reason})");
        return buy;
    }

    // Advisor for a trip passing near a shop (a bank run's return leg, the walk back
    // from a trainer): what to buy to bring the pack up to Carry hours of light, or
    // null when it already holds that much, no light is wanted ahead, or this
    // character keeps no stock (spell-only mode, Carry hours 0, AutoLight off).
    // The light is the Preferred one, else the kind lit now. roomsAhead is what the
    // trip goes back to (the loop circuit); a light is wanted when a room there is
    // dark to worn gear and the buff spell, or a light is lit right now (an
    // Auto-Lair run has no circuit to read). Only whole lights under the target are
    // bought, so a pack a little short isn't sent shopping.
    public AutoLightBuyRequest? PlanStockUp(IReadOnlyList<RoomKey> roomsAhead)
    {
        ArgumentNullException.ThrowIfNull(roomsAhead);
        if (!_isEnabled()) return null;
        AutoLightSettings s = _settings();
        if (s.UseRoomLightSpellOnly || s.CarryHours <= 0) return null;

        InventorySnapshot snap = _snapshot();
        IReadOnlyList<LightItem> catalogue = _catalogue();
        LightItem? light = FindByName(s.PreferredLightName, catalogue)
            ?? (snap.ReadiedLight is { } lit ? FindByName(lit.Name, catalogue) : null);
        if (light is not { } stock || stock.BurnTime <= TimeSpan.Zero) return null;

        bool lightWanted = snap.ReadiedLight is not null
            || RouteLightScanner.Scan(roomsAhead, _resolveRoom, _wornIllu() + BuffStrength(_roomLightSpell())).NeedsLight;
        if (!lightWanted) return null;

        TimeSpan have = CarriedLightTime(snap, catalogue);
        TimeSpan shortBy = TimeSpan.FromHours(s.CarryHours) - have;
        int more = (int)Math.Floor(shortBy / stock.BurnTime);
        if (more < 1) return null;

        int target = CarriedCopies(snap.CarriedItems, stock) + more;
        if (ResolveBuy(stock.Name, target, catalogue) is not { } buy) return null;
        NotePlannedBuy($"{buy.LightName} x{more} to stock up (carrying ~{have.TotalHours:0.#} h of {s.CarryHours} h)");
        _log?.Info(LogCategory,
            $"stock-up planned: {more} more {stock.Name} (carrying ~{have.TotalHours:0.#} h of light, target {s.CarryHours} h)");
        return buy;
    }

    // Asked by the auto-get path for each item on a room's floor: true when it is a
    // light worth picking up — AutoLight is on, items are allowed (not spell-only
    // mode), it is a light that projects illumination, and the pack holds less than
    // Carry hours of light (with Carry hours 0, when no light is carried at all).
    public bool WantsFloorLight(int itemNumber)
    {
        if (!_isEnabled()) return false;
        AutoLightSettings s = _settings();
        if (s.UseRoomLightSpellOnly) return false;
        IReadOnlyList<LightItem> catalogue = _catalogue();
        LightItem? light = null;
        foreach (LightItem l in catalogue)
            if (l.Number == itemNumber) { light = l; break; }
        if (light is not { Strength: > 0 }) return false;

        InventorySnapshot snap = _snapshot();
        if (s.CarryHours <= 0)
            return snap.ReadiedLight is null && CarriedLights(snap.CarriedItems, catalogue).Count == 0;
        return CarriedLightTime(snap, catalogue) < TimeSpan.FromHours(s.CarryHours);
    }

    // Reorder poll bound to InventoryManager.Changed. An i dump is the only moment
    // the readied light's charge refreshes, so this is where a dwindling supply is
    // caught; it is also where the item a light-making spell made shows up.
    public void OnInventoryChanged()
    {
        if (!_isEnabled()) return;

        InventorySnapshot snap = _snapshot();
        // A fresh `i` dump is ground truth for what's readied, so the burned-out
        // bridge flag has served its purpose.
        _readiedLightBurnedOut = false;
        RetireReorderLatch(snap.ReadiedLight);
        TakeMadeItem(snap);
        if (snap.ReadiedLight is null) return;

        AutoLightPlan plan = PlanFor(RouteLightScan.Empty, snap, out IReadOnlyList<LightItem> catalogue);
        if (plan.Action == AutoLightAction.Reorder)
            RequestReorder(plan, snap.ReadiedLight, catalogue);
    }

    // The burn-out line: the readied light is gone, though the snapshot won't say so
    // until the next `i`. If the room is dark the game's can't-see line follows and
    // the dark-room path readies a spare; here only the latches keyed on the gone
    // light are dropped, so a re-ready isn't blocked as "still in flight" (the
    // stuck-blind report) and OnRoomEntered doesn't `rem` a light that is gone.
    private void OnReadiedLightExpired()
    {
        if (!_isEnabled()) return;
        _readiedLightBurnedOut = true;
        _pendingReadyName = null;
        _autoReadiedName = null;
        _log?.Info(LogCategory, "readied light burned out — cleared latches for re-ready");
    }

    // A live "can't see" line — the server saying this room is unseeable right now.
    // The fallback for a room the graph can't score (an unmapped next room never
    // reaches OnApproachingRoom), and the check that what was lit ahead worked.
    private void OnDarkRoomObserved()
    {
        if (!_isEnabled()) return;
        RoomLightSpell? spell = _roomLightSpell();
        bool spellOnly = _settings().UseRoomLightSpellOnly;
        Room? here = _currentRoom();

        // The buff goes first: it adds to whatever light is lit.
        if (spell is { IsBuff: true } buff && !_roomLightSpellUp(buff.CastCode)
            && !SpellShortFor(buff, here)
            && CastRoomLightSpell(buff, "dark room observed"))
        {
            // A cast lights the room without showing it; the bare CR redisplays it,
            // as after `use`, so a monster standing here unseen is listed.
            _wire.Send(string.Empty);
            return;
        }

        if (TryReadyBestCarried("dark room observed: ready carried light", spell, spellOnly))
        {
            // `use <light>` only grants vision — it does NOT redisplay the room
            // (GAME_MECHANICS "Light sources"); the bare CR does, revealing standing
            // mobs. Only after a `use` actually went out, so a repeated can't-see
            // line doesn't spam CRs.
            if (_readyJustSentUse) _wire.Send(string.Empty);
            return;
        }

        if (spell is not { } s) return;
        if (SpellInEffect(s))
        {
            LatchSpellShort(s, here);
            return;
        }
        if (s.Kind == RoomLightSpellKind.MakesLightItem && !SpellShortFor(s, here))
            CastLightMakingSpell(s, "dark room observed: nothing carried lights it");
    }

    // Predictive one-room lookahead. The walker / loop-runner call this the instant
    // they commit to a step, with the room about to be entered. If that room reads
    // dark (its mapped Rooms.Light against worn gear, the buff when up, and what is
    // lit), the buff goes up and / or a carried light is `use`d NOW — ahead of the
    // move bytes on the same dispatch, so the room renders on arrival. A one-room
    // horizon lights only in the last lit room before the dark one, so a light's
    // burn timer isn't spent early; a mis-mapped room self-corrects when
    // OnRoomEntered `rem`s it. A room the graph can't resolve is left to the
    // can't-see line.
    public void OnApproachingRoom(RoomKey target)
    {
        if (!_isEnabled()) return;
        if (_resolveRoom(target) is not { } room) return;   // unmapped — reactive path covers it
        int worn = _wornIllu();
        if (LightModel.IlluGapToSee(worn, room.Light) == 0) return;   // seeable on worn gear

        InventorySnapshot snap = _snapshot();
        RetirePendingReady(snap);
        IReadOnlyList<LightItem> catalogue = _catalogue();
        RoomLightSpell? spell = _roomLightSpell();
        bool spellOnly = _settings().UseRoomLightSpellOnly;
        int buffUp = BuffStrength(spell);
        int lit = LitStrength(snap, catalogue);
        if (LightModel.IlluGapToSee(worn + buffUp + lit, room.Light) == 0) return;   // what's up covers it

        if (spell is { IsBuff: true } buff && buffUp == 0 && !SpellShortFor(buff, room)
            && CastRoomLightSpell(buff, $"approaching dark room {target}")
            && LightModel.IlluGapToSee(worn + buff.Strength + lit, room.Light) == 0)
            return;   // the buff alone will cover it

        if (TryReadyBestCarried($"approaching dark room {target}: ready carried light", spell, spellOnly)) return;
        if (spell is { Kind: RoomLightSpellKind.MakesLightItem } maker && !SpellInEffect(maker)
            && !SpellShortFor(maker, room))
            CastLightMakingSpell(maker, $"approaching dark room {target}: nothing carried lights it");
    }

    // Stepping into a room the graph knows the light level of: if THIS engine has a
    // light readied and the room is seeable without it (worn gear plus the buff when
    // up), put the light away with `rem`. Guarded upstream by
    // !RoomTracker.IsInDarkRoom so a dark room the game hasn't rendered never
    // reaches here. A light the player readied by hand is left untouched.
    public void OnRoomEntered(Room room)
    {
        ArgumentNullException.ThrowIfNull(room);
        if (!_isEnabled()) return;
        if (_autoReadiedName is not { } lit) return;
        if (LightModel.IlluGapToSee(_wornIllu() + BuffStrength(_roomLightSpell()), room.Light) != 0) return;
        if (SneakKept($"putting away {lit}")) return;

        _wire.Send($"rem {lit}");
        _autoReadiedName = null;
        _pendingReadyName = null;
        _log?.Info(LogCategory, $"removed auto-readied {lit} — room seeable without it");
    }

    // Ready the best carried light for a room we can't see — shared by the reactive
    // and predictive paths. Readies UNLESS what's already lit is at least as strong
    // (a weaker torch over a lit lantern only downgrades). In spell-only mode the
    // only light it may ready is the one the room-light spell made. Returns true
    // when a covering carried light was readied (or a re-ready is already in
    // flight); false when nothing carried can help.
    private bool TryReadyBestCarried(string reason, RoomLightSpell? spell, bool spellOnly)
    {
        InventorySnapshot snap = _snapshot();
        RetirePendingReady(snap);

        IReadOnlyList<LightItem> catalogue = _catalogue();
        IReadOnlyList<LightItem> carried = CarriedLights(snap.CarriedItems, catalogue);
        if (spellOnly)
            carried = spell is { MadeItem: { } made }
                ? carried.Where(l => l.Number == made.Number).ToList()
                : Array.Empty<LightItem>();
        if (carried.Count == 0) return false;

        LightItem? pick = (spellOnly ? null : FindByName(_settings().PreferredLightName, carried))
            ?? StrongestCarried(carried);
        if (pick is not { } chosen) return false;

        int litStrength = !_readiedLightBurnedOut && snap.ReadiedLight is { } lit
            ? StrengthOf(lit.Name, catalogue)
            : 0;
        if (chosen.Strength <= litStrength) return false;   // nothing carried beats what's lit

        // Swap only when a genuinely-lit different light is being replaced; a
        // burned-out light needs no `rem` (it's already gone).
        string? swapFrom = _readiedLightBurnedOut ? null : snap.ReadiedLight?.Name;
        ReadyLight(chosen.Name, swapFrom, snap.LastUpdated, reason);
        return true;
    }

    // Cast the room-light spell. Anti-spam is the caller's (never while it is up or
    // in effect, never once latched short) plus CastCoordinator's round cooldown.
    private bool CastRoomLightSpell(RoomLightSpell spell, string reason)
    {
        if (SneakKept($"room-light spell '{spell.CastCode}'")) return false;
        if (!_castRoomLightSpell(spell.CastCode))
        {
            _log?.Debug(LogCategory, $"room-light spell '{spell.CastCode}' not cast now (cast slot busy) — {reason}");
            return false;
        }
        _log?.Info(LogCategory, $"cast room-light spell '{spell.CastCode}' ({spell.Name}) — {reason}");
        return true;
    }

    // Cast a spell that makes a light item, then read the inventory so the item
    // lands in the snapshot: the dump readies it (TakeMadeItem) while the room is
    // dark, or the next can't-see line does. No second cast while one is awaited.
    private void CastLightMakingSpell(RoomLightSpell spell, string reason)
    {
        if (_awaitingMadeItem is not null || spell.MadeItem is not { } made) return;
        DateTimeOffset stamp = _snapshot().LastUpdated;
        if (!CastRoomLightSpell(spell, reason)) return;
        _awaitingMadeItem = (made.Name, stamp);
        _wire.Send("i");
    }

    // The first dump newer than a light-making cast: the item is there (lit at once
    // when we stand in the dark) or the cast didn't take (a later cast may retry).
    private void TakeMadeItem(InventorySnapshot snap)
    {
        if (_awaitingMadeItem is not { } awaited || snap.LastUpdated <= awaited.Stamp) return;
        _awaitingMadeItem = null;
        IReadOnlyList<LightItem> carried = CarriedLights(snap.CarriedItems, _catalogue());
        if (!carried.Any(l => string.Equals(l.Name, awaited.Name, StringComparison.OrdinalIgnoreCase)))
        {
            _log?.Info(LogCategory, $"room-light spell made no {awaited.Name} — the cast didn't take");
            return;
        }
        _log?.Info(LogCategory, $"room-light spell made a {awaited.Name}");
        if (!_inDarkRoom()) return;   // the next dark room readies it
        if (TryReadyBestCarried($"{awaited.Name} made in a dark room", _roomLightSpell(), _settings().UseRoomLightSpellOnly)
            && _readyJustSentUse)
            _wire.Send(string.Empty);
    }

    // Whether the spell is lighting us right now: the buff's timer is running, or the
    // item it makes is lit (or its `use` / its making is in flight).
    private bool SpellInEffect(RoomLightSpell spell)
    {
        if (spell.IsBuff) return _roomLightSpellUp(spell.CastCode);
        if (spell.MadeItem is not { } made) return false;
        if (_awaitingMadeItem is not null) return true;
        if (string.Equals(_pendingReadyName, made.Name, StringComparison.OrdinalIgnoreCase)) return true;
        return !_readiedLightBurnedOut
            && string.Equals(_snapshot().ReadiedLight?.Name, made.Name, StringComparison.OrdinalIgnoreCase);
    }

    // The spell was in effect and the room still can't be seen. Illumination adds
    // up and the spell's share is fixed, so it is not enough in any room as dark or
    // darker either, while worn gear is no better: latch on the room's Light rather
    // than on this one room (a dark area is many rooms of one Light, and a room
    // latch would cast again in each) or until the spell runs out (it would be cast
    // again in the same dark). A brighter room can still be lit by it. An unknown
    // room sets nothing; the spell being in effect already keeps it from recasting.
    private void LatchSpellShort(RoomLightSpell spell, Room? here)
    {
        if (here is null)
        {
            _log?.Debug(LogCategory, $"room-light spell '{spell.CastCode}' in effect and still dark in an unknown room");
            return;
        }
        if (SpellShortFor(spell, here)) return;
        int worn = _wornIllu();
        _spellShort = (spell.CastCode, here.Light, here.Key, worn);
        _log?.Info(LogCategory,
            $"room-light spell '{spell.CastCode}' is not enough in {here.Key} (Light {here.Light}): "
            + $"not casting it for a room this dark or darker while worn illu stays {worn}");
    }

    private bool SpellShortFor(RoomLightSpell spell, Room? room)
    {
        if (_spellShort is not { } latch || room is null) return false;
        if (!string.Equals(latch.Code, spell.CastCode, StringComparison.OrdinalIgnoreCase)) return false;
        if (_wornIllu() > latch.Worn) return false;
        return room.Light <= latch.Light || room.Key.Equals(latch.Room);
    }

    // The buff's strength while it is up, else 0 — a light-making spell adds nothing
    // on its own.
    private int BuffStrength(RoomLightSpell? spell)
        => spell is { IsBuff: true } b && _roomLightSpellUp(b.CastCode) ? b.Strength : 0;

    // What's lit right now, counting a `use` still in flight: 0 when nothing or the
    // light just burned out.
    private int LitStrength(InventorySnapshot snap, IReadOnlyList<LightItem> catalogue)
    {
        if (_pendingReadyName is { } pending) return StrengthOf(pending, catalogue);
        return !_readiedLightBurnedOut && snap.ReadiedLight is { } lit ? StrengthOf(lit.Name, catalogue) : 0;
    }

    private void RetirePendingReady(InventorySnapshot snap)
    {
        if (_pendingReadyName is not null
            && (string.Equals(snap.ReadiedLight?.Name, _pendingReadyName, StringComparison.OrdinalIgnoreCase)
                || snap.LastUpdated > _pendingSnapshotTime))
            _pendingReadyName = null;
    }

    // The planner's verdict for a route. The buff counts toward coverage (it is the
    // baseline the player keeps up); a light-making spell counts as one more carried
    // light, since what it makes is one.
    private AutoLightPlan PlanFor(IReadOnlyList<RoomKey> route, InventorySnapshot snap, out IReadOnlyList<LightItem> catalogue)
        => PlanFor(RouteLightScanner.Scan(route, _resolveRoom, _wornIllu()), snap, out catalogue);

    private AutoLightPlan PlanFor(RouteLightScan scan, InventorySnapshot snap, out IReadOnlyList<LightItem> catalogue)
    {
        catalogue = _catalogue();
        RoomLightSpell? spell = _roomLightSpell();
        IReadOnlyList<LightItem> carried = CarriedLights(snap.CarriedItems, catalogue);
        if (spell is { MadeItem: { } made } && !carried.Any(l => l.Number == made.Number))
            carried = carried.Append(made).ToList();
        return AutoLightPlanner.Plan(
            scan, _wornIllu(), spell is { IsBuff: true } b ? b.Strength : 0, snap.ReadiedLight,
            carried, catalogue, _settings());
    }

    private static LightItem? StrongestCarried(IReadOnlyList<LightItem> carried)
    {
        LightItem? best = null;
        foreach (LightItem l in carried)
            if (best is not { } b || l.Strength > b.Strength) best = l;
        return best;
    }

    private static LightItem? FindByName(string? name, IReadOnlyList<LightItem> lights)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        foreach (LightItem l in lights)
            if (string.Equals(l.Name, name.Trim(), StringComparison.OrdinalIgnoreCase))
                return l;
        return null;
    }

    // The catalogue strength of a light by name, or 0 when the name isn't a
    // catalogue light.
    private static int StrengthOf(string name, IReadOnlyList<LightItem> catalogue)
        => FindByName(name, catalogue)?.Strength ?? 0;

    // Fire a reorder once per readied-light instance. While a loop or Auto-Lair
    // drives movement it goes to the loop restock trip (latched unless that trip is
    // only busy for now); on a walk, to the shop router.
    private void RequestReorder(
        AutoLightPlan plan, ReadiedLight? current, IReadOnlyList<LightItem> catalogue)
    {
        if (_reorderRequestedFor is not null) return;
        if (_loopRestock is { } restock && _engineRunning?.Invoke() == true)
        {
            if (ResolveBuy(plan.LightName!, plan.BuyCount, catalogue) is not { } req) return;
            LightRestockOutcome outcome = restock(req);
            if (outcome == LightRestockOutcome.Busy)
            {
                _log?.Debug(LogCategory, $"loop reorder waits — another trip has the walker ({plan.Reason})");
                return;
            }
            // Latched either way: a trip that can't be made from here won't become
            // possible on the next `i` dump, and retrying would log it on every one.
            _reorderRequestedFor = current;
            if (outcome == LightRestockOutcome.Started)
            {
                NotePlannedBuy($"{req.LightName} x{req.Count} on a loop restock trip ({plan.Reason})");
                _log?.Info(LogCategory, $"loop reorder: restock trip for {req.LightName} x{req.Count} ({plan.Reason})");
            }
            else
                _log?.Info(LogCategory, $"loop reorder: no restock trip for {req.LightName} possible from here ({plan.Reason})");
            return;
        }
        if (RequestProvision(plan.LightName!, plan.BuyCount, catalogue, plan.Reason))
            _reorderRequestedFor = current;
    }

    // Retire the reorder latch once the light we reordered for is gone or a fresh
    // one is lit (a climb in Readied, a name change or a null).
    private void RetireReorderLatch(ReadiedLight? current)
    {
        if (_reorderRequestedFor is not { } prev) return;
        if (current is not { } cur
            || !string.Equals(cur.Name, prev.Name, StringComparison.OrdinalIgnoreCase)
            || cur.Readied > prev.Readied)
            _reorderRequestedFor = null;
    }

    // SneakGuard.Holds — `rem` and a cast end a sneak (GAME_MECHANICS "Sneaking —
    // commands, equip order, and the sneak state machine" → "What ends a sneak").
    // Lighting with `use` doesn't: it ends one only when the item casts a
    // spell, and a light carries an illumination ability, not a spell, so `use
    // <light>` is never held. Held light changes simply re-evaluate on the next room.
    private Func<bool>? _sneakKept;

    public void SetSneakKeptProbe(Func<bool> sneakKept) => _sneakKept = sneakKept;

    private bool SneakKept(string what)
    {
        if (_sneakKept?.Invoke() != true) return false;
        _log?.Debug(LogCategory, $"{what} held — it would end the sneak we're keeping");
        return true;
    }

    private void ReadyLight(string name, string? readiedName, DateTimeOffset snapTime, string reason)
    {
        _readyJustSentUse = false;

        // The pending `use` is still in flight (snapshot hasn't caught up) — don't
        // fire the same one again on this stale snapshot.
        if (_pendingReadyName is not null
            && string.Equals(name, _pendingReadyName, StringComparison.OrdinalIgnoreCase))
            return;

        // Swap: a different light is lit — put it away before lighting the new
        // one (Stock refuses a second light; GAME_MECHANICS "Light sources").
        if (readiedName is not null
            && !string.Equals(readiedName, name, StringComparison.OrdinalIgnoreCase))
        {
            if (SneakKept($"swapping {readiedName} for {name}")) return;
            _wire.Send($"rem {readiedName}");
        }

        _wire.Send($"use {name}");
        _readyJustSentUse = true;
        _pendingReadyName = name;
        _pendingSnapshotTime = snapTime;
        _autoReadiedName = name;
        _readiedLightBurnedOut = false;
        _log?.Info(LogCategory, $"readied {name} ({reason})");
    }

    // Hand a Buy / Reorder verdict to the shop-detour router. Returns whether the
    // hand-off fired — the reorder latch keys off that so a dropped request (no
    // router wired / unknown id) doesn't wedge the latch shut.
    private bool RequestProvision(
        string name, int count, IReadOnlyList<LightItem> catalogue, string reason)
    {
        if (_provision is null)
        {
            _log?.Debug(LogCategory, $"buy not handed off (no shop router bound): {reason}");
            return false;
        }
        if (ResolveBuy(name, count, catalogue) is not { } req) return false;

        NotePlannedBuy($"{req.LightName} x{req.Count} before the walk ({reason})");
        _log?.Info(LogCategory, $"provisioning plan: {req.LightName} x{req.Count} — {reason}");
        _provision(req);
        return true;
    }

    private void NotePlannedBuy(string what) =>
        _lastPlannedBuy = $"{what} at {DateTimeOffset.Now.ToLocalTime():HH:mm:ss}";

    // Resolve a light name + carry count into a buy request, looking up the light's
    // MDB id from the catalogue (shop / carried-count lookups key on id). Null when
    // the name isn't a catalogue light (no id to buy against).
    private AutoLightBuyRequest? ResolveBuy(string name, int count, IReadOnlyList<LightItem> catalogue)
    {
        if (FindByName(name, catalogue) is not { Number: > 0 } light)
        {
            _log?.Debug(LogCategory, $"buy skipped: no catalogue id for '{name}'");
            return null;
        }
        return new AutoLightBuyRequest(light.Number, light.Name, Math.Max(1, count));
    }

    // Hours of light the pack holds: every carried copy at its full burn time (a
    // spare's charge is unknown until it is lit) plus what the lit one has left.
    private static TimeSpan CarriedLightTime(InventorySnapshot snap, IReadOnlyList<LightItem> catalogue)
    {
        TimeSpan total = snap.ReadiedLight is { } lit ? lit.RemainingTime : TimeSpan.Zero;
        foreach (string token in snap.CarriedItems)
        {
            (int count, string bare) = CountedCommand.SplitLeadingCount(token.Trim());
            if (FindByName(bare, catalogue) is { } light)
                total += light.BurnTime * count;
        }
        return total;
    }

    private static int CarriedCopies(IReadOnlyList<string> carried, LightItem light)
    {
        int copies = 0;
        foreach (string token in carried)
        {
            (int count, string bare) = CountedCommand.SplitLeadingCount(token.Trim());
            if (string.Equals(bare, light.Name, StringComparison.OrdinalIgnoreCase)) copies += count;
        }
        return copies;
    }

    // The carried-but-unworn tokens from the last `i` dump that name a light in
    // the active catalogue. A stack keeps its leading count in the dump ("5
    // torch"), but the catalogue keys on the bare name, so the count is stripped
    // (CountedCommand.SplitLeadingCount) before a case-insensitive match —
    // otherwise a pack of torches reads as zero carried lights and the engine never
    // readies (or, worse, buys more).
    private static IReadOnlyList<LightItem> CarriedLights(
        IReadOnlyList<string> carried, IReadOnlyList<LightItem> catalogue)
    {
        List<LightItem>? lights = null;
        foreach (string token in carried)
            if (FindByName(CountedCommand.SplitLeadingCount(token.Trim()).Name, catalogue) is { } light)
                (lights ??= new List<LightItem>()).Add(light);
        return (IReadOnlyList<LightItem>?)lights ?? Array.Empty<LightItem>();
    }
}
