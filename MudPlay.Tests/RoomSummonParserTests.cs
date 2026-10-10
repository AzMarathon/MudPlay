using System.Collections.Generic;
using MudPlay.Game.Map;
using Xunit;

namespace MudPlay.Tests;

// Pins RoomSummonParser — the TBInfo d100-roll-table read behind the estimator's
// room-spell summon credit. The canonical case is Paradigm "crypt summon 2"
// (spell 5248 → TBInfo 3411 → 3412), reproduced from the live game data.
public sealed class RoomSummonParserTests
{
    // TBInfo 3411/3412 verbatim from data-Paradigm-1.9.1.
    private static readonly Dictionary<int, string> CryptSummon = new()
    {
        [3411] = "nomonsters:random 3412\n\n",
        [3412] = "60:addevil 0\n85:message 4064\n90:message 4063:summon 2111\n"
               + "95:message 4063:summon 2119\n100:message 4063:summon 2122",
    };

    // cairn wraith / ogre skeleton / zombie warrior exp.
    private static (int, string) Mon(int id) => id switch
    {
        2111 => (13000, "cairn wraith"),
        2119 => (12000, "ogre skeleton"),
        2122 => (12000, "zombie warrior"),
        _ => (0, ""),
    };

    private static string? Tb(IReadOnlyDictionary<int, string> d, int n) => d.TryGetValue(n, out string? a) ? a : null;

    [Fact]
    public void Resolve_CryptSummon_ComputesExpectedExpAndChance()
    {
        RoomSummonTable? table = RoomSummonParser.Resolve(3411, n => Tb(CryptSummon, n), Mon);

        Assert.NotNull(table);
        // 15% summon chance (three 5% bands), the rest nothing/message.
        Assert.Equal(0.15, table!.SummonChance, 5);
        // 0.05×13000 + 0.05×12000 + 0.05×12000 = 1850.
        Assert.Equal(1850.0, table.ExpPerRoll, 3);
        Assert.True(table.NoMonstersGate);
        Assert.Equal(3, table.Entries.Count);
    }

    [Fact]
    public void Resolve_FollowsDirectTable_NoRedirect()
    {
        // A TextBlock that IS the roll table (no nomonsters/random indirection).
        var d = new Dictionary<int, string> { [900] = "50:message 1\n100:summon 2111" };
        RoomSummonTable? table = RoomSummonParser.Resolve(900, n => Tb(d, n), Mon);

        Assert.NotNull(table);
        Assert.Equal(0.50, table!.SummonChance, 5);       // 51–100 band
        Assert.Equal(6500.0, table.ExpPerRoll, 3);        // 0.5 × 13000
        Assert.False(table.NoMonstersGate);
    }

    [Fact]
    public void Resolve_NonSummonTextBlock_IsNull()
    {
        // A message-only TextBlock summons nothing → not a summon room-spell.
        var d = new Dictionary<int, string> { [10] = "50:message 4064\n100:message 4065" };
        Assert.Null(RoomSummonParser.Resolve(10, n => Tb(d, n), Mon));
    }

    [Fact]
    public void Resolve_NonHundredDenominator_UsesTopThreshold()
    {
        // Bands topping out at 50 → each 25-wide band is half the rolls.
        var d = new Dictionary<int, string> { [1] = "25:addevil 0\n50:summon 2119" };
        RoomSummonTable? table = RoomSummonParser.Resolve(1, n => Tb(d, n), Mon);

        Assert.NotNull(table);
        Assert.Equal(0.50, table!.SummonChance, 5);       // (50−25)/50
        Assert.Equal(6000.0, table.ExpPerRoll, 3);        // 0.5 × 12000
    }

    [Fact]
    public void Resolve_LineAskingAboutARoomItem_IsNotCredited()
    {
        // "graveyard" (spell 1126 → TBInfo 9471 → 9515), verbatim from data-v1.11p. The
        // Death Shrieker line needs the weeping statue (item 1684) in the room, and 7
        // of the spell's 110 rooms hold one.
        var d = new Dictionary<int, string>
        {
            [9471] = "random 9515\n\n",
            [9515] = "96:addevil 0\n97:roomitem 1684:clearitem 1684:summon 809\n98:nomonsters:message 2956:summon 808\n"
                   + "99:nomonsters:message 2956:summon 806:summon 806\n100:random 9355\n\n",
        };
        static (int, string) Undead(int id) => id switch
        {
            806 => (4800, "weeping apparition"),
            808 => (4700, "vampire fledgling"),
            809 => (150000, "Death Shrieker"),
            _ => (0, ""),
        };

        RoomSummonTable? table = RoomSummonParser.Resolve(9471, n => Tb(d, n), Undead);

        Assert.NotNull(table);
        Assert.DoesNotContain(table!.Entries, e => e.Monster == 809);
        Assert.Equal(0.02, table.SummonChance, 5);
        Assert.Equal(95.0, table.ExpPerRoll, 3);          // 0.01 × 4700 + 0.01 × 4800
    }

    [Fact]
    public void Resolve_RoomItemOnTheBlockLeadingToTheTable_StillReads()
    {
        // "bone dock" (spell 1152 → TBInfo 9655 → 9656), verbatim from
        // data-Paradigm-1.9.1: the portal the block asks for is in the spell's one room.
        var d = new Dictionary<int, string>
        {
            [9655] = "roomitem 1807:random 9656\n\n",
            [9656] = "10:summon 925\n20:summon 926:summon 927\n30:summon 926:summon 927:summon 2395\n"
                   + "40:summon 925:summon 926:summon 2395:summon 2395\n100:addevil 0\n\n",
        };
        static (int, string) Bone(int id) => id is 925 or 926 ? (5000, "bone warrior") : (0, "");

        RoomSummonTable? table = RoomSummonParser.Resolve(9655, n => Tb(d, n), Bone);

        Assert.NotNull(table);
        Assert.Equal(0.40, table!.SummonChance, 5);
        Assert.Equal(2000.0, table.ExpPerRoll, 3);        // 4 × 0.1 × 5000, a line's first summon
    }

    [Fact]
    public void Resolve_ZeroOrMissingTextBlock_IsNull()
    {
        Assert.Null(RoomSummonParser.Resolve(0, n => Tb(CryptSummon, n), Mon));
        Assert.Null(RoomSummonParser.Resolve(99999, n => Tb(CryptSummon, n), Mon));
    }
}
