using System.IO;
using System.Text;
using MudPlay.Game.Map;
using MudPlay.Game.Recovery;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// Pins the order the Stock engine spills a dead character's items into other rooms
// (GAME_MECHANICS "Deathpile — where the items go"), on small hand-built graphs read
// through the real room graph so each exit is what the imported data makes of it.
public sealed class DeathSpillOrderTests : IDisposable
{
    private static readonly string[] s_columns = { "N", "S", "E", "W", "NE", "NW", "SE", "SW", "U", "D" };

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "mudplay-spillorder-" + Path.GetRandomFileName());

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    // One Rooms row: "map/room" and its exit cells as "N=1/2 (Door)" pairs.
    private static string Row(string key, params string[] exits)
    {
        string[] parts = key.Split('/');
        Dictionary<string, string> cells = exits
            .Select(e => e.Split('=', 2))
            .ToDictionary(p => p[0], p => p[1]);
        StringBuilder sb = new();
        sb.Append($"{{ \"Map Number\": {parts[0]}, \"Room Number\": {parts[1]}, \"Name\": \"Room {parts[1]}\", ");
        sb.Append("\"Light\": 0, \"Shop\": 0, \"Lair\": \"\", \"Delay\": 0");
        foreach (string col in s_columns)
            sb.Append($", \"{col}\": \"{(cells.TryGetValue(col, out string? cell) ? cell : "0")}\"");
        sb.Append(" }");
        return sb.ToString();
    }

    private IReadOnlyList<RoomKey> Candidates(params string[] rows)
    {
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), "[" + string.Join(",\n", rows) + "]");
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");
        return DeathSpillOrder.Candidates(new RoomKey(1, 1), graph.GetRoom);
    }

    private static RoomKey[] Rooms(params int[] rooms) => rooms.Select(r => new RoomKey(1, r)).ToArray();

    [Fact]
    public void Exits_AreTriedInTheEnginesOrder()
    {
        // Room numbers run against the order, so only the exit order can produce it.
        IReadOnlyList<RoomKey> order = Candidates(
            Row("1/1", "D=1/2", "U=1/3", "SW=1/4", "SE=1/5", "NW=1/6", "NE=1/7", "W=1/8", "E=1/9", "S=1/10", "N=1/11"),
            Row("1/2"), Row("1/3"), Row("1/4"), Row("1/5"), Row("1/6"),
            Row("1/7"), Row("1/8"), Row("1/9"), Row("1/10"), Row("1/11"));

        // N, S, E, W, NE, NW, SE, SW, U, D
        Assert.Equal(Rooms(11, 10, 9, 8, 7, 6, 5, 4, 3, 2), order);
    }

    [Fact]
    public void GoesOnFromTheRoomJustTried_BeforeComingBackForTheNextExit()
    {
        //   4 ─E─ 5
        //   │N
        //   2
        //   │N
        //   1 ─S─ 3
        IReadOnlyList<RoomKey> order = Candidates(
            Row("1/1", "N=1/2", "S=1/3"),
            Row("1/2", "N=1/4"),
            Row("1/3"),
            Row("1/4", "E=1/5"),
            Row("1/5"));

        // Depth first: all the way out through north before the south exit is tried.
        Assert.Equal(Rooms(2, 4, 5, 3), order);
    }

    [Fact]
    public void MapChangeAndRemoteActionExits_ArePassedOver()
    {
        // 1's north leads to another map; its west slot holds a Remote Action (the
        // lever for 3's hidden north exit), which is a command, not a way out.
        IReadOnlyList<RoomKey> order = Candidates(
            Row("1/1", "N=2/7", "E=1/3", "W=Action#1 [on the N exit of room 1/3]: pull lever"),
            Row("1/3", "N=1/4 (Hidden/Needs 1 Actions, any order)"),
            Row("1/4"),
            Row("2/7", "N=2/8"),
            Row("2/8"));

        Assert.Equal(Rooms(3, 4), order);
    }

    [Fact]
    public void NestsFiveRoomsBeyondTheDeathRoom_AndNoFurther()
    {
        IReadOnlyList<RoomKey> order = Candidates(
            Row("1/1", "N=1/2"), Row("1/2", "N=1/3"), Row("1/3", "N=1/4"), Row("1/4", "N=1/5"),
            Row("1/5", "N=1/6"), Row("1/6", "N=1/7"), Row("1/7", "N=1/8"), Row("1/8"));

        Assert.Equal(Rooms(2, 3, 4, 5, 6), order);
    }

    [Fact]
    public void DoorsLocksHiddenExitsTrapsAndTextExits_DoNotStopAnItem()
    {
        IReadOnlyList<RoomKey> order = Candidates(
            Row("1/1", "N=1/2 (Door [50 picklocks/strength])"),
            Row("1/2", "E=1/3 (Hidden)"),
            Row("1/3", "W=1/4 (Key: 172 [or 100 picklocks])"),
            Row("1/4", "D=1/5 (Text: go hole)"),
            Row("1/5", "U=1/6 (Trap, 36 damage)"),
            Row("1/6"));

        Assert.Equal(Rooms(2, 3, 4, 5, 6), order);
    }

    // The engine keeps no list of rooms it has tried. Room 7 is first reached at the
    // nesting limit, the long way round, where it can't be gone on from; the death
    // room's own east exit then reaches it one step out, and from there room 8 is in
    // range. A walk that never re-entered a room would miss 8.
    [Fact]
    public void ARoomFirstReachedAtTheLimit_IsGoneOnFromWhenAShorterWayReachesIt()
    {
        IReadOnlyList<RoomKey> order = Candidates(
            Row("1/1", "N=1/2", "E=1/7"),
            Row("1/2", "N=1/3"),
            Row("1/3", "N=1/4"),
            Row("1/4", "N=1/5"),
            Row("1/5", "N=1/7"),
            Row("1/7", "N=1/8"),
            Row("1/8"));

        Assert.Equal(Rooms(2, 3, 4, 5, 7, 8), order);
    }

    [Fact]
    public void EachRoomIsListedOnce_AndTheDeathRoomNever()
    {
        IReadOnlyList<RoomKey> order = Candidates(
            Row("1/1", "N=1/2"),
            Row("1/2", "S=1/1", "E=1/3"),
            Row("1/3", "W=1/2", "S=1/1"));

        Assert.Equal(Rooms(2, 3), order);
    }

    [Fact]
    public void AnExitToARoomTheGraphDoesNotHave_IsPassedOver()
    {
        IReadOnlyList<RoomKey> order = Candidates(
            Row("1/1", "N=1/99", "S=1/2"),
            Row("1/2"));

        Assert.Equal(Rooms(2), order);
    }

    [Fact]
    public void StaysWithCharacter_IsLoyalAndCursedMajor_NotPlainCursed()
    {
        Assert.True(DeathPileRules.StaysWithCharacter(new[] { 4, 100 }));   // LoyalItem
        Assert.True(DeathPileRules.StaysWithCharacter(new[] { 83 }));       // CursedMajor
        Assert.False(DeathPileRules.StaysWithCharacter(new[] { 82 }));      // Cursed drops
        Assert.False(DeathPileRules.StaysWithCharacter(Array.Empty<int>()));
    }

    [Fact]
    public void ANameSharedByAnItemThatDrops_IsNotTreatedAsStaying()
    {
        int[] loyal = { 100 }, plain = { 4 };
        Assert.True(DeathPileRules.EveryItemOfTheNameStays(new[] { loyal }));
        Assert.True(DeathPileRules.EveryItemOfTheNameStays(new[] { loyal, new[] { 83 } }));
        Assert.False(DeathPileRules.EveryItemOfTheNameStays(new[] { loyal, plain }));
        Assert.False(DeathPileRules.EveryItemOfTheNameStays(Array.Empty<int[]>()));
    }
}
