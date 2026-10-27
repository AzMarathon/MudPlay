using System.Collections.Generic;
using System.IO;
using System.Linq;
using MudPlay.Game;
using MudPlay.Game.Combat;
using MudPlay.Game.Inventory;
using MudPlay.Game.Map;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using Xunit;

namespace MudPlay.Tests;

// PostKillFloorCheck sends one bare Enter after a kill whose monster could have
// dropped an item, so the floor is shown again, and holds the walker until that
// display has been read. Kills come in as the game prints them, through
// MonsterDeathWatcher; the Enter goes out through a send-gate-wrapped sender, as
// every engine's does; room displays are parsed by RoomDisplayParser.
public sealed class PostKillFloorCheckTests : IDisposable
{
    private const int Dropper = 1;       // orc lieutenant: item 900 at 5%
    private const int NonDropper = 2;    // giant rat: no drops
    private const int OtherDropper = 3;  // kobold: item 901 at 10%
    private const int ZeroChance = 4;    // cave bear: item 900 listed at 0%

    private const string Monsters = """
        [
          { "Number": 1, "Name": "orc lieutenant", "EXP": 50, "DropItem-0": 900, "DropItem%-0": 5 },
          { "Number": 2, "Name": "giant rat", "EXP": 10 },
          { "Number": 3, "Name": "kobold", "EXP": 20, "DropItem-0": 901, "DropItem%-0": 10 },
          { "Number": 4, "Name": "cave bear", "EXP": 30, "DropItem-0": 900, "DropItem%-0": 0 }
        ]
        """;

    private const string Rooms = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Town Gates",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "0", "S": "1/2", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "South Road",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "1/1", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    private readonly string _root;
    private readonly MessageRouter _router = new();
    private readonly ServerFeed _feed;
    private readonly MonsterDeathWatcher _deaths;
    private readonly RoomDisplayParser _displays;
    private readonly ProfileService _profile = TestProfiles.Loaded();
    private readonly AutoModeController _master;
    private readonly EngineSendGate _sendGate = new();
    private readonly MovementCoordinator _coordinator = new();
    private readonly AcquisitionGate _acquisition;
    private readonly ManualTimers _timers = new();
    private readonly PostKillFloorCheck _check;
    private readonly List<string> _sent = new();

    // What CombatManager's attribution would name as the monster that died.
    private int? _fighting;
    private bool _hostilesLeft;
    private bool _moveOnItsWay;
    private bool _canSee = true;
    private readonly HashSet<int> _flaggedAutoCollect = new();

    public PostKillFloorCheckTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-floorcheck-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Monsters.json"), Monsters);
        File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), Rooms);
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        MonsterCatalog catalog = new(cache);
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");

        DefaultPatterns.Seed(_router);
        _feed = new ServerFeed(_router);
        _deaths = new MonsterDeathWatcher(_router);
        _displays = new RoomDisplayParser(_feed.Lines, new RoomTracker(graph));
        _master = new AutoModeController(_profile);
        _acquisition = new AcquisitionGate(_coordinator);

        _check = new PostKillFloorCheck(_router,
            dyingMonsters: _ => _fighting is { } n ? new[] { n } : System.Array.Empty<int>(),
            monster: catalog.Get,
            autoCollectWanted: _flaggedAutoCollect.Contains,
            lookForAnyDrop: () =>
                SettingsSection.Read<CashSettings>(_profile.Current, "Cash").LookAfterKillingDropper,
            hasEngageableHostiles: () => _hostilesLeft,
            canSeeRoom: () => _canSee,
            moveOnItsWay: () => _moveOnItsWay,
            sendGateHeld: () => _sendGate.IsLocked,
            bareEntersSent: () => _sendGate.BareEntersSent,
            blocked: detail => _master.Blocks(PostKillFloorCheck.SystemName, detail),
            itemName: id => id switch { 900 => "black star key", 901 => "rusty dagger", _ => null },
            schedule: _timers.Arm);
        _check.SetWireSender(_sendGate.WrapEngineSender(bytes => _sent.Add(Text(bytes))));
        _check.SetAcquisitionGate(_acquisition);
        _check.SetRoomRedisplay(new RoomRedisplayCoordinator());
        // As AppServices and MainWindowViewModel wire them.
        _deaths.MonsterDied += _check.OnMonsterDied;
        _displays.RoomParsed += _ => _check.NoteRoomDisplayed();
    }

    public void Dispose()
    {
        _check.Dispose();
        _displays.Dispose();
        _deaths.Dispose();
        _acquisition.Dispose();
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    private static string Text(byte[] bytes) => System.Text.Encoding.Latin1.GetString(bytes);

    // One server read: the rows arrive together, as a round's output does.
    private void Read(params string[] rows) => _feed.Raw(string.Join("\r\n", rows) + "\r\n");

    private void Kill(int monster, string deathLine = "The monster collapses.", int exp = 50)
    {
        _fighting = monster;
        Read(deathLine, $"You gain {exp} experience.", "*Combat Off*", "[HP=100/MA=50]:");
    }

    private void RoomDisplay() => Read("Town Gates", "Obvious exits: south", "[HP=100/MA=50]:");

    private bool WalkerHeld => _coordinator.IsGateAsserted(MovementCoordinator.AcquisitionGate);

    [Fact]
    public void KillingAMonsterThatCanDrop_SendsOneEnter_AndHoldsTheWalkerUntilTheRoomShows()
    {
        Kill(Dropper);

        Assert.Equal(new[] { "\r" }, _sent);
        Assert.True(WalkerHeld);
        Assert.Contains("sent an Enter after 1 kill(s)", _check.LastCheck);

        RoomDisplay();

        Assert.False(WalkerHeld);
        Assert.Single(_sent);
    }

    [Fact]
    public void KillingAMonsterThatDropsNothing_SendsNothing()
    {
        Kill(NonDropper);

        Assert.Empty(_sent);
        Assert.False(WalkerHeld);
    }

    // The game data is the only guide: a drop listed at 0% is no chance of one.
    [Fact]
    public void ZeroPercentDrop_SendsNothing()
    {
        Kill(ZeroChance, exp: 30);

        Assert.Empty(_sent);
    }

    [Fact]
    public void AnUnknownMonster_SendsNothing()
    {
        _fighting = null;
        Read("You gain 50 experience.", "*Combat Off*");

        Assert.Empty(_sent);
    }

    [Fact]
    public void TwoDroppersKilledInOneRead_ShareOneEnter()
    {
        _fighting = Dropper;
        Read("The orc lieutenant collapses.", "You gain 50 experience.", "*Combat Off*",
             "The kobold collapses.", "You gain 20 experience.", "*Combat Off*", "[HP=100/MA=50]:");

        Assert.Equal(new[] { "\r" }, _sent);
        Assert.Contains("after 2 kill(s)", _check.LastCheck);
    }

    // Kills a round apart: the look waits for the room's fight to end, then goes once.
    [Fact]
    public void DroppersKilledOneByOne_LookOnceTheFightIsOver()
    {
        _hostilesLeft = true;
        Kill(Dropper);
        Assert.Empty(_sent);
        Assert.Contains("waiting for the room's fight to end", _check.Describe());

        Kill(OtherDropper, exp: 20);
        Assert.Empty(_sent);

        // The last monster, which drops nothing, ends the fight: the look owed for the
        // others goes out, and the walker is held for it.
        _hostilesLeft = false;
        Kill(NonDropper, exp: 10);

        Assert.Equal(new[] { "\r" }, _sent);
        Assert.Contains("after 2 kill(s)", _check.LastCheck);
        Assert.True(WalkerHeld);
    }

    [Fact]
    public void SettingOff_SendsNothing()
    {
        SettingsSection.Write(_profile.Current!, "Cash", new CashSettings { LookAfterKillingDropper = false }, "test");

        Kill(Dropper);

        Assert.Empty(_sent);
        Assert.False(WalkerHeld);
    }

    [Fact]
    public void SettingIsOnByDefault()
    {
        Assert.True(SettingsSection.Read<CashSettings>(_profile.Current, "Cash").LookAfterKillingDropper);
    }

    // An Auto-collect drop is looked for with the setting off, and while the fight
    // goes on, so auto-get can take it.
    [Fact]
    public void AutoCollectDrop_LooksAtOnce_WhateverTheSetting()
    {
        SettingsSection.Write(_profile.Current!, "Cash", new CashSettings { LookAfterKillingDropper = false }, "test");
        _flaggedAutoCollect.Add(900);
        _hostilesLeft = true;

        Kill(Dropper);

        Assert.Equal(new[] { "\r" }, _sent);
        Assert.Contains("flagged Auto-collect", _check.LastCheck);
    }

    [Fact]
    public void RoomShownAgainAfterTheKill_NoEnterOfOurOwn()
    {
        _fighting = Dropper;
        Read("You gain 50 experience.", "*Combat Off*", "Town Gates", "Obvious exits: south", "[HP=100/MA=50]:");

        Assert.Empty(_sent);
        Assert.False(WalkerHeld);
        Assert.Contains("the room was displayed after the kill", _check.LastCheck);
    }

    // The combat engine's roster re-read (or the cash engine's look) put an Enter on
    // the wire in the kill's own pass: its display shows the floor, so ours isn't sent,
    // and the walker still waits for that display.
    [Fact]
    public void AnotherEnginesEnterSinceTheKill_NoSecondEnter_WalkerStillWaits()
    {
        Action<byte[]> combat = _sendGate.WrapEngineSender(bytes => _sent.Add(Text(bytes)));
        _deaths.MonsterDied += _ => combat(new[] { (byte)'\r' });

        Kill(Dropper);

        Assert.Equal(new[] { "\r" }, _sent);
        Assert.True(WalkerHeld);
        Assert.Contains("an Enter has gone out since the kill", _check.LastCheck);

        RoomDisplay();
        Assert.False(WalkerHeld);
    }

    // A walk step already sent: the room shown next is the next room, so no Enter, and
    // the walker isn't held back.
    [Fact]
    public void MoveOnItsWay_NoEnter_WalkerNotHeld()
    {
        _moveOnItsWay = true;

        Kill(Dropper);

        Assert.Empty(_sent);
        Assert.False(WalkerHeld);
        Assert.Contains("a move is on its way", _check.LastCheck);
    }

    // No step leaves between the kill and the display the Enter asked for: the hold is
    // up from the death itself, before the read ends and before the Enter goes out.
    [Fact]
    public void WalkStep_WaitsForTheLook_ThenMayGo()
    {
        bool heldAtTheDeath = false;
        _deaths.MonsterDied += _ => heldAtTheDeath = WalkerHeld;

        Kill(Dropper);

        Assert.True(heldAtTheDeath);
        Assert.True(_coordinator.IsPaused);
        RoomDisplay();
        Assert.False(_coordinator.IsPaused);
    }

    [Fact]
    public void LeftTheRoomBeforeTheLook_NothingOwed()
    {
        _hostilesLeft = true;
        Kill(Dropper);
        _check.OnRoomChanged();
        _hostilesLeft = false;
        Read("[HP=100/MA=50]:");

        Assert.Empty(_sent);
        Assert.Contains("left the room first", _check.LastCheck);
    }

    [Fact]
    public void SendGateHeld_NoEnter()
    {
        _sendGate.Hold("test hold");

        Kill(Dropper);

        Assert.Empty(_sent);
        Assert.False(WalkerHeld);
        Assert.Contains("the send gate is held", _check.LastCheck);
    }

    [Fact]
    public void MasterSwitchOff_NoEnter()
    {
        _master.TurnOff("test");

        Kill(Dropper);

        Assert.Empty(_sent);
        Assert.Contains("the master switch is off", _check.LastCheck);
    }

    [Fact]
    public void DarkOrBlind_NoEnter()
    {
        _canSee = false;

        Kill(Dropper);

        Assert.Empty(_sent);
        Assert.Contains("can't be seen", _check.LastCheck);
    }

    [Fact]
    public void DisplayThatNeverComes_LetsTheWalkerGo()
    {
        Kill(Dropper);
        Assert.True(WalkerHeld);

        _timers.Advance(PostKillFloorCheck.ReleaseAfter);

        Assert.False(WalkerHeld);
    }

    [Fact]
    public void RoomDisplayWithNoLookOut_LeavesAnotherHoldAlone()
    {
        _acquisition.NoteDeferredPending(1);

        RoomDisplay();

        Assert.True(WalkerHeld);
    }
}
