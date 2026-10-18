using System.Text;
using MudPlay.Game;
using MudPlay.Game.Map;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// After a party-splitting teleport the leader invites each member as they land and
// lets the @join nag run its 3 nags before the wait for them may run out (user,
// 2026-10-17: "it should invite, wait for the @join nag to kick in. After the 3
// nags, if they don't join, then follow the behavior we have in the client").
// Lines go through the router; sends are what the managers put on their senders.
// The nag tick is AutoPartyManager's injected scheduler: the test runs it.
public sealed class SplitReformJoinNagTests
{
    private sealed class Harness : IDisposable
    {
        public MessageRouter Router { get; } = new();
        public PartyState Party { get; } = new();
        public PartyManager Manager { get; }
        public AutoPartyManager AutoParty { get; }
        public MovementCoordinator Coord { get; } = new();
        public List<string> Wire { get; } = new();
        public DateTime Clock { get; set; } = new(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);
        private readonly ManualTimers _timers = new();
        private readonly DateTime _start;

        public Harness()
        {
            DefaultPatterns.Seed(Router);
            Manager = new PartyManager(Router, Party) { LocalCharacterName = "MudPlay" };
            AutoParty = new AutoPartyManager(Router, new PlayerDatabase(new ProfileService(), () => null), Party,
                schedule: _timers.Arm, now: () => Clock)
            {
                // Shorter than the nags take, so it's the nags that decide.
                InviteWaitWindow = TimeSpan.FromSeconds(10),
            };
            AutoParty.SetWireSender(b => Wire.Add(Encoding.Latin1.GetString(b)));
            AutoParty.SetMovementGate(Coord, isLooping: () => false);
            _start = Clock;
            Line("Raijin started to follow you.");
        }

        public void Line(string text) => Router.Dispatch(new LineExtractor.EmittedLine(
            text, Array.Empty<CellAttributes>(), Clock, IsPromptLine: false));

        // Run the nag tick at this many seconds after the start.
        public void TickAt(int seconds)
        {
            Clock = _start + TimeSpan.FromSeconds(seconds);
            _timers.RunDue(AutoPartyManager.NagTick);
        }

        public int Nags => Wire.Count(w => w == "/Raijin @join\r");
        public bool Uninvited => Wire.Contains("uninvite Raijin\r");
        public bool Held => Coord.AssertedGates.Contains(MovementCoordinator.PartyInviteGate);

        // The teleport splits the party, Raijin lands, and the game answers the
        // invite that sends.
        public void Split()
        {
            AutoParty.NotePartySplitTeleport();
            Line("Raijin is no longer following you.");
            Line("Raijin appears in a blinding flash of light!");
            if (Wire.Contains("invite Raijin\r")) Line("You have invited Raijin to follow you.");
        }

        public void Dispose()
        {
            AutoParty.Dispose();
            Manager.Dispose();
        }
    }

    [Fact]
    public void Split_InvitesTheMemberOnLanding()
    {
        using Harness h = new();
        h.Split();
        Assert.Contains("invite Raijin\r", h.Wire);
        Assert.True(h.Held);
    }

    // Rejoining after the first nag ends the wait and the nags: no uninvite.
    [Fact]
    public void RejoinAfterTheFirstNag_NoUninvite()
    {
        using Harness h = new();
        h.Split();
        h.TickAt(5);
        Assert.Equal(1, h.Nags);
        h.TickAt(10);                                  // the wait's own window is over
        Assert.False(h.Uninvited);

        h.Line("Raijin started to follow you.");
        Assert.False(h.Held);
        h.TickAt(15);
        h.TickAt(60);

        Assert.Equal(1, h.Nags);
        Assert.False(h.Uninvited);
    }

    // No rejoin: three nags, then, a nag's interval after the third, the client's
    // existing give-up (the uninvite), and no fourth nag.
    [Fact]
    public void NoRejoinAfterThreeNags_TheExistingFallbackRuns()
    {
        using Harness h = new();
        h.Split();
        h.TickAt(5);
        h.TickAt(10);
        h.TickAt(15);
        h.TickAt(25);
        Assert.Equal(AutoPartyManager.SplitReformJoinNags, h.Nags);
        h.TickAt(30);                                  // the third is 5 s old
        Assert.False(h.Uninvited);
        Assert.True(h.Held);

        h.TickAt(35);
        Assert.True(h.Uninvited);
        Assert.False(h.Held);
        h.TickAt(45);
        Assert.Equal(AutoPartyManager.SplitReformJoinNags, h.Nags);
    }

    // A wait longer than the nags: three nags and no more, then the wait runs out
    // when it always did.
    [Fact]
    public void LongerWait_ThreeNagsOnly_ThenTheWaitEndsWhenItAlwaysDid()
    {
        using Harness h = new();
        h.AutoParty.InviteWaitWindow = TimeSpan.FromSeconds(90);
        h.Split();
        for (int t = 5; t < 90; t += 5) h.TickAt(t);
        Assert.Equal(AutoPartyManager.SplitReformJoinNags, h.Nags);
        Assert.False(h.Uninvited);

        h.TickAt(90);
        Assert.True(h.Uninvited);
    }

    // A long resend interval: the nag's own total time (55 s) doesn't cut the
    // three nags short; the third goes at 55 s and the fallback a full interval on.
    [Fact]
    public void LongResendInterval_ThreeNagsThenAFullInterval()
    {
        using Harness h = new();
        h.AutoParty.JoinNagFrequency = TimeSpan.FromSeconds(25);
        h.Split();
        for (int t = 5; t <= 75; t += 5) h.TickAt(t);
        Assert.Equal(AutoPartyManager.SplitReformJoinNags, h.Nags);   // at 5, 30, 55
        Assert.False(h.Uninvited);

        h.TickAt(80);
        Assert.True(h.Uninvited);
        Assert.Equal(AutoPartyManager.SplitReformJoinNags, h.Nags);
    }

    // The master switch off between nags: they resume when it's back on, all three
    // go out, and the fallback still waits a full interval after the third.
    [Fact]
    public void MasterSwitchOffMidway_ThreeNagsThenAFullInterval()
    {
        using Harness h = new();
        bool off = false;
        h.AutoParty.MasterSwitchOff = () => off;
        h.Split();
        h.TickAt(5);                                   // nag 1
        off = true;
        for (int t = 15; t <= 40; t += 5) h.TickAt(t);
        off = false;
        h.TickAt(41);                                  // nag 2
        h.TickAt(51);                                  // nag 3
        for (int t = 52; t <= 60; t++) h.TickAt(t);    // past the nag's 55 s total
        Assert.Equal(AutoPartyManager.SplitReformJoinNags, h.Nags);
        Assert.False(h.Uninvited);

        h.TickAt(61);
        Assert.True(h.Uninvited);
    }

    // {Ok} stops the nags short of three; the wait then ends at its window, as it
    // did before, instead of holding for good.
    [Fact]
    public void OkReply_TheWaitStillEndsAtItsWindow()
    {
        using Harness h = new();
        h.AutoParty.InviteWaitWindow = TimeSpan.FromSeconds(30);
        h.Split();
        h.TickAt(5);
        h.Line("Raijin telepaths: {Ok}");
        h.TickAt(15);
        h.TickAt(25);
        Assert.Equal(1, h.Nags);
        Assert.False(h.Uninvited);

        h.TickAt(30);
        Assert.True(h.Uninvited);
        Assert.False(h.Held);
    }

    // With the @join nag turned off there are no nags to wait out: the wait ends
    // as it always did.
    [Fact]
    public void NagsTurnedOff_TheWaitEndsAsBefore()
    {
        using Harness h = new();
        h.AutoParty.JoinNagEnabled = false;
        h.Split();
        h.TickAt(10);
        Assert.Equal(0, h.Nags);
        Assert.True(h.Uninvited);
    }

    // The master switch holds it as it holds everything automatic: off at the
    // split, nothing is invited or held; off midway, no nag and no uninvite goes
    // out until it is back on.
    [Fact]
    public void MasterSwitchOff_AtTheSplit_NothingHappens()
    {
        using Harness h = new();
        h.AutoParty.MasterSwitchOff = () => true;
        h.Split();
        h.TickAt(60);

        Assert.DoesNotContain("invite Raijin\r", h.Wire);
        Assert.False(h.Held);
        Assert.Equal(0, h.Nags);
    }

    [Fact]
    public void MasterSwitchOff_Midway_HoldsTheNagsAndTheUninvite()
    {
        using Harness h = new();
        bool off = false;
        h.AutoParty.MasterSwitchOff = () => off;
        h.Split();
        h.TickAt(5);
        Assert.Equal(1, h.Nags);

        off = true;
        h.TickAt(15);
        h.TickAt(25);
        h.TickAt(40);
        Assert.Equal(1, h.Nags);
        Assert.False(h.Uninvited);

        off = false;
        h.TickAt(41);
        Assert.Equal(2, h.Nags);
        Assert.False(h.Uninvited);
    }
}
