using System.Collections.Generic;
using System.IO;
using System.Linq;
using MudPlay.Game.Map;
using MudPlay.Game.Simulation;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// The summon a room's entry spell hands the Exp/Hr estimate and the loop simulator,
// read from the spell's textblock wherever the spell keeps that block's number, with
// the lines the estimate leaves out: one that needs an item in the room, and one that
// summons a boss only one of which can be alive. Spells, textblocks and monsters are
// the imported rows (Stock 1.11p; "crypt summon 2" and "farnholme portal" are
// Paradigm's), cut to the fields read; "blank" is made up, and "fortress trigger"'s
// block 4173 is cut to two of its twenty lines.
public sealed class RouteExpResolverSummonTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "mudplay-summon-" + Path.GetRandomFileName());

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }

    private static readonly RoomKey Graveyard = new(17, 241), Crypt = new(17, 242), Blank = new(17, 243),
        Portal = new(17, 244), Fortress = new(17, 245);

    //  17/241 graveyard ─N─ 17/242 crypt summon 2 ─N─ 17/243 blank ─N─ 17/244 farnholme
    //  portal ─N─ 17/245 fortress trigger
    private const string Rooms = """
        [
          { "Map Number": 17, "Room Number": 241, "Name": "Graveyard", "Spell": 1126, "N": "17/242" },
          { "Map Number": 17, "Room Number": 242, "Name": "Crypt", "Spell": 5248, "S": "17/241", "N": "17/243" },
          { "Map Number": 17, "Room Number": 243, "Name": "Blank", "Spell": 700, "S": "17/242", "N": "17/244" },
          { "Map Number": 17, "Room Number": 244, "Name": "Portal", "Spell": 5676, "S": "17/243", "N": "17/245" },
          { "Map Number": 17, "Room Number": 245, "Name": "Fortress", "Spell": 1267, "S": "17/244" }
        ]
        """;

    // 1126 and 5676 keep their block in MinBase / MaxBase with 0 in the slot; 5248 and
    // 1267 keep it in the slot; 700 names none at all.
    private const string Spells = """
        [
          { "Number": 1126, "Name": "graveyard", "MinBase": 9471, "MaxBase": 9471, "Diff": 100, "Targets": 1, "AttType": 4,
            "Abil-0": 148, "AbilVal-0": 0, "Abil-1": 115, "AbilVal-1": 66, "Abil-2": 0, "AbilVal-2": 0, "Abil-3": 0, "AbilVal-3": 9999 },
          { "Number": 5248, "Name": "crypt summon 2", "MinBase": 0, "MaxBase": 0, "Abil-0": 148, "AbilVal-0": 3411 },
          { "Number": 700, "Name": "blank", "MinBase": 0, "MaxBase": 0, "Abil-0": 148, "AbilVal-0": 0 },
          { "Number": 5676, "Name": "farnholme portal", "MinBase": 5636, "MaxBase": 5636, "Abil-0": 148, "AbilVal-0": 0 },
          { "Number": 1267, "Name": "fortress trigger", "MinBase": 0, "MaxBase": 0, "Abil-0": 148, "AbilVal-0": 4173 }
        ]
        """;

    private const string TBInfo = """
        [
          { "Number": 9471, "LinkTo": 0, "Action": "random 9515\n\n" },
          { "Number": 9515, "LinkTo": 0, "Action": "96:addevil 0\n97:roomitem 1684:clearitem 1684:summon 809\n98:nomonsters:message 2956:summon 808\n99:nomonsters:message 2956:summon 806:summon 806\n100:random 9355\n\n" },
          { "Number": 3411, "LinkTo": 0, "Action": "nomonsters:random 3412\n\n" },
          { "Number": 3412, "LinkTo": 0, "Action": "60:addevil 0\n85:message 4064\n90:message 4063:summon 2111\n95:message 4063:summon 2119\n100:message 4063:summon 2122" },
          { "Number": 5636, "LinkTo": 0, "Action": "roomitem 3775:nomonsters:random 5637\n\n" },
          { "Number": 5637, "LinkTo": 0, "Action": "50:addevil 0\n60:summon 2739\n67:summon 2739:summon 2739\n74:summon 2739:summon 2739:summon 2739\n80:summon 2739:summon 2739:summon 2739:summon 2739\n100:message 9788:summon 2738\n\n" },
          { "Number": 4173, "LinkTo": 0, "Action": "takeitem 1921:random 4174\nfailitem 185:random 4174\n\n" },
          { "Number": 4174, "LinkTo": 0, "Action": "80:addevil 0\n90:nomonsters:summon 1012\n100:nomonsters:random 4320\n\n" }
        ]
        """;

    // GameLimit 1 marks the three one-at-a-time monsters: the Death Shrieker (regen 0,
    // kept out by the room item its line asks for), Lord Skorne and the Angelic Hunter
    // (regen waits, kept out as bosses).
    private const string Monsters = """
        [
          { "Number": 806, "Name": "weeping apparition", "EXP": 4800, "ExpMulti": 1, "GameLimit": 0, "RegenTime": 0 },
          { "Number": 808, "Name": "vampire fledgling", "EXP": 4700, "ExpMulti": 1, "GameLimit": 0, "RegenTime": 0 },
          { "Number": 809, "Name": "Death Shrieker", "EXP": 15000, "ExpMulti": 10, "GameLimit": 1, "RegenTime": 0 },
          { "Number": 1012, "Name": "Angelic Hunter", "EXP": 50000, "ExpMulti": 40, "GameLimit": 1, "RegenTime": 1 },
          { "Number": 2111, "Name": "cairn wraith", "EXP": 13000, "ExpMulti": 1, "GameLimit": 0, "RegenTime": 0 },
          { "Number": 2119, "Name": "ogre skeleton", "EXP": 12000, "ExpMulti": 1, "GameLimit": 0, "RegenTime": 0 },
          { "Number": 2122, "Name": "zombie warrior", "EXP": 12000, "ExpMulti": 1, "GameLimit": 0, "RegenTime": 0 },
          { "Number": 2738, "Name": "Lord Skorne", "EXP": 2000000, "ExpMulti": 1, "GameLimit": 1, "RegenTime": 2 },
          { "Number": 2739, "Name": "brute zombie", "EXP": 16250, "ExpMulti": 1, "GameLimit": 0, "RegenTime": 0 }
        ]
        """;

    private static readonly LoopWaypoint[] Loop =
        { new(Graveyard), new(Fortress) };

    private (RouteExpResolver Resolver, LairTimerStore Timers, LogService Log) Build() =>
        Build(Rooms, Spells, TBInfo, Monsters);

    private (RouteExpResolver Resolver, LairTimerStore Timers, LogService Log) Build(
        string rooms, string spells, string tbInfo, string monsters)
    {
        string set = Path.Combine(_root, "alpha");
        Directory.CreateDirectory(set);
        File.WriteAllText(Path.Combine(set, "Rooms.json"), rooms);
        File.WriteAllText(Path.Combine(set, "Spells.json"), spells);
        File.WriteAllText(Path.Combine(set, "TBInfo.json"), tbInfo);
        File.WriteAllText(Path.Combine(set, "Monsters.json"), monsters);
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
            // A vampire fledgling on 1% of rolls and two weeping apparitions on
            // another 1%. The Death Shrieker's line asks for a statue only some
            // graveyard rooms hold.
            Assert.Equal(143.0, summon.Value.ExpPerRoll, 3);
            Assert.Equal(0.02, summon.Value.SummonChance, 5);
            // The `nomonsters` here sits on the lines, not on the block that leads to
            // the table: the spell isn't gated as a whole, and all it summons is.
            Assert.False(summon.Value.NoMonstersGated);
            Assert.Equal(143.0, summon.Value.EmptyRoomExpPerRoll, 3);
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
    public void OneAtATimeBoss_IsLeftOut_AndTheOrdinaryMonstersBesideItStillCount()
    {
        (RouteExpResolver resolver, LairTimerStore timers, _) = Build();
        using (timers)
        using (resolver)
        {
            ExpRoute route = resolver.Resolve(Loop);

            // The portal: brute zombies on 30% of rolls; Lord Skorne's 20% is gone.
            RoomSummon? portal = SummonAt(route, Portal);
            Assert.NotNull(portal);
            Assert.Equal(11212.5, portal!.Value.ExpPerRoll, 3);
            Assert.Equal(0.30, portal.Value.SummonChance, 5);
            // The fortress summons the Angelic Hunter and nothing else.
            Assert.Null(SummonAt(route, Fortress));
        }
    }

    [Fact]
    public void OneAtATimeMonsterWithNoRegenWait_IsCountedLikeAnyOther()
    {
        // "Dino trigger" (Paradigm spell 5081 → TBInfo 3354 → 3355) cut to its last two
        // lines: the young tyrannosaur (508) is GameLimit 1 with RegenTime 0, so it can
        // be summoned again as soon as it is dead and counts (user, 2026-10-10); the
        // zapsalis (511) beside it is ordinary. Lord Skorne, with a regen wait, stays out.
        const string rooms = """
            [
              { "Map Number": 18, "Room Number": 41, "Name": "Forest", "Spell": 5081, "N": "18/42" },
              { "Map Number": 18, "Room Number": 42, "Name": "Clearing", "S": "18/41" }
            ]
            """;
        const string spells = """
            [ { "Number": 5081, "Name": "Dino trigger", "MinBase": 3354, "MaxBase": 3354, "Abil-0": 148, "AbilVal-0": 3354 } ]
            """;
        const string tbInfo = """
            [
              { "Number": 3354, "LinkTo": 0, "Action": "roomitem 3390:nomonsters:random 3355\n" },
              { "Number": 3355, "LinkTo": 0, "Action": "50:addevil 0\n94:summon 508\n98:summon 511\n100:summon 2738\n" }
            ]
            """;
        const string monsters = """
            [
              { "Number": 508, "Name": "young tyrannosaur", "EXP": 131000, "ExpMulti": 1, "GameLimit": 1, "RegenTime": 0 },
              { "Number": 511, "Name": "zapsalis", "EXP": 68000, "ExpMulti": 1, "GameLimit": 0, "RegenTime": 0 },
              { "Number": 2738, "Name": "Lord Skorne", "EXP": 2000000, "ExpMulti": 1, "GameLimit": 1, "RegenTime": 2 }
            ]
            """;
        (RouteExpResolver resolver, LairTimerStore timers, _) = Build(rooms, spells, tbInfo, monsters);
        using (timers)
        using (resolver)
        {
            RoomSummon? summon = SummonAt(resolver.Resolve(new[] { new LoopWaypoint(new RoomKey(18, 41)), new LoopWaypoint(new RoomKey(18, 42)) }),
                new RoomKey(18, 41));

            Assert.NotNull(summon);
            // 0.44 × 131000 + 0.04 × 68000; the 2% of Lord Skorne's line is left out.
            Assert.Equal(60360.0, summon!.Value.ExpPerRoll, 3);
            Assert.Equal(0.48, summon.Value.SummonChance, 5);
        }
    }

    [Fact]
    public void SimulatorLapCarriesTheSameTables()
    {
        (RouteExpResolver resolver, LairTimerStore timers, _) = Build();
        using (timers)
        using (resolver)
        {
            IReadOnlyList<SimRoom> lap = resolver.ResolveSimLap(Loop);
            RoomSummonTable? TableAt(RoomKey room) => lap.First(r => r.Key == room).Summon;

            RoomSummonTable? graveyard = TableAt(Graveyard);
            Assert.NotNull(graveyard);
            Assert.Contains(graveyard!.Entries, e => e.Monsters.SequenceEqual(new[] { 808 }) && e.EmptyRoomOnly);
            Assert.Contains(graveyard.Entries, e => e.Monsters.SequenceEqual(new[] { 806, 806 }) && e.EmptyRoomOnly);
            Assert.Equal(3, TableAt(Crypt)!.Entries.Count);
            Assert.Null(TableAt(Blank));
            // No line the simulator can roll brings a one-at-a-time boss.
            Assert.DoesNotContain(graveyard.Entries, e => e.Monsters.Contains(809));
            Assert.DoesNotContain(TableAt(Portal)!.Entries, e => e.Monsters.Contains(2738));
            Assert.Null(TableAt(Fortress));
        }
    }

    [Fact]
    public void LogsOnceWhichPlacedRoomSpellsSummonAndWhatWasLeftOut()
    {
        (RouteExpResolver resolver, LairTimerStore timers, LogService log) = Build();
        using (timers)
        using (resolver)
        {
            var lines = new List<string>();
            log.EntryAdded += e => { if (e.Source == "RouteExpResolver") lines.Add(e.Message); };

            resolver.Resolve(Loop);
            resolver.ResolveSimLap(Loop);

            Assert.Equal(2, lines.Count);
            Assert.Contains("3 of 5 placed room spell(s) summon monsters, over 3 room(s)", lines[0]);
            Assert.Contains("1126 graveyard (textblock 9471, 1 room(s), 143 exp a roll, 2% summon, 143 of it in an empty room only)", lines[0]);
            Assert.Contains("5248 crypt summon 2 (textblock 3411, 1 room(s), 1850 exp a roll, 15% summon, empty room only)", lines[0]);
            Assert.Contains("1126 graveyard line 97: 809 Death Shrieker (needs an item in the room)", lines[1]);
            Assert.Contains("1267 fortress trigger line 90: 1012 Angelic Hunter (one-at-a-time boss)", lines[1]);
            Assert.Contains("5676 farnholme portal line 100: 2738 Lord Skorne (one-at-a-time boss)", lines[1]);
        }
    }
}
