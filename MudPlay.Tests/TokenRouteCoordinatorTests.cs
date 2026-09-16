using System;
using System.Collections.Generic;
using MudPlay.Game.Map;
using MudPlay.Game.Tokens;
using Xunit;

namespace MudPlay.Tests;

// Drives TokenRouteCoordinator through its FSM with fake delegates and a
// controllable schedule (scheduled callbacks are fired manually, LIFO — the newest
// timer is always the live one; stale ones are inert via the generation guard).
public sealed class TokenRouteCoordinatorTests
{
    private sealed class Harness
    {
        public bool InParty, IsLeader, RoomHasNpc;
        public readonly List<string> Members = new();
        public readonly List<string> Sent = new();
        public readonly List<RoomKey> Walked = new();
        public int Stops;
        public string? FailReason;
        private readonly List<Action> _scheduled = new();
        public readonly TokenRouteCoordinator Coord;

        public Harness()
        {
            Coord = new TokenRouteCoordinator(
                inParty: () => InParty,
                isLeader: () => IsLeader,
                membersToRegroup: () => Members,
                roomHasNpc: () => RoomHasNpc,
                walkToDest: Walked.Add,
                stopWalker: () => Stops++,
                send: Sent.Add,
                schedule: (_, a) => _scheduled.Add(a),
                log: null);
            Coord.RegroupFailed += r => FailReason = r;
        }

        // Fire the most recently scheduled callback (the live timer); a re-scheduled
        // timer appends a new one, so repeated calls drive the retry loop.
        public void FireLast()
        {
            Action a = _scheduled[^1];
            _scheduled.RemoveAt(_scheduled.Count - 1);
            a();
        }
    }

    private static readonly RoomKey Landing = new(1, 1813);
    private static readonly RoomKey Dest = new(1, 2000);

    [Fact]
    public void Solo_ClearRoom_UsesThenResumesFromLanding()
    {
        var h = new Harness();   // solo, clear room
        Assert.True(h.Coord.TryBegin("Silvermere", Landing, Dest));
        Assert.Contains("use token of Silvermere", h.Sent);

        h.Coord.OnTokenUsed("Silvermere");
        h.Coord.OnRoomChanged(Landing);
        h.FireLast();            // post-land settle → resume

        Assert.Equal(new[] { Dest }, h.Walked);
        Assert.False(h.Coord.Active);
    }

    [Fact]
    public void Solo_NpcRoom_WalksOverland_ThenUsesAtFirstClearRoom()
    {
        var h = new Harness { RoomHasNpc = true };   // current room not clear
        Assert.True(h.Coord.TryBegin("Silvermere", Landing, Dest));
        Assert.Empty(h.Sent);                        // didn't use — walking overland
        Assert.Equal(new[] { Dest }, h.Walked);      // heads toward the destination

        h.Coord.OnRoomChanged(new RoomKey(1, 500));  // still has NPCs → keep walking
        Assert.Empty(h.Sent);
        Assert.Equal(0, h.Stops);

        h.RoomHasNpc = false;
        h.Coord.OnRoomChanged(new RoomKey(1, 501));  // clear → stop and use here
        Assert.Equal(1, h.Stops);
        Assert.Contains("use token of Silvermere", h.Sent);
    }

    [Fact]
    public void Solo_UsesDisplayNameWithArticle_ForLostCity()
    {
        var h = new Harness();
        h.Coord.TryBegin("the Lost City", Landing, Dest);
        // The command must carry the full item name, not the normalized "lost city".
        Assert.Contains("use token of the Lost City", h.Sent);
    }

    [Fact]
    public void PartyFollower_Declines_SoCallerWalksOverland()
    {
        var h = new Harness { InParty = true, IsLeader = false };
        Assert.False(h.Coord.TryBegin("Silvermere", Landing, Dest));
        Assert.Empty(h.Sent);
    }

    [Fact]
    public void Leader_Regroups_ThenResumes_WhenAllMembersArrive()
    {
        var h = new Harness { InParty = true, IsLeader = true };
        h.Members.Add("Boost");
        h.Members.Add("Ermias the Bold");

        h.Coord.TryBegin("Silvermere", Landing, Dest);
        h.Coord.OnTokenUsed("Silvermere");
        h.Coord.OnRoomChanged(Landing);
        h.FireLast();            // settle → regrouping, sends the @do wave

        Assert.Contains("/Boost @do use token of Silvermere", h.Sent);
        Assert.Contains("/Ermias @do use token of Silvermere", h.Sent);
        Assert.Empty(h.Walked);  // not resumed until everyone's in

        h.Coord.OnMemberArrived("Boost");
        Assert.Empty(h.Walked);  // one still out

        h.Coord.OnMemberArrived("Ermias the Bold");   // given name "Ermias" matches
        Assert.Equal(new[] { Dest }, h.Walked);       // fully regrouped → resume
        Assert.False(h.Coord.Active);
    }

    [Fact]
    public void Leader_FailsOut_WhenAMemberNeverArrives()
    {
        var h = new Harness { InParty = true, IsLeader = true };
        h.Members.Add("Boost");

        h.Coord.TryBegin("Silvermere", Landing, Dest);
        h.Coord.OnTokenUsed("Silvermere");
        h.Coord.OnRoomChanged(Landing);
        h.FireLast();   // settle → regrouping (wave 1) + tick scheduled
        h.FireLast();   // tick → retry 1
        h.FireLast();   // tick → retry 2
        h.FireLast();   // tick → retry 3
        h.FireLast();   // tick → retries exhausted → fail out

        Assert.NotNull(h.FailReason);
        Assert.Contains("Boost", h.FailReason!);
        Assert.Empty(h.Walked);          // sat for user action, did not resume
        Assert.False(h.Coord.Active);
    }
}
