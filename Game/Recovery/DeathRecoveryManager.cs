using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using MudPlay.Game.Combat;
using MudPlay.Game.Inventory;
using MudPlay.Game.Map;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.Terminal;

namespace MudPlay.Game.Recovery;

// Death observation aggregator. Composes DeathLineWatcher.PlayerDied and the
// per-character CharacterProfile.DeathHistory into a live observable shape that
// the Workshop DEATH section binds to.
//
// Surfaces the loaded profile's Records (the deathpile grid), the persisted
// AutoRecover / AutoEquip toggles, and the recovery actions. The recovery state
// machine drives each record's Active → Partial → Recovered transition off
// observed room re-entry and "You pick up …" confirmations. Current lives are
// surfaced separately on the Character Info tab (from PlayerStats.Lives), not
// here.
//
// The @comeback remote command is a separate party-pickup flow (stranded-
// follower → leader) owned by PartyComebackManager, not this aggregator — it has
// nothing to do with death recovery.
public sealed partial class DeathRecoveryManager : ObservableObject, IDisposable
{
    // LogService category — appears as [DeathRecovery] rows per observation +
    // comeback request.
    public const string LogCategory = "DeathRecovery";

    private readonly DeathLineWatcher _deathWatcher;
    private readonly ProfileService _profile;
    private readonly RoomTracker _roomTracker;
    private readonly LogService? _log;
    private AutoWalkManager? _walker;
    private Action<byte[]>? _wireSender;
    private LineExtractor? _lines;
    private Func<InventorySnapshot>? _inventorySnapshot;
    private Func<IReadOnlyList<TranscriptSnapshot.Line>>? _transcriptTail;
    private bool _disposed;

    // In-progress recovery context — one deathpile at a time (you stand in one
    // room). _activeRecovery is the record we're recovering. Stock's deathpile is
    // a single "corpse of <given-name>" object recovered by ONE `recover corpse
    // <name>` command (not a per-item get), so there's no per-item remaining set:
    // the corpse either shows in the room's "You notice" survey (recover it) or it
    // doesn't (mark Missing). _grabOnSurvey arms the recover for the next survey —
    // the survey line lands AFTER the room-change fires, so we can't read it on
    // entry. _pendingRecoverNow is a record the user pressed "Recover Now" on while
    // away — it forces a grab on arrival even when Auto-Recover is off.
    private DeathRecord? _activeRecovery;
    private DeathRecord? _pendingRecoverNow;
    private bool _grabOnSurvey;
    private GroundItemTracker? _groundItems;

    // Combat-aware re-equip interleaving. Recovering the corpse itself
    // (`recover corpse`) doesn't break combat, but each re-equip (wear/eq) DOES —
    // exactly like a between-round cast (see OutboundCastObserver). So when the
    // corpse comes back in a room with a live hostile, the wear/eq burst is paced
    // a few pieces per combat round instead of firing all at once, letting the
    // weapon attack keep landing between bursts; the remainder is flushed the
    // moment the room clears. _pendingEquip holds the ordered pieces still to go
    // on; _equipRoom pins the room they belong to (abandon if we leave it). All
    // delegates are wired by AppServices after the combat engine exists; unbound
    // (tests / no hostile), ReequipAllWorn sends everything at once as before.
    private readonly Queue<DeathItem> _pendingEquip = new();
    private (int Map, int Room)? _equipRoom;
    private bool _recoveryGateHeld;
    private Func<bool>? _hostilesPresent;
    private Action? _armCombatResume;
    private Action? _assertRecoveryGate;
    private Action? _clearRecoveryGate;
    private Func<string, int>? _armourClass;

    // Pieces re-equipped per combat round while a hostile is up. A wear/eq breaks
    // the round, so ~4 fits the 5 s round window and still leaves time for the
    // re-attack to land before the next round (user-tuned "3-4 pieces / ~3 s").
    private const int EquipBurstPerRound = 4;

    // Stock ground recovery. On Stock, death scatters items LOOSE on the floor
    // (Paradigm packs them in a corpse — the path is chosen by _isParadigm). We
    // `get <name>` each pile item that's actually in the survey (never an absent
    // one — that was the old get-spam), confirmed one at a time by "You took
    // <name>."; when the whole pile is back the record finalises and worn gear
    // re-equips (paced by the combat interleave). Items not on this floor stay
    // unrecovered — the pile holds at Partial, retried on re-entry (and, on a
    // deliberate recovery, swept from adjacent rooms — later stage). _stockRecovering
    // scopes the "You took" tracking to our own in-progress grab.
    private Func<bool>? _isParadigm;
    private bool _stockRecovering;
    // Heartbeats of quiet before the death-room `get` burst counts as settled (so
    // the spillover sweep can start on the leftovers). Reset by each "You took".
    private int _stockSettleTicks;
    // A deliberate recovery (Recover Now, or auto-recover walked TO the death room)
    // earns the adjacent-room spillover sweep; a pass-through walk does not.
    private bool _deliberateRecovery;
    // Set when a deliberate Stock recovery wanted to sweep but a hostile was still
    // in the death room — the heartbeat starts the sweep once the room clears.
    private bool _stockSweepPending;
    // Pass-through spillover grab (Stock only): auto-recover walking through a room
    // ADJACENT to an unrecovered deathpile grabs our overflow there in-stride, no
    // detour. Kept separate from the death-room grab so both can run on one walk.
    private DeathRecord? _spilloverPile;
    private bool _spilloverGrabOnSurvey;
    private bool _spilloverRecovering;
    private readonly DeathGroundSweep _sweep;

    // Heartbeats (1 s) of quiet after the death-room `get` burst before it counts
    // as settled and the sweep can start on the leftovers.
    private const int StockSettleTicks = 2;

    public DeathRecoveryManager(
        DeathLineWatcher deathWatcher,
        ProfileService profile,
        RoomTracker roomTracker,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(deathWatcher);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(roomTracker);
        _deathWatcher = deathWatcher;
        _profile = profile;
        _roomTracker = roomTracker;
        _log = log;
        // The spillover sweep drives `look`/`get` through our own Send and the
        // walker (bound later via AttachWalker) for its adjacent-room detours.
        _sweep = new DeathGroundSweep(Send, key => { _walker?.WalkTo(key); }, log);

        _deathWatcher.PlayerDied += OnPlayerDied;
        // Re-entering a room that holds one of our deathpiles drives the
        // Active → Partial → Recovered transitions (and the auto-grab).
        _roomTracker.StateChanged += OnRoomChanged;
        // A death record was just appended — snapshot the backscroll tail now,
        // before the graveyard room display floods scrollback and pushes the
        // fatal scene out of the "How did I Die?" window.
        _roomTracker.PlayerDeathObserved += OnDeathObserved;
    }

    private void OnPlayerDied(PlayerDiedEvent evt)
    {
        _log?.Info(LogCategory, $"player slain by={evt.Killer}");
        // The death record is written separately by
        // DeathDetector → RoomTracker.NoteDeath → profile.DeathHistory.
        // Nudge the grid so the new record surfaces once that write lands.
        OnPropertyChanged(nameof(Records));
    }

    // Wire the walker used by WalkToDeathRoom / RecoverNow. Set post-construction
    // because the AutoWalkManager is built after this manager in AppServices. The
    // spillover sweep also needs the walker's arrival events to advance its
    // collect-and-return legs.
    public void AttachWalker(AutoWalkManager walker)
    {
        if (ReferenceEquals(_walker, walker)) return;
        if (_walker is not null) _walker.Event -= OnWalkEvent;
        _walker = walker;
        _walker.Event += OnWalkEvent;
    }

    private void OnWalkEvent(WalkEvent evt)
    {
        if (_sweep.Active && evt.Kind == WalkEventKind.Finished && evt.Destination is { } arrived)
            _sweep.OnWalkerArrived(arrived);
    }

    // Bind the gate-wrapped wire sender so auto-recover can send get / wear /
    // hold commands. Bound by MainWindowViewModel on connect; unbound, the grab +
    // re-equip are no-ops (status transitions still follow observed pickups).
    public void SetWireSender(Action<byte[]> sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        _wireSender = sender;
    }

    // Bind the per-session LineExtractor so the manager can watch for
    // "You pick up ..." confirmations that drive the Partial → Recovered
    // transition (and the per-item re-equip). Bound by MainWindowViewModel on
    // connect.
    public void AttachLineExtractor(LineExtractor lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (ReferenceEquals(_lines, lines)) return;
        if (_lines is not null) _lines.LineEmitted -= OnLine;
        _lines = lines;
        _lines.LineEmitted += OnLine;
    }

    // Bind the room's floor-survey tracker. Auto-recover reads its parsed "You
    // notice" list to confirm the corpse is actually in the room before sending
    // `recover corpse`, and arms off its SurveyUpdated event (the survey lands
    // after the room-change fires, so it can't be read on entry). Wired by
    // AppServices once GroundItems exists (it's built after this manager).
    public void AttachGroundItems(GroundItemTracker ground)
    {
        ArgumentNullException.ThrowIfNull(ground);
        if (ReferenceEquals(_groundItems, ground)) return;
        if (_groundItems is not null) _groundItems.SurveyUpdated -= OnSurveyUpdated;
        _groundItems = ground;
        _groundItems.SurveyUpdated += OnSurveyUpdated;
    }

    // Wire the combat-interleaving probes so an in-combat corpse recovery paces
    // its re-equip across rounds instead of dumping the whole wear/eq burst (which
    // would repeatedly break the round). hostilesPresent reports a live engageable
    // hostile in the room; armCombatResume nudges the combat engine to re-attack
    // on the *Combat Off* a burst produces (same signal a between-round cast arms);
    // assert/clearRecoveryGate hold the walker on the CorpseRecovery gate while
    // pieces are still going on; armourClass returns an item's game-data ArmourClass
    // for the highest-AC-first ordering. Bound by AppServices once the combat
    // engine + tick exist; the tick drives OnRecoveryCombatRound / OnRecoveryHeartbeat.
    // Unbound, ReequipAllWorn falls back to the immediate all-at-once burst.
    public void AttachCombatInterleave(
        Func<bool> hostilesPresent,
        Action armCombatResume,
        Action assertRecoveryGate,
        Action clearRecoveryGate,
        Func<string, int> armourClass)
    {
        ArgumentNullException.ThrowIfNull(hostilesPresent);
        ArgumentNullException.ThrowIfNull(armCombatResume);
        ArgumentNullException.ThrowIfNull(assertRecoveryGate);
        ArgumentNullException.ThrowIfNull(clearRecoveryGate);
        ArgumentNullException.ThrowIfNull(armourClass);
        _hostilesPresent = hostilesPresent;
        _armCombatResume = armCombatResume;
        _assertRecoveryGate = assertRecoveryGate;
        _clearRecoveryGate = clearRecoveryGate;
        _armourClass = armourClass;
    }

    // Bind the realm probe that chooses the recovery mechanic: Paradigm packs the
    // pile into a `corpse of <name>` (one `recover corpse`), Stock scatters it
    // loose on the floor (per-item `get`). Wired by AppServices from the active
    // game-data set's realm. Unbound, recovery defaults to the corpse path.
    public void SetRealmProbe(Func<bool> isParadigm)
    {
        ArgumentNullException.ThrowIfNull(isParadigm);
        _isParadigm = isParadigm;
    }

    // Bind the live inventory snapshot provider so SimulateDeath captures a
    // realistic deathpile. Real deaths capture via RoomTracker.NoteDeath; this is
    // only for the test button.
    public void AttachInventorySnapshot(Func<InventorySnapshot> provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _inventorySnapshot = provider;
    }

    // Bind the backscroll-tail provider — the live terminal transcript's last
    // ~200 lines, oldest → newest. Bound by MainWindowViewModel, where the
    // Emulator lives. Unbound (headless / test paths), death logs simply aren't
    // captured: the record still exists, just without a "How did I Die?" replay.
    public void AttachTranscriptTail(Func<IReadOnlyList<TranscriptSnapshot.Line>> provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _transcriptTail = provider;
    }

    // The loaded profile's death history (oldest → newest). Empty when no profile
    // is loaded or the lucky character has never died. The DEATH grid sorts this
    // newest-first for display.
    public IReadOnlyList<DeathRecord> Records =>
        _profile.Current?.DeathHistory is { } list ? list : Array.Empty<DeathRecord>();

    // Worn pieces still queued for the combat-paced re-equip (0 when idle) — a
    // bug report taken mid-recovery shows how far the in-combat burst has drained.
    public int PendingReequipCount => _pendingEquip.Count;

    // Auto-grab a deathpile's lost items (ignoring per-item auto-get policy) when
    // re-entering the death room. Persisted per-character. The grab itself is
    // inert until inventory tracking records lost items; the preference is stored
    // now.
    public bool AutoRecover
    {
        get => _profile.Current?.DeathAutoRecover ?? false;
        set
        {
            if (_profile.Current is not { } p || p.DeathAutoRecover == value) return;
            p.DeathAutoRecover = value;
            _profile.Save();
            OnPropertyChanged();
        }
    }

    // Re-equip items that were worn at death after recovering them. Persisted
    // per-character; inert until inventory tracking records what was equipped at
    // death.
    public bool AutoEquip
    {
        get => _profile.Current?.DeathAutoEquip ?? false;
        set
        {
            if (_profile.Current is not { } p || p.DeathAutoEquip == value) return;
            p.DeathAutoEquip = value;
            _profile.Save();
            OnPropertyChanged();
        }
    }

    // Manually flag a record as fully recovered (user pressed "Mark Recovered").
    // Sets Recovered, persists, and notifies binders.
    public void MarkRecovered(DeathRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.Status == DeathRecoveryStatus.Recovered) return;
        record.Status = DeathRecoveryStatus.Recovered;
        record.RecoveryMessage = "Marked recovered by user.";
        _profile.Save();
        OnPropertyChanged(nameof(Records));
    }

    // Remove a single record from the history (and its death-log file, if any).
    public void ClearSelected(DeathRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (_profile.Current?.DeathHistory is not { } list || !list.Remove(record)) return;
        DeleteDeathLog(record);
        _profile.Save();
        OnPropertyChanged(nameof(Records));
    }

    // Remove every record whose status is Recovered (and their death-log files).
    public void ClearAllRecovered()
    {
        if (_profile.Current?.DeathHistory is not { } list) return;
        List<DeathRecord> removed = list.Where(r => r.Status == DeathRecoveryStatus.Recovered).ToList();
        if (removed.Count == 0) return;
        foreach (DeathRecord r in removed)
        {
            DeleteDeathLog(r);
            list.Remove(r);
        }
        _profile.Save();
        OnPropertyChanged(nameof(Records));
    }

    // Walk to the room a death occurred in. Returns false when no walker is
    // attached or the record has no recorded room.
    public bool WalkToDeathRoom(DeathRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (_walker is null || record.Room is not { } r) return false;
        return _walker.WalkTo(new RoomKey(r.Map, r.Room));
    }

    // Demand signal to recover a deathpile. If we're already standing in the
    // death room, grab every recorded pile item in place (and re-equip the worn
    // ones when Auto-Equip is on); otherwise start walking there and grab on
    // arrival. The grab is forced regardless of the Auto-Recover toggle — the
    // user asked for it explicitly.
    public bool RecoverNow(DeathRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.Room is not { } target) return false;

        Room? here = _roomTracker.State.CurrentRoom;
        bool inRoom = here is not null && here.Key.Map == target.Map && here.Key.Room == target.Room;
        string where = record.RoomName ?? record.RoomKeyText;

        if (inRoom)
        {
            _log?.Info(LogCategory, $"recover-now: in death room {where} — surveying for the corpse");
            BeginRecovery(record, autoGrab: true, deliberate: true);
            // Already standing here, so no room-change survey is coming — re-look
            // to re-render the "You notice" list, which fires SurveyUpdated and
            // drives the corpse grab.
            Send("look");
            return true;
        }

        // Walking back: arm a one-shot force-grab so arrival recovers even
        // when Auto-Recover is off.
        _pendingRecoverNow = record;
        bool walking = WalkToDeathRoom(record);
        if (walking)
            _log?.Info(LogCategory, $"recover-now: walking to {where} then recovering");
        else
            _pendingRecoverNow = null;
        return walking;
    }

    // ----- recovery state machine -------------------------------------

    // Fires on every room transition. On entering (Confirmed) a room that holds
    // an un-recovered deathpile, begin recovery; on leaving the pile we were
    // recovering, drop the in-progress tracker (the record stays Partial until we
    // return and finish).
    private void OnRoomChanged(RoomTransition t)
    {
        // The spillover sweep drives its own room-to-room detours (out to a
        // neighbour, back to the death room) — let it own every transition while
        // it runs, so a mid-sweep hop doesn't drop _activeRecovery or re-arm a
        // different pile. The sweep advances off the walker's arrival events.
        if (_sweep.Active) return;

        // Fresh room — any prior pass-through spillover grab is done or abandoned.
        _spilloverGrabOnSurvey = false;
        _spilloverRecovering = false;
        _spilloverPile = null;

        Room? room = t.NewRoom;

        if (_activeRecovery is { Room: { } ar }
            && (room is null || ar.Map != room.Key.Map || ar.Room != room.Key.Room))
        {
            _activeRecovery = null;
            _grabOnSurvey = false;
            _stockRecovering = false;
            _stockSweepPending = false;
        }

        // Left the room our paced re-equip pieces belong to (rare — the
        // CorpseRecovery gate holds the walker while pieces are pending, so this is
        // really only a manual move): stop equipping and release the gate rather
        // than putting the rest on in the wrong room.
        if (_pendingEquip.Count > 0 && _equipRoom is { } er
            && (room is null || er.Map != room.Key.Map || er.Room != room.Key.Room))
            AbandonPendingEquip();

        if (room is null || t.NewConfidence != RoomConfidence.Confirmed) return;

        DeathRecord? rec = FindRecoverableAt(room.Key);
        if (rec is not null && !ReferenceEquals(_activeRecovery, rec))
        {
            bool force = ReferenceEquals(_pendingRecoverNow, rec);
            if (force) _pendingRecoverNow = null;
            BeginRecovery(rec, autoGrab: AutoRecover || force, deliberate: force || WalkedToDeathRoom(rec));
            return;
        }

        // This room holds no deathpile of ours — but if it's adjacent to one, an
        // auto-recover pass-through grabs our overflow here in-stride (Stock only),
        // covering the rooms right before and after a death room on the route.
        TryArmSpillover(room);
    }

    // Arm a pass-through spillover grab: when auto-recover is on and the room we
    // just walked into borders an un-recovered Stock deathpile, get our overflow
    // off this floor on its next survey — no detour. Paradigm never spills (the
    // corpse holds everything), so it's Stock-only.
    private void TryArmSpillover(Room room)
    {
        if (!AutoRecover || _isParadigm?.Invoke() == true) return;
        if (FindPileAdjacentTo(room) is not { } dp) return;
        _spilloverPile = dp;
        _spilloverGrabOnSurvey = true;
        _log?.Info(LogCategory,
            $"pass-through: {room.Key.Map}/{room.Key.Room} borders deathpile at {dp.RoomKeyText} "
            + $"({dp.UnrecoveredItems?.Count ?? 0} still out) — arming an in-stride grab");
    }

    // Newest un-recovered pile whose death room borders `room` (one of its exits
    // leads there) — the spillover target for a pass-through grab.
    private DeathRecord? FindPileAdjacentTo(Room room)
    {
        if (_profile.Current?.DeathHistory is not { } list) return null;
        DeathRecord? best = null;
        foreach (DeathRecord r in list)
        {
            if (r.Status is DeathRecoveryStatus.Recovered or DeathRecoveryStatus.Missing) continue;
            if (r.UnrecoveredItems is not { Count: > 0 } || r.Room is not { } rr) continue;
            bool borders = room.Exits.Values.Any(e => e.Target.Map == rr.Map && e.Target.Room == rr.Room);
            if (borders && (best is null || r.RecordNumber > best.RecordNumber)) best = r;
        }
        return best;
    }

    // A recovery is "deliberate" (earns the adjacent-room sweep) when we didn't
    // just pass through the death room en route somewhere else — i.e. the walker
    // is idle/standing here, or the death room is its actual destination. A walk
    // whose destination lies beyond this room is a pass-through.
    private bool WalkedToDeathRoom(DeathRecord rec)
    {
        if (_walker is not { } w || w.Destination is not { } dest) return true;
        return rec.Room is { } r && dest.Map == r.Map && dest.Room == r.Room;
    }

    // Newest un-recovered record whose death room matches key.
    private DeathRecord? FindRecoverableAt(RoomKey key)
    {
        if (_profile.Current?.DeathHistory is not { } list) return null;
        DeathRecord? best = null;
        foreach (DeathRecord r in list)
        {
            // Recovered + Missing are terminal — a Missing pile (corpse wasn't in
            // the room) must not re-arm on re-entry, or it would spam-retry again.
            // The user re-tries a Missing pile explicitly via Recover Now.
            if (r.Status is DeathRecoveryStatus.Recovered or DeathRecoveryStatus.Missing) continue;
            if (r.Room is not { } room || room.Map != key.Map || room.Room != key.Room) continue;
            if (best is null || r.RecordNumber > best.RecordNumber) best = r;
        }
        return best;
    }

    // Start (or restart) recovering a deathpile. Mark the record Partial and,
    // when autoGrab, ARM the corpse grab for the room's next floor survey — the
    // "You notice" line lands after this room-change fires, so we can't read it
    // yet (see OnSurveyUpdated / TryCorpseRecover). A known-empty pile (nothing
    // was lost) jumps straight to Recovered.
    private void BeginRecovery(DeathRecord record, bool autoGrab, bool deliberate)
    {
        _activeRecovery = record;
        _grabOnSurvey = false;
        _stockRecovering = false;
        _deliberateRecovery = deliberate;

        List<string> pile = PileNames(record);
        record.UnrecoveredItems = pile.Count > 0 ? pile : null;   // corpse contents, for the detail panel

        _log?.Info(LogCategory,
            $"begin recovery: {record.RoomKeyText} realm={(_isParadigm?.Invoke() == true ? "Paradigm" : "Stock")} "
            + $"deliberate={deliberate} autoGrab={autoGrab} pile={pile.Count} item(s)");

        bool known = record.EquippedAtDeath is not null || record.LostItems is not null;
        if (known && pile.Count == 0)
        {
            _log?.Info(LogCategory, "recovery: nothing was lost at death — done");
            FinalizeRecovered(record, "Nothing was lost at death.");
            return;
        }

        // Active/Missing → Partial: we're back in the room. Missing flips back
        // when the user Recover-Nows a pile whose corpse has reappeared.
        if (record.Status is DeathRecoveryStatus.Active or DeathRecoveryStatus.Missing)
            SetStatus(record, DeathRecoveryStatus.Partial, "Returned to the death room — recovering.");

        _grabOnSurvey = autoGrab;
        if (!autoGrab)
            _log?.Info(LogCategory, "recovery: auto-recover off — armed nothing (manual Recover Now only)");
    }

    // The room's floor survey ("You notice … here.") was just reparsed. If we're
    // armed to auto-recover a pile here, act on it now — via the realm's mechanic:
    // Paradigm recovers the `corpse of <name>`, Stock `get`s the loose items.
    private void OnSurveyUpdated()
    {
        // Spillover sweep LOOK phase: each `look <dir>` re-parses the PEEKED room's
        // floor into GroundItemTracker (it doesn't skip look-direction peeks), and
        // its Items are already multi-line-stitched — so hand those to the sweep for
        // the exit we're currently peeking. Reusing the tracker's parser is what
        // makes a crowded, line-wrapped "You notice" survey match (a naive single-line
        // parse missed it, so the sweep never walked — report stock-20260825-101612).
        if (_sweep.Active && _groundItems is { } ground)
        {
            _sweep.OnPeekedNotice(ground.Items);
            return;
        }

        // Pass-through: grab our overflow off an adjacent-death-room's neighbour we
        // just walked into (Stock only; armed by TryArmSpillover).
        if (_spilloverGrabOnSurvey && _spilloverPile is { } sp)
        {
            _spilloverGrabOnSurvey = false;
            if (GetOurItemsHere(sp) > 0)
            {
                _spilloverRecovering = true;
                _log?.Info(LogCategory, "stock-recover: grabbing spillover in a room next to the death room");
            }
        }

        if (_activeRecovery is not { } record || !_grabOnSurvey) return;
        if (_isParadigm?.Invoke() ?? true) TryCorpseRecover(record);
        else TryGroundRecover(record);
    }

    // Stock deathpile = one "corpse of <given-name>" object. If our corpse is in
    // the survey, send ONE `recover corpse <name>` (own corpse needs no password,
    // and naming it disambiguates when several corpses share the room). If it
    // isn't there, the pile is gone — mark Missing so we neither retry nor spam.
    private void TryCorpseRecover(DeathRecord record)
    {
        _grabOnSurvey = false;   // one shot per arming — never loop
        string? corpse = FindOurCorpse();
        if (corpse is null)
        {
            _log?.Info(LogCategory, "auto-recover: corpse not in the room survey — marking Missing.");
            SetStatus(record, DeathRecoveryStatus.Missing, "Corpse was not in the room — pile appears lost.");
            _activeRecovery = null;
            return;
        }
        _log?.Info(LogCategory, $"auto-recover: recover corpse {corpse}");
        Send($"recover corpse {corpse}");
    }

    // Stock deathpile = loose items on the floor. `get <name>` each pile item
    // that's actually in this room's survey (article/count-insensitive match) —
    // never an absent one, which is what made the old flow spam "You don't see X
    // here.". Each grab confirms with a "You took <name>." line (see OnStockItemTaken)
    // that decrements the pile; the record finalises when everything's back. Items
    // NOT on this floor stay unrecovered — the pile holds at Partial (retried on
    // re-entry, or swept from adjacent rooms on a deliberate recovery).
    private void TryGroundRecover(DeathRecord record)
    {
        _grabOnSurvey = false;   // one shot per arming — never loop
        int sent = GetOurItemsHere(record);
        if (sent > 0)
        {
            _stockRecovering = true;
            _stockSettleTicks = StockSettleTicks;   // heartbeat settles the burst → sweep the leftovers
            _log?.Info(LogCategory, $"stock-recover: get {sent} pile item(s) from the floor");
        }
        else
        {
            // Nothing of ours on this floor — no "You took" is coming, so the
            // death-room phase is already settled (everything spilled / gone).
            _log?.Info(LogCategory, "stock-recover: none of our pile items on this floor");
            OnStockDeathRoomSettled(record);
        }
    }

    // `get` each of record's still-missing items that's on the CURRENT room's floor
    // (article/count-insensitive; never an absent one — that avoids the get-spam).
    // Returns how many gets were sent. Shared by the death-room grab, the spillover
    // sweep's neighbour grabs, and the pass-through grab.
    private int GetOurItemsHere(DeathRecord record)
    {
        if (record.UnrecoveredItems is not { Count: > 0 } remaining || _groundItems is not { } ground)
            return 0;

        HashSet<string> floor = ground.Items
            .Select(ItemNameStore.Normalize)
            .Where(n => n.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        int sent = 0;
        foreach (string pileName in remaining)
        {
            if (!floor.Contains(ItemNameStore.Normalize(pileName))) continue;
            Send($"get {pileName}");
            sent++;
        }
        return sent;
    }

    // The death-room `get` burst has settled with items still missing. Re-equip
    // whatever worn gear we did get back (combat-paced), then — on a deliberate
    // recovery in a cleared room with exits to check — sweep the neighbours for the
    // spillover; otherwise hold the pile at Partial (retried on re-entry / manually).
    private void OnStockDeathRoomSettled(DeathRecord record)
    {
        _stockRecovering = false;
        _stockSettleTicks = 0;
        if (record.UnrecoveredItems is not { Count: > 0 } left) return;   // already fully recovered

        ReequipAllWorn(record);   // wear the recovered half now (paced if a hostile's up)

        bool hostile = _hostilesPresent?.Invoke() ?? false;
        _log?.Info(LogCategory,
            $"stock-recover: death-room grab settled, {left.Count} item(s) still missing "
            + $"(deliberate={_deliberateRecovery}, hostile={hostile})");

        if (_deliberateRecovery && _isParadigm?.Invoke() != true && !hostile && StartStockSweep(record))
            return;

        // Can't sweep now (pass-through, Paradigm, hostile still here, or no exits) —
        // a hostile just defers it: the heartbeat retries the sweep once clear.
        _stockSweepPending = _deliberateRecovery && _isParadigm?.Invoke() != true;
        _log?.Info(LogCategory, _stockSweepPending
            ? "stock-recover: sweep deferred (hostile in the death room) — retrying on clear"
            : "stock-recover: no sweep (pass-through / no exits) — holding Partial");
        SetStatus(record, DeathRecoveryStatus.Partial,
            $"Recovered what was here — {left.Count} item(s) not in this room.");
    }

    // Drop one entry matching a "You took <item>." from pile's unrecovered set
    // (article/count-insensitive). Returns true when an entry was removed. Shared by
    // the death-room grab and the pass-through grab.
    private bool RemoveRecoveredItem(DeathRecord pile, string rawItem)
    {
        if (pile.UnrecoveredItems is not { } remaining) return false;
        string norm = ItemNameStore.Normalize(rawItem);
        if (norm.Length == 0) return false;
        int idx = remaining.FindIndex(n =>
            string.Equals(ItemNameStore.Normalize(n), norm, StringComparison.OrdinalIgnoreCase));
        if (idx < 0) return false;
        remaining.RemoveAt(idx);
        OnPropertyChanged(nameof(Records));
        return true;
    }

    // A "You took <item>." landed while grabbing the death-room stock pile. Drop it
    // from the unrecovered set; once the whole pile is back, finalise Recovered and
    // re-equip the worn half (paced by the combat interleave, same as the corpse path).
    private void OnStockItemTaken(string rawItem)
    {
        if (_activeRecovery is not { } record) return;
        if (!RemoveRecoveredItem(record, rawItem))
        {
            // We `get`-ed a floor item but it matched no unrecovered pile entry —
            // shouldn't happen (we only `get` pile items), so trace the mismatch for
            // diagnosis rather than silently leaving the pile count wrong.
            _log?.Debug(LogCategory,
                $"stock-recover: 'You took {rawItem}' matched no unrecovered pile item "
                + $"(remaining: {(record.UnrecoveredItems is { } r ? string.Join(", ", r) : "none")})");
            return;
        }
        _stockSettleTicks = StockSettleTicks;   // more may still be arriving — keep waiting
        _log?.Info(LogCategory, $"stock-recover: got {rawItem} ({record.UnrecoveredItems?.Count ?? 0} left)");

        if (record.UnrecoveredItems is { Count: 0 })
        {
            _stockRecovering = false;
            int total = PileNames(record).Count;
            ReequipAllWorn(record);
            FinalizeRecovered(record, $"Recovered the deathpile ({total} item(s)).");
        }
    }

    // Sweep the death room's exits for spillover: peek each neighbour, then walk to
    // the ones holding our still-missing items, grab them, and return. Returns false
    // (caller holds Partial) when the sweep can't run — no current room / exits, or
    // nothing left to find. The record's UnrecoveredItems list is handed in by
    // reference so the sweep decrements it as items come back.
    private bool StartStockSweep(DeathRecord record)
    {
        if (record.UnrecoveredItems is not { Count: > 0 } remaining) return false;
        if (_roomTracker.State.CurrentRoom is not { } here) return false;

        var neighbours = new Dictionary<Direction, RoomKey>();
        foreach ((Direction dir, RoomExit exit) in here.Exits)
            neighbours[dir] = exit.Target;
        if (neighbours.Count == 0) return false;

        AssertRecoveryGate();   // hold the walker's own routing until the sweep ends
        bool started = _sweep.Begin(here.Key, neighbours, remaining, () => OnStockSweepComplete(record));
        if (!started) ReleaseRecoveryGate();
        else _log?.Info(LogCategory, "stock-recover: spillover sweep started");
        return started;
    }

    // The spillover sweep finished. Whatever it recovered is now in the pack — wear
    // the recovered worn half and finalise: Recovered if the sweep emptied the pile,
    // else Partial (the sweep ran; some items are truly gone).
    private void OnStockSweepComplete(DeathRecord record)
    {
        _stockSweepPending = false;
        ReleaseRecoveryGate();
        ReequipAllWorn(record);
        if (record.UnrecoveredItems is not { Count: > 0 } left)
        {
            _log?.Info(LogCategory, "stock-sweep: recovered everything via the adjacent-room sweep");
            FinalizeRecovered(record, "Recovered the deathpile (adjacent-room sweep).");
        }
        else
        {
            _log?.Info(LogCategory, $"stock-sweep: done — {left.Count} item(s) truly gone, holding Partial");
            SetStatus(record, DeathRecoveryStatus.Partial,
                $"Adjacent-room sweep done — {left.Count} item(s) still missing.");
            _activeRecovery = null;
        }
    }

    // The given name of OUR corpse as it appears in the floor survey ("corpse of
    // Ermias" → "Ermias"), or null when no matching corpse is on the floor. The
    // survey shows the GIVEN name only, so we compare against the first token of
    // our character name. With several corpses we require the name match so we
    // never recover another player's corpse; a single corpse in our own death
    // room is taken when we have no name to match against.
    private string? FindOurCorpse()
    {
        if (_groundItems is not { } ground) return null;
        string ourGiven = FirstToken(_profile.Current?.Name);
        string? loneCorpse = null;
        int corpseCount = 0;
        foreach (string item in ground.Items)
        {
            Match cm = CorpseRegex().Match(item);
            if (!cm.Success) continue;
            corpseCount++;
            string given = cm.Groups["name"].Value.Trim();
            if (ourGiven.Length > 0 && string.Equals(given, ourGiven, StringComparison.OrdinalIgnoreCase))
                return given;
            loneCorpse ??= given;
        }
        return ourGiven.Length == 0 && corpseCount == 1 ? loneCorpse : null;
    }

    private static string FirstToken(string? name) =>
        string.IsNullOrWhiteSpace(name) ? string.Empty : name.Trim().Split(' ')[0];

    private static List<string> PileNames(DeathRecord record)
    {
        var names = new List<string>();
        if (record.EquippedAtDeath is { } worn)
            names.AddRange(worn.Where(i => !string.IsNullOrWhiteSpace(i.Name)).Select(i => i.Name));
        if (record.LostItems is { } lost)
            names.AddRange(lost.Where(i => !string.IsNullOrWhiteSpace(i.Name)).Select(i => i.Name));
        return names;
    }

    // Recovery confirmations. Paradigm: the whole pile returns on one "You have
    // recovered the corpse of <name>." line — finalise and (Auto-Equip) re-wear
    // everything worn at death (works for a manual `recover corpse` too, since we
    // key off the confirmation, not who sent it). Stock: items come back one "You
    // took <item>." at a time — decrement the pile per confirmation.
    private void OnLine(LineExtractor.EmittedLine line)
    {
        if (line.IsPromptLine) return;

        // While the sweep runs, its peeked-floor surveys arrive through
        // GroundItemTracker → OnSurveyUpdated (multi-line-safe); here we only need
        // the "You took" confirmations from the sweep's own neighbour grabs.
        if (_sweep.Active)
        {
            if (YouTookRegex().Match(line.Text) is { Success: true } grabbed)
                _sweep.OnItemTaken(grabbed.Groups["item"].Value);
            return;
        }

        // Pass-through spillover "You took" is independent of any death-room
        // recovery (we're standing in a NEIGHBOUR of the death room, not it), so it
        // runs before the _activeRecovery guard below.
        if (_spilloverRecovering && YouTookRegex().Match(line.Text) is { Success: true } spill)
        {
            OnSpilloverItemTaken(spill.Groups["item"].Value);
            return;
        }

        if (_activeRecovery is null) return;

        if (CorpseRecoveredRegex().IsMatch(line.Text))
        {
            DeathRecord record = _activeRecovery;
            int total = PileNames(record).Count;
            _log?.Info(LogCategory, $"paradigm: corpse recovered ({total} item(s)) — re-equipping worn gear");
            record.UnrecoveredItems = null;   // corpse = the whole pile is back at once
            ReequipAllWorn(record);
            FinalizeRecovered(record,
                total > 0 ? $"Recovered the corpse ({total} item(s))." : "Recovered the corpse.");
            return;
        }

        if (_stockRecovering && YouTookRegex().Match(line.Text) is { Success: true } took)
            OnStockItemTaken(took.Groups["item"].Value);
    }

    // A "You took <item>." landed while grabbing spillover in a room next to a death
    // room. Decrement that pile; if this passing grab happened to complete it, wear
    // the recovered gear and mark it Recovered.
    private void OnSpilloverItemTaken(string rawItem)
    {
        if (_spilloverPile is not { } pile || !RemoveRecoveredItem(pile, rawItem)) return;
        _log?.Info(LogCategory, $"stock-recover: grabbed spillover {rawItem} ({pile.UnrecoveredItems?.Count ?? 0} left)");
        if (pile.UnrecoveredItems is not { Count: 0 }) return;

        _spilloverRecovering = false;
        _spilloverPile = null;
        _log?.Info(LogCategory, $"pass-through: overflow grab completed the pile at {pile.RoomKeyText} — re-equipping");
        ReequipAllWorn(pile);
        pile.UnrecoveredItems = null;
        SetStatus(pile, DeathRecoveryStatus.Recovered, "Recovered — overflow grabbed in passing.");
    }

    // Re-equip the worn half we've RECOVERED (Auto-Equip). Paradigm gets the whole
    // pile back at once; a Stock recovery may leave some pieces on a neighbour's
    // floor, so we only re-wear worn items no longer in UnrecoveredItems (a caller
    // nulls that list when the whole pile is confirmed back — see the corpse path).
    // Ordering is weapon(s) first then armour highest-AC-first (see OrderForReequip).
    // When a hostile is in the room the wear/eq burst would repeatedly break the
    // combat round, so we don't fire it all at once — enqueue it and pace it across
    // rounds (OnRecoveryCombatRound), holding the CorpseRecovery gate meanwhile. No
    // hostile (or interleaving unbound) → put everything on at once, as before.
    private void ReequipAllWorn(DeathRecord record)
    {
        if (!AutoEquip || record.EquippedAtDeath is not { } worn || worn.Count == 0) return;

        HashSet<string> missing = record.UnrecoveredItems is { Count: > 0 } rem
            ? rem.Select(ItemNameStore.Normalize).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        List<DeathItem> recovered = worn
            .Where(i => !missing.Contains(ItemNameStore.Normalize(i.Name)))
            .ToList();
        if (recovered.Count == 0) return;

        List<DeathItem> ordered = OrderForReequip(recovered, _armourClass);

        if (_hostilesPresent?.Invoke() == true && record.Room is { } room)
        {
            _pendingEquip.Clear();
            foreach (DeathItem item in ordered) _pendingEquip.Enqueue(item);
            _equipRoom = (room.Map, room.Room);
            AssertRecoveryGate();
            _log?.Info(LogCategory,
                $"auto-equip: hostile present — pacing {_pendingEquip.Count} piece(s) across combat rounds");
            return;
        }

        foreach (DeathItem item in ordered) SendEquip(item);
    }

    // Order the worn set for re-equip: weapon(s) first — so our swings do real
    // damage sooner while the fight's still on — then armour highest-AC-first
    // (weakest last). Weapon Hand precedes Off-Hand; armour AC is the item's
    // game-data ArmourClass (0 when the lookup is unbound or the item's unknown,
    // which sorts it last). Stable ordering keeps same-AC pieces in captured order.
    // internal + static so a test can pin the order without a live manager.
    internal static List<DeathItem> OrderForReequip(
        IReadOnlyList<DeathItem> worn, Func<string, int>? armourClass)
    {
        List<DeathItem> named = worn.Where(i => !string.IsNullOrWhiteSpace(i.Name)).ToList();
        List<DeathItem> ordered = named
            .Where(i => i.IsHeld)
            .OrderBy(i => i.Slot == "Off-Hand" ? 1 : 0)
            .ToList();
        ordered.AddRange(named
            .Where(i => !i.IsHeld)
            .OrderByDescending(i => armourClass?.Invoke(i.Name) ?? 0));
        return ordered;
    }

    // Send one piece's slot-correct equip verb. A held item (weapon / off-hand) is
    // wielded with `eq` — matching EquipmentManager's wield path — NOT `hold`,
    // which only carries it in hand rather than wielding it (report: corpse
    // recovery sent "hold platinum mace" for the weapon). Body armour uses `wear`.
    // Lights never reach here: a readied light is tracked separately (not in the
    // worn slot set) and has its own `use` verb (see AutoLightProvisioner).
    private void SendEquip(DeathItem item)
    {
        string verb = item.IsHeld ? "eq" : "wear";
        _log?.Info(LogCategory, $"auto-equip: {verb} {item.Name}");
        Send($"{verb} {item.Name}");
    }

    // Combat-round pulse (TickEngine.CombatTickElapsed, ~5 s, driven by damage
    // lines — NOT by the *Combat Off* our own equips emit, so it can't re-enter
    // mid-burst). Put the next handful of pieces on, then nudge the engine to
    // re-attack on the *Combat Off* they cause. Once the room is clear (the mob
    // died) or the queue empties, the remainder goes on at once. No-op when nothing
    // is pending.
    public void OnRecoveryCombatRound()
    {
        if (_pendingEquip.Count == 0) return;
        if (_hostilesPresent?.Invoke() != true) { FlushEquipQueue(); return; }

        for (int i = 0; i < EquipBurstPerRound && _pendingEquip.Count > 0; i++)
            SendEquip(_pendingEquip.Dequeue());
        _armCombatResume?.Invoke();

        if (_pendingEquip.Count == 0) ReleaseRecoveryGate();
    }

    // 1 s heartbeat. Flushes the paced re-equip the instant the room clears (a kill
    // between combat rounds), settles the Stock death-room `get` burst so its
    // leftovers can be swept, paces the sweep's own looks/collects, and starts a
    // sweep that was deferred while a hostile held the death room.
    public void OnRecoveryHeartbeat()
    {
        if (_pendingEquip.Count > 0 && _hostilesPresent?.Invoke() != true)
            FlushEquipQueue();

        if (_sweep.Active) { _sweep.OnHeartbeat(); return; }

        // Death-room grab quieted down with items still out → decide sweep vs Partial.
        if (_stockRecovering && _stockSettleTicks > 0 && --_stockSettleTicks == 0
            && _activeRecovery is { } settling)
            OnStockDeathRoomSettled(settling);

        // A deferred sweep (hostile cleared) can run now.
        if (_stockSweepPending && !(_hostilesPresent?.Invoke() ?? false)
            && _activeRecovery is { } pending)
        {
            _stockSweepPending = false;
            _log?.Info(LogCategory, "stock-recover: death room clear — starting the deferred spillover sweep");
            if (!StartStockSweep(pending))
                SetStatus(pending, DeathRecoveryStatus.Partial,
                    pending.UnrecoveredItems is { Count: > 0 } l
                        ? $"Recovered what was here — {l.Count} item(s) not in this room."
                        : "Recovered the deathpile.");
        }
    }

    private void FlushEquipQueue()
    {
        while (_pendingEquip.Count > 0) SendEquip(_pendingEquip.Dequeue());
        _equipRoom = null;
        ReleaseRecoveryGate();
    }

    // Give up on the remaining pieces (we left the recovery room) — drop them and
    // release the gate. The items are back in the pack either way; the user can
    // re-equip manually.
    private void AbandonPendingEquip()
    {
        if (_pendingEquip.Count > 0)
            _log?.Info(LogCategory,
                $"auto-equip: left the room with {_pendingEquip.Count} piece(s) unequipped — abandoning");
        _pendingEquip.Clear();
        _equipRoom = null;
        ReleaseRecoveryGate();
    }

    private void AssertRecoveryGate()
    {
        if (_recoveryGateHeld) return;
        _recoveryGateHeld = true;
        _assertRecoveryGate?.Invoke();
    }

    private void ReleaseRecoveryGate()
    {
        if (!_recoveryGateHeld) return;
        _recoveryGateHeld = false;
        _clearRecoveryGate?.Invoke();
    }

    private void FinalizeRecovered(DeathRecord record, string message)
    {
        _log?.Info(LogCategory, $"recovery complete: {record.RoomKeyText} — {message}");
        _activeRecovery = null;
        _grabOnSurvey = false;
        _stockRecovering = false;
        _stockSweepPending = false;
        record.UnrecoveredItems = null;   // everything accounted for
        SetStatus(record, DeathRecoveryStatus.Recovered, message);
    }

    private void SetStatus(DeathRecord record, DeathRecoveryStatus status, string message)
    {
        if (record.Status == status) return;
        record.Status = status;
        record.RecoveryMessage = message;
        _profile.Save();
        OnPropertyChanged(nameof(Records));
    }

    private void Send(string text)
    {
        if (_wireSender is null) return;
        _wireSender(Encoding.Latin1.GetBytes(text + "\r"));
    }

    // Test seam — feed a plain inbound line to the pickup parser.
    internal void FeedTestLine(string text, DateTimeOffset? when = null)
        => OnLine(new LineExtractor.EmittedLine(text, [], when ?? DateTimeOffset.UtcNow, false));

    // Append a synthetic death record (the "Simulate Death" button) so the DEATH
    // grid + recovery flow can be exercised without dying in game. Decrements the
    // displayed lives by one, floored at zero.
    public void SimulateDeath()
    {
        if (_profile.Current is not { } p) return;
        p.DeathHistory ??= new List<DeathRecord>();
        Room? here = _roomTracker.State.CurrentRoom;
        // Continue the declining-lives series from the most recent record.
        int prevLives = p.DeathHistory.Count > 0 ? p.DeathHistory[^1].LivesRemaining : 0;
        var record = new DeathRecord(
            DateTimeOffset.UtcNow,
            here is null ? null : new RoomRef(here.Key.Map, here.Key.Room),
            Math.Max(0, prevLives - 1),
            "Simulated death (test).")
        {
            RecordNumber = p.DeathHistory.Count + 1,
            RoomName = here?.Name,
            Status = DeathRecoveryStatus.Active,
        };
        if (_inventorySnapshot is { } provider)
        {
            InventorySnapshot snapshot = provider();
            (List<DeathItem> equipped, List<DeathItem> lost) =
                DeathLootCapture.FromSnapshot(snapshot);
            record.EquippedAtDeath = equipped;
            record.LostItems = lost;
            record.CoinsAtDeath = snapshot.Currency;
        }
        p.DeathHistory.Add(record);
        _profile.Save();
        // Real deaths capture via the PlayerDeathObserved hook; the test button
        // bypasses RoomTracker.NoteDeath, so snapshot the tail here too.
        CaptureDeathLog(record);
        OnPropertyChanged(nameof(Records));
    }

    // ----- death-log capture ("How did I Die?") -----------------------

    // PlayerDeathObserved fires synchronously from RoomTracker.NoteDeath right
    // after the record is appended, so the just-added record is the history tail.
    // Snapshot its backscroll before the graveyard display floods scrollback.
    private void OnDeathObserved()
    {
        if (_profile.Current?.DeathHistory is not { Count: > 0 } list) return;
        DeathRecord last = list[^1];
        if (last.DeathLogFile is not null) return;   // already captured
        CaptureDeathLog(last);
    }

    // Snapshot the transcript tail to a per-character death-log file and pin its
    // name on the record. No-op without a bound transcript provider or a named
    // profile (drafts / tests have nowhere to write). Best-effort: a capture
    // failure must never break the death-record write it rides on.
    private void CaptureDeathLog(DeathRecord record)
    {
        if (_transcriptTail is not { } provider) return;
        if (record.DeathLogFile is not null) return;
        if (_profile.CurrentBbsName is not { } bbs || _profile.CurrentProfileName is not { } chr) return;

        IReadOnlyList<TranscriptSnapshot.Line> lines;
        try { lines = provider(); }
        catch { return; }
        if (lines.Count == 0) return;

        string fileName = $"death-{record.At.LocalDateTime:yyyyMMdd-HHmmss}-{record.RecordNumber}.log";
        try
        {
            Directory.CreateDirectory(AppPaths.DeathLogsFolder(bbs, chr));
            File.WriteAllText(AppPaths.DeathLogFile(bbs, chr, fileName),
                RenderDeathLog(record, lines, chr));
            record.DeathLogFile = fileName;
            _profile.Save();
            _log?.Info(LogCategory, $"death log captured: {fileName} ({lines.Count} lines)");
        }
        catch (Exception ex)
        {
            _log?.Warn(LogCategory, $"death log capture failed: {ex.Message}");
        }
    }

    // True when the record names a death-log file that still exists on disk.
    public bool HasDeathLog(DeathRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return ResolveDeathLogPath(record) is { } path && File.Exists(path);
    }

    // Read a record's captured death log, or null when it has none / the file is
    // gone / unreadable. The "How did I Die?" viewer treats null as "nothing to
    // show".
    public string? ReadDeathLog(DeathRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (ResolveDeathLogPath(record) is not { } path) return null;
        try { return File.Exists(path) ? File.ReadAllText(path) : null; }
        catch { return null; }
    }

    // Resolve the on-disk path for a record's death log, or null when the record
    // has no log or no profile is loaded to scope it.
    private string? ResolveDeathLogPath(DeathRecord record)
    {
        if (record.DeathLogFile is not { Length: > 0 } fileName) return null;
        if (_profile.CurrentBbsName is not { } bbs || _profile.CurrentProfileName is not { } chr) return null;
        return AppPaths.DeathLogFile(bbs, chr, fileName);
    }

    private void DeleteDeathLog(DeathRecord record)
    {
        if (ResolveDeathLogPath(record) is not { } path) return;
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best-effort; an orphaned log file is harmless clutter */ }
    }

    // Render a captured death log as plain text: a short header (who / when /
    // where / lives / death line) followed by the backscroll tail, oldest →
    // newest. Scrollback rows carry the wall-clock instant they scrolled off; the
    // live-screen tail (the grid at death) has no per-row time. internal + static
    // so the format can be pinned by a test without a live profile.
    internal static string RenderDeathLog(
        DeathRecord record, IReadOnlyList<TranscriptSnapshot.Line> lines, string characterName)
    {
        StringBuilder sb = new();
        sb.Append("Death log — ").Append(characterName).Append('\n');
        sb.Append("Died: ").Append(record.DiedText).Append('\n');
        sb.Append("Room: ")
          .Append(record.RoomName ?? "(unknown)").Append("  ").Append(record.RoomKeyText).Append('\n');
        sb.Append("Lives remaining: ").Append(record.LivesRemaining).Append('\n');
        if (!string.IsNullOrWhiteSpace(record.MessageText))
            sb.Append("Death line: ").Append(record.MessageText).Append('\n');
        sb.Append('\n')
          .Append("Last ").Append(lines.Count)
          .Append(" line(s) of backscroll before death (each content row prefixed with its write time):\n");
        sb.Append(new string('-', 60)).Append('\n');
        foreach (TranscriptSnapshot.Line line in lines)
        {
            sb.Append(line.Timestamp is { } t ? t.ToLocalTime().ToString("HH:mm:ss") : "        ")
              .Append(' ').Append(line.Text).Append('\n');
        }
        return sb.ToString();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _deathWatcher.PlayerDied -= OnPlayerDied;
        _roomTracker.StateChanged -= OnRoomChanged;
        _roomTracker.PlayerDeathObserved -= OnDeathObserved;
        if (_lines is not null) _lines.LineEmitted -= OnLine;
        _lines = null;
        if (_groundItems is not null) _groundItems.SurveyUpdated -= OnSurveyUpdated;
        _groundItems = null;
        if (_walker is not null) _walker.Event -= OnWalkEvent;
        _sweep.Cancel();
        AbandonPendingEquip();   // never strand the CorpseRecovery gate asserted
    }

    // A floor-survey entry naming our deathpile corpse: "corpse of <given-name>"
    // (the survey renders the given name only, no article). The captured name is
    // the exact token `recover corpse <name>` takes. Paradigm only — Stock scatters
    // the pile loose instead of packing it in a corpse.
    [GeneratedRegex(@"^corpse of (?<name>.+?)\s*$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex CorpseRegex();

    // "You have recovered the corpse of <name>." — the single line that ends a
    // successful `recover corpse`, after which the whole pile (items + coins) is
    // back in the pack. A realm that phrases this differently simply leaves the
    // record Partial (the user can Mark Recovered); no false transition occurs.
    [GeneratedRegex(@"^You have recovered the corpse of .+?\.$", RegexOptions.CultureInvariant)]
    private static partial Regex CorpseRecoveredRegex();

    // "You took <item>." — the own-pickup confirmation for a Stock ground `get`
    // (matches KnownPatterns.PlayerGets' own branch; the "<player> picks up" form
    // is another player and never reaches here). Drives the per-item Stock
    // deathpile decrement.
    [GeneratedRegex(@"^You took (?<item>.+?)\.$", RegexOptions.CultureInvariant)]
    private static partial Regex YouTookRegex();
}
