using System;
using System.Collections.Generic;
using MudPlay.Game.Inventory;
using Xunit;

namespace MudPlay.Tests;

// Report paradigm-20261007-111929: a boss died, its Grab All went out, and the game
// echoed the four `get`s without running any (the boss's death leaves the room
// unable to act for a moment). The drops stayed on the floor. The follow-up looks at
// the room again and asks for what is still there.
public sealed class BossGrabRetryTests
{
    private sealed class Harness
    {
        public List<string> Sent { get; } = new();
        public List<Action> Scheduled { get; } = new();
        public DateTimeOffset Now = new(2026, 10, 7, 11, 19, 23, TimeSpan.Zero);
        public BossGrabRetry Retry { get; }

        public Harness() =>
            Retry = new BossGrabRetry(Sent.Add, (_, action) => Scheduled.Add(action), () => Now);

        public void RunScheduled()
        {
            Action[] due = Scheduled.ToArray();
            Scheduled.Clear();
            foreach (Action a in due) a();
        }
    }

    [Fact]
    public void DropStillOnTheFloor_IsAskedForAgain_AndTheRoomLookedAtAgain()
    {
        Harness h = new();
        h.Retry.Arm(new[] { "consecrated tabard", "gleaming chain legguards", "amber pendant" });

        h.RunScheduled();                                   // the look after the grab
        Assert.Equal(new[] { "" }, h.Sent);

        h.Now = h.Now.AddSeconds(2);
        h.Retry.OnNoticeSurvey("5 platinum pieces, gleaming chain legguards");

        Assert.Equal(new[] { "", "get gleaming chain legguards" }, h.Sent);
        Assert.Single(h.Scheduled);                         // and a look to see it took

        // Taken this time: nothing more is sent.
        h.Now = h.Now.AddSeconds(2);
        h.Retry.OnNoticeSurvey("5 platinum pieces");
        Assert.Equal(2, h.Sent.Count);
        Assert.False(h.Retry.IsArmed);
    }

    // A room drawn before the game ran the get still lists the drop.
    [Fact]
    public void ARoomDisplayRightAfterTheGrab_DoesNotAskAgain()
    {
        Harness h = new();
        h.Retry.Arm(new[] { "amber pendant" });

        h.Now = h.Now.AddMilliseconds(200);
        h.Retry.OnNoticeSurvey("amber pendant");

        Assert.Empty(h.Sent);
    }

    [Fact]
    public void GivesUpAfterAFewTries_AndStopsOnLeavingTheRoom()
    {
        Harness h = new();
        h.Retry.Arm(new[] { "amber pendant" });
        for (int i = 0; i < BossGrabRetry.MaxTries + 2; i++)
        {
            h.Now = h.Now.AddSeconds(2);
            h.Retry.OnNoticeSurvey("amber pendant");
        }
        Assert.Equal(BossGrabRetry.MaxTries, h.Sent.Count);
        Assert.False(h.Retry.IsArmed);

        h.Retry.Arm(new[] { "amber pendant" });
        h.Retry.Clear();
        h.Now = h.Now.AddSeconds(2);
        h.Retry.OnNoticeSurvey("amber pendant");
        h.RunScheduled();
        Assert.Equal(BossGrabRetry.MaxTries, h.Sent.Count);
    }
}
