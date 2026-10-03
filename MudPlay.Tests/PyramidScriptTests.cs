using System.Collections.Generic;
using System.Linq;
using MudPlay.Game.Map;
using Xunit;

namespace MudPlay.Tests;

// Pins the canned Great Pyramid solve scripts (see GAME_MECHANICS.md) against the
// game-data trace: floor detection by room number, per-floor step shape, the F3
// wait/bash/key door classification, and the sphinx keywords.
public sealed class PyramidScriptTests
{
    [Theory]
    [InlineData(12, 1239, PyramidFloor.Firepit)]
    [InlineData(12, 1800, PyramidFloor.F1)]
    [InlineData(12, 1920, PyramidFloor.F1)]
    [InlineData(12, 1921, PyramidFloor.F2)]
    [InlineData(12, 2001, PyramidFloor.F2)]
    [InlineData(12, 2002, PyramidFloor.F3)]
    [InlineData(12, 2051, PyramidFloor.F3)]
    [InlineData(12, 2052, PyramidFloor.F4)]
    [InlineData(12, 2076, PyramidFloor.F4)]
    [InlineData(12, 2077, PyramidFloor.F5)]
    [InlineData(12, 2084, PyramidFloor.F5)]
    [InlineData(12, 2085, PyramidFloor.Top)]
    [InlineData(12, 335, PyramidFloor.None)]
    [InlineData(5, 2085, PyramidFloor.None)]
    public void FloorOf_MapsRoomToFloor(int map, int room, PyramidFloor expected)
        => Assert.Equal(expected, PyramidScript.FloorOf(map, room));

    [Theory]
    [InlineData(12, 1239, true)]   // firepit
    [InlineData(12, 1278, true)]   // top of the firepit range
    [InlineData(12, 335, true)]    // desert secondary
    [InlineData(12, 1800, false)]  // F1 — not a scatter room
    [InlineData(12, 2085, false)]  // target
    [InlineData(5, 1250, false)]   // wrong map
    public void IsScatterRoom_DetectsFailLandings(int map, int room, bool expected)
        => Assert.Equal(expected, PyramidScript.IsScatterRoom(map, room));

    [Fact]
    public void F1_Is126MovesFivePushBlocksThenFireSphinx()
    {
        var steps = PyramidScript.Steps(PyramidFloor.F1);
        Assert.Equal(PyramidScript.Floor1MoveCount, steps.Count(s => s.Kind == PyramidStepKind.Move));
        Assert.Equal(5, steps.Count(s => s.Kind == PyramidStepKind.PushBlock));
        Assert.Equal(PyramidScript.Floor1ActionCount,
            steps.Count(s => s.Kind is PyramidStepKind.PushBlock or PyramidStepKind.AskSphinx));
        PyramidStep last = steps[^1];
        Assert.Equal(PyramidStepKind.AskSphinx, last.Kind);
        Assert.Equal("fire", last.Word);
        Assert.Equal(Direction.U, last.Dir);
    }

    [Fact]
    public void F2_Is33MovesThenSunSphinx()
    {
        var steps = PyramidScript.Steps(PyramidFloor.F2);
        Assert.Equal(33, steps.Count(s => s.Kind == PyramidStepKind.Move));
        Assert.Equal("sun", steps[^1].Word);
        Assert.Equal(PyramidStepKind.AskSphinx, steps[^1].Kind);
    }

    [Fact]
    public void F3_HasTwentyDoorsFourWaitOneKeyThenStarsSphinx()
    {
        var steps = PyramidScript.Steps(PyramidFloor.F3);
        var doors = steps.Where(s => s.Kind == PyramidStepKind.Door).ToList();
        Assert.Equal(24, doors.Count);                       // 25 door-steps total, 1 is the KeyDoor
        Assert.Equal(20, doors.Count(d => d.Bashable));      // bashable plain doors
        Assert.Equal(4, doors.Count(d => !d.Bashable));      // 1000-picklock wait doors
        Assert.Equal(1, steps.Count(s => s.Kind == PyramidStepKind.KeyDoor));
        Assert.Equal("stars", steps[^1].Word);

        // Wait doors are exactly steps 3, 11, 14, 21 (1-based over the door sequence).
        var doorKinds = steps.Where(s => s.Kind is PyramidStepKind.Door or PyramidStepKind.KeyDoor).ToList();
        Assert.False(doorKinds[2].Bashable);   // step 3
        Assert.False(doorKinds[10].Bashable);  // step 11
        Assert.False(doorKinds[13].Bashable);  // step 14
        Assert.False(doorKinds[20].Bashable);  // step 21
        Assert.Equal(PyramidStepKind.KeyDoor, doorKinds[21].Kind);  // step 22
    }

    // Walks each floor's script through the game data's exits from the floor's entry
    // room: every step must have an exit to take, start in the room the table says it
    // does, and the last one must land on the next floor. A script that is one move
    // out anywhere fails here rather than in the pyramid.
    [Theory]
    [InlineData(PyramidFloor.F1, 1800)]
    [InlineData(PyramidFloor.F2, 1921)]
    [InlineData(PyramidFloor.F3, 2002)]
    [InlineData(PyramidFloor.F4, 2052)]
    [InlineData(PyramidFloor.F5, 2077)]
    public void Script_WalksTheGameDataFromEntryToTheNextFloor(PyramidFloor floor, int entry)
    {
        var steps = PyramidScript.Steps(floor);
        IReadOnlyList<int> from = PyramidScript.FromRooms(floor)!;
        Assert.Equal(steps.Count, from.Count);

        int room = entry;
        for (int i = 0; i < steps.Count; i++)
        {
            Assert.True(from[i] == room, $"{floor} step {i + 1}: table says 12/{from[i]}, the walk is in 12/{room}");
            Assert.Equal(floor, PyramidScript.FloorOf(PyramidScript.PyramidMap, room));
            if (steps[i].Kind == PyramidStepKind.PushBlock) continue;
            int? next = PyramidRooms.Target(room, steps[i].Dir);
            Assert.True(next is not null, $"{floor} step {i + 1}: no {steps[i].Dir} exit from 12/{room}");
            room = next!.Value;
        }
        Assert.Equal(PyramidScript.EndRoom(floor), room);
    }

    // Each push block is sent from a room that carries the block's action, and each
    // sphinx is asked in a room that has one.
    [Fact]
    public void PushBlocksAndSphinxes_SitInTheRightRooms()
    {
        foreach (PyramidFloor floor in new[] { PyramidFloor.F1, PyramidFloor.F2, PyramidFloor.F3 })
        {
            var steps = PyramidScript.Steps(floor);
            IReadOnlyList<int> from = PyramidScript.FromRooms(floor)!;
            for (int i = 0; i < steps.Count; i++)
            {
                if (steps[i].Kind == PyramidStepKind.PushBlock)
                    Assert.Contains(PyramidRooms.ExitCells[from[i]].Values, c => c.Contains("push block"));
                if (steps[i].Kind == PyramidStepKind.AskSphinx)
                    Assert.Contains("Hidden/Needs 1 Actions", PyramidRooms.ExitCells[from[i]]["U"]);
            }
        }
    }

    // The door classes the F3 script assigns are the game data's: a wait door is a
    // 1000-picklock door, the key door takes the golden lion key, the rest are plain.
    [Fact]
    public void F3_DoorKinds_MatchTheGameData()
    {
        var steps = PyramidScript.Steps(PyramidFloor.F3);
        IReadOnlyList<int> from = PyramidScript.FromRooms(PyramidFloor.F3)!;
        for (int i = 0; i < steps.Count; i++)
        {
            if (steps[i].Kind == PyramidStepKind.AskSphinx) continue;
            string cell = PyramidRooms.ExitCells[from[i]][steps[i].Dir.ToString()];
            if (steps[i].Kind == PyramidStepKind.KeyDoor) Assert.Contains("(Key: 1175", cell);
            else if (steps[i].Bashable) Assert.EndsWith("(Door)", cell);
            else Assert.Contains("(Door [1000 picklocks/strength])", cell);
        }
    }

    [Fact]
    public void FromRooms_OnlyOnTheClimbedFloors()
    {
        Assert.Null(PyramidScript.FromRooms(PyramidFloor.Firepit));
        Assert.Null(PyramidScript.FromRooms(PyramidFloor.Top));
        Assert.Equal(2051, PyramidScript.FromRooms(PyramidFloor.F3)![^1]);   // stars sphinx
    }

    [Fact]
    public void F4_Is22MovesForwardOnlyNoSphinx()
    {
        var steps = PyramidScript.Steps(PyramidFloor.F4);
        Assert.Equal(22, steps.Count);
        Assert.All(steps, s => Assert.Equal(PyramidStepKind.Move, s.Kind));
        Assert.Equal(Direction.U, steps[^1].Dir);   // final ascent to F5
    }

    [Fact]
    public void F5_IsFiveMovesToTarget()
    {
        var steps = PyramidScript.Steps(PyramidFloor.F5);
        Assert.Equal(5, steps.Count);
        Assert.All(steps, s => Assert.Equal(PyramidStepKind.Move, s.Kind));
    }

    [Fact]
    public void BlindFast_OnlyFloors1And2()
    {
        Assert.True(PyramidScript.IsBlindFast(PyramidFloor.F1));
        Assert.True(PyramidScript.IsBlindFast(PyramidFloor.F2));
        Assert.False(PyramidScript.IsBlindFast(PyramidFloor.F3));
        Assert.False(PyramidScript.IsBlindFast(PyramidFloor.F4));
        Assert.False(PyramidScript.IsBlindFast(PyramidFloor.F5));
    }
}
