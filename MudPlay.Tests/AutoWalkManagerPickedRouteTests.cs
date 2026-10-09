using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MudPlay.Game.Map;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// The walk a route card starts is the route the card showed. The walker plans for
// itself from what it is told, and on a re-plan or a detour leg again, so these pin
// what it has to be told and keep: which gates the route went round (report
// paradigm-20261008-173911) and how it takes teleports (report
// paradigm-20261008-174236).
public sealed class AutoWalkManagerPickedRouteTests : IDisposable
{
    private readonly string _root;

    public AutoWalkManagerPickedRouteTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-walker-pickedroute-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    // The talisman exit (815) is one hop from Hills to Lake; the way round is three.
    // Past the lake both cross the orb gate (807) to Dest, and the Giver stands
    // beyond it.
    //   1/1 Hills ─E(815)─ 1/4 Lake ─E(807)─ 1/9 Dest ─E─ 1/10 Giver
    //     └N─ 1/2 ─N─ 1/3 ─E─┘
    private const string GatedRooms = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Hills", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/2", "S": "0", "E": "1/4 (Item: 815)", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "Tunnel", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/3", "S": "1/1", "E": "0", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 3, "Name": "Tunnel End", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/2", "E": "1/4", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 4, "Name": "Lake", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/9 (Item: 807)", "W": "1/3", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 9, "Name": "Dest", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "1/10", "W": "1/4", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 10, "Name": "Giver", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "1/9", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    // Three steps north on foot, or one teleport hop, from Grove to the Stone Arch.
    //   1/10 Grove ─N─ 1/11 ─N─ 1/12 ─N─ 7/131 Stone Arch
    //     └─ SW (CMD 100 teleport) ───────────┘
    private const string TeleportRooms = """
        [
          { "Map Number": 1, "Room Number": 10, "Name": "Grove", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0, "CMD": 100,
            "N": "1/11", "S": "0", "E": "0", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "7/131 (Item: 474)", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 11, "Name": "Trail", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/12", "S": "1/10", "E": "0", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 12, "Name": "Ridge", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "7/131", "S": "1/11", "E": "0", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 7, "Room Number": 131, "Name": "Stone Arch", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/12", "E": "0", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    private static readonly RoomKey Hills = new(1, 1), Dest = new(1, 9), Giver = new(1, 10);
    private static readonly RoomKey Grove = new(1, 10), Trail = new(1, 11), Arch = new(7, 131);
    private static readonly int[] Talisman = { 815 };

    private sealed class Harness
    {
        public required BfsMapper Bfs { get; init; }
        public required RoomGraphManager Graph { get; init; }
        public required MovementFilter Filter { get; init; }
        public required MovementCoordinator Coordinator { get; init; }
        public required AutoWalkManager Walker { get; init; }
        public List<string> Sent { get; } = new();
        public List<WalkEvent> Events { get; } = new();

        // The walk planned last, as "3 step(s)" or "0 move(s), 1 action(s)".
        public string LastPlan => Events.Last(e => e.Kind == WalkEventKind.Started).Detail;
    }

    // The walker starts paused, so each WalkTo plans and waits: a test reads the plan
    // and lets it go with Play.
    // carriesEverything: the teleport fixture's exit is an item-use one, shut to a
    // crosser without its item, and those tests aren't about item gates.
    private Harness NewHarness(string rooms, RoomKey at, bool carriesEverything = false)
    {
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), rooms);
        File.WriteAllText(Path.Combine(_root, "alpha", "TBInfo.json"),
            """[ { "Number": 100, "Action": "go arch:teleport 131 7\n" } ]""");
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        TBInfoStore tbinfo = new(cache);
        tbinfo.OnActiveSetChanged("alpha");
        RoomGraphManager graph = new(cache, log: null, tbinfo);
        graph.OnActiveSetChanged("alpha");
        RoomTracker tracker = new(graph);
        MovementCoordinator coordinator = new();
        BfsMapper bfs = new(graph);
        ProfileService profile = new();
        profile.LoadBlank();
        MovementFilter filter = new(profile)
        {
            InventoryReadyProbe = () => true,
            ItemCarriedProbe = _ => carriesEverything,
        };
        AutoWalkManager walker = new(graph, bfs, tracker, coordinator, filter);
        Harness h = new() { Bfs = bfs, Graph = graph, Filter = filter, Coordinator = coordinator, Walker = walker };
        walker.SetWireSender(b => h.Sent.Add(Encoding.Latin1.GetString(b).TrimEnd('\r')));
        walker.SetTeleportResolver((_, _) => "go arch");
        walker.Event += h.Events.Add;
        tracker.SetLocated(at);
        coordinator.AssertGate(MovementCoordinator.UserGate);
        return h;
    }

    private static void Play(Harness h) => h.Coordinator.ClearGate(MovementCoordinator.UserGate);

    // ----- the gates a picked route goes round ------------------------------

    [Fact]
    public void GatedPick_WalksTheRouteItsCardShowed_NotTheShortcutItWentRound()
    {
        Harness h = NewHarness(GatedRooms, Hills);
        RouteChoice card = RouteChoicePlanner.Evaluate(h.Bfs, h.Filter, h.Graph, Hills, Dest)!;
        Assert.Equal(4, card.GatedStepCount);                     // round the talisman exit
        Assert.Equal(Talisman, card.ClosedGateItems);

        h.Walker.WalkTo(Dest, planThroughAcquirableGates: true, pickedRoute: true,
            keepGatesClosedFor: card.ClosedGateItems);

        Assert.Equal("4 step(s)", h.LastPlan);
        Play(h);
        Assert.Equal(new[] { "n" }, h.Sent);
    }

    // What the walker did before it was told: every gate open, so straight at the
    // talisman exit, which the crosser can't pass.
    [Fact]
    public void GatedWalk_ToldNothing_TakesTheShortcut()
    {
        Harness h = NewHarness(GatedRooms, Hills);

        h.Walker.WalkTo(Dest, planThroughAcquirableGates: true, pickedRoute: true);

        Assert.Equal("2 step(s)", h.LastPlan);
    }

    // A detour leg is a walk of its own that states nothing. It plans through gates
    // while an item is still owed, and must still go round the ones the route did.
    [Fact]
    public void DetourLeg_KeepsTheJourneysClosedGates()
    {
        Harness h = NewHarness(GatedRooms, Hills);
        h.Walker.WalkTo(Dest, planThroughAcquirableGates: true, pickedRoute: true, keepGatesClosedFor: Talisman);

        h.Walker.WalkTo(Giver, planThroughAcquirableGates: true, supersedeSilently: true);

        Assert.Equal("5 step(s)", h.LastPlan);                    // not 3, through the talisman exit
        Assert.Equal(Talisman, h.Walker.JourneyClosedGateItems);
    }

    [Fact]
    public void ANewWalk_StartsWithoutTheLastJourneysClosedGates()
    {
        Harness h = NewHarness(GatedRooms, Hills);
        h.Walker.WalkTo(Dest, planThroughAcquirableGates: true, pickedRoute: true, keepGatesClosedFor: Talisman);

        h.Walker.WalkTo(Giver, planThroughAcquirableGates: true);

        Assert.Equal("3 step(s)", h.LastPlan);
        Assert.Null(h.Walker.JourneyClosedGateItems);
    }

    // ----- teleports on the legs of a walk the user started -----------------

    private static IReadOnlySet<(RoomKey From, RoomKey To)> NoTeleports() => new HashSet<(RoomKey, RoomKey)>();

    // The user picked a route that teleports. A detour router takes the walk out to
    // a giver and back; the leg back states no preference, and used to be planned
    // by the automatic-walk setting as if the client had started it.
    [Fact]
    public void DetourLegOfAUserWalk_TakesTeleportsAsThatWalkDoes()
    {
        Harness h = NewHarness(TeleportRooms, Grove, carriesEverything: true);
        h.Walker.SetAutomaticWalkTeleports(NoTeleports);
        h.Walker.WalkTo(Arch, preferTeleportFree: false);

        h.Walker.WalkTo(Arch, supersedeSilently: true);
        Play(h);

        Assert.Equal(new[] { "go arch" }, h.Sent);
        Assert.DoesNotContain(h.Events, e => e.Kind == WalkEventKind.Failed);
    }

    // A walk the client started on its own has no such preference to hand on, so its
    // detour legs follow the setting as before.
    [Fact]
    public void DetourLegOfAnAutomaticWalk_StillFollowsTheSetting()
    {
        Harness h = NewHarness(TeleportRooms, Grove, carriesEverything: true);
        h.Walker.SetAutomaticWalkTeleports(NoTeleports);
        h.Walker.WalkTo(Arch);

        h.Walker.WalkTo(Arch, supersedeSilently: true);
        Play(h);

        Assert.Equal(new[] { "n" }, h.Sent);
    }

    // Stopped, the journey is over: a later silent walk is nobody's detour leg.
    [Fact]
    public void AStoppedJourney_HandsNothingOn()
    {
        Harness h = NewHarness(TeleportRooms, Grove, carriesEverything: true);
        h.Walker.SetAutomaticWalkTeleports(NoTeleports);
        h.Walker.WalkTo(Arch, preferTeleportFree: false);
        h.Walker.Stop();

        h.Walker.WalkTo(Arch, supersedeSilently: true);
        Play(h);

        Assert.Equal(new[] { "n" }, h.Sent);
        Assert.Null(h.Walker.JourneyPreferTeleportFree);
    }

    // The leg out to the giver ends there, and the journey goes on to its own end.
    [Fact]
    public void ADetourLegArriving_LeavesTheJourneyStanding()
    {
        Harness h = NewHarness(TeleportRooms, Trail, carriesEverything: true);
        h.Walker.WalkTo(Arch, preferTeleportFree: false);

        h.Walker.WalkTo(Trail, supersedeSilently: true);          // already there: finishes at once

        Assert.Contains(h.Events, e => e.Kind == WalkEventKind.Finished);
        Assert.False(h.Walker.JourneyPreferTeleportFree);
    }
}
