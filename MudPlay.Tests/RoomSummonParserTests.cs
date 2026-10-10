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

    // cairn wraith / ogre skeleton / zombie warrior exp; none is a one-at-a-time boss.
    private static (int, bool) Mon(int id) => id switch
    {
        2111 => (13000, false),
        2119 => (12000, false),
        2122 => (12000, false),
        _ => (0, false),
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
        // The weeping apparition, the vampire fledgling, and the Death Shrieker, which
        // the test leaves an ordinary monster so only the room item keeps it out.
        static (int, bool) Undead(int id) => id switch
        {
            806 => (4800, false),
            808 => (4700, false),
            809 => (150000, false),
            _ => (0, false),
        };
        var leftOut = new List<RoomSummonLeftOut>();

        RoomSummonTable? table = RoomSummonParser.Resolve(9471, n => Tb(d, n), Undead, leftOut);

        Assert.NotNull(table);
        Assert.DoesNotContain(table!.Entries, e => e.Monsters.Contains(809));
        Assert.Equal(0.02, table.SummonChance, 5);
        Assert.Equal(143.0, table.ExpPerRoll, 3);         // 0.01 × 4700 + 0.01 × 2 × 4800
        RoomSummonLeftOut line = Assert.Single(leftOut);
        Assert.Equal(97, line.Threshold);
        Assert.Equal(new[] { 809 }, line.Monsters);
        Assert.Equal(RoomSummonParser.NeedsRoomItem, line.Reason);
    }

    [Fact]
    public void Resolve_LineWithItsOwnNoMonsters_IsEmptyRoomOnly()
    {
        // The same graveyard table: the block leading to it has no `nomonsters`, the
        // two lines counted each have one, so the spell isn't gated and all it
        // summons is.
        var d = new Dictionary<int, string>
        {
            [9471] = "random 9515\n\n",
            [9515] = "96:addevil 0\n97:roomitem 1684:clearitem 1684:summon 809\n98:nomonsters:message 2956:summon 808\n"
                   + "99:nomonsters:message 2956:summon 806:summon 806\n100:random 9355\n\n",
        };
        static (int, bool) Undead(int id) => id == 808 ? (4700, false) : (4800, false);

        RoomSummonTable? table = RoomSummonParser.Resolve(9471, n => Tb(d, n), Undead);

        Assert.NotNull(table);
        Assert.False(table!.NoMonstersGate);
        Assert.Equal(table.ExpPerRoll, table.EmptyRoomExpPerRoll, 3);
        Assert.All(table.Entries, e => Assert.True(e.EmptyRoomOnly));
    }

    [Fact]
    public void Resolve_LineWithoutNoMonsters_RollsWhateverIsInTheRoom()
    {
        // "nether" (Paradigm spell 5449 → TBInfo 9844 → 9845), verbatim: neither the
        // block nor the two summon lines ask for an empty room.
        var d = new Dictionary<int, string>
        {
            [9844] = "random 9845\n\n",
            [9845] = "90:addevil 0\n92:message 8959\n94:message 8960\n95:message 8961:summon 1226\n96:message 8962:summon 1258\n"
                   + "97:message 8963\n99:message 8964\n100:message 8965\n\n",
        };
        static (int, bool) Nether(int id) => id == 1226 ? (69000, false) : (52000, false);

        RoomSummonTable? table = RoomSummonParser.Resolve(9844, n => Tb(d, n), Nether);

        Assert.NotNull(table);
        Assert.Equal(1210.0, table!.ExpPerRoll, 3);
        Assert.Equal(0.0, table.EmptyRoomExpPerRoll, 3);
        Assert.All(table.Entries, e => Assert.False(e.EmptyRoomOnly));
    }

    [Fact]
    public void Resolve_RoomItemOnTheBlockLeadingToTheTable_StillReads_AndEverySummonOfALineCounts()
    {
        // "bone dock" (spell 1152 → TBInfo 9655 → 9656), verbatim from
        // data-Paradigm-1.9.1: the portal the block asks for is in the spell's one
        // room, and three of the table's lines bring more than one bone warrior.
        var d = new Dictionary<int, string>
        {
            [9655] = "roomitem 1807:random 9656\n\n",
            [9656] = "10:summon 925\n20:summon 926:summon 927\n30:summon 926:summon 927:summon 2395\n"
                   + "40:summon 925:summon 926:summon 2395:summon 2395\n100:addevil 0\n\n",
        };
        static (int, bool) Bone(int id) => (id == 2395 ? 6000 : 5000, false);

        RoomSummonTable? table = RoomSummonParser.Resolve(9655, n => Tb(d, n), Bone);

        Assert.NotNull(table);
        Assert.Equal(0.40, table!.SummonChance, 5);
        // 0.1 × (5000 + 10000 + 16000 + 22000)
        Assert.Equal(5300.0, table.ExpPerRoll, 3);
        Assert.Equal(new[] { 925, 926, 2395, 2395 }, table.Entries[3].Monsters);
    }

    [Fact]
    public void Resolve_LineSummoningAOneAtATimeBoss_IsNotCredited()
    {
        // "farnholme portal" (Paradigm spell 5676 → TBInfo 5636 → 5637), verbatim:
        // brute zombies (2739) on four lines, and Lord Skorne (2738, GameLimit 1,
        // 2,000,000 exp) on the last fifth of the rolls.
        var d = new Dictionary<int, string>
        {
            [5636] = "roomitem 3775:nomonsters:random 5637\n\n",
            [5637] = "50:addevil 0\n60:summon 2739\n67:summon 2739:summon 2739\n74:summon 2739:summon 2739:summon 2739\n"
                   + "80:summon 2739:summon 2739:summon 2739:summon 2739\n100:message 9788:summon 2738\n\n",
        };
        static (int, bool) Portal(int id) => id == 2738 ? (2000000, true) : (16250, false);
        var leftOut = new List<RoomSummonLeftOut>();

        RoomSummonTable? table = RoomSummonParser.Resolve(5636, n => Tb(d, n), Portal, leftOut);

        Assert.NotNull(table);
        Assert.DoesNotContain(table!.Entries, e => e.Monsters.Contains(2738));
        Assert.Equal(0.30, table.SummonChance, 5);
        // 16250 × (0.10 × 1 + 0.07 × 2 + 0.07 × 3 + 0.06 × 4)
        Assert.Equal(11212.5, table.ExpPerRoll, 3);
        Assert.True(table.NoMonstersGate);
        RoomSummonLeftOut line = Assert.Single(leftOut);
        Assert.Equal(100, line.Threshold);
        Assert.Equal(RoomSummonParser.OneAtATimeBoss, line.Reason);
    }

    [Fact]
    public void Resolve_TableWhoseOnlySummonIsABoss_SummonsNothing()
    {
        // "fortress trigger" (spell 1267), its table 4174 verbatim: the Angelic
        // Hunter (1012, GameLimit 1) is all it summons.
        var d = new Dictionary<int, string>
        {
            [4174] = "80:addevil 0\n90:nomonsters:summon 1012\n100:nomonsters:random 4320\n\n",
        };
        var leftOut = new List<RoomSummonLeftOut>();

        Assert.Null(RoomSummonParser.Resolve(4174, n => Tb(d, n), _ => (2000000, true), leftOut));
        Assert.Equal(new[] { 1012 }, Assert.Single(leftOut).Monsters);
    }

    [Fact]
    public void Resolve_ZeroOrMissingTextBlock_IsNull()
    {
        Assert.Null(RoomSummonParser.Resolve(0, n => Tb(CryptSummon, n), Mon));
        Assert.Null(RoomSummonParser.Resolve(99999, n => Tb(CryptSummon, n), Mon));
    }
}
