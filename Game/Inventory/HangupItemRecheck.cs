using MudPlay.Game.Map;
using MudPlay.Models.Profile;
using MudPlay.Services;

namespace MudPlay.Game.Inventory;

// Looks for what a penalised hang-up dropped, on the way back into the game. Some
// boards drop a few of a character's items on the floor of the room when the link
// goes down in a fight (GAME_MECHANICS "Hang-up / lost carrier"), and nothing
// printed on the next entry says which. So what is held is written to the profile
// while the character plays (StampForSave), and the next entry compares that list
// with the login's own inventory read: what is short and lying in the room is
// taken back.
//
// It runs only on a realm whose settings say items are dropped (Settings → BBS,
// HangupPenaltyNotice.DropsItems), once per connection, and has no part in
// deciding a hang-up.
//
// The order on the way in:
//   connect       — a list is stored and the realm drops items: movement is held,
//                   so a loop that restarts on the first prompt can't walk out of
//                   the room before the check has run.
//   game entered  — wait for an inventory read. The login sends `i` itself; an
//                   entry made by hand gets one asked for after InventoryWaitTicks.
//   inventory in  — compare. Nothing short ends it. Otherwise the room is needed:
//                   the login's own display, or one bare Enter when none was read.
//   room known    — another room than the one the link dropped in ends it. Else a
//                   `get` for each short item the floor list shows, once any
//                   fight in the room is over.
//   gets answered — worn pieces go back on by death recovery's rule, and the hold
//                   on movement is dropped.
//
// Nothing is retried: one pass per connection, and what isn't on the floor then
// is logged as still missing and forgotten. Only a pass cut short by the link
// dropping again carries its unfound items over to the next entry.
public sealed class HangupItemRecheck
{
    public const string LogCategory = "HangupItems";
    public const string AsserterName = nameof(HangupItemRecheck);

    // Counted in heartbeats (TickEngine.HeartbeatInterval, 1 s).
    // The login's own `i` goes out under a second after the first room display;
    // this is how long it is given before one is asked for, and how long the
    // answer to that one is given.
    private const int InventoryWaitTicks = 4;
    // A room redisplay answers within the second; a dark room never does.
    private const int RoomWaitTicks = 2;
    // Quiet after the last `You took` before the gets count as answered. A get
    // the game refuses prints a line this doesn't read, and must not hold movement.
    private const int GetSettleTicks = 3;

    private enum Phase { Idle, AwaitingEntry, AwaitingInventory, AwaitingRoom, AwaitingFightEnd, PickingUp }

    private readonly MovementCoordinator _coordinator;
    private readonly Func<CharacterProfile?> _profile;
    private readonly Action _saveProfile;
    private readonly Func<bool> _realmDropsItems;
    private readonly Func<InventorySnapshot> _inventory;
    private readonly Func<RoomKey?> _confirmedRoom;
    private readonly Func<IReadOnlyList<string>> _floor;
    private readonly Func<string, string>? _itemKey;
    private readonly Func<bool> _fighting;
    private readonly Func<bool> _hostilePresent;
    private readonly Func<bool> _isAutoEnabled;
    private readonly Func<bool> _roomRedisplayFree;
    private readonly Action<string, int> _collect;
    private readonly Action<IReadOnlyList<DeathItem>> _rewear;
    private readonly Action<Action> _post;
    private readonly Func<DateTimeOffset> _now;
    private readonly LogService? _log;
    private readonly WireSender _wire = new();

    private Phase _phase;
    private HeldAtDisconnect? _before;
    private List<HangupMissingItem> _missing = new();
    private List<(string Name, int Count)> _plan = new();
    private readonly Dictionary<string, int> _taken = new(StringComparer.OrdinalIgnoreCase);
    private int _ticks;
    private bool _askedInventory;
    private bool _holding;

    // Per connection. The room and floor flags also fall with a room change: a
    // display read before it shows another room's floor.
    private bool _inGame;
    private bool _enteredThisLink;
    private bool _inventoryReadThisLink;
    private bool _roomShownHere;
    private bool _floorShownHere;

    //   profile          — the loaded character, whose HeldAtDisconnect is read and written.
    //   realmDropsItems  — HangupPenaltyNotice.DropsItems for the realm being played.
    //   confirmedRoom    — the room the map is sure of, else null.
    //   floor            — the room's latest floor list (GroundItemTracker.Items).
    //   itemKey          — see HangupItemPlan.Pickup; null for its default.
    //   fighting         — a monster the client is set to fight is in the room: the
    //                      pickup waits for the fight to end, as collect-after-combat does.
    //   hostilePresent   — any hostile monster is in the room, fought or not.
    //   isAutoEnabled    — Auto-All; off, nothing is sent.
    //   roomRedisplayFree — RoomRedisplayCoordinator.ShouldSend.
    //   collect          — AutoGetItemsManager.CollectNamed.
    //   rewear           — DeathRecoveryManager.ReequipWorn.
    //   post             — run after the handlers of the line now being read; the room
    //                      display parser reports a display before the map has taken it in.
    public HangupItemRecheck(
        MovementCoordinator coordinator,
        Func<CharacterProfile?> profile,
        Action saveProfile,
        Func<bool> realmDropsItems,
        Func<InventorySnapshot> inventory,
        Func<RoomKey?> confirmedRoom,
        Func<IReadOnlyList<string>> floor,
        Func<bool> fighting,
        Func<bool> hostilePresent,
        Func<bool> isAutoEnabled,
        Func<bool> roomRedisplayFree,
        Action<string, int> collect,
        Action<IReadOnlyList<DeathItem>> rewear,
        Func<string, string>? itemKey = null,
        Action<Action>? post = null,
        Func<DateTimeOffset>? now = null,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(saveProfile);
        ArgumentNullException.ThrowIfNull(realmDropsItems);
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(confirmedRoom);
        ArgumentNullException.ThrowIfNull(floor);
        ArgumentNullException.ThrowIfNull(fighting);
        ArgumentNullException.ThrowIfNull(hostilePresent);
        ArgumentNullException.ThrowIfNull(isAutoEnabled);
        ArgumentNullException.ThrowIfNull(roomRedisplayFree);
        ArgumentNullException.ThrowIfNull(collect);
        ArgumentNullException.ThrowIfNull(rewear);
        _coordinator = coordinator;
        _profile = profile;
        _saveProfile = saveProfile;
        _realmDropsItems = realmDropsItems;
        _inventory = inventory;
        _confirmedRoom = confirmedRoom;
        _floor = floor;
        _itemKey = itemKey;
        _fighting = fighting;
        _hostilePresent = hostilePresent;
        _isAutoEnabled = isAutoEnabled;
        _roomRedisplayFree = roomRedisplayFree;
        _collect = collect;
        _rewear = rewear;
        _post = post ?? (static run => run());
        _now = now ?? (static () => DateTimeOffset.Now);
        _log = log;
    }

    // ----- for the bug report -------------------------------------------

    // Where the check stands, in words.
    public string Status => _phase switch
    {
        Phase.AwaitingEntry => "waiting for the game to be entered",
        Phase.AwaitingInventory => "waiting for an inventory read",
        Phase.AwaitingRoom => "waiting for a room display",
        Phase.AwaitingFightEnd => "waiting for the fight in the room to end",
        Phase.PickingUp => "picking up",
        _ => "idle",
    };

    // How the last check ended, with its time; "none" before the first.
    public string LastOutcome { get; private set; } = "none this session";

    // What the last comparison found short, and what of it was still short when
    // the check ended.
    public IReadOnlyList<HangupMissingItem> LastMissing { get; private set; } = Array.Empty<HangupMissingItem>();
    public IReadOnlyList<HangupMissingItem> LastStillMissing { get; private set; } = Array.Empty<HangupMissingItem>();

    public void SetWireSender(Action<byte[]> sender) => _wire.Bind(sender);

    internal List<byte[]> LastSentForTests => _wire.LastSentForTests;

    // ----- the list on disk ---------------------------------------------

    // ProfileService.ProfileSaving: write what is held now. Only while in the game
    // with an inventory read on this connection: before that the record in memory
    // is the last session's, and the list on disk is the one still to be compared.
    public void StampForSave(CharacterProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (!_inGame || !_inventoryReadThisLink) return;
        profile.HeldAtDisconnect = new HeldAtDisconnect
        {
            At = _now(),
            Room = _confirmedRoom() is { } room ? new RoomRef(room.Map, room.Room) : null,
            Items = HangupItemPlan.WithOutstanding(HangupItemPlan.Held(_inventory()), Outstanding()),
        };
    }

    // Short items of a check that hasn't ended, which a list saved now must keep.
    private List<HangupMissingItem> Outstanding() =>
        _phase is Phase.AwaitingRoom or Phase.AwaitingFightEnd or Phase.PickingUp
            ? HangupItemPlan.Outstanding(_missing, _taken)
            : new List<HangupMissingItem>();

    // ----- connection and game entry ------------------------------------

    // A new link is up. The check is decided here, not on the first game prompt,
    // because the movement engines act on that prompt too and the hold has to be
    // in place before any of them.
    public void NoteConnected()
    {
        Abandon("a new connection began");
        _inGame = false;
        _enteredThisLink = false;
        _inventoryReadThisLink = false;
        _roomShownHere = false;
        _floorShownHere = false;

        if (_profile()?.HeldAtDisconnect is not { Items.Count: > 0 } before) return;
        if (!_realmDropsItems()) return;

        _before = before;
        _phase = Phase.AwaitingEntry;
        Hold("items a hang-up may have dropped are checked for on entering the game");
        _log?.Info(LogCategory,
            $"This realm drops items for a hang-up. {before.Items.Count} kind(s) of item were held when the character "
            + $"was last in the game ({before.At.ToLocalTime():yyyy-MM-dd HH:mm:ss}, {RoomText(before.Room)}); "
            + "they are checked against the inventory on entering the game.");
    }

    // The link is gone. InGameCapture has already reported leaving the game
    // (OnInGameChanged), which is where an unfinished check is saved and dropped;
    // this ends one that never reached the game.
    public void NoteDisconnected() => Abandon("the link dropped");

    // InGameCapture.InGameChanged.
    public void OnInGameChanged(bool inGame)
    {
        if (inGame)
        {
            _inGame = true;
            if (_enteredThisLink) return;
            _enteredThisLink = true;
            if (_phase != Phase.AwaitingEntry) return;
            _phase = Phase.AwaitingInventory;
            _ticks = InventoryWaitTicks;
            _askedInventory = false;
            return;
        }

        // Out of the game, by a dropped link or an exit to the board's menu: what
        // is held now is what the next entry is checked against. Saved while the
        // check still counts as under way, so its unfound items go with it.
        if (_inGame && _inventoryReadThisLink) _saveProfile();
        _inGame = false;
        if (_phase is not (Phase.Idle or Phase.AwaitingEntry)) Abandon("the game was left before the check ended");
    }

    // Another character was loaded: the check belonged to the one before.
    public void OnProfileLoaded()
    {
        Abandon("another profile was loaded");
        _inventoryReadThisLink = false;
    }

    // Reset States.
    public void Cancel(string reason) => Abandon(reason);

    // ----- the pass -----------------------------------------------------

    // InventoryManager.FullInventoryParsed.
    public void OnInventoryRead()
    {
        _inventoryReadThisLink = true;
        if (_phase != Phase.AwaitingInventory || _before is null) return;
        Compare(_before);
        // Still under way: put what is held now on disk in place of the list just
        // read, with the short items kept on it (StampForSave).
        if (_phase != Phase.Idle) _saveProfile();
    }

    private void Compare(HeldAtDisconnect before)
    {
        _taken.Clear();
        _missing = HangupItemPlan.Missing(before.Items, _inventory());
        LastMissing = _missing;
        LastStillMissing = _missing;
        _log?.Debug(LogCategory, $"held before: {Names(before.Items.Select(i => (i.Name, i.Count)))}");
        if (_missing.Count == 0)
        {
            Finish("nothing held before is missing");
            return;
        }
        _log?.Info(LogCategory,
            $"Held before and missing now: {Names(_missing.Select(m => (m.Name, m.Count)))}"
            + (_missing.Any(m => m.WornSlots.Count > 0)
                ? $" (worn: {string.Join(", ", _missing.Where(m => m.WornSlots.Count > 0).Select(m => m.Name))})."
                : "."));

        if (!_isAutoEnabled())
        {
            Finish("Auto-All is off, so nothing was looked for or picked up");
            return;
        }
        // Known already to be another room: no need to draw it again to say so.
        if (_roomShownHere || CameInElsewhere(before) is not null)
        {
            CheckRoom(before);
            return;
        }

        _phase = Phase.AwaitingRoom;
        _ticks = RoomWaitTicks;
        if (_roomRedisplayFree())
        {
            _log?.Info(LogCategory, "No room display has been read for this room: asking for one.");
            _wire.Send("");   // a bare Enter redraws the room, floor list included
        }
    }

    private void CheckRoom(HeldAtDisconnect before)
    {
        if (CameInElsewhere(before) is { } elsewhere)
        {
            Finish(elsewhere);
            return;
        }
        RoomKey? here = _confirmedRoom();
        if (before.Room is null || here is null)
            _log?.Info(LogCategory,
                "The room can't be compared with the one the character left the game in "
                + $"(then {RoomText(before.Room)}, now {(here is { } k ? $"room {k}" : "room not known")}): "
                + "checking this room's floor.");
        TryPickUp();
    }

    // How the check ends when the map is sure of both rooms and they differ: what
    // was dropped lies where the character left, not here. Null otherwise.
    private string? CameInElsewhere(HeldAtDisconnect before) =>
        before.Room is { } left && _confirmedRoom() is { } here && (here.Map != left.Map || here.Room != left.Room)
            ? $"the character came in at {here}, not at {left.Map}/{left.Room} where it left the game, "
              + "so there is nothing of its own to pick up here"
            : null;

    private void TryPickUp()
    {
        IReadOnlyList<string> floor = _floorShownHere ? _floor() : Array.Empty<string>();
        _plan = HangupItemPlan.Pickup(HangupItemPlan.Outstanding(_missing, _taken), floor, _itemKey);
        if (_plan.Count == 0)
        {
            Finish("none of the missing items is on the floor here");
            return;
        }
        if (_fighting())
        {
            if (_phase != Phase.AwaitingFightEnd)
                _log?.Info(LogCategory, $"On the floor here: {Names(_plan)}. A fight is on in the room: picking up when it ends.");
            _phase = Phase.AwaitingFightEnd;
            return;
        }
        if (_hostilePresent())
        {
            Finish($"a hostile monster is in the room, so {Names(_plan)} stayed on the floor");
            return;
        }

        _log?.Info(LogCategory, $"On the floor here: {Names(_plan)}. Picking up.");
        _phase = Phase.PickingUp;
        _ticks = GetSettleTicks;
        foreach ((string name, int count) in _plan) _collect(name, count);
    }

    // InventoryManager.ItemTaken: a `You took`, ours or typed.
    public void OnItemTaken(string name, int count)
    {
        if (_phase != Phase.PickingUp) return;
        string key = Key(name);
        int at = _plan.FindIndex(p => string.Equals(Key(p.Name), key, StringComparison.OrdinalIgnoreCase));
        if (at < 0) return;
        (string planned, int wanted) = _plan[at];
        _taken[planned] = Math.Min(wanted, _taken.GetValueOrDefault(planned) + count);
        _ticks = GetSettleTicks;
        if (_plan.TrueForAll(p => _taken.GetValueOrDefault(p.Name) >= p.Count)) Complete();
    }

    private void Complete()
    {
        List<(string Name, int Count)> got = _plan
            .Select(p => (p.Name, Count: _taken.GetValueOrDefault(p.Name)))
            .Where(p => p.Count > 0).ToList();
        List<DeathItem> worn = new();
        foreach (HangupMissingItem item in _missing)
        {
            int back = Math.Min(_taken.GetValueOrDefault(item.Name), item.WornSlots.Count);
            for (int i = 0; i < back; i++) worn.Add(new DeathItem(item.Name, item.WornSlots[i]));
        }

        _log?.Info(LogCategory, got.Count > 0
            ? $"Picked up: {Names(got)}."
            : "Nothing was picked up: the game didn't confirm any of the gets.");
        // Ahead of the hold being dropped, so the wear commands go out before a
        // waiting loop's next step.
        if (worn.Count > 0)
        {
            _log?.Info(LogCategory,
                $"{worn.Count} of them were worn ({string.Join(", ", worn.Select(w => w.Name))}): they go back on "
                + "as after a corpse recovery (Auto-Equip After Recovery, and Auto-All).");
            _rewear(worn);
        }
        Finish(got.Count > 0 ? $"picked up {Names(got)}" : "the gets weren't confirmed");
    }

    // ----- what moves the pass along ------------------------------------

    // TickEngine.HeartbeatElapsed.
    public void OnHeartbeat()
    {
        switch (_phase)
        {
            case Phase.AwaitingInventory:
                if (--_ticks > 0) return;
                if (!_askedInventory && _isAutoEnabled())
                {
                    _askedInventory = true;
                    _ticks = InventoryWaitTicks;
                    _log?.Info(LogCategory, "No inventory read has come since entering the game: sending `i`.");
                    _wire.Send("i");
                    return;
                }
                Abandon("no inventory read came after entering the game");
                break;
            case Phase.AwaitingRoom:
                if (--_ticks > 0 || _before is null) return;
                _log?.Info(LogCategory, "No room display came back: going by what has been read.");
                CheckRoom(_before);
                break;
            case Phase.AwaitingFightEnd:
                OnRoomObserved();
                break;
            case Phase.PickingUp:
                if (--_ticks <= 0) Complete();
                break;
        }
    }

    // RoomDisplayParser.RoomParsed: a room display was read to its exits line.
    public void NoteRoomDisplayed()
    {
        _roomShownHere = true;
        if (_phase != Phase.AwaitingRoom) return;
        _post(() =>
        {
            if (_phase == Phase.AwaitingRoom && _before is { } before) CheckRoom(before);
        });
    }

    // GroundItemTracker.SurveyUpdated. An empty floor prints no list, so a list
    // the tracker still holds from before the link dropped says nothing about now.
    public void NoteFloorSurveyed() => _floorShownHere = true;

    // The room's occupants were read again, or combat was force-cleared.
    public void OnRoomObserved()
    {
        if (_phase == Phase.AwaitingFightEnd && !_fighting()) TryPickUp();
    }

    // The map moved the character to another room (or lost it, on a death).
    public void OnRoomChanged()
    {
        _roomShownHere = false;
        _floorShownHere = false;
        if (_phase == Phase.AwaitingFightEnd)
            Finish($"the character left the room before the fight ended, so {Names(_plan)} stayed on the floor");
        else if (_phase == Phase.PickingUp)
            Complete();
    }

    // ----- ending -------------------------------------------------------

    // The pass ran to an answer. What it didn't find is not looked for again.
    private void Finish(string outcome)
    {
        LastStillMissing = HangupItemPlan.Outstanding(_missing, _taken);
        if (LastStillMissing.Count > 0)
            _log?.Info(LogCategory, $"Still missing: {Names(LastStillMissing.Select(m => (m.Name, m.Count)))}.");
        LastOutcome = $"{_now():HH:mm:ss} {outcome}";
        _log?.Info(LogCategory, $"Check ended: {outcome}.");
        End(outcome);
        // The list just compared is spent: what is held now replaces it.
        _saveProfile();
    }

    // The pass was cut short. The list on disk stays for the next entry.
    private void Abandon(string why)
    {
        if (_phase == Phase.Idle) return;
        LastOutcome = $"{_now():HH:mm:ss} not finished: {why}";
        _log?.Info(LogCategory, $"Check not finished: {why}.");
        End(why);
    }

    private void End(string why)
    {
        _phase = Phase.Idle;
        _before = null;
        _missing = new List<HangupMissingItem>();
        _plan = new List<(string Name, int Count)>();
        _taken.Clear();
        if (!_holding) return;
        _holding = false;
        _coordinator.ClearGate(MovementCoordinator.HangupItemCheckGate, AsserterName, why);
    }

    private void Hold(string reason)
    {
        if (_holding) return;
        _holding = true;
        _coordinator.AssertGate(MovementCoordinator.HangupItemCheckGate, AsserterName, reason);
    }

    private string Key(string name) => (_itemKey ?? ItemNameStore.Normalize)(name);

    private static string RoomText(RoomRef? room) => room is null ? "room not known" : $"room {room.Map}/{room.Room}";

    private static string Names(IEnumerable<(string Name, int Count)> items) =>
        string.Join(", ", items.Select(i => i.Count > 1 ? $"{i.Count} {i.Name}" : i.Name));
}
