using System.IO;
using MudPlay.Game.GameData;
using MudPlay.Game.Map;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// Who can cast a spell, read off rows copied from the Paradigm 1.9.1 set (the
// freezing family of report paradigm-20261011-165726, cut down to the columns the
// index reads): monsters from the Monsters table, rooms from their own Spell, an
// EndCast chain handing a spell its parent's casters, and everything the client
// can't see cast leaving the spell open.
public sealed class SpellCasterIndexTests : IDisposable
{
    private readonly string _root;

    public SpellCasterIndexTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-spell-casters-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    // An empty cell of these columns is a NUL, and a list cut at 250 characters ends
    // in "+" and one.
    internal const string Spells = """
        [
          { "Number": 1042, "Name": "glacial mist", "Learnable": 1, "Learned From": "NPC #244", "Targets": 12,
            "Casted By": "Monster #1032, Monster #2661" },
          { "Number": 1336, "Name": "galacial mist", "Learnable": 0, "Learned From": "\u0000",
            "Casted By": "Monster #1069" },
          { "Number": 135, "Name": "ice storm", "Learnable": 1, "Learned From": "Item #502", "Targets": 12,
            "Casted By": "Monster #833" },
          { "Number": 1363, "Name": "ice storm", "Learnable": 0, "Learned From": "\u0000",
            "Casted By": "Monster #108, Monster #297, Monster #311, Monster #2661" },
          { "Number": 5027, "Name": "ice storm", "Learnable": 0, "Learned From": "\u0000",
            "Casted By": "Monster #176, Monster #1069" },
          { "Number": 771, "Name": "blizzard", "Learnable": 1, "Learned From": "Item #1243", "Targets": 12,
            "Casted By": "Monster #2661", "Abil-1": 151, "AbilVal-1": 1015 },
          { "Number": 5019, "Name": "blizzard", "Learnable": 0, "Learned From": "\u0000",
            "Casted By": "Monster #637", "Abil-1": 151, "AbilVal-1": 1015 },
          { "Number": 1015, "Name": "freezing", "Learnable": 0, "Learned From": "\u0000",
            "Casted By": "Spell #771, Spell #5019" },
          { "Number": 5522, "Name": "storm king temp", "Learnable": 0, "Learned From": "\u0000",
            "Casted By": "Monster #637" },
          { "Number": 511, "Name": "freezing water", "Learnable": 0, "Learned From": "\u0000",
            "Casted By": "Room 6/1139, Room 6/1140, R+\u0000", "Abil-2": 151, "AbilVal-2": 512 },
          { "Number": 512, "Name": "holding breath", "Learnable": 0, "Learned From": "\u0000",
            "Casted By": "Room 6/1138, Room 6/1209, Spell #511, Spell #515, Textblock #2500, Textblock #2501",
            "Abil-1": 151, "AbilVal-1": 513 },
          { "Number": 513, "Name": "drowning", "Learnable": 0, "Learned From": "\u0000",
            "Casted By": "Spell #512, Spell #515", "Abil-1": 151, "AbilVal-1": 514 },
          { "Number": 515, "Name": "stop drowning", "Learnable": 0, "Learned From": "\u0000",
            "Casted By": "Room 12/72, R+\u0000",
            "Abil-0": 153, "AbilVal-0": 512, "Abil-1": 153, "AbilVal-1": 513 },
          { "Number": 5535, "Name": "lesser arcane enchantment", "Learnable": 0, "Learned From": "\u0000",
            "Casted By": "Monster #108", "Abil-0": 43, "AbilVal-0": 5536 },
          { "Number": 5536, "Name": "arcane hits", "Learnable": 0, "Learned From": "\u0000",
            "Casted By": "Spell #5535" },
          { "Number": 829, "Name": "confusion", "Learnable": 0, "Learned From": "\u0000",
            "Casted By": "Room 16/498, Room 16/519" },
          { "Number": 156, "Name": "jade amulet", "Learnable": 0, "Learned From": "\u0000",
            "Casted By": "Item #406" },
          { "Number": 5257, "Name": "ocean drowning", "Learnable": 0, "Learned From": "\u0000",
            "Casted By": "\u0000" },
          { "Number": 179, "Name": "heavy black rod", "Learnable": 0, "Learned From": "\u0000",
            "Casted By": "Monster #2756, Item #462" },
          { "Number": 212, "Name": "chaos storm", "Learnable": 0, "Learned From": "\u0000",
            "Casted By": "Room 14/491, Room 14/492, Room 14/493, Room 14/494, Room 14/495, Room 14/496, Room 14/497, Monster #122" },
          { "Number": 9100, "Name": "(not from the set)", "Learnable": 0, "Learned From": "\u0000",
            "Casted By": "Monster #108, Monster #7777" },
          { "Number": 9199, "Name": "(not from the set: the ice sorceress alone)", "Learnable": 0,
            "Learned From": "\u0000", "Casted By": "Monster #108" },
          { "Number": 9101, "Name": "(9199 and a textblock)", "Learnable": 0, "Learned From": "\u0000",
            "Casted By": "Monster #108, Textblock #2500(8%)" },
          { "Number": 9102, "Name": "(9199 with its list cut off)", "Learnable": 0, "Learned From": "\u0000",
            "Casted By": "Monster #108, Mons+\u0000" },
          { "Number": 9201, "Name": "(9199, an item casts it, its list cut off before the item)", "Learnable": 0,
            "Learned From": "\u0000", "Casted By": "Monster #108, Ite+\u0000" },
          { "Number": 9202, "Name": "(9199, hostile, an item teaches it, unlisted)", "Learnable": 0,
            "Learned From": "\u0000", "Casted By": "Monster #108", "Targets": 8 },
          { "Number": 9203, "Name": "(9199, a room command's textblock casts it, unlisted)", "Learnable": 0,
            "Learned From": "\u0000", "Casted By": "Monster #108" },
          { "Number": 9204, "Name": "(9199, hostile, a textblock teaches it, unlisted)", "Learnable": 0,
            "Learned From": "\u0000", "Casted By": "Monster #108", "Targets": 8 },
          { "Number": 9214, "Name": "(9199, hostile, learnable)", "Learnable": 1,
            "Learned From": "\u0000", "Casted By": "Monster #108", "Targets": 8 },
          { "Number": 9220, "Name": "(the ice sorceress's, an EndCast of 0 over 9221 to 9222)", "Learnable": 0,
            "Learned From": "\u0000", "Casted By": "Monster #108", "MinBase": 9221, "MaxBase": 9222,
            "Abil-0": 151, "AbilVal-0": 0 },
          { "Number": 9221, "Name": "(9199 inside 9220's range, its list cut off)", "Learnable": 0,
            "Learned From": "\u0000", "Casted By": "Monster #108, Spe+\u0000" },
          { "Number": 9205, "Name": "(9199, an exit casts it, unlisted)", "Learnable": 0,
            "Learned From": "\u0000", "Casted By": "Monster #108" },
          { "Number": 9206, "Name": "(9199, a spell trap casts it, unlisted)", "Learnable": 0,
            "Learned From": "\u0000", "Casted By": "Monster #108" },
          { "Number": 9207, "Name": "(9199, another spell's enchantment casts it, unlisted)", "Learnable": 0,
            "Learned From": "\u0000", "Casted By": "Monster #108" },
          { "Number": 9208, "Name": "(an enchantment)", "Learnable": 0, "Learned From": "\u0000",
            "Casted By": "\u0000", "Abil-0": 43, "AbilVal-0": 9207, "Abil-1": 160, "AbilVal-1": 9209 },
          { "Number": 9209, "Name": "(9199, another spell grants it, unlisted)", "Learnable": 0,
            "Learned From": "\u0000", "Casted By": "Monster #108" },
          { "Number": 9210, "Name": "(the ice sorceress's, lasting three rounds)", "Learnable": 0,
            "Learned From": "\u0000", "Casted By": "Monster #108", "Dur": 3, "Abil-0": 151, "AbilVal-0": 9211 },
          { "Number": 9211, "Name": "(cast when 9210 runs out)", "Learnable": 0, "Learned From": "\u0000",
            "Casted By": "Spell #9210" },
          { "Number": 9212, "Name": "(the ice sorceress's, gone at once)", "Learnable": 0,
            "Learned From": "\u0000", "Casted By": "Monster #108", "Dur": 0, "Abil-0": 151, "AbilVal-0": 9213 },
          { "Number": 9213, "Name": "(cast as 9212 lands)", "Learnable": 0, "Learned From": "\u0000",
            "Casted By": "Spell #9212" },
          { "Number": 9103, "Name": "(9199 and a room whose own spell it is not)", "Learnable": 0,
            "Learned From": "\u0000", "Casted By": "Monster #108, Room 6/1138" },
          { "Number": 9104, "Name": "(9199 and an enchantment's on-hit spell)", "Learnable": 0,
            "Learned From": "\u0000", "Casted By": "Monster #108, Spell #5535" },
          { "Number": 9105, "Name": "(9199, learnable)", "Learnable": 1, "Learned From": "\u0000",
            "Casted By": "Monster #108" },
          { "Number": 9106, "Name": "(9199, taught by an item)", "Learnable": 0, "Learned From": "Item #119",
            "Casted By": "Monster #108" },
          { "Number": 9107, "Name": "(9199, learnable, aimed at a foe)", "Learnable": 1, "Learned From": "\u0000",
            "Targets": 8, "Casted By": "Monster #108" },
          { "Number": 9108, "Name": "(learnable, aimed at a foe, no monster)", "Learnable": 1,
            "Learned From": "\u0000", "Targets": 12, "Casted By": "\u0000" },
          { "Number": 9109, "Name": "(9107 and a textblock)", "Learnable": 1, "Learned From": "\u0000",
            "Targets": 8, "Casted By": "Monster #108, Textblock #2500(8%)" },
          { "Number": 9110, "Name": "(a learnable curse, Monster scope)", "Learnable": 1, "Learned From": "\u0000",
            "Targets": 4, "Casted By": "Monster #108" },
          { "Number": 9111, "Name": "(a learnable party buff)", "Learnable": 1, "Learned From": "\u0000",
            "Targets": 13, "Casted By": "Monster #108" },
          { "Number": 9112, "Name": "(a learnable single-target buff)", "Learnable": 1, "Learned From": "\u0000",
            "Targets": 2, "Casted By": "Monster #108" }
        ]
        """;

    // The monsters those lists name, with the slots that carry these spells; the
    // storm giant king's spell slot can land (Att% 100) and his death spell is #5522.
    // The "sleeper" is not from the set: a spell slot that can never land, nor is the
    // ogre-mage's second slot, which carries the cut-off #9102.
    internal const string Monsters = """
        [
          { "Number": 108,  "Name": "ice sorceress", "MidSpell-0": 1363 },
          { "Number": 297,  "Name": "ogre-mage", "MidSpell-0": 1363, "MidSpell-1": 9102 },
          { "Number": 311,  "Name": "Goru-Nezar", "MidSpell-3": 1363 },
          { "Number": 176,  "Name": "dark wizard", "MidSpell-3": 5027 },
          { "Number": 833,  "Name": "vampire magus", "MidSpell-2": 135 },
          { "Number": 1069, "Name": "white queen", "MidSpell-0": 5027, "MidSpell-3": 1336 },
          { "Number": 1032, "Name": "Lorzina", "MidSpell-0": 1042 },
          { "Number": 2661, "Name": "Fallen King", "MidSpell-1": 1363, "MidSpell-3": 1042, "MidSpell-4": 771 },
          { "Number": 637,  "Name": "storm giant king", "DeathSpell": 5522,
            "AttType-4": 2, "AttAcc-4": 5019, "Att%-4": 100 },
          { "Number": 2756, "Name": "Jareth", "AttType-4": 2, "Att%-4": 100, "AttHitSpell-4": 179 },
          { "Number": 122,  "Name": "high druid", "AttType-4": 2, "AttAcc-4": 212, "Att%-4": 100 },
          { "Number": 9001, "Name": "sleeper", "AttType-0": 2, "AttAcc-0": 1336, "Att%-0": 0 }
        ]
        """;

    // Not from the set: an item that casts #9201 on a third of its swings, and one
    // that teaches #9202.
    internal const string Items = """
        [
          { "Number": 9301, "Name": "(swings 9201)", "Abil-0": 114, "AbilVal-0": 33, "Abil-1": 43, "AbilVal-1": 9201 },
          { "Number": 9302, "Name": "(teaches 9202)", "Abil-0": 42, "AbilVal-0": 9202 }
        ]
        """;

    // Not from the set: a room command that casts #9203, and a roll that teaches #9204.
    internal const string Textblocks = """
        [
          { "Number": 9401, "LinkTo": 0, "Action": "pull lever:message 1:cast 9203\n", "Called From": "Room 6/1138" },
          { "Number": 9402, "LinkTo": 0, "Action": "40:message 2\n100:learnspell 9204\n", "Called From": "Textblock #9401" }
        ]
        """;

    internal static readonly Room[] Rooms =
    {
        RoomAt(6, 1139, "Underwater Passage", 511),
        RoomAt(6, 1140, "Underwater Passage", 511),
        RoomAt(6, 1209, "Ice Jewel Cave", 512),
        // Not the set's exits: a post-cast of #9205 and a spell trap of #9206.
        RoomAt(6, 1138, "Icy Lake", 0, "6/1139 (Cast: pre-0, post-9205)", "6/1209 (Spell Trap: 9206)"),
        RoomAt(12, 72, "Desert", 515),
        RoomAt(16, 498, "Training Grounds", 829),
        RoomAt(16, 519, "Training Grounds", 829),
        RoomAt(14, 491, "Chaos Storm", 212), RoomAt(14, 492, "Chaos Storm", 212),
        RoomAt(14, 493, "Chaos Storm", 212), RoomAt(14, 494, "Chaos Storm", 212),
        RoomAt(14, 495, "Chaos Storm", 212), RoomAt(14, 496, "Chaos Storm", 212),
        RoomAt(14, 497, "Chaos Storm", 212),
    };

    // exits: MDB exit cells, given the directions east, west, north in that order.
    private static Room RoomAt(int map, int room, string name, int spell, params string[] exits)
    {
        Dictionary<Direction, RoomExit> byDirection = new();
        Direction[] directions = { Direction.E, Direction.W, Direction.N };
        for (int i = 0; i < exits.Length; i++)
            if (RoomExit.TryParseWire(exits[i], out RoomExit exit)) byDirection[directions[i]] = exit;
        return new Room { Key = new RoomKey(map, room), Name = name, Spell = spell, Exits = byDirection };
    }

    internal static (GameDataCache Cache, SpellCasterIndex Index) NewIndex(
        string root, string spells = Spells, string monsters = Monsters, string set = "alpha",
        string items = Items, string textblocks = Textblocks, IReadOnlyList<Room>? rooms = null,
        LogService? log = null)
    {
        string dir = Path.Combine(root, set);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Spells.json"), spells);
        File.WriteAllText(Path.Combine(dir, "Monsters.json"), monsters);
        File.WriteAllText(Path.Combine(dir, "Items.json"), items);
        File.WriteAllText(Path.Combine(dir, "TBInfo.json"), textblocks);
        GameDataCache cache = new(root);
        // As the app has it: the textblocks reload with the set, before the index
        // is built.
        TBInfoStore store = new(cache);
        cache.ActiveSetChanged += store.OnActiveSetChanged;
        cache.SwitchSet(set);
        IReadOnlyList<Room> map = rooms ?? Rooms;
        SpellCasterIndex index = new(cache, () => map, log, store);
        index.Warm();
        return (cache, index);
    }

    private SpellCasterIndex.Casters Of(int spell)
    {
        (_, SpellCasterIndex index) = NewIndex(_root);
        return index.Of(spell)!;
    }

    [Fact]
    public void ASpellOnlyMonstersCast_NamesThem_AndIsClosed()
    {
        SpellCasterIndex.Casters storm = Of(1363);

        Assert.Equal(
            new[] { "Fallen King", "Goru-Nezar", "ice sorceress", "ogre-mage" },
            storm.Monsters.Order(StringComparer.Ordinal));
        Assert.Empty(storm.RoomSpells);
        Assert.True(storm.OnlyMonstersAndRooms);
    }

    [Fact]
    public void ASlotThatCanNeverLand_IsNoCaster()
        => Assert.Equal(new[] { "white queen" }, Of(1336).Monsters);

    // Glacial mist is learnable and hostile (Targets 12): its casters are its
    // monsters and the players who learned it.
    [Fact]
    public void AHostileSpellAPlayerCanLearn_IsCastByMonstersAndPlayers()
    {
        SpellCasterIndex.Casters mist = Of(1042);

        Assert.Contains("Lorzina", mist.Monsters);
        Assert.False(mist.OnlyMonstersAndRooms);
        Assert.True(mist.PlayersAtFoes);
    }

    // Every enemy scope, the single-target ones included.
    [Theory]
    [InlineData(9107)]   // Targets 8, a monster casts it too
    [InlineData(9108)]   // Targets 12, nothing else does
    [InlineData(9110)]   // Targets 4, a curse
    public void ALearnableHostileSpell_IsPlayersAtFoes(int spell)
    {
        SpellCasterIndex.Casters casters = Of(spell);

        Assert.True(casters.PlayersAtFoes);
        Assert.False(casters.OnlyMonstersAndRooms);
    }

    // A heal or buff a player can learn can be cast on the character by anyone, the
    // character included (9105 self, no Targets; 9111 the party; 9112 one player);
    // a hostile one a textblock casts as well has a caster not in view (9109).
    [Theory]
    [InlineData(9105)]
    [InlineData(9111)]
    [InlineData(9112)]
    [InlineData(9109)]
    public void ALearnableSpell_NotHostileOrWithACasterNotInView_IsOpen(int spell)
    {
        SpellCasterIndex.Casters casters = Of(spell);

        Assert.False(casters.PlayersAtFoes);
        Assert.False(casters.OnlyMonstersAndRooms);
    }

    // Each row here has a monster for a caster and one thing more, and is kept open
    // by that one thing alone: #9199, the same row without it, is closed. The rows
    // numbered 91xx and 92xx are made for this and are not the set's (its own rows
    // with these sources mostly carry two of them at once); #179 and #5522 are.
    [Theory]
    [InlineData(179)]    // an item casts it too (the heavy black rod, beside Jareth)
    [InlineData(9101)]   // a textblock casts it too
    [InlineData(9103)]   // a room is listed whose own spell it is not: an exit's or a command's cast
    [InlineData(9104)]   // another spell casts it otherwise than by ending in it
    [InlineData(9105)]   // a player can learn it, and it isn't hostile
    [InlineData(9106)]   // an item teaches it, and it isn't hostile
    [InlineData(9201)]   // an item casts it, where the list was cut off
    [InlineData(9203)]   // a room command's textblock casts it, unlisted
    [InlineData(9205)]   // an exit casts it as it is walked, unlisted
    [InlineData(9206)]   // a spell trap casts it, unlisted
    [InlineData(9207)]   // another spell's enchantment casts it, unlisted
    [InlineData(9209)]   // another spell grants it to whoever it lands on, unlisted
    [InlineData(9211)]   // it lands when a spell with a duration runs out
    [InlineData(9100)]   // it lists a monster the set has no row for
    [InlineData(5522)]   // a death spell: its caster is gone from the roster when it lands
    public void AMonstersSpell_WithOneCasterNotInView_IsNeverClosed(int spell)
    {
        (_, SpellCasterIndex index) = NewIndex(_root);

        Assert.True(index.Of(9199)!.OnlyMonstersAndRooms);
        Assert.NotEmpty(index.Of(spell)!.Monsters);
        Assert.False(index.Of(spell)!.OnlyMonstersAndRooms);
    }

    // The set's own rows: an item alone, a textblock and a foreign room together,
    // nothing listed, a spell tied to another that doesn't end in it.
    [Theory]
    [InlineData(156)]
    [InlineData(512)]
    [InlineData(5257)]
    [InlineData(5536)]
    public void ASpellWithNoMonsterOrRoomOfItsOwnToGoBy_IsNeverClosed(int spell)
        => Assert.False(Of(spell).OnlyMonstersAndRooms);

    // A list cut off at the column's 250 characters says nothing of what it lost: the
    // tables stand for it. #9102's second caster is the ogre-mage, by the Monsters
    // table; freezing water's list ends "R+", and its rooms are on the map.
    [Fact]
    public void AListCutOff_WithItsCastersInTheirOwnTables_IsClosed()
    {
        (_, SpellCasterIndex index) = NewIndex(_root);

        SpellCasterIndex.Casters cut = index.Of(9102)!;
        Assert.Equal(new[] { "ice sorceress", "ogre-mage" }, cut.Monsters.Order(StringComparer.Ordinal));
        Assert.True(cut.OnlyMonstersAndRooms);
        Assert.True(index.Of(511)!.OnlyMonstersAndRooms);
    }

    // Save one tie only the list shows: #9221 lies in the range of #9220, whose
    // EndCast is 0, and its list may have lost a "Spell #9220".
    [Fact]
    public void AListCutOff_InsideTheRangeOfAnEndCastOfNone_StaysOpen()
    {
        (_, SpellCasterIndex index) = NewIndex(_root);

        Assert.True(index.Of(9220)!.OnlyMonstersAndRooms);
        Assert.False(index.Of(9221)!.OnlyMonstersAndRooms);
    }

    // An item's LearnSp slot and a textblock's `learnspell` make a player the
    // caster, as "Learnable" does: #9202 and #9204, hostile, read as #9214 does (a
    // player at a foe), not as an item's or a textblock's cast, which would leave
    // them open. The log counts them apart from the casts: the
    // fixture's items cast #9201 alone and its textblocks #9203 alone.
    [Fact]
    public void ASpellTaughtByAnItemOrATextblock_IsLearnable()
    {
        LogService log = new();
        List<string> lines = new();
        log.EntryAdded += entry => lines.Add(entry.Message);
        (_, SpellCasterIndex index) = NewIndex(_root, log: log);
        SpellCasterIndex.Casters learnable = index.Of(9214)!;

        string built = Assert.Single(lines, line => line.StartsWith("Spell casters", StringComparison.Ordinal));
        Assert.Contains("2 taught by items or textblocks, read as learnable. Never ruled out: 1 cast by items, 1 by textblocks",
            built, StringComparison.Ordinal);

        foreach (int taught in new[] { 9202, 9204 })
        {
            SpellCasterIndex.Casters casters = index.Of(taught)!;
            Assert.Equal(learnable.Monsters, casters.Monsters);
            Assert.Equal(learnable.RoomSpells, casters.RoomSpells);
            Assert.Equal(learnable.OnlyMonstersAndRooms, casters.OnlyMonstersAndRooms);
            Assert.Equal(learnable.PlayersAtFoes, casters.PlayersAtFoes);
            Assert.False(casters.OnlyMonstersAndRooms);
            Assert.True(casters.PlayersAtFoes);
        }
    }

    // An EndCast from a spell with no duration lands as that spell does, its caster
    // still there; from one with a duration, when it runs out.
    [Fact]
    public void AnEndCastOfASpellGoneAtOnce_KeepsItsCasters()
    {
        (_, SpellCasterIndex index) = NewIndex(_root);

        Assert.Equal(new[] { "ice sorceress" }, index.Of(9213)!.Monsters);
        Assert.True(index.Of(9213)!.OnlyMonstersAndRooms);
        Assert.True(index.Of(9210)!.OnlyMonstersAndRooms);
        Assert.False(index.Of(9211)!.OnlyMonstersAndRooms);
    }

    // Stock 1.11p's blazing comet (#420): "Casted By" names only the diamond guardian,
    // whose on-hit spell it is, but the starhammer casts it on a third of its swings.
    private const string StockBlazingComet = """
        [ { "Number": 420, "Name": "blazing comet", "Dur": 0, "AttType": 4, "Abil-0": 17, "Learnable": 0,
            "Learned From": "\u0000", "Casted By": "Monster #537" } ]
        """;

    private const string StockDiamondGuardian = """
        [ { "Number": 537, "Name": "diamond guardian", "DeathSpell": 0, "CreateSpell": 0,
            "AttType-0": 1, "AttAcc-0": 200, "Att%-0": 90, "AttHitSpell-0": 420,
            "AttType-1": 2, "AttAcc-1": 430, "Att%-1": 100 } ]
        """;

    private const string StockStarhammer = """
        [ { "Number": 1245, "Name": "starhammer", "Abil-0": 116, "AbilVal-0": -35, "Abil-1": 142, "AbilVal-1": 5,
            "Abil-2": 135, "AbilVal-2": 50, "Abil-3": 114, "AbilVal-3": 33, "Abil-4": 43, "AbilVal-4": 420 } ]
        """;

    [Fact]
    public void StockBlazingComet_TheStarhammerCastsIt()
    {
        (_, SpellCasterIndex index) = NewIndex(_root, StockBlazingComet, StockDiamondGuardian,
            items: StockStarhammer, textblocks: "[]", rooms: Array.Empty<Room>());

        Assert.Equal(new[] { "diamond guardian" }, index.Of(420)!.Monsters);
        Assert.False(index.Of(420)!.OnlyMonstersAndRooms);
    }

    [Fact]
    public void StockBlazingComet_WithoutTheStarhammer_IsTheGuardiansAlone()
    {
        (_, SpellCasterIndex index) = NewIndex(_root, StockBlazingComet, StockDiamondGuardian,
            items: "[]", textblocks: "[]", rooms: Array.Empty<Room>());

        Assert.True(index.Of(420)!.OnlyMonstersAndRooms);
    }

    // Paradigm 1.9.1's clean room (#5841) is the spell of The Clean Room, and its
    // east exit casts it too, before the move; "Casted By" names the room once.
    private const string ParadigmCleanRoom = """
        [ { "Number": 5841, "Name": "clean room", "Dur": 0, "AttType": 4,
            "Abil-0": 148, "AbilVal-0": 10229, "Abil-1": 144, "Abil-2": 115, "AbilVal-2": 66,
            "Abil-3": 153, "AbilVal-3": 5838, "Abil-4": 153, "AbilVal-4": 5842,
            "Abil-5": 122, "AbilVal-5": 5838, "Abil-6": 122, "AbilVal-6": 5842,
            "Learnable": 0, "Learned From": "\u0000", "Casted By": "Room 17/10691" } ]
        """;

    [Theory]
    [InlineData("17/10689 (Cast: pre-5841, post-0)", false)]
    [InlineData("17/10689", true)]
    public void ParadigmCleanRoom_IsOpenForItsExitsCast(string eastExit, bool closed)
    {
        Room[] rooms = { RoomAt(17, 10691, "The Clean Room", 5841, eastExit) };
        (_, SpellCasterIndex index) = NewIndex(_root, ParadigmCleanRoom, "[]",
            items: "[]", textblocks: "[]", rooms: rooms);

        Assert.Equal(new[] { 5841 }, index.Of(5841)!.RoomSpells);
        Assert.Equal(closed, index.Of(5841)!.OnlyMonstersAndRooms);
    }

    // Chaos storm is the spell of seven rooms and of the high druid.
    [Fact]
    public void ASpellOfRoomsAndAMonster_IsClosedOnBoth()
    {
        SpellCasterIndex.Casters storm = Of(212);

        Assert.Equal(new[] { "high druid" }, storm.Monsters);
        Assert.Equal(new[] { 212 }, storm.RoomSpells);
        Assert.True(storm.OnlyMonstersAndRooms);
    }

    [Fact]
    public void ASpellARoomCarries_IsClosedOnTheRoom()
    {
        SpellCasterIndex.Casters confusion = Of(829);

        Assert.Equal(new[] { 829 }, confusion.RoomSpells);
        Assert.Empty(confusion.Monsters);
        Assert.True(confusion.OnlyMonstersAndRooms);
    }

    // Blizzard ends in freezing (ability 151): freezing is cast by whoever casts a
    // blizzard, players among them, since one of the two blizzards can be learned.
    [Fact]
    public void ASpellReachedThroughAnother_HasThatSpellsCasters()
    {
        SpellCasterIndex.Casters freezing = Of(1015);

        Assert.Contains("storm giant king", freezing.Monsters);
        Assert.False(freezing.OnlyMonstersAndRooms);
        Assert.True(freezing.PlayersAtFoes);
    }

    // Freezing water, on the Underwater Passage rooms, ends in holding breath, which
    // ends in drowning: two steps back from drowning is the room's spell.
    [Fact]
    public void AChainFromARoomsSpell_PutsTheRoomSpellBehindEveryStep()
    {
        (_, SpellCasterIndex index) = NewIndex(_root);

        Assert.Contains(511, index.Of(512)!.RoomSpells);
        Assert.Contains(511, index.Of(513)!.RoomSpells);
        Assert.Contains(512, index.Of(513)!.RoomSpells);
        Assert.DoesNotContain(513, index.Of(511)!.RoomSpells);
    }

    // "Casted By" also names stop drowning for holding breath and drowning, which it
    // takes off (ability 153). It is on the desert's rooms, and casts neither there.
    [Fact]
    public void ASpellThatOnlyTakesAnotherOff_IsNotItsCaster()
    {
        (_, SpellCasterIndex index) = NewIndex(_root);

        Assert.Contains(515, index.Of(515)!.RoomSpells);
        Assert.DoesNotContain(515, index.Of(512)!.RoomSpells);
        Assert.DoesNotContain(515, index.Of(513)!.RoomSpells);
    }

    [Fact]
    public void KnowsMonster_ByNameWhateverTheCase()
    {
        (_, SpellCasterIndex index) = NewIndex(_root);

        Assert.True(index.KnowsMonster("Ice Sorceress"));
        Assert.False(index.KnowsMonster("snow leopard"));
    }

    [Fact]
    public void BeforeItIsBuilt_NothingIsKnown()
    {
        string dir = Path.Combine(_root, "alpha");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Spells.json"), Spells);
        File.WriteAllText(Path.Combine(dir, "Monsters.json"), Monsters);
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        SpellCasterIndex index = new(cache, () => Rooms);

        Assert.Null(index.Of(1363));
        Assert.False(index.KnowsMonster("ice sorceress"));
    }

    // A set imported again over itself keeps its name. What was built from the old
    // tables answers nothing until the index is built again, and then answers from
    // the new ones.
    [Fact]
    public void ASetImportedAgain_IsFollowed()
    {
        (GameDataCache cache, SpellCasterIndex index) = NewIndex(_root);
        Assert.True(index.Of(1363)!.OnlyMonstersAndRooms);

        File.WriteAllText(Path.Combine(_root, "alpha", "Spells.json"),
            """ [ { "Number": 1363, "Name": "ice storm", "Learnable": 1, "Casted By": "Monster #108" } ] """);
        cache.ReloadActiveSet();
        Assert.Null(index.Of(1363));

        index.Warm();
        Assert.False(index.Of(1363)!.OnlyMonstersAndRooms);
        Assert.Null(index.Of(1336));
    }

    // The items and textblocks are read again with the rest.
    [Fact]
    public void ASetImportedAgain_ItsItemsAndTextblocksAreFollowed()
    {
        (GameDataCache cache, SpellCasterIndex index) = NewIndex(_root);
        Assert.True(index.Of(1363)!.OnlyMonstersAndRooms);
        Assert.True(index.Of(9199)!.OnlyMonstersAndRooms);

        File.WriteAllText(Path.Combine(_root, "alpha", "TBInfo.json"),
            """ [ { "Number": 9403, "LinkTo": 0, "Action": "cast 1363\n" } ] """);
        File.WriteAllText(Path.Combine(_root, "alpha", "Items.json"),
            """ [ { "Number": 9303, "Name": "(uses 9199)", "Abil-0": 43, "AbilVal-0": 9199 } ] """);
        cache.ReloadActiveSet();
        index.Warm();

        Assert.False(index.Of(1363)!.OnlyMonstersAndRooms);
        Assert.False(index.Of(9199)!.OnlyMonstersAndRooms);
        Assert.True(index.Of(9203)!.OnlyMonstersAndRooms);
    }

    // The cache's load moves before the textblocks reload. A build in between would
    // go by the last set's, so none is made until they are this set's.
    [Fact]
    public void BeforeTheTextblocksAreThisSets_NothingIsBuilt()
    {
        string dir = Path.Combine(_root, "alpha");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Spells.json"), Spells);
        File.WriteAllText(Path.Combine(dir, "Monsters.json"), Monsters);
        File.WriteAllText(Path.Combine(dir, "TBInfo.json"), Textblocks);
        GameDataCache cache = new(_root);
        TBInfoStore store = new(cache);
        cache.SwitchSet("alpha");
        SpellCasterIndex index = new(cache, () => Rooms, textblocks: store);

        index.Warm();
        Assert.Null(index.Of(9203));

        store.OnActiveSetChanged("alpha");
        index.Warm();
        Assert.False(index.Of(9203)!.OnlyMonstersAndRooms);
    }

    [Fact]
    public void AnotherSetSwitchedTo_IsFollowed()
    {
        (GameDataCache cache, SpellCasterIndex index) = NewIndex(_root);
        string dir = Path.Combine(_root, "beta");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Spells.json"), "[]");
        File.WriteAllText(Path.Combine(dir, "Monsters.json"), "[]");

        cache.SwitchSet("beta");
        Assert.Null(index.Of(1363));

        index.Warm();
        Assert.Null(index.Of(1363));
        Assert.False(index.KnowsMonster("ice sorceress"));
    }
}
