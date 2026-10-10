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
// The pickup runs only on a realm whose settings say items are dropped (Settings →
// BBS, HangupPenaltyNotice.MaxItemsDropped), once per connection, takes back no
// more copies than those settings say were dropped, and has no part in deciding a
// hang-up.
//
// The order on the way in:
//   connect       — a list is stored for this realm and the realm drops items (or
//                   a death is in question, as described at the end): movement
//                   is held, so a loop that restarts on the first prompt can't
//                   walk out of the room before the check has run.
//   game entered  — wait for an inventory read. The login sends `i` itself; an
//                   entry made by hand gets one asked for after InventoryWaitTicks.
//   inventory in  — compare. Nothing short ends it. Otherwise the room is needed:
//                   the login's own display, or one bare Enter when none was read.
//   room known    — another room than the one the link dropped in ends it. Else a
//                   `get` for each short item the floor list shows, once any
//                   fight in the room is over.
//   gets answered — the gear set that was on is applied again when a piece of it
//                   came back, and the hold on movement is dropped.
//
// The hold never outlasts its reason. An entry or an inventory read that doesn't
// come in time gives it up, and so does Reset States; the list is kept, and the
// comparison still runs when the read arrives, without the hold, picking up only
// where the map is sure the character stands in the room it left the game in.
//
// Nothing is retried: one pass per connection, and what isn't on the floor then
// is logged as still missing and forgotten. Only a pass cut short by the link
// dropping again carries its unfound items over to the next connection.
//
// The same pass answers a second question first: did the hang-up kill the
// character? A board that penalises a hang-up kills a character that was dropped,
// or low enough for the HP it takes to drop it under the death threshold, after
// the link is gone, so no death line is ever seen (Recovery.HangupDeath has the
// rule). When the list says the character was that low and the realm penalises,
// the pass runs for that alone, item counts or not: at the inventory read it
// asks for a `stat` if none has given the lives, then judges. Only a life lost
// says "died". A death it finds is recorded as any other and ends the pass: what
// is missing then is a deathpile, Death Recovery's to fetch, and nothing here
// picks it up or calls it missing. When it can't tell, it says so once and
// records nothing.
//
// The question outlives the pass when the lives can't be had (Auto-All off, or
// the hold already given up): it stays open, without a hold and with nothing
// sent, and any `stat` read on the connection answers it. It is judged on what
// the entry showed (HP at the first prompt, the first inventory read), never on
// what the character has become by the time the answer arrives.
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
    // From the connect to the game: a login takes seconds, and a login that stops
    // at the board's menu (after a hang-up the user enters by hand) can take as
    // long as the user likes. Past this the hold is given up.
    private const int EntryWaitTicks = 180;
    // A `stat` answers within the second.
    private const int LivesWaitTicks = 3;

    // The line a death record shows where a witnessed death has the game's own.
    public const string DeathMessage = "Killed by the hang-up penalty (not seen: worked out on entering the game).";

    private enum Phase { Idle, AwaitingEntry, AwaitingInventory, AwaitingLives, AwaitingRoom, AwaitingFightEnd, PickingUp }

    private readonly MovementCoordinator _coordinator;
    private readonly Func<CharacterProfile?> _profile;
    private readonly Action _saveProfile;
    private readonly Func<string?> _realmKey;
    private readonly Func<int> _maxItemsDropped;
    private readonly Func<InventorySnapshot> _inventory;
    private readonly Func<RoomKey?> _confirmedRoom;
    private readonly Func<IReadOnlyList<string>> _floor;
    private readonly Func<string, string>? _itemKey;
    private readonly Func<bool> _fighting;
    private readonly Func<bool> _hostilePresent;
    private readonly Func<bool> _isAutoEnabled;
    private readonly Func<bool> _roomRedisplayFree;
    private readonly Action<string, int> _collect;
    private readonly Func<IReadOnlyCollection<string>, string?> _reapplyGearSet;
    private readonly Action<string>? _notice;
    private readonly Action<Action> _post;
    private readonly Func<DateTimeOffset> _now;
    private readonly LogService? _log;
    private readonly WireSender _wire = new();

    // What the death question reads. All unset in tests that don't ask it: the
    // list then carries no HP, and a list without one raises no suspicion.
    private readonly Func<(int Hp, int? MaxHp)?>? _vitals;
    private readonly Func<int?>? _lives;
    private readonly Func<bool>? _pvpFight;
    private readonly Func<bool>? _monsterFight;
    private readonly Func<bool, bool, int?>? _hpShareTop;
    private readonly Func<bool>? _stockRealm;
    private readonly Func<Recovery.UnwitnessedDeath, DeathRecord?>? _recordDeath;

    private Phase _phase;
    private HeldAtDisconnect? _before;
    private List<(string Name, int Count)> _missing = new();
    private List<(string Name, int Count)> _plan = new();
    private readonly Dictionary<string, int> _taken = new(StringComparer.OrdinalIgnoreCase);
    private int _ticks;
    private int _entryTicks;
    private bool _askedInventory;
    private bool _askedLives;
    private bool _holding;

    // Per connection.
    private bool _linkUp;
    private bool _inGame;
    private bool _enteredThisLink;
    private bool _inventoryReadThisLink;
    // The character died and no inventory has been read since: the record in
    // memory still lists what went into the deathpile, so nothing it says is held.
    private bool _heldUnknown;
    // These fall with a room change: a display read before it shows another
    // room's floor.
    private bool _roomShownHere;
    private bool _floorShownHere;
    // A move, typed or an engine's, went out after the game was entered.
    private bool _moveSentSinceEntry;
    // Lives off a `stat` read on this connection. The count the client carries
    // over a reconnect is the one from before, which is the other side of the
    // comparison.
    private int? _livesReadThisLink;
    // The count the client holds was given by the game on this connection (a
    // `stat`, or the readout of a death that was seen). Only then does it go on
    // the list: a count carried over from an earlier session would hide a death
    // nobody saw in between, and the next comparison would pin it on the wrong
    // hang-up.
    private bool _livesKnownThisLink;
    // A game prompt was read on this connection, so the HP the client holds is
    // this connection's and not the one it left the game with.
    private bool _promptThisLink;

    // The death question, open from the connect until it is answered or the link
    // drops: the list it is asked of. What it is judged on is taken when the game
    // is entered, not when the answer comes, since the answer can come much later
    // (the lives need a `stat`) and by then the character has played on.
    private HeldAtDisconnect? _deathBefore;
    // HP at the first prompt of this connection.
    private bool _hpAtEntryTaken;
    private int? _hpAtEntry;
    // The first inventory read of this connection.
    private InventorySnapshot? _firstInventory;
    // The board's two hang-up lines were printed on this connection.
    private bool _loginLines;
    // The terminal has been told the question is waiting for a `stat`.
    private bool _saidWaiting;

    //   profile          — the named character that is loaded, whose HeldAtDisconnect
    //                      is read and written; null with none (the default profile
    //                      is the template new characters are made from).
    //   realmKey         — the BBS and realm being played, as HeldAtDisconnect.Realm.
    //   maxItemsDropped  — HangupPenaltyNotice.MaxItemsDropped for that realm.
    //   confirmedRoom    — the room the map is sure of, else null.
    //   floor            — the room's latest floor list (GroundItemTracker.Items).
    //   fighting         — a monster the client is set to fight is in the room: the
    //                      pickup waits for the fight to end, as collect-after-combat does.
    //   hostilePresent   — any hostile monster is in the room, fought or not.
    //   isAutoEnabled    — Auto-All; off, nothing is sent.
    //   roomRedisplayFree — RoomRedisplayCoordinator.ShouldSend.
    //   collect          — AutoGetItemsManager.CollectNamed.
    //   reapplyGearSet   — AutoEquipCoordinator.ReapplySetAfterItemsReturned: given
    //                      the names picked up, the set it applied again, or null.
    //   notice           — a line for the terminal.
    //   itemKey          — see HangupItemPlan.Pickup; null for its default.
    //   post             — run after the handlers of the line now being read; the room
    //                      display parser reports a display before the map has taken it in.
    //   vitals           — HP off the statline, null until a prompt has given it, and
    //                      max HP, null until one is known (the statline carries none).
    //   lives            — the lives the client knows of, null when it knows none.
    //   pvpFight         — a fight with a player is on, or one has just attacked.
    //   monsterFight     — in combat with a monster, or a hostile one in the room.
    //   hpShareTop       — HangupPenaltyNotice.HpShareTop for the realm, given whether
    //                      the character was in a fight with a player, and with a monster.
    //   stockRealm       — the realm runs the Stock engine, where a death is known to
    //                      unequip everything and the board prints its hang-up lines.
    //   recordDeath      — RoomTracker.NoteUnwitnessedDeath: the record it made.
    public HangupItemRecheck(
        MovementCoordinator coordinator,
        Func<CharacterProfile?> profile,
        Action saveProfile,
        Func<string?> realmKey,
        Func<int> maxItemsDropped,
        Func<InventorySnapshot> inventory,
        Func<RoomKey?> confirmedRoom,
        Func<IReadOnlyList<string>> floor,
        Func<bool> fighting,
        Func<bool> hostilePresent,
        Func<bool> isAutoEnabled,
        Func<bool> roomRedisplayFree,
        Action<string, int> collect,
        Func<IReadOnlyCollection<string>, string?> reapplyGearSet,
        Action<string>? notice = null,
        Func<string, string>? itemKey = null,
        Action<Action>? post = null,
        Func<DateTimeOffset>? now = null,
        LogService? log = null,
        Func<(int Hp, int? MaxHp)?>? vitals = null,
        Func<int?>? lives = null,
        Func<bool>? pvpFight = null,
        Func<bool>? monsterFight = null,
        Func<bool, bool, int?>? hpShareTop = null,
        Func<bool>? stockRealm = null,
        Func<Recovery.UnwitnessedDeath, DeathRecord?>? recordDeath = null)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(saveProfile);
        ArgumentNullException.ThrowIfNull(realmKey);
        ArgumentNullException.ThrowIfNull(maxItemsDropped);
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(confirmedRoom);
        ArgumentNullException.ThrowIfNull(floor);
        ArgumentNullException.ThrowIfNull(fighting);
        ArgumentNullException.ThrowIfNull(hostilePresent);
        ArgumentNullException.ThrowIfNull(isAutoEnabled);
        ArgumentNullException.ThrowIfNull(roomRedisplayFree);
        ArgumentNullException.ThrowIfNull(collect);
        ArgumentNullException.ThrowIfNull(reapplyGearSet);
        _coordinator = coordinator;
        _profile = profile;
        _saveProfile = saveProfile;
        _realmKey = realmKey;
        _maxItemsDropped = maxItemsDropped;
        _inventory = inventory;
        _confirmedRoom = confirmedRoom;
        _floor = floor;
        _fighting = fighting;
        _hostilePresent = hostilePresent;
        _isAutoEnabled = isAutoEnabled;
        _roomRedisplayFree = roomRedisplayFree;
        _collect = collect;
        _reapplyGearSet = reapplyGearSet;
        _notice = notice;
        _itemKey = itemKey;
        _post = post ?? (static run => run());
        _now = now ?? (static () => DateTimeOffset.Now);
        _log = log;
        _vitals = vitals;
        _lives = lives;
        _pvpFight = pvpFight;
        _monsterFight = monsterFight;
        _hpShareTop = hpShareTop;
        _stockRealm = stockRealm;
        _recordDeath = recordDeath;
    }

    // ----- for the bug report -------------------------------------------

    private const string NoneYet = "none for this character this session";

    // Where the check stands, in words.
    public string Status => _phase switch
    {
        Phase.AwaitingEntry => "waiting for the game to be entered",
        Phase.AwaitingInventory => "waiting for an inventory read",
        Phase.AwaitingLives => "waiting for a `stat` read",
        Phase.AwaitingRoom => "waiting for a room display",
        Phase.AwaitingFightEnd => "waiting for the fight in the room to end",
        Phase.PickingUp => "picking up",
        _ => "idle",
    } + (_phase != Phase.Idle && !_holding ? " (movement no longer held)" : "")
      + (_phase == Phase.Idle && _deathBefore is not null ? "; a death by the hang-up is still to be judged, on the next `stat` read" : "");

    // How the last check ended, with its time.
    public string LastOutcome { get; private set; } = NoneYet;

    // What the last comparison found short, and what of it was still short when
    // the check ended.
    public IReadOnlyList<(string Name, int Count)> LastMissing { get; private set; } = [];
    public IReadOnlyList<(string Name, int Count)> LastStillMissing { get; private set; } = [];

    // The last time a hang-up that could have killed was looked into: what was
    // seen, and what it was taken to mean.
    public string LastDeathCheck { get; private set; } = NoneYet;

    public void SetWireSender(Action<byte[]> sender) => _wire.Bind(sender);

    internal List<byte[]> LastSentForTests => _wire.LastSentForTests;

    // ----- the list on disk ---------------------------------------------

    // ProfileService.ProfileSaving: write what is held now. Only for the character
    // the check reads from, in the game, with an inventory read on this connection:
    // before that the record in memory is the last session's, and the list on disk
    // is the one still to be compared. After a death there is nothing to write.
    public void StampForSave(CharacterProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (!ReferenceEquals(profile, _profile()) || !_inGame) return;
        if (_heldUnknown)
        {
            profile.HeldAtDisconnect = null;
            return;
        }
        // While the lives are waited for the inventory has been read and the list
        // on disk not yet compared with it.
        if (!_inventoryReadThisLink || _phase == Phase.AwaitingLives) return;

        List<(string Name, int Count)> outstanding = Outstanding();
        InventorySnapshot held = _inventory();
        (List<DeathItem> worn, List<DeathItem> carried) = DeathLootCapture.FromSnapshot(held);
        profile.HeldAtDisconnect = new HeldAtDisconnect
        {
            At = _now(),
            Realm = _realmKey(),
            Room = _confirmedRoom() is { } room ? new RoomRef(room.Map, room.Room) : null,
            // What an unfinished check hasn't found was dropped before this
            // connection; a drop of this one comes on top of it.
            PenaltiesSpanned = outstanding.Count > 0 ? Spanned(_before) + 1 : 1,
            Items = HangupItemPlan.WithOutstanding(HangupItemPlan.Held(held), outstanding),
            Hp = VitalsNow()?.Hp,
            MaxHp = VitalsNow()?.MaxHp,
            Lives = _livesKnownThisLink ? _lives?.Invoke() : null,
            PvpFight = _pvpFight?.Invoke() ?? false,
            InCombat = _monsterFight?.Invoke() ?? false,
            Worn = worn,
            Carried = carried,
            Coins = held.Currency,
        };
    }

    // Short items of a check that hasn't ended, which a list saved now must keep.
    private List<(string Name, int Count)> Outstanding() =>
        _phase is Phase.AwaitingRoom or Phase.AwaitingFightEnd or Phase.PickingUp
            ? HangupItemPlan.Outstanding(_missing, _taken)
            : new List<(string Name, int Count)>();

    private static int Spanned(HeldAtDisconnect? list) => Math.Max(1, list?.PenaltiesSpanned ?? 1);

    // ----- connection and game entry ------------------------------------

    // A new link is up. The check is decided here, not on the first game prompt,
    // because the movement engines act on that prompt too and the hold has to be
    // in place before any of them.
    public void NoteConnected()
    {
        Abandon("a new connection began");
        _linkUp = true;
        _inGame = false;
        _enteredThisLink = false;
        _inventoryReadThisLink = false;
        _heldUnknown = false;
        _roomShownHere = false;
        _floorShownHere = false;
        _moveSentSinceEntry = false;
        _livesReadThisLink = null;
        _livesKnownThisLink = false;
        _promptThisLink = false;
        _askedLives = false;
        _deathBefore = null;
        _hpAtEntryTaken = false;
        _hpAtEntry = null;
        _firstInventory = null;
        _loginLines = false;
        _saidWaiting = false;

        if (_profile()?.HeldAtDisconnect is not { } before) return;
        bool items = before.Items.Count > 0 && _maxItemsDropped() > 0;
        bool death = DeathSuspected(before);
        if (!items && !death) return;
        string? realm = _realmKey();
        if (realm is null || !string.Equals(before.Realm, realm, StringComparison.OrdinalIgnoreCase))
        {
            _log?.Debug(LogCategory,
                $"the stored list is for '{before.Realm ?? "(no realm)"}', not '{realm ?? "(no realm)"}': left alone");
            return;
        }

        _before = before;
        _phase = Phase.AwaitingEntry;
        _entryTicks = 0;
        string left = $"{before.At.ToLocalTime():yyyy-MM-dd HH:mm:ss}, {RoomText(before.Room)}";
        if (death)
        {
            _deathBefore = before;
            Hold("whether the hang-up penalty killed the character is checked on entering the game");
            _log?.Info(LogCategory,
                $"The character was {HpText(before)} when it was last in the game ({left}), {FightText(before)}, and this "
                + "realm's settings penalise that hang-up. A board that does kills a character that is dropped, or that "
                + "the HP it takes drops under the death threshold: checked on entering the game, from its lives "
                + $"({Shown(before.Lives)} then) against a `stat` read on this connection.");
            return;
        }
        Hold("items a hang-up may have dropped are checked for on entering the game");
        _log?.Info(LogCategory,
            $"This realm drops items for a hang-up. {before.Items.Count} kind(s) of item were held when the character "
            + $"was last in the game ({left}); they are checked against the inventory on entering the game.");
    }

    // The link is going down, told ahead of everything the drop tears down (the
    // fight with a player, combat, the room's occupants): the list written here
    // says what fight the character was in, which decides the penalty.
    public void NoteLinkDropping() => LeaveGame();

    // The link is gone. InGameCapture has normally reported leaving the game
    // already; an entry it never saw (taken from a room display) is left here.
    public void NoteDisconnected()
    {
        LeaveGame();
        _linkUp = false;
        // A question the pass left open, waiting for a `stat` that never came, ends
        // with the link: by now the list on disk is this connection's own.
        if (_deathBefore is not null && _phase == Phase.Idle)
        {
            LastDeathCheck = $"{_now():HH:mm:ss} not checked: the link dropped before a `stat` was read";
            _log?.Info(LogCategory,
                "Whether the hang-up penalty killed the character was never checked: the link dropped before a `stat` was read.");
        }
        _deathBefore = null;
        Abandon("the link dropped");
    }

    // InGameCapture.InGameChanged: on, a game prompt was read.
    public void OnInGameChanged(bool inGame)
    {
        if (!inGame)
        {
            LeaveGame();
            return;
        }
        _promptThisLink = true;
        // The statline's own handler runs ahead of this one (PromptParser is built
        // first), so the HP read here is that first prompt's.
        if (!_hpAtEntryTaken)
        {
            _hpAtEntryTaken = true;
            _hpAtEntry = _vitals?.Invoke()?.Hp;
        }
        EnterGame();
    }

    // The board's `Last time you were on, you disconnected while playing.`
    public void NoteHangupLoginLine()
    {
        if (_linkUp) _loginLines = true;
    }

    // StatParser.ScreenParsed, when that `stat` gave the lives.
    public void NoteLivesRead(int lives)
    {
        if (!_linkUp) return;
        _livesReadThisLink = lives;
        _livesKnownThisLink = true;
        if (_before is { } before && _phase == Phase.AwaitingLives)
            CompareAndSave(before);
        // Left open by a pass that couldn't ask for the lives itself: any `stat`
        // read on this connection answers it, the user's own included.
        else if (_deathBefore is { } open && _firstInventory is not null
                 && _phase is not (Phase.AwaitingEntry or Phase.AwaitingInventory))
            AnswerOpenQuestion(open);
    }

    private void EnterGame()
    {
        _inGame = true;
        if (_enteredThisLink) return;
        _enteredThisLink = true;
        if (_phase != Phase.AwaitingEntry) return;
        _phase = Phase.AwaitingInventory;
        _ticks = InventoryWaitTicks;
        _askedInventory = false;
    }

    // Out of the game, by a dropped link or an exit to the board's menu: what is
    // held now is what the next entry is checked against. Saved while the check
    // still counts as under way, so what it hasn't found goes onto the list.
    private void LeaveGame()
    {
        if (!_inGame) return;
        if (_inventoryReadThisLink) _saveProfile();
        _inGame = false;
        switch (_phase)
        {
            case Phase.AwaitingInventory or Phase.AwaitingLives:
                // Not compared yet, and nothing was stamped over the list: it waits
                // for the game to be entered again, on this link or the next. An
                // inventory read while the lives were waited for doesn't count on
                // that entry: a save made there would write over the list.
                _phase = Phase.AwaitingEntry;
                _enteredThisLink = false;
                _inventoryReadThisLink = false;
                break;
            case Phase.AwaitingRoom or Phase.AwaitingFightEnd or Phase.PickingUp:
                Abandon("the game was left before the check ended; what it hadn't found is kept on the list");
                break;
        }
    }

    // Another character was loaded: the check, and what it last found, belonged to
    // the one before.
    public void OnProfileLoaded()
    {
        Abandon("another profile was loaded");
        _inventoryReadThisLink = false;
        _heldUnknown = false;
        _deathBefore = null;
        _livesReadThisLink = null;
        _livesKnownThisLink = false;
        _firstInventory = null;
        LastOutcome = NoneYet;
        LastMissing = [];
        LastStillMissing = [];
        LastDeathCheck = NoneYet;
    }

    // RoomTracker.PlayerDeathObserved. The inventory record goes on naming what
    // went into the deathpile until it is read again (PostDeathInventoryRefresh
    // asks for that at the graveyard, and the link can drop first). A list written
    // from it would name the whole pile as held, and the next entry would read all
    // of it as dropped by a hang-up. The list is void until that read arrives.
    public void OnPlayerDied()
    {
        // The death's own readout gave the lives, and a death seen on this
        // connection has its own record: an open question about the hang-up before
        // it could no longer be told apart from it.
        _livesKnownThisLink = _linkUp;
        _deathBefore = null;
        if (!_inGame) return;
        _inventoryReadThisLink = false;
        _heldUnknown = true;
        if (_phase is Phase.Idle or Phase.AwaitingEntry) _saveProfile();
        // No word on the terminal: what is missing now is in the deathpile.
        else Finish("the character died, so what was held before no longer says what is missing", quiet: true);
    }

    // Reset States. Before the comparison only the hold goes: the list is still to
    // be compared, and is when an inventory is read. After it the check stops.
    public void Cancel(string reason)
    {
        switch (_phase)
        {
            case Phase.AwaitingEntry or Phase.AwaitingInventory or Phase.AwaitingLives:
                ReleaseHold($"{reason}: movement is no longer held; the list is kept and compared when an inventory is read");
                break;
            case Phase.AwaitingRoom or Phase.AwaitingFightEnd or Phase.PickingUp:
                Finish($"stopped ({reason})");
                break;
        }
    }

    // ----- the pass -----------------------------------------------------

    // InventoryManager.FullInventoryParsed.
    public void OnInventoryRead()
    {
        _inventoryReadThisLink = true;
        _heldUnknown = false;
        if (_linkUp) _firstInventory ??= _inventory();
        if (_phase != Phase.AwaitingInventory || _before is null) return;
        CompareAndSave(_before);
    }

    private void CompareAndSave(HeldAtDisconnect before)
    {
        Compare(before);
        // Still under way: put what is held now on disk in place of the list just
        // read, with the short items kept on it (StampForSave).
        if (_phase is not (Phase.Idle or Phase.AwaitingLives)) _saveProfile();
    }

    private void Compare(HeldAtDisconnect before)
    {
        _taken.Clear();
        _missing = new List<(string Name, int Count)>();
        LastMissing = [];
        LastStillMissing = [];
        _log?.Debug(LogCategory, $"held before: {Names(before.Items.Select(i => (i.Name, i.Count)))}");

        // A death seen since the list was written took what the list names into
        // its own pile, and has its own record.
        if (DeathSeenSince(before))
        {
            _deathBefore = null;
            Finish("a death was recorded after the list was written, so it no longer says what a hang-up took", quiet: true);
            return;
        }
        if (ReferenceEquals(_deathBefore, before))
        {
            switch (AskDeath(before, mayAsk: true))
            {
                case DeathAnswer.Asked or DeathAnswer.Recorded:
                    return;
                case DeathAnswer.Open or DeathAnswer.CantTell when !SameRoomAsLeft(before):
                    // A death isn't ruled out and the character isn't known to be
                    // where it left the game: what is missing may be a deathpile,
                    // and is neither picked up nor reported as dropped.
                    Finish("a death by the hang-up isn't ruled out, and the character isn't known to be in the room "
                           + "it left the game in", quiet: true);
                    return;
            }
        }
        if (before.Items.Count == 0 || _maxItemsDropped() <= 0)
        {
            Finish("the realm's settings don't say a hang-up drops items");
            return;
        }

        _missing = HangupItemPlan.Missing(before.Items, _inventory());
        LastMissing = _missing;
        LastStillMissing = _missing;
        if (_missing.Count == 0)
        {
            Finish("nothing held before is missing");
            return;
        }
        _log?.Info(LogCategory, $"Held before and missing now: {Names(_missing)}.");

        if (!_isAutoEnabled())
        {
            Finish("Auto-All is off");
            return;
        }
        if (CameInElsewhere(before) is { } elsewhere)
        {
            Finish(elsewhere);
            return;
        }
        // Once the hold is given up, or a move has gone out, nothing has kept the
        // character where it came in. Then only the map can say this is still the
        // room the items fell in, and a map that isn't sure of both rooms can't: it
        // keeps its old room while unsure, so walking doesn't show as a room change.
        if ((!_holding || _moveSentSinceEntry) && !SameRoomAsLeft(before))
        {
            Finish(_holding
                ? "a move was sent since the game was entered, and the map can't say this is the room the character left the game in"
                : "the inventory read came after the check had stopped holding movement, and the map can't say this "
                  + "is the room the character left the game in");
            return;
        }
        if (_roomShownHere)
        {
            CheckRoom(before);
            return;
        }
        // No hold, so no time to draw the room in: the floor stays unread.
        if (!_holding)
        {
            Finish(FloorUnseen);
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

    // ----- a hang-up that could have killed ------------------------------

    // The list says the character was dropped, or low enough for the penalty to
    // drop it, and the realm's settings penalise a hang-up in the fight it was in.
    // Only then is a death looked for.
    private bool DeathSuspected(HeldAtDisconnect before) =>
        Recovery.HangupDeath.Suspected(before.Hp, before.MaxHp, ShareTop(before));

    private int? ShareTop(HeldAtDisconnect before) => _hpShareTop?.Invoke(before.PvpFight, before.InCombat);

    private (int Hp, int? MaxHp)? VitalsNow() => _promptThisLink ? _vitals?.Invoke() : null;

    // A death seen since the list was written took what the list names into its
    // own pile, has its own record, and accounts for a life.
    private bool DeathSeenSince(HeldAtDisconnect before) =>
        _profile()?.DeathHistory is { } deaths && deaths.Exists(d => d.At > before.At);

    private enum DeathAnswer
    {
        No,         // no death: the question is closed
        Recorded,   // a death was recorded, and the pass ended with it
        Asked,      // a `stat` went out; the pass waits for it
        Open,       // the lives aren't known and can't be asked for; a `stat` read later answers
        CantTell,   // nothing will tell; said once, nothing recorded
    }

    // Asks the open question with what is known now.
    //   mayAsk — the pass is at its inventory read and may send a `stat` for the lives.
    private DeathAnswer AskDeath(HeldAtDisconnect before, bool mayAsk)
    {
        bool stock = _stockRealm?.Invoke() ?? false;
        InventorySnapshot held = _firstInventory ?? _inventory();
        int? share = ShareTop(before);
        // Stock only: where else a death unequips, or a board prints these lines,
        // isn't recorded.
        bool? worn = stock && _firstInventory is not null ? held.EquippedItems.Count > 0 : null;
        bool? loginLines = stock ? _loginLines : null;
        (Recovery.HangupDeathVerdict verdict, string why) = Recovery.HangupDeath.Judge(
            before.Hp, before.MaxHp, share, _hpAtEntry, before.Lives, _livesReadThisLink, worn, loginLines);

        List<(string Name, int Count)> gone = HangupItemPlan.Missing(before.Items, held);
        RoomKey? here = _confirmedRoom();
        string evidence =
            $"left the game {before.At.ToLocalTime():yyyy-MM-dd HH:mm:ss} at {RoomText(before.Room)} with HP "
            + $"{Shown(before.Hp)} of {Shown(before.MaxHp)}, lives {Shown(before.Lives)}, {FightText(before)}; the realm's "
            + $"settings say that hang-up costs up to {Shown(share)}% of max HP; on this connection: HP {Shown(_hpAtEntry)} "
            + $"at the first prompt, lives {Shown(_livesReadThisLink)} from a `stat`, "
            + (worn is { } w ? $"{(w ? "something" : "nothing")} worn at the first inventory read, " : "")
            + (loginLines is { } l ? $"the board's hang-up lines {(l ? "printed" : "not printed")}, " : "")
            + $"{(here is { } at ? $"room {at}" : "room not known")} now; held then and not at the first inventory read: "
            + (gone.Count > 0 ? Names(gone) : "nothing");
        _log?.Info(LogCategory, $"A hang-up the penalty could have killed for: {evidence}.");

        switch (verdict)
        {
            // Died is only ever said on a count read on this connection, so the
            // record never carries a made-up one.
            case Recovery.HangupDeathVerdict.Died when _livesReadThisLink is { } livesNow:
                RecordDeath(before, held, livesNow, why, evidence);
                return DeathAnswer.Recorded;

            case Recovery.HangupDeathVerdict.NeedsLives:
                // The login sends a `stat` ahead of its `i`; an entry made by hand
                // doesn't. One is sent only while the pass still holds movement:
                // once it has let go, nothing more goes out for it.
                if (mayAsk && !_askedLives && _holding && _inGame && _isAutoEnabled())
                {
                    _askedLives = true;
                    _phase = Phase.AwaitingLives;
                    _ticks = LivesWaitTicks;
                    _log?.Info(LogCategory, "No `stat` has been read since entering the game: sending one, for the lives.");
                    _wire.Send("stat");
                    return DeathAnswer.Asked;
                }
                LastDeathCheck = $"{_now():HH:mm:ss} waiting for a `stat` on this connection: {why}. Seen: {evidence}";
                SayWaiting(before, !_isAutoEnabled() ? "Auto-All is off, so no `stat` was sent"
                    : _askedLives ? "the `stat` sent for it wasn't answered"
                    : "the check had stopped holding movement, so no `stat` was sent");
                return DeathAnswer.Open;

            case Recovery.HangupDeathVerdict.Unsure:
                _deathBefore = null;
                LastDeathCheck = $"{_now():HH:mm:ss} can't tell, nothing recorded: {why}. Seen: {evidence}";
                _log?.Info(LogCategory, $"No death is recorded for the hang-up: {why}.");
                _notice?.Invoke($"[Hang-up check: you were {HpText(before)} when you left the game and this realm penalises "
                                + $"that hang-up, which can kill. No death was recorded: {why}"
                                + (gone.Count > 0 ? $". Held then and not now: {Names(gone)}" : "") + "]");
                return DeathAnswer.CantTell;

            default:
                _deathBefore = null;
                LastDeathCheck = $"{_now():HH:mm:ss} no death: {why}. Seen: {evidence}";
                _log?.Info(LogCategory, $"The hang-up didn't kill the character: {why}.");
                return DeathAnswer.No;
        }
    }

    // A `stat` was read while the question stood open after its pass.
    private void AnswerOpenQuestion(HeldAtDisconnect before)
    {
        if (DeathSeenSince(before))
        {
            _deathBefore = null;
            _log?.Info(LogCategory, "A death was recorded after the list was written: the hang-up before it is not judged.");
            return;
        }
        AskDeath(before, mayAsk: false);
    }

    // The question can't be answered yet and nothing more will be sent for it: say
    // so once, with what will answer it. Not when HP at the first prompt already
    // says there was no death.
    private void SayWaiting(HeldAtDisconnect before, string why)
    {
        if (_saidWaiting) return;
        if (_hpAtEntry is { } hp && (hp <= 0 || hp < (before.Hp ?? int.MinValue))) return;
        _saidWaiting = true;
        _log?.Info(LogCategory,
            $"Whether the hang-up penalty killed the character hasn't been checked: {why}. A `stat` and an inventory read on "
            + "this connection will answer it.");
        _notice?.Invoke($"[Hang-up check: you were {HpText(before)} when you left the game and this realm penalises that "
                        + $"hang-up, which can kill. Whether it did hasn't been checked: {why}. Type `stat` and `i` and it will be]");
    }

    // The record a death line would have made, from the list: where and when the
    // character was last in the game, and as its pile what it held then and
    // didn't at the first inventory read. Nothing is picked up here and nothing
    // reported as dropped; Death Recovery has the record from here on.
    private void RecordDeath(HeldAtDisconnect before, InventorySnapshot held, int lives, string why, string evidence)
    {
        _deathBefore = null;
        (List<DeathItem> equipped, List<DeathItem> lost) = Recovery.HangupDeath.Pile(before, held);
        DeathRecord? record = _recordDeath?.Invoke(new Recovery.UnwitnessedDeath(
            before.Room, before.At, lives, DeathMessage, equipped, lost, before.Coins));

        string where = record?.RoomName is { Length: > 0 } name ? $"{name} ({record.RoomKeyText})" : RoomText(before.Room);
        string number = record is null ? "" : $" #{record.RecordNumber}";
        LastDeathCheck = $"{_now():HH:mm:ss} died, death{number} recorded: {why}. Seen: {evidence}";
        _log?.Info(LogCategory,
            $"Death{number} recorded for the hang-up at {where}: {why}. Pile: {equipped.Count} worn, {lost.Count} carried"
            + $"{(before.Coins is { TotalCoinCount: > 0 } coins ? $", {coins.TotalCoinCount} coin(s)" : "")}. "
            + "The item check stands down: what is missing is the pile.");
        _notice?.Invoke(
            $"[Hang-up check: the character died to the hang-up penalty at {where}: it was {HpText(before)} when the link "
            + $"went down ({before.At.ToLocalTime():HH:mm:ss}), and has {lives} {(lives == 1 ? "life" : "lives")} left. "
            + $"Death{number} is recorded; Recover Now in Death Recovery goes for the pile]");
        // What an item pass called missing was the pile.
        LastMissing = [];
        _missing = new List<(string Name, int Count)>();
        Finish("the hang-up killed the character; the death is recorded and the pile is Death Recovery's", quiet: true);
    }

    private static string HpText(HeldAtDisconnect before) =>
        before.Hp is <= 0 ? $"dropped (HP {before.Hp})" : $"at {Shown(before.Hp)} of {Shown(before.MaxHp)} HP";

    private static string FightText(HeldAtDisconnect before) =>
        before.PvpFight ? "in a fight with a player"
        : before.InCombat ? "in a fight with a monster"
        : "in no fight the client knew of";

    private static string Shown(int? value) =>
        value is { } known ? known.ToString(System.Globalization.CultureInfo.InvariantCulture) : "not known";

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
            ? $"the character is at {here}, not at {left.Map}/{left.Room} where it left the game"
            : null;

    private bool SameRoomAsLeft(HeldAtDisconnect before) =>
        before.Room is { } left && _confirmedRoom() is { } here && here.Map == left.Map && here.Room == left.Room;

    // A dark room, or a blinded character, prints no room display and no floor
    // list. That is not the items being gone.
    private const string FloorUnseen = "the floor here couldn't be seen (no room display could be read: dark, or blinded)";

    private void TryPickUp()
    {
        List<(string Name, int Count)> outstanding = HangupItemPlan.Outstanding(_missing, _taken);
        // It can have been switched off while a fight was waited out.
        if (!_isAutoEnabled())
        {
            Finish("Auto-All is off");
            return;
        }
        if (!_roomShownHere && !_floorShownHere)
        {
            Finish(FloorUnseen);
            return;
        }

        IReadOnlyList<string> floor = _floorShownHere ? _floor() : Array.Empty<string>();
        List<(string Name, int Count)> lying = HangupItemPlan.Pickup(outstanding, floor, _itemKey);
        if (lying.Count == 0)
        {
            Finish("none of them is on the floor here");
            return;
        }

        // No more than the board can have dropped: its count for one hang-up, for
        // each drop of the link the list covers.
        int most = Math.Max(0, _maxItemsDropped()) * Spanned(_before);
        _plan = HangupItemPlan.Capped(lying, most);
        int beyond = lying.Sum(p => p.Count) - _plan.Sum(p => p.Count);
        if (beyond > 0)
            _log?.Info(LogCategory,
                $"On the floor here: {Names(lying)}. The realm's settings say a hang-up drops at most {most} item(s), "
                + $"so {beyond} of them are beyond what the penalty takes and are left.");
        if (_plan.Count == 0)
        {
            Finish("the realm's settings no longer say a hang-up drops items");
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
            Finish("a hostile monster the client isn't fighting is in the room");
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
        if (got.Count == 0)
        {
            Finish("the game didn't confirm any of the gets");
            return;
        }

        _log?.Info(LogCategory, $"Picked up: {Names(got)}.");
        _notice?.Invoke($"[Hang-up item check: picked up {Names(got)} from the floor]");
        // Ahead of the hold being dropped, so the wear commands go out before a
        // waiting loop's next step. Which pieces go on, and whether any does, is the
        // equipment manager's to say.
        string? set = _reapplyGearSet(got.Select(g => g.Name).ToList());
        _log?.Info(LogCategory, set is not null
            ? $"Gear set '{set}' was applied again, for the pieces of it that came back."
            : "No gear set was applied again: none of what came back is in the set that is on, or the equipment "
              + "manager held the apply (its own [Equipment] line says which).");
        Finish($"picked up {Names(got)}",
            notPickedUp: "not on the floor here, or more than the realm's settings say a hang-up drops");
    }

    // ----- what moves the pass along ------------------------------------

    // TickEngine.HeartbeatElapsed.
    public void OnHeartbeat()
    {
        switch (_phase)
        {
            case Phase.AwaitingEntry:
                if (_holding && ++_entryTicks >= EntryWaitTicks)
                    ReleaseHold($"the game wasn't entered within {EntryWaitTicks / 60} minutes of connecting: movement is no "
                                + "longer held; the list is kept and compared if the game is entered");
                break;
            case Phase.AwaitingInventory:
                // Given up: the read is waited for without asking or holding.
                if (!_holding || --_ticks > 0) return;
                if (!_askedInventory && _isAutoEnabled())
                {
                    _askedInventory = true;
                    _ticks = InventoryWaitTicks;
                    _log?.Info(LogCategory, "No inventory read has come since entering the game: sending `i`.");
                    _wire.Send("i");
                    return;
                }
                ReleaseHold("no inventory read came after entering the game: movement is no longer held; the list is "
                            + "kept and compared when one is read");
                break;
            case Phase.AwaitingLives:
                if (--_ticks > 0 || _before is null) return;
                _log?.Info(LogCategory, "No `stat` read came back: the item check goes on without it.");
                CompareAndSave(_before);
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
        // Only the game draws a room. The game prompt is what normally says the
        // game was entered, and a statline it can't read would leave the hold up
        // on a character that is plainly in the game.
        if (_linkUp && !_inGame)
        {
            _log?.Debug(LogCategory, "a room display was read before any game prompt: taken as the game being entered");
            EnterGame();
        }
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
            Finish("the character left the room before the fight in it ended");
        else if (_phase == Phase.PickingUp)
            Complete();
    }

    // OutboundMovementObserver.MoveSent.
    public void NoteMoveSent()
    {
        if (_enteredThisLink) _moveSentSinceEntry = true;
    }

    // ----- ending -------------------------------------------------------

    // The pass ran to an answer. What it didn't find is not looked for again, so
    // the terminal says what that is and why, however the pass ended.
    //   notPickedUp — the reason for the terminal, where the outcome isn't one.
    //   quiet       — say nothing on the terminal.
    private void Finish(string outcome, string? notPickedUp = null, bool quiet = false)
    {
        LastStillMissing = HangupItemPlan.Outstanding(_missing, _taken);
        if (LastStillMissing.Count > 0)
        {
            _log?.Info(LogCategory, $"Still missing: {Names(LastStillMissing)}.");
            if (!quiet)
                _notice?.Invoke($"[Hang-up item check: {Names(LastStillMissing)} missing since you were last in the game. "
                                + $"Not picked up: {notPickedUp ?? outcome}]");
        }
        LastOutcome = $"{_now():HH:mm:ss} {outcome}";
        _log?.Info(LogCategory, $"Check ended: {outcome}.");
        End(outcome);
        // The list just compared is spent: what is held now replaces it.
        _saveProfile();
    }

    // The pass was cut short by the link or by another character being loaded.
    // The list on disk is what the next connection reads: the one still to be
    // compared, since nothing is written before an inventory read, or the one
    // LeaveGame saved with the unfound items on it.
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
        _missing = new List<(string Name, int Count)>();
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

    // Stop holding movement and keep waiting: the list is still to be compared.
    private void ReleaseHold(string why)
    {
        if (!_holding) return;
        _holding = false;
        _log?.Info(LogCategory, $"Hold given up: {why}.");
        LastOutcome = $"{_now():HH:mm:ss} still waiting, without the hold: {why}";
        _coordinator.ClearGate(MovementCoordinator.HangupItemCheckGate, AsserterName, why);
        // From here nothing more is sent for a death in question either.
        if (_deathBefore is { } open)
            SayWaiting(open, "the check stopped holding movement before it could run");
    }

    private string Key(string name) => (_itemKey ?? ItemNameStore.Normalize)(name);

    private static string RoomText(RoomRef? room) => room is null ? "room not known" : $"room {room.Map}/{room.Room}";

    private static string Names(IEnumerable<(string Name, int Count)> items) =>
        string.Join(", ", items.Select(i => i.Count > 1 ? $"{i.Count} {i.Name}" : i.Name));
}
