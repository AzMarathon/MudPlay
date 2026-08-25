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
    // because the AutoWalkManager is built after this manager in AppServices.
    public void AttachWalker(AutoWalkManager walker) => _walker = walker;

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
            BeginRecovery(record, autoGrab: true);
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
        Room? room = t.NewRoom;

        if (_activeRecovery is { Room: { } ar }
            && (room is null || ar.Map != room.Key.Map || ar.Room != room.Key.Room))
        {
            _activeRecovery = null;
            _grabOnSurvey = false;
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
        if (rec is null || ReferenceEquals(_activeRecovery, rec)) return;

        bool force = ReferenceEquals(_pendingRecoverNow, rec);
        if (force) _pendingRecoverNow = null;
        BeginRecovery(rec, autoGrab: AutoRecover || force);
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
    private void BeginRecovery(DeathRecord record, bool autoGrab)
    {
        _activeRecovery = record;
        _grabOnSurvey = false;

        List<string> pile = PileNames(record);
        record.UnrecoveredItems = pile.Count > 0 ? pile : null;   // corpse contents, for the detail panel

        bool known = record.EquippedAtDeath is not null || record.LostItems is not null;
        if (known && pile.Count == 0)
        {
            FinalizeRecovered(record, "Nothing was lost at death.");
            return;
        }

        // Active/Missing → Partial: we're back in the room. Missing flips back
        // when the user Recover-Nows a pile whose corpse has reappeared.
        if (record.Status is DeathRecoveryStatus.Active or DeathRecoveryStatus.Missing)
            SetStatus(record, DeathRecoveryStatus.Partial, "Returned to the death room — recovering.");

        _grabOnSurvey = autoGrab;
    }

    // The room's floor survey ("You notice … here.") was just reparsed. If we're
    // armed to auto-recover a pile here, act on it now: recover the corpse if it's
    // present, else mark the pile Missing (nothing on the floor to grab).
    private void OnSurveyUpdated()
    {
        if (_activeRecovery is { } record && _grabOnSurvey) TryCorpseRecover(record);
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

    // Watch for the single "You have recovered the corpse of <name>." line that
    // ends a `recover corpse` — the whole pile (items + coins) is back at once, so
    // finalise and (Auto-Equip) re-wear everything that was worn at death. Works
    // for a manual `recover corpse` too, since we key off the confirmation, not
    // who sent the command.
    private void OnLine(LineExtractor.EmittedLine line)
    {
        if (line.IsPromptLine || _activeRecovery is null) return;
        if (!CorpseRecoveredRegex().IsMatch(line.Text)) return;

        DeathRecord record = _activeRecovery;
        int total = PileNames(record).Count;
        ReequipAllWorn(record);
        FinalizeRecovered(record,
            total > 0 ? $"Recovered the corpse ({total} item(s))." : "Recovered the corpse.");
    }

    // Re-equip everything worn at death after the corpse is recovered (Auto-Equip).
    // The recover command drops the whole pile back into the pack; this puts the
    // worn half back on. Ordering is weapon(s) first then armour highest-AC-first
    // (see OrderForReequip). When a hostile is in the room the wear/eq burst would
    // repeatedly break the combat round, so we don't fire it all at once — enqueue
    // it and pace it across rounds (OnRecoveryCombatRound), holding the walker on
    // the CorpseRecovery gate meanwhile. No hostile (or interleaving unbound) →
    // put everything on at once, as before.
    private void ReequipAllWorn(DeathRecord record)
    {
        if (!AutoEquip || record.EquippedAtDeath is not { } worn || worn.Count == 0) return;

        List<DeathItem> ordered = OrderForReequip(worn, _armourClass);

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

    // 1 s heartbeat — only used to flush the remaining pieces the instant the room
    // clears (a kill between combat rounds), rather than waiting up to a full round
    // for the next combat pulse. No-op while a hostile is still up (the combat
    // pulse paces those) or nothing is pending.
    public void OnRecoveryHeartbeat()
    {
        if (_pendingEquip.Count == 0 || _hostilesPresent?.Invoke() == true) return;
        FlushEquipQueue();
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
        _activeRecovery = null;
        _grabOnSurvey = false;
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
        AbandonPendingEquip();   // never strand the CorpseRecovery gate asserted
    }

    // A floor-survey entry naming our deathpile corpse: "corpse of <given-name>"
    // (stock renders the given name only, no article). The captured name is the
    // exact token `recover corpse <name>` takes.
    [GeneratedRegex(@"^corpse of (?<name>.+?)\s*$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex CorpseRegex();

    // "You have recovered the corpse of <name>." — the single line that ends a
    // successful `recover corpse`, after which the whole pile (items + coins) is
    // back in the pack. A realm that phrases this differently simply leaves the
    // record Partial (the user can Mark Recovered); no false transition occurs.
    [GeneratedRegex(@"^You have recovered the corpse of .+?\.$", RegexOptions.CultureInvariant)]
    private static partial Regex CorpseRecoveredRegex();
}
