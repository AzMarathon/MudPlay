using System.Collections.Generic;
using System.IO;
using System.Linq;
using MudPlay.Game.Map;
using MudPlay.Game.Spells;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// RoomSpellTeleportIndex over a small game-data set on disk: it classes the spells
// rooms carry (and only those), holds a spell the data can't answer for as unknown
// and says so in the log, and starts over on a set change.
public sealed class RoomSpellTeleportIndexTests : IDisposable
{
    private readonly string _root;

    public RoomSpellTeleportIndexTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-spellteleport-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    private static string RoomRow(int number, int spell) =>
        $$"""
        { "Map Number": 1, "Room Number": {{number}}, "Name": "Room {{number}}", "Light": 0, "Shop": 0,
          "Spell": {{spell}}, "Lair": "", "Delay": 5, "N": "0", "S": "0", "E": "0", "W": "0",
          "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        """;

    // Spells 1257 (own teleport), 1145 (teleport behind item checks, block in
    // MinBase), 526 (damage) and 943 (names the absent block 4043), as in the Stock
    // data; 9999 is on no room.
    private const string Spells = """
        [
          { "Number": 1257, "Name": "gloomy teleport", "MinBase": 633, "MaxBase": 656, "Abil-0": 140, "AbilVal-0": 0, "Abil-1": 141, "AbilVal-1": 8 },
          { "Number": 1145, "Name": "ice cavern level 2", "MinBase": 9410, "MaxBase": 9410, "Abil-0": 148, "AbilVal-0": 0 },
          { "Number": 526, "Name": "magma heat", "MinBase": 30, "MaxBase": 60, "Abil-0": 1, "AbilVal-0": 0 },
          { "Number": 943, "Name": "sys j", "MinBase": 100, "MaxBase": 100, "Abil-0": 148, "AbilVal-0": 4042 },
          { "Number": 9999, "Name": "unplaced teleport", "Abil-0": 140, "AbilVal-0": 5 }
        ]
        """;

    private const string TBInfo = """
        [
          { "Number": 9410, "LinkTo": 0, "Action": "failitem 930:failitem 191:message 2979:teleport 297 10:message 2980" },
          { "Number": 4042, "LinkTo": 0, "Action": "checkspell 935 4043:addevil 0" }
        ]
        """;

    private void WriteSet(string set, params (int Room, int Spell)[] rooms)
    {
        string dir = Path.Combine(_root, set);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Rooms.json"), "[" + string.Join(",", rooms.Select(r => RoomRow(r.Room, r.Spell))) + "]");
        File.WriteAllText(Path.Combine(dir, "Spells.json"), Spells);
        File.WriteAllText(Path.Combine(dir, "TBInfo.json"), TBInfo);
    }

    [Fact]
    public void ClassesThePlacedSpells_LogsTheGap_AndStartsOverOnASetChange()
    {
        WriteSet("one", (1, 1257), (2, 1145), (3, 526), (4, 943), (5, 1145), (6, 0), (7, 777));
        WriteSet("two", (1, 526));

        GameDataCache cache = new(_root);
        TBInfoStore tbinfo = new(cache);
        RoomGraphManager graph = new(cache);
        LogService log = new();
        List<string> lines = new();
        log.EntryAdded += e => { if (e.Source == "RoomSpellTeleportIndex") lines.Add(e.Message); };
        RoomSpellTeleportIndex index = new(cache, graph, new KnownSpellCatalog(cache), tbinfo, log);
        int reloads = 0;
        index.StoreReloaded += () => reloads++;

        void Switch(string? set)
        {
            cache.SwitchSet(set);
            tbinfo.OnActiveSetChanged(set);
            graph.OnActiveSetChanged(set);
            index.OnActiveSetChanged(set);
        }

        Switch("one");
        IReadOnlyDictionary<int, RoomSpellTeleport> first = index.Classes;
        Assert.Equal(new[] { 526, 777, 943, 1145, 1257 }, first.Keys.Order());
        Assert.Equal(RoomSpellTeleport.Sudden, index.ClassOf(1257));
        Assert.Equal(RoomSpellTeleport.Conditional, index.ClassOf(1145));
        Assert.Equal(RoomSpellTeleport.None, index.ClassOf(526));
        // A missing textblock and a room naming a spell the set lacks: unknown, and
        // the same for a spell no room carries, which was never read.
        Assert.Equal(RoomSpellTeleport.Unknown, index.ClassOf(943));
        Assert.Equal(RoomSpellTeleport.Unknown, index.ClassOf(777));
        Assert.Equal(RoomSpellTeleport.Unknown, index.ClassOf(9999));
        Assert.Contains(lines, l => l.Contains("5 room spell(s)") && l.Contains("2 unknown"));
        string gaps = Assert.Single(lines, l => l.StartsWith("Room spell(s) not read in full"));
        Assert.Contains("777 (spell 777 missing; unknown)", gaps);
        Assert.Contains("943 (textblock 4043 missing; unknown)", gaps);

        lines.Clear();
        Switch("two");
        Assert.NotSame(first, index.Classes);
        Assert.Equal(new[] { 526 }, index.Classes.Keys);
        Assert.Equal(RoomSpellTeleport.Unknown, index.ClassOf(1257));
        Assert.DoesNotContain(lines, l => l.StartsWith("Room spell(s) not read in full"));

        Switch(null);
        Assert.Empty(index.Classes);
        Assert.Equal(3, reloads);
    }
}
