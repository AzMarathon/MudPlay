using System.Collections.Generic;
using System.IO;
using System.Linq;
using MudPlay.Game.Map;
using MudPlay.Game.Simulation;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// The summon a room's entry spell hands the Exp/Hr estimate and the loop simulator,
// read from the spell's textblock wherever the spell keeps that block's number.
// Spells, textblocks and monsters are the imported Stock 1.11p rows ("crypt summon 2"
// is Paradigm's), cut to the fields read; "blank" is made up.
public sealed class RouteExpResolverSummonTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "mudplay-summon-" + Path.GetRandomFileName());

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }

    private static readonly RoomKey Graveyard = new(17, 241), Crypt = new(17, 242), Blank = new(17, 243);

    //  17/241 (graveyard) ─N─ 17/242 (crypt summon 2) ─N─ 17/243 (blank)
    private const string Rooms = """
        [
          { "Map Number": 17, "Room Number": 241, "Name": "Graveyard", "Spell": 1126, "N": "17/242" },
          { "Map Number": 17, "Room Number": 242, "Name": "Crypt", "Spell": 5248, "S": "17/241", "N": "17/243" },
          { "Map Number": 17, "Room Number": 243, "Name": "Blank", "Spell": 700, "S": "17/242" }
        ]
        """;

    // 1126 keeps its block in MinBase / MaxBase with 0 in the slot; 5248 keeps it in
    // the slot; 700 names none at all.
    private const string Spells = """
        [
          { "Number": 1126, "Name": "graveyard", "MinBase": 9471, "MaxBase": 9471, "Diff": 100, "Targets": 1, "AttType": 4,
            "Abil-0": 148, "AbilVal-0": 0, "Abil-1": 115, "AbilVal-1": 66, "Abil-2": 0, "AbilVal-2": 0, "Abil-3": 0, "AbilVal-3": 9999 },
          { "Number": 5248, "Name": "crypt summon 2", "MinBase": 0, "MaxBase": 0, "Abil-0": 148, "AbilVal-0": 3411 },
          { "Number": 700, "Name": "blank", "MinBase": 0, "MaxBase": 0, "Abil-0": 148, "AbilVal-0": 0 }
        ]
        """;

    private const string TBInfo = """
        [
          { "Number": 9471, "LinkTo": 0, "Action": "random 9515\n\n" },
          { "Number": 9515, "LinkTo": 0, "Action": "96:addevil 0\n97:roomitem 1684:clearitem 1684:summon 809\n98:nomonsters:message 2956:summon 808\n99:nomonsters:message 2956:summon 806:summon 806\n100:random 9355\n\n" },
          { "Number": 3411, "LinkTo": 0, "Action": "nomonsters:random 3412\n\n" },
          { "Number": 3412, "LinkTo": 0, "Action": "60:addevil 0\n85:message 4064\n90:message 4063:summon 2111\n95:message 4063:summon 2119\n100:message 4063:summon 2122" }
        ]
        """;

    private const string Monsters = """
        [
          { "Number": 806, "Name": "weeping apparition", "EXP": 4800, "ExpMulti": 1, "GameLimit": 0, "RegenTime": 0 },
          { "Number": 808, "Name": "vampire fledgling", "EXP": 4700, "ExpMulti": 1, "GameLimit": 0, "RegenTime": 0 },
          { "Number": 809, "Name": "Death Shrieker", "EXP": 15000, "ExpMulti": 10, "GameLimit": 1, "RegenTime": 0 },
          { "Number": 2111, "Name": "cairn wraith", "EXP": 13000, "ExpMulti": 1, "GameLimit": 0, "RegenTime": 0 },
          { "Number": 2119, "Name": "ogre skeleton", "EXP": 12000, "ExpMulti": 1, "GameLimit": 0, "RegenTime": 0 },
          { "Number": 2122, "Name": "zombie warrior", "EXP": 12000, "ExpMulti": 1, "GameLimit": 0, "RegenTime": 0 }
        ]
        """;

    private static readonly LoopWaypoint[] Loop =
        { new(Graveyard), new(Blank) };

    private (RouteExpResolver Resolver, LairTimerStore Timers, LogService Log) Build()
    {
        string set = Path.Combine(_root, "alpha");
        Directory.CreateDirectory(set);
        File.WriteAllText(Path.Combine(set, "Rooms.json"), Rooms);
        File.WriteAllText(Path.Combine(set, "Spells.json"), Spells);
        File.WriteAllText(Path.Combine(set, "TBInfo.json"), TBInfo);
        File.WriteAllText(Path.Combine(set, "Monsters.json"), Monsters);
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");
        var timers = new LairTimerStore(cache, graph, new RoomTracker(graph));
        var log = new LogService();
        return (new RouteExpResolver(graph, new BfsMapper(graph), timers, cache, log), timers, log);
    }

    private static RoomSummon? SummonAt(ExpRoute route, RoomKey room) => route.Lap.First(v => v.Room == room).Summon;

    [Fact]
    public void BlockNumberKeptInMinBase_IsRead()
    {
        (RouteExpResolver resolver, LairTimerStore timers, _) = Build();
        using (timers)
        using (resolver)
        {
            RoomSummon? summon = SummonAt(resolver.Resolve(Loop), Graveyard);

            Assert.NotNull(summon);
            Assert.Equal("graveyard", summon!.Value.SpellName);
            // The vampire fledgling and weeping apparition lines, 1% each. The Death
            // Shrieker's asks for a statue only some graveyard rooms hold.
            Assert.Equal(95.0, summon.Value.ExpPerRoll, 3);
            Assert.Equal(0.02, summon.Value.SummonChance, 5);
            // The `nomonsters` here sits on single lines of the table, not on the
            // block that leads to it, so the spell as a whole isn't empty-room only.
            Assert.False(summon.Value.NoMonstersGated);
        }
    }

    [Fact]
    public void BlockNumberKeptInTheAbility_IsReadAsBefore()
    {
        (RouteExpResolver resolver, LairTimerStore timers, _) = Build();
        using (timers)
        using (resolver)
        {
            RoomSummon? summon = SummonAt(resolver.Resolve(Loop), Crypt);

            Assert.NotNull(summon);
            Assert.Equal("crypt summon 2", summon!.Value.SpellName);
            Assert.Equal(1850.0, summon.Value.ExpPerRoll, 3);
            Assert.Equal(0.15, summon.Value.SummonChance, 5);
            Assert.True(summon.Value.NoMonstersGated);
        }
    }

    [Fact]
    public void TextBlockAbilityNamingNoBlock_SummonsNothing()
    {
        (RouteExpResolver resolver, LairTimerStore timers, _) = Build();
        using (timers)
        using (resolver)
            Assert.Null(SummonAt(resolver.Resolve(Loop), Blank));
    }

    [Fact]
    public void SimulatorLapCarriesTheSameTables()
    {
        (RouteExpResolver resolver, LairTimerStore timers, _) = Build();
        using (timers)
        using (resolver)
        {
            IReadOnlyList<SimRoom> lap = resolver.ResolveSimLap(Loop);

            RoomSummonTable? graveyard = lap.First(r => r.Key == Graveyard).Summon;
            Assert.NotNull(graveyard);
            Assert.Contains(graveyard!.Entries, e => e.Monster == 808);
            Assert.Contains(graveyard.Entries, e => e.Monster == 806);
            Assert.DoesNotContain(graveyard.Entries, e => e.Monster == 809);
            Assert.Equal(3, lap.First(r => r.Key == Crypt).Summon!.Entries.Count);
            Assert.Null(lap.First(r => r.Key == Blank).Summon);
        }
    }

    [Fact]
    public void LogsOnceWhichPlacedRoomSpellsSummon()
    {
        (RouteExpResolver resolver, LairTimerStore timers, LogService log) = Build();
        using (timers)
        using (resolver)
        {
            var lines = new List<string>();
            log.EntryAdded += e => { if (e.Source == "RouteExpResolver") lines.Add(e.Message); };

            resolver.Resolve(Loop);
            resolver.ResolveSimLap(Loop);

            string line = Assert.Single(lines);
            Assert.Contains("2 of 3 placed room spell(s) summon monsters, over 2 room(s)", line);
            Assert.Contains("1126 graveyard (textblock 9471, 1 room(s), 95 exp a roll, 2% summon)", line);
            Assert.Contains("5248 crypt summon 2 (textblock 3411, 1 room(s), 1850 exp a roll, 15% summon, empty room only)", line);
        }
    }
}
