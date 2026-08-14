using MudPlay.Game.Map;
using Xunit;

namespace MudPlay.Tests;

// The blocked-exit reason is directional and names the barrier room + the key /
// picklocks-strength it needs, so a blocked walk can't be mistaken for the door's
// far side (which may carry a different requirement entirely).
public sealed class BlockedExitDescriberTests
{
    [Fact]
    public void KeyDoor_WithBashableAlternative_NamesRoomKeyAndStrength()
    {
        // 10/218 (Frozen Cavern) heading south into 10/219: glass key or 61 pick/str.
        Assert.True(RoomExit.TryParseWire("10/219 (Key: 520 [or 61 picklocks/strength])", out RoomExit exit));

        string reason = BlockedExitDescriber.Describe(
            new RoomKey(10, 218), Direction.S, in exit,
            key => key == new RoomKey(10, 218) ? "Frozen Cavern" : null,
            id => id == 520 ? "glass key" : null);

        Assert.Equal(
            "a locked door south from 10/218 (Frozen Cavern) — needs the glass key, or 61 picklocks/strength",
            reason);
    }

    [Fact]
    public void KeyDoor_PickOnly_OmitsStrength()
    {
        // 2/176 (Rocky Valley, Massive Doors) south into Rhudaur: black serpent key
        // or 81 picklocks (no "/strength" — pick-only).
        Assert.True(RoomExit.TryParseWire("2/2519 (Key: 593 [or 81 picklocks])", out RoomExit exit));

        string reason = BlockedExitDescriber.Describe(
            new RoomKey(2, 176), Direction.S, in exit,
            _ => "Rocky Valley, Massive Doors",
            id => id == 593 ? "black serpent key" : null);

        Assert.Equal(
            "a locked door south from 2/176 (Rocky Valley, Massive Doors) — needs the black serpent key, or 81 picklocks",
            reason);
    }

    [Fact]
    public void PlainDoor_StatOnly_NamesTheSkill()
    {
        Assert.True(RoomExit.TryParseWire("3/14 (Door [50 picklocks])", out RoomExit exit));

        string reason = BlockedExitDescriber.Describe(
            new RoomKey(3, 12), Direction.W, in exit,
            _ => "Cellar", _ => null);

        Assert.Equal("a door west from 3/12 (Cellar) — needs 50 picklocks", reason);
    }

    [Fact]
    public void UnknownItemName_FallsBackToId()
    {
        Assert.True(RoomExit.TryParseWire("1/9 (Key: 777)", out RoomExit exit));

        string reason = BlockedExitDescriber.Describe(
            new RoomKey(1, 1), Direction.E, in exit, _ => null, _ => null);

        // No room name, no item name → key by id, room by its raw map/room.
        Assert.Equal("a locked door east from 1/1 — needs item #777", reason);
    }
}
