using MudPlay.Game.Map;
using Xunit;

namespace MudPlay.Tests;

// The graph's compact exit set must read exactly as the dictionary of whole exits
// it replaced: the same exits, in Direction order, through every member of the
// read-only dictionary the rest of the client reads a room's exits by.
public sealed class RoomExitSetTests
{
    private static RoomExit Bare(int room) => new(new RoomKey(1, room), RoomExitHint.None, null);

    private static readonly RoomExit Door = new(new RoomKey(1, 20), RoomExitHint.Door, "Door [50 picklocks/strength]", StatRequirement: 50);
    private static readonly RoomExit Text = new(new RoomKey(2, 30), RoomExitHint.Text, "Text: go path, enter path", TextCommands: new[] { "go path", "enter path" });

    private static RoomExitSet Build(params (Direction Direction, RoomExit Exit)[] exits)
    {
        RoomExitSet.Builder builder = new();
        foreach ((Direction direction, RoomExit exit) in exits) builder.Set(direction, exit);
        return builder.Build();
    }

    private static void AssertReadsAs(IReadOnlyDictionary<Direction, RoomExit> set, params (Direction Direction, RoomExit Exit)[] expected)
    {
        Assert.Equal(expected.Length, set.Count);
        Assert.Equal(expected.Select(e => new KeyValuePair<Direction, RoomExit>(e.Direction, e.Exit)), set);
        Assert.Equal(expected.Select(e => e.Direction), set.Keys);
        Assert.Equal(expected.Select(e => e.Exit), set.Values);
        foreach (Direction direction in Enum.GetValues<Direction>())
        {
            int at = Array.FindIndex(expected, e => e.Direction == direction);
            Assert.Equal(at >= 0, set.ContainsKey(direction));
            Assert.Equal(at >= 0, set.TryGetValue(direction, out RoomExit exit));
            Assert.Equal(at >= 0 ? expected[at].Exit : default, exit);
            if (at >= 0) Assert.Equal(expected[at].Exit, set[direction]);
            else Assert.Throws<KeyNotFoundException>(() => set[direction]);
        }
    }

    [Fact]
    public void Exits_come_back_in_direction_order_however_they_were_given()
    {
        RoomExitSet set = Build((Direction.Teleport, Text), (Direction.D, Bare(4)), (Direction.N, Bare(2)), (Direction.E, Door));

        AssertReadsAs(set, (Direction.N, Bare(2)), (Direction.E, Door), (Direction.D, Bare(4)), (Direction.Teleport, Text));
    }

    [Fact]
    public void No_exits_is_the_one_empty_set()
    {
        RoomExitSet set = Build();

        Assert.Same(RoomExitSet.Empty, set);
        AssertReadsAs(set);
    }

    [Fact]
    public void An_exit_of_only_a_target_and_one_with_more_read_back_whole()
    {
        RoomExitSet set = Build((Direction.S, Bare(7)), (Direction.W, Door), (Direction.U, Text), (Direction.D, Bare(9)));

        AssertReadsAs(set, (Direction.S, Bare(7)), (Direction.W, Door), (Direction.U, Text), (Direction.D, Bare(9)));
        Assert.Same(Text.TextCommands, set[Direction.U].TextCommands);
        Assert.Equal(new[] { new RoomKey(1, 7), new RoomKey(1, 20), new RoomKey(2, 30), new RoomKey(1, 9) }, set.Targets.ToArray());
    }

    // Each of these differs from a bare exit in one field only. The set keeps an
    // exit as its target alone only when nothing else about it is set.
    private static readonly RoomExit[] s_oneFieldSet =
    {
        Bare(5) with { Hint = RoomExitHint.SearchableHidden },
        Bare(5) with { RawHint = "" },
        Bare(5) with { RawHint = "Hidden/Passable" },
        Bare(5) with { StatRequirement = 1 },
        Bare(5) with { CanBash = false },
        Bare(5) with { KeyItemId = 1 },
        Bare(5) with { TollGold = 1 },
        Bare(5) with { TextCommands = Array.Empty<string>() },
        Bare(5) with { MultiAction = new MultiActionExitData(0, false, Array.Empty<ExitAction>()) },
        Bare(5) with { TrapDamage = 1 },
        Bare(5) with { MinLevel = 1 },
        Bare(5) with { MaxLevel = 1 },
        Bare(5) with { ClassGate = 1 },
        Bare(5) with { PreCastSpell = 1 },
        Bare(5) with { PostCastSpell = 1 },
        Bare(5) with { CastTeleportRandom = true },
        Bare(5) with { CastPocketEntrance = true },
        Bare(5) with { GatewayTeleport = true },
        Bare(5) with { MovesWholeParty = true },
        Bare(5) with { CastTeleportTargets = Array.Empty<RoomKey>() },
        Bare(5) with { AlignmentGate = (0, 0) },
        Bare(5) with { FareCopper = 1 },
        Bare(5) with { RaceGate = 1 },
        Bare(5) with { CastLandings = Array.Empty<(int ItemId, RoomKey Room)>() },
    };

    [Fact]
    public void An_exit_with_any_one_field_set_keeps_it()
    {
        foreach (RoomExit exit in s_oneFieldSet)
        {
            Assert.NotEqual(Bare(5), exit);

            RoomExitSet set = Build((Direction.N, Bare(1)), (Direction.NE, exit), (Direction.U, Bare(3)));

            AssertReadsAs(set, (Direction.N, Bare(1)), (Direction.NE, exit), (Direction.U, Bare(3)));
        }
    }

    // A field added to RoomExit later needs a case above, so the set is shown to
    // keep it: Target is what a bare exit holds, and RawHint has two cases.
    [Fact]
    public void Every_field_of_an_exit_has_a_one_field_case()
    {
        int fields = typeof(RoomExit).GetConstructors().Max(c => c.GetParameters().Length);

        Assert.Equal(fields, s_oneFieldSet.Length);
    }

    [Fact]
    public void With_replaces_an_exit_in_place_and_adds_a_new_one_in_its_order()
    {
        RoomExitSet set = Build((Direction.N, Bare(2)), (Direction.E, Door), (Direction.D, Bare(4)));

        RoomExit gated = Bare(4) with { MinLevel = 1, MaxLevel = 3 };
        RoomExitSet replaced = set.With(Direction.D, gated);
        RoomExitSet opened = set.With(Direction.E, Bare(20));
        RoomExitSet added = set.With(Direction.Teleport, Text);
        RoomExitSet inserted = set.With(Direction.S, Door);

        AssertReadsAs(replaced, (Direction.N, Bare(2)), (Direction.E, Door), (Direction.D, gated));
        AssertReadsAs(opened, (Direction.N, Bare(2)), (Direction.E, Bare(20)), (Direction.D, Bare(4)));
        AssertReadsAs(added, (Direction.N, Bare(2)), (Direction.E, Door), (Direction.D, Bare(4)), (Direction.Teleport, Text));
        AssertReadsAs(inserted, (Direction.N, Bare(2)), (Direction.S, Door), (Direction.E, Door), (Direction.D, Bare(4)));
        // The set it was made from is as it was.
        AssertReadsAs(set, (Direction.N, Bare(2)), (Direction.E, Door), (Direction.D, Bare(4)));
    }

    [Fact]
    public void From_takes_any_exits_and_hands_a_set_back_as_itself()
    {
        Dictionary<Direction, RoomExit> dictionary = new()
        {
            [Direction.W] = Door,
            [Direction.N] = Bare(2),
        };

        RoomExitSet set = RoomExitSet.From(dictionary);

        AssertReadsAs(set, (Direction.N, Bare(2)), (Direction.W, Door));
        Assert.Same(set, RoomExitSet.From(set));
    }

    [Fact]
    public void A_builder_starts_over_after_each_set()
    {
        RoomExitSet.Builder builder = new();
        builder.Set(Direction.N, Door);
        builder.Set(Direction.S, Bare(2));
        builder.Build();

        builder.Set(Direction.S, Bare(3));

        AssertReadsAs(builder.Build(), (Direction.S, Bare(3)));
        Assert.Same(RoomExitSet.Empty, builder.Build());
    }

    [Fact]
    public void A_value_that_is_not_a_direction_is_not_found_and_cannot_be_set()
    {
        RoomExitSet set = Build((Direction.N, Bare(2)), (Direction.Teleport, Text));

        foreach (Direction stray in new[] { (Direction)(-1), (Direction)11, (Direction)16, (Direction)32, (Direction)99 })
        {
            Assert.False(set.ContainsKey(stray));
            Assert.False(set.TryGetValue(stray, out RoomExit exit));
            Assert.Equal(default, exit);
            Assert.Throws<KeyNotFoundException>(() => set[stray]);
            Assert.Throws<ArgumentOutOfRangeException>(() => new RoomExitSet.Builder().Set(stray, Bare(1)));
        }
    }

    [Fact]
    public void A_walk_can_be_read_again_from_the_start()
    {
        IReadOnlyDictionary<Direction, RoomExit> set = Build((Direction.N, Bare(2)), (Direction.E, Door));

        IEnumerable<RoomExit> values = set.Values;
        using IEnumerator<KeyValuePair<Direction, RoomExit>> pairs = set.GetEnumerator();
        Assert.True(pairs.MoveNext());
        Assert.True(pairs.MoveNext());
        Assert.False(pairs.MoveNext());
        pairs.Reset();
        Assert.True(pairs.MoveNext());

        Assert.Equal(new KeyValuePair<Direction, RoomExit>(Direction.N, Bare(2)), pairs.Current);
        Assert.Equal(new[] { Bare(2), Door }, values);
        Assert.Equal(new[] { Bare(2), Door }, values);
    }
}
