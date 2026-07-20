using System.IO;
using System.Linq;
using FujinTerm.Game.Map;
using FujinTerm.Game.Spells;
using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

public sealed class BoatPassageResolverTests : IDisposable
{
    private readonly string _root;

    public BoatPassageResolverTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fujinterm-boat-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    private (TBInfoStore Store, KnownSpellCatalog Catalog) NewSet(string tbinfoJson, string spellsJson)
    {
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "TBInfo.json"), tbinfoJson);
        File.WriteAllText(Path.Combine(_root, "alpha", "Spells.json"), spellsJson);
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        TBInfoStore store = new(cache);
        store.OnActiveSetChanged("alpha");
        return (store, new KnownSpellCatalog(cache));
    }

    // Structurally identical to the verified Paradigm-1.9.1 dock 14/759:
    //   dock CMD #100 → two `secure passage` lines → random tables #200 / #210 →
    //   `<roll>:cast <board 500>:cast <trip>` → the trip spell's EndCast (151)
    //   chain → a fixed-teleport `disembark` spell = the arrival room.
    // Board spell 500 is a RANDOM teleport (Abil 140 val 0 over 715..723): it must
    // never be mistaken for the arrival — only the trip chain's fixed disembark is.
    private const string DockTbInfo = """
        [
          { "Number": 100, "LinkTo": 0,
            "Action": "secure passage to albion:minlevel 50 3887:price 2000000 3890:random 200:text 4989\nsecure passage to talkiran:checkability 211 3:minlevel 65 3887:price 6000000 3890:random 210:text 4989\n",
            "Called From": "Room 14/759" },
          { "Number": 200, "LinkTo": 0,
            "Action": "99:cast 500:cast 501\n100:cast 500:cast 501\n",
            "Called From": "Textblock(rndm) #100" },
          { "Number": 210, "LinkTo": 0,
            "Action": "100:cast 500:cast 510\n",
            "Called From": "Textblock(rndm) #100" }
        ]
        """;
    private const string DockSpells = """
        [
          { "Number": 500, "Name": "board ship",  "MinBase": 715, "MaxBase": 723,
            "Abil-0": 140, "AbilVal-0": 0, "Abil-1": 141, "AbilVal-1": 14 },
          { "Number": 501, "Name": "albion trip 1", "MinBase": 501, "MaxBase": 501,
            "Abil-0": 29, "AbilVal-0": 1, "Abil-1": 151, "AbilVal-1": 502 },
          { "Number": 502, "Name": "albion trip 2", "MinBase": 502, "MaxBase": 502,
            "Abil-0": 29, "AbilVal-0": 1, "Abil-1": 151, "AbilVal-1": 503 },
          { "Number": 503, "Name": "disembark to kingsport", "MinBase": 0, "MaxBase": 0,
            "Abil-0": 140, "AbilVal-0": 702, "Abil-1": 141, "AbilVal-1": 14, "Abil-2": 151, "AbilVal-2": 504 },
          { "Number": 504, "Name": "kingsport landing", "MinBase": 0, "MaxBase": 0,
            "Abil-0": 18, "AbilVal-0": 0 },
          { "Number": 510, "Name": "talkiran trip 1", "MinBase": 510, "MaxBase": 510,
            "Abil-0": 29, "AbilVal-0": 1, "Abil-1": 151, "AbilVal-1": 511 },
          { "Number": 511, "Name": "disembark to talkiran", "MinBase": 0, "MaxBase": 0,
            "Abil-0": 140, "AbilVal-0": 15000, "Abil-1": 141, "AbilVal-1": 14 }
        ]
        """;

    private BoatPassage[] Resolve(string tb = DockTbInfo, string spells = DockSpells, int cmd = 100)
    {
        (TBInfoStore store, KnownSpellCatalog catalog) = NewSet(tb, spells);
        return BoatPassageResolver
            .EnumerateBoatPassages(store, new RoomKey(14, 759), cmd, catalog)
            .ToArray();
    }

    [Fact]
    public void AlbionPassage_ResolvesFullChainToArrivalRoom()
    {
        BoatPassage p = Resolve().Single(x => x.Place == "albion");

        Assert.Equal("secure passage to albion", p.Keyword);
        Assert.Equal(new RoomKey(14, 759), p.DockRoom);
        Assert.Equal(new RoomKey(14, 702), p.ArrivalRoom);   // disembark 503, not a ship room
        Assert.Equal(50, p.MinLevel);
        Assert.Equal(2_000_000, p.FareCopper);
        Assert.False(p.RequiresCheckability);
    }

    [Fact]
    public void TalkiranPassage_FlagsCheckabilityAndResolvesArrival()
    {
        BoatPassage p = Resolve().Single(x => x.Place == "talkiran");

        Assert.Equal("secure passage to talkiran", p.Keyword);
        Assert.Equal(new RoomKey(14, 15000), p.ArrivalRoom);
        Assert.Equal(65, p.MinLevel);
        Assert.Equal(6_000_000, p.FareCopper);
        Assert.True(p.RequiresCheckability);
    }

    [Fact]
    public void Enumerate_ReturnsEveryPassageLine()
    {
        Assert.Equal(2, Resolve().Length);
    }

    [Fact]
    public void BoardSpell_RandomTeleport_NotReturnedAsArrival()
    {
        // The arrival is the fixed disembark (14/702), never a ship room in the
        // board spell's 715..723 random pool.
        BoatPassage p = Resolve().Single(x => x.Place == "albion");
        Assert.NotInRange(p.ArrivalRoom.Room, 715, 723);
    }

    [Fact]
    public void TransitChain_SumsSpellDur_AsVoyageRounds()
    {
        // Board (random, resolves nothing) then two trip legs (Dur 20 + 10) that
        // EndCast to an instantaneous disembark (Dur 0). The voyage length is the
        // summed transit-spell duration in spell rounds (30), which the walker
        // later turns into a wall-clock wait (rounds*3 + buffer).
        const string tb = """
            [
              { "Number": 100, "LinkTo": 0,
                "Action": "secure passage to isle:minlevel 1 0:price 1 0:random 200:text 0\n",
                "Called From": "Room 14/759" },
              { "Number": 200, "LinkTo": 0, "Action": "100:cast 500:cast 600\n", "Called From": "rndm" }
            ]
            """;
        const string spells = """
            [
              { "Number": 500, "Name": "board", "MinBase": 715, "MaxBase": 723,
                "Abil-0": 140, "AbilVal-0": 0, "Abil-1": 141, "AbilVal-1": 14 },
              { "Number": 600, "Name": "trip 1", "Dur": 20, "Abil-0": 151, "AbilVal-0": 601 },
              { "Number": 601, "Name": "trip 2", "Dur": 10, "Abil-0": 151, "AbilVal-0": 602 },
              { "Number": 602, "Name": "disembark", "Dur": 0, "Abil-0": 140, "AbilVal-0": 42 }
            ]
            """;
        BoatPassage p = Assert.Single(Resolve(tb, spells));
        Assert.Equal(new RoomKey(14, 42), p.ArrivalRoom);
        Assert.Equal(30, p.VoyageRounds);
    }

    [Fact]
    public void DisembarkWithoutTeleportMap_FallsBackToDockMap()
    {
        // Trip chain whose disembark carries no Abil 141 — arrival map falls back
        // to the dock room's map (14).
        const string tb = """
            [
              { "Number": 100, "LinkTo": 0,
                "Action": "secure passage to cove:minlevel 1 0:price 500 0:random 200:text 0\n",
                "Called From": "Room 14/759" },
              { "Number": 200, "LinkTo": 0, "Action": "100:cast 600\n", "Called From": "rndm" }
            ]
            """;
        const string spells = """
            [ { "Number": 600, "Name": "hop", "MinBase": 0, "MaxBase": 0,
                "Abil-0": 140, "AbilVal-0": 88 } ]
            """;
        BoatPassage p = Assert.Single(Resolve(tb, spells));
        Assert.Equal(new RoomKey(14, 88), p.ArrivalRoom);
        Assert.Equal(500, p.FareCopper);
    }

    [Fact]
    public void UnresolvableTransitChain_SkipsPassage()
    {
        // Trip spell chains to a spell that isn't in the catalog — no arrival can
        // be landed, so the passage is not offered.
        const string tb = """
            [
              { "Number": 100, "LinkTo": 0,
                "Action": "secure passage to nowhere:minlevel 1 0:price 1 0:random 200:text 0\n",
                "Called From": "Room 14/759" },
              { "Number": 200, "LinkTo": 0, "Action": "100:cast 500:cast 700\n", "Called From": "rndm" }
            ]
            """;
        const string spells = """
            [
              { "Number": 500, "Name": "board", "MinBase": 715, "MaxBase": 723,
                "Abil-0": 140, "AbilVal-0": 0, "Abil-1": 141, "AbilVal-1": 14 },
              { "Number": 700, "Name": "broken trip", "MinBase": 0, "MaxBase": 0,
                "Abil-0": 29, "AbilVal-0": 1, "Abil-1": 151, "AbilVal-1": 99999 }
            ]
            """;
        Assert.Empty(Resolve(tb, spells));
    }

    [Fact]
    public void NonPassageLines_Ignored()
    {
        const string tb = """
            [ { "Number": 100, "LinkTo": 0,
                "Action": "ring chime:message 1:teleport 65 1\n", "Called From": "Room 1/1" } ]
            """;
        Assert.Empty(Resolve(tb, "[]"));
    }

    [Fact]
    public void ZeroOrMissingCmd_YieldsNothing()
    {
        Assert.Empty(Resolve(cmd: 0));
        Assert.Empty(Resolve(cmd: 99999));   // no such entry
    }

    [Fact]
    public void CyclicTransitChain_DoesNotHang_SkipsPassage()
    {
        // A self-referential EndCast loop with no fixed teleport must terminate
        // via the visited-guard and drop the passage.
        const string tb = """
            [
              { "Number": 100, "LinkTo": 0,
                "Action": "secure passage to loop:minlevel 1 0:price 1 0:random 200:text 0\n",
                "Called From": "Room 14/759" },
              { "Number": 200, "LinkTo": 0, "Action": "100:cast 800\n", "Called From": "rndm" }
            ]
            """;
        const string spells = """
            [
              { "Number": 800, "Name": "loop a", "MinBase": 0, "MaxBase": 0,
                "Abil-0": 151, "AbilVal-0": 801 },
              { "Number": 801, "Name": "loop b", "MinBase": 0, "MaxBase": 0,
                "Abil-0": 151, "AbilVal-0": 800 }
            ]
            """;
        Assert.Empty(Resolve(tb, spells));
    }
}
