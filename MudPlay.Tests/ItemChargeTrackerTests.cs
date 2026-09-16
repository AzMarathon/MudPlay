using System;
using System.Collections.Generic;
using System.Text;
using MudPlay.Game.Inventory;
using Xunit;

namespace MudPlay.Tests;

// Pins ItemChargeTracker's look→charges capture: an outbound `look <item>` for a
// carried item arms the capture, and the reply's "Uses remaining: N" records against
// that item. Unrelated / unarmed charge lines are ignored, and directions / non-items
// don't arm.
public sealed class ItemChargeTrackerTests
{
    private sealed class Harness
    {
        public readonly List<string> Carried = new();
        private readonly List<Action> _scheduled = new();
        public readonly ItemChargeTracker Tracker;

        public Harness()
            => Tracker = new ItemChargeTracker(() => Carried, (_, a) => _scheduled.Add(a), log: null);

        public void Look(string cmd) => Tracker.ObserveOutbound(Encoding.Latin1.GetBytes(cmd + "\r"));
        public void Line(string text) => Tracker.HandleLine(text);
        public void FireTimers() { foreach (Action a in _scheduled) a(); _scheduled.Clear(); }
    }

    [Fact]
    public void Look_ThenUsesRemaining_RecordsAgainstCarriedItem()
    {
        var h = new Harness();
        h.Carried.Add("silverbark canoe");
        h.Look("look silver");                 // partial resolves to the carried canoe
        h.Line("A sturdy little boat.");
        h.Line("Uses remaining: 5");
        Assert.Equal(5, h.Tracker.ChargesFor("silverbark canoe"));
    }

    [Fact]
    public void TokenLoginLook_FullName_Records()
    {
        var h = new Harness();
        h.Carried.Add("token of Silvermere");
        h.Look("look token of Silvermere");    // the shape TokenTracker sends on login
        h.Line("Uses remaining: 3");
        Assert.Equal(3, h.Tracker.ChargesFor("token of Silvermere"));
    }

    [Fact]
    public void UsesRemaining_WithoutAPrecedingLook_IsIgnored()
    {
        var h = new Harness();
        h.Carried.Add("silverbark canoe");
        h.Line("Uses remaining: 5");            // no look armed it
        Assert.Null(h.Tracker.ChargesFor("silverbark canoe"));
    }

    [Fact]
    public void LookAtNonCarriedThing_DoesNotArm()
    {
        var h = new Harness();
        h.Carried.Add("silverbark canoe");
        h.Look("look orc");                     // not a carried item
        h.Line("Uses remaining: 5");
        Assert.Null(h.Tracker.ChargesFor("silverbark canoe"));
    }

    [Fact]
    public void LookDirection_DoesNotArm()
    {
        var h = new Harness();
        h.Carried.Add("stone necklace");
        h.Look("look ne");                      // a direction, too short to be an item
        h.Line("Uses remaining: 5");
        Assert.Null(h.Tracker.ChargesFor("stone necklace"));
    }

    [Fact]
    public void TimeoutClearsPending_SoALaterChargeLineIsNotMisattributed()
    {
        var h = new Harness();
        h.Carried.Add("silverbark canoe");
        h.Look("look silver");
        h.FireTimers();                         // look reply window lapsed with no charges
        h.Line("Uses remaining: 5");            // belongs to something else
        Assert.Null(h.Tracker.ChargesFor("silverbark canoe"));
    }

    [Fact]
    public void Clear_EmptiesCharges()
    {
        var h = new Harness();
        h.Carried.Add("silverbark canoe");
        h.Look("look silver");
        h.Line("Uses remaining: 5");
        h.Tracker.Clear();
        Assert.Null(h.Tracker.ChargesFor("silverbark canoe"));
    }
}
