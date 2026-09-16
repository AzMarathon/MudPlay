using System;
using System.Collections.Generic;
using MudPlay.Game.Map;
using MudPlay.Game.Tokens;
using Xunit;

namespace MudPlay.Tests;

// Drives TokenRouteCoordinator through its FSM with fake delegates and a
// controllable schedule (scheduled callbacks are fired manually, LIFO — the newest
// timer is the live one; stale ones are inert via the generation guard). Covers the
// solo path and the party-leader flow: members first via .@party, targeted @do
// retries by room-check, leader tokens LAST, and the fail-out vs use-regardless split.
public sealed class TokenRouteCoordinatorTests
{
    private sealed class Harness
    {
        public bool InParty, IsLeader, RoomHasNpc, UseWhenIncomplete;
        public readonly List<string> Members = new();     // given-names of party members
        public List<string> StillHere = new();            // room-check result (test-controlled)
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
                partyMembers: () => Members,
                membersStillHere: () => StillHere,
                useWhenIncomplete: () => UseWhenIncomplete,
                roomHasNpc: () => RoomHasNpc,
                walkToDest: Walked.Add,
                stopWalker: () => Stops++,
                send: Sent.Add,
                schedule: (_, a) => _scheduled.Add(a),
                log: null);
            Coord.RegroupFailed += r => FailReason = r;
        }

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
        var h = new Harness { RoomHasNpc = true };
        Assert.True(h.Coord.TryBegin("Silvermere", Landing, Dest));
        Assert.Empty(h.Sent);                         // didn't use — walking overland
        Assert.Equal(new[] { Dest }, h.Walked);

        h.Coord.OnRoomChanged(new RoomKey(1, 500));   // still has NPCs → keep walking
        Assert.Empty(h.Sent);
        Assert.Equal(0, h.Stops);

        h.RoomHasNpc = false;
        h.Coord.OnRoomChanged(new RoomKey(1, 501));   // clear → stop and use
        Assert.Equal(1, h.Stops);
        Assert.Contains("use token of Silvermere", h.Sent);
    }

    [Fact]
    public void Solo_UsesDisplayNameWithArticle_ForLostCity()
    {
        var h = new Harness();
        h.Coord.TryBegin("the Lost City", Landing, Dest);
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
    public void Leader_SendsPartyFirst_ThenTokensLast_WhenAllPort()
    {
        var h = new Harness { InParty = true, IsLeader = true };
        h.Members.Add("Boost");
        h.Members.Add("Ermias");

        h.Coord.TryBegin("Silvermere", Landing, Dest);
        Assert.Contains(".@party use token of Silvermere", h.Sent);   // members first
        Assert.DoesNotContain("use token of Silvermere", h.Sent);     // leader hasn't tokened yet

        h.Coord.OnMemberDeparted("Boost");
        Assert.DoesNotContain("use token of Silvermere", h.Sent);     // one still here
        h.Coord.OnMemberDeparted("Ermias the Bold");                  // given name "Ermias" matches

        int party = h.Sent.IndexOf(".@party use token of Silvermere");
        int own = h.Sent.IndexOf("use token of Silvermere");
        Assert.True(own > party && party >= 0);                       // leader tokened LAST

        h.Coord.OnTokenUsed("Silvermere");
        h.Coord.OnRoomChanged(Landing);
        h.FireLast();                                                 // settle → resume
        Assert.Equal(new[] { Dest }, h.Walked);
        Assert.False(h.Coord.Active);
    }

    [Fact]
    public void Leader_RetriesRemaining_ByRoomCheck_ThenTokens()
    {
        var h = new Harness { InParty = true, IsLeader = true };
        h.Members.Add("Boost");
        h.Members.Add("Ermias");

        h.Coord.TryBegin("Silvermere", Landing, Dest);
        h.Coord.OnMemberDeparted("Boost");            // Boost ported; Ermias hasn't

        h.StillHere = new List<string> { "Ermias" };  // room-check: Ermias still here
        int before = h.Sent.Count;
        h.FireLast();                                 // regroup tick → re-broadcast (only Ermias hears it)
        Assert.Contains(".@party use token of Silvermere", h.Sent.GetRange(before, h.Sent.Count - before));
        Assert.DoesNotContain("use token of Silvermere", h.Sent);   // leader still waiting

        h.StillHere = new List<string>();             // Ermias ported now
        h.FireLast();                                 // tick → room empty → leader tokens
        Assert.Contains("use token of Silvermere", h.Sent);
    }

    [Fact]
    public void Leader_FailsOutInRoom_WhenMemberStranded_AndSettingOff()
    {
        var h = new Harness { InParty = true, IsLeader = true, UseWhenIncomplete = false };
        h.Members.Add("Boost");
        h.StillHere = new List<string> { "Boost" };   // never ports

        h.Coord.TryBegin("Silvermere", Landing, Dest);
        h.FireLast();   // tick → retry 1
        h.FireLast();   // tick → retry 2
        h.FireLast();   // tick → retry 3
        h.FireLast();   // tick → retries exhausted → fail out in room

        Assert.NotNull(h.FailReason);
        Assert.Contains("Boost", h.FailReason!);
        Assert.DoesNotContain("use token of Silvermere", h.Sent);   // leader did NOT token
        Assert.Empty(h.Walked);
        Assert.False(h.Coord.Active);
    }

    [Fact]
    public void Leader_TokensAnyway_WhenMemberStranded_AndSettingOn()
    {
        var h = new Harness { InParty = true, IsLeader = true, UseWhenIncomplete = true };
        h.Members.Add("Boost");
        h.StillHere = new List<string> { "Boost" };   // never ports

        h.Coord.TryBegin("Silvermere", Landing, Dest);
        h.FireLast();   // retry 1
        h.FireLast();   // retry 2
        h.FireLast();   // retry 3
        h.FireLast();   // exhausted → use regardless

        Assert.Null(h.FailReason);
        Assert.Contains("use token of Silvermere", h.Sent);   // leader tokened anyway
    }
}
