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
            "Targets": 8, "Casted By": "Monster #108, Textblock #2500(8%)" }
        ]
        """;

    // The monsters those lists name, with the slots that carry these spells; the
    // storm giant king's spell slot can land (Att% 100) and his death spell is #5522.
    // The "sleeper" is not from the set: a spell slot that can never land.
    internal const string Monsters = """
        [
          { "Number": 108,  "Name": "ice sorceress", "MidSpell-0": 1363 },
          { "Number": 297,  "Name": "ogre-mage", "MidSpell-0": 1363 },
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

    internal static readonly Room[] Rooms =
    {
        RoomAt(6, 1139, "Underwater Passage", 511),
        RoomAt(6, 1209, "Ice Jewel Cave", 512),
        RoomAt(6, 1138, "Icy Lake", 0),
        RoomAt(12, 72, "Desert", 515),
        RoomAt(16, 498, "Training Grounds", 829),
        RoomAt(16, 519, "Training Grounds", 829),
        RoomAt(14, 491, "Chaos Storm", 212), RoomAt(14, 492, "Chaos Storm", 212),
        RoomAt(14, 493, "Chaos Storm", 212), RoomAt(14, 494, "Chaos Storm", 212),
        RoomAt(14, 495, "Chaos Storm", 212), RoomAt(14, 496, "Chaos Storm", 212),
        RoomAt(14, 497, "Chaos Storm", 212),
    };

    private static Room RoomAt(int map, int room, string name, int spell) => new()
    {
        Key = new RoomKey(map, room), Name = name, Spell = spell,
        Exits = new Dictionary<Direction, RoomExit>(),
    };

    internal static (GameDataCache Cache, SpellCasterIndex Index) NewIndex(
        string root, string spells = Spells, string monsters = Monsters, string set = "alpha")
    {
        string dir = Path.Combine(root, set);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Spells.json"), spells);
        File.WriteAllText(Path.Combine(dir, "Monsters.json"), monsters);
        GameDataCache cache = new(root);
        cache.SwitchSet(set);
        SpellCasterIndex index = new(cache, () => Rooms);
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

    // Glacial mist is learnable and aimed at foes (Targets 12): its casters are its
    // monsters and the players who learned it.
    [Fact]
    public void ASpellAPlayerCanLearn_AimedAtFoes_IsCastByMonstersAndPlayers()
    {
        SpellCasterIndex.Casters mist = Of(1042);

        Assert.Contains("Lorzina", mist.Monsters);
        Assert.False(mist.OnlyMonstersAndRooms);
        Assert.True(mist.PlayersAtFoes);
    }

    [Theory]
    [InlineData(9107)]   // a monster casts it too
    [InlineData(9108)]   // nothing else does
    public void ALearnableSpellAimedAtFoes_IsPlayersAtFoes(int spell)
    {
        SpellCasterIndex.Casters casters = Of(spell);

        Assert.True(casters.PlayersAtFoes);
        Assert.False(casters.OnlyMonstersAndRooms);
    }

    // A buff a player can learn (9105 has no Targets aimed at foes) can be cast on
    // the character by anyone, the character included; one a textblock casts as well
    // has a caster not in view.
    [Theory]
    [InlineData(9105)]
    [InlineData(9109)]
    public void ALearnableSpell_NotAimedAtFoesOrWithACasterNotInView_IsOpen(int spell)
    {
        SpellCasterIndex.Casters casters = Of(spell);

        Assert.False(casters.PlayersAtFoes);
        Assert.False(casters.OnlyMonstersAndRooms);
    }

    // Each row here has a monster for a caster and one thing more, and is kept open
    // by that one thing alone: #9199, the same row without it, is closed. The rows
    // numbered 91xx are made for this and are not the set's (its own rows with these
    // sources mostly carry two of them at once); #179 and #5522 are.
    [Theory]
    [InlineData(179)]    // an item casts it too (the heavy black rod, beside Jareth)
    [InlineData(9101)]   // a textblock casts it too
    [InlineData(9102)]   // its list was cut off at the column's 250 characters
    [InlineData(9103)]   // a room is listed whose own spell it is not: an exit's or a command's cast
    [InlineData(9104)]   // another spell casts it otherwise than by ending in it
    [InlineData(9105)]   // a player can learn it, and it isn't aimed at foes
    [InlineData(9106)]   // an item teaches it, and it isn't aimed at foes
    [InlineData(9100)]   // it lists a monster the set has no row for
    [InlineData(5522)]   // a death spell: its caster is gone from the roster when it lands
    public void AMonstersSpell_WithOneCasterNotInView_IsNeverClosed(int spell)
    {
        (_, SpellCasterIndex index) = NewIndex(_root);

        Assert.True(index.Of(9199)!.OnlyMonstersAndRooms);
        Assert.NotEmpty(index.Of(spell)!.Monsters);
        Assert.False(index.Of(spell)!.OnlyMonstersAndRooms);
    }

    // The set's own rows: an item alone, a textblock and a foreign room together, a
    // list cut off, nothing listed, a spell tied to another that doesn't end in it.
    [Theory]
    [InlineData(156)]
    [InlineData(512)]
    [InlineData(511)]
    [InlineData(5257)]
    [InlineData(5536)]
    public void ASpellWithNoMonsterOrRoomOfItsOwnToGoBy_IsNeverClosed(int spell)
        => Assert.False(Of(spell).OnlyMonstersAndRooms);

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
