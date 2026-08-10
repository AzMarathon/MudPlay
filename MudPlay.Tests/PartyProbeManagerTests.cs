using System;
using System.Collections.Generic;
using System.Text;
using MudPlay.Game;
using MudPlay.Game.Remote;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using Xunit;

namespace MudPlay.Tests;

/// <summary>
/// PartyProbeManager telepaths @level + @version to a member the FIRST time we
/// party with them on a local day, and records the @version reply onto the
/// player record. These cover the daily gate, the invited→joined edge, the
/// enable/suspend gates, and the brace-wrapped version-reply capture.
/// </summary>
public sealed class PartyProbeManagerTests
{
    private sealed class Harness
    {
        public readonly MessageRouter Router;
        public readonly ChatRouter Chat;
        public readonly PartyState State = new();
        public readonly PlayerDatabase Players = new();
        public readonly List<byte[]> Wire = new();
        public readonly PartyProbeManager Probe;

        public DateTime Now = DateTime.UnixEpoch;

        public Harness()
        {
            Router = new MessageRouter();
            DefaultPatterns.Seed(Router);
            Chat = new ChatRouter(Router);
            Probe = new PartyProbeManager(Chat, State, Players, clock: () => Now, log: null);
            Probe.SetWireSender(Wire.Add);
        }

        public PartyMember AddMember(string name, bool self = false, bool invited = false)
        {
            PartyMember m = new() { Name = name, IsSelf = self, IsInvited = invited };
            State.Members.Add(m);
            return m;
        }

        public void Reply(string sender, string message)
        {
            string line = $"{sender} telepaths: {message}";
            Router.Dispatch(new Terminal.LineExtractor.EmittedLine(
                line, new Terminal.CellAttributes[line.Length], DateTimeOffset.UnixEpoch, IsPromptLine: false));
        }

        public string WireText => string.Join("|", Wire.ConvertAll(b => Encoding.Latin1.GetString(b)));
    }

    // ----- daily gate ----------------------------------------------------

    [Fact]
    public void FirstPartyOfDay_SendsLevelAndVersion()
    {
        var h = new Harness();
        h.AddMember("Bob");

        Assert.Contains("/Bob @level\r", h.WireText);
        Assert.Contains("/Bob @version\r", h.WireText);
        // The join is stamped so a same-day rejoin won't re-probe.
        Assert.Equal(h.Now, h.Players.GetLastPartiedUtc("Bob"));
    }

    [Fact]
    public void SecondPartySameDay_DoesNotResend()
    {
        var h = new Harness();
        h.Players.RecordPartied("Bob", h.Now);   // already partied earlier today

        h.AddMember("Bob");

        Assert.Empty(h.Wire);
    }

    [Fact]
    public void NextDay_ReprobesTheSamePlayer()
    {
        var h = new Harness();
        h.Players.RecordPartied("Bob", h.Now);   // partied "yesterday"
        h.Now += TimeSpan.FromHours(25);          // a later calendar day

        h.AddMember("Bob");

        Assert.Contains("/Bob @version\r", h.WireText);
    }

    // ----- membership edges ----------------------------------------------

    [Fact]
    public void InvitedMember_NotProbedUntilTheyJoin()
    {
        var h = new Harness();
        PartyMember m = h.AddMember("Tristian", invited: true);
        Assert.Empty(h.Wire);

        m.IsInvited = false;   // acceptance edge

        Assert.Contains("/Tristian @version\r", h.WireText);
    }

    [Fact]
    public void SelfMember_NeverProbed()
    {
        var h = new Harness();
        h.AddMember("Me", self: true);
        Assert.Empty(h.Wire);
    }

    // ----- enable / suspend gates ----------------------------------------

    [Fact]
    public void Disabled_SendsNothing()
    {
        var h = new Harness();
        h.Probe.Enabled = false;
        h.AddMember("Bob");
        Assert.Empty(h.Wire);
    }

    [Fact]
    public void Suspended_SendsNothing_ThenResumes()
    {
        var h = new Harness();
        h.Probe.NotifyDisconnected();
        h.AddMember("Bob");
        Assert.Empty(h.Wire);

        h.Probe.NotifyEnteredRealm();
        h.AddMember("Al");
        Assert.Contains("/Al @version\r", h.WireText);
    }

    [Fact]
    public void TrainerMenu_SuppressesTheProbe()
    {
        var h = new Harness();
        h.Probe.IsInTrainerMenu = () => true;
        h.AddMember("Bob");
        Assert.Empty(h.Wire);
    }

    // ----- @version reply capture ----------------------------------------

    [Fact]
    public void VersionReply_RecordedOntoPlayer()
    {
        var h = new Harness();
        h.AddMember("Bob");

        h.Reply("Bob", "{MudPlay 2.37.0}");

        Assert.Equal("MudPlay 2.37.0", h.Players.Find("Bob")!.Version);
    }

    [Fact]
    public void VersionReply_MegaMudForm_Recorded()
    {
        var h = new Harness();
        h.AddMember("Bob");

        h.Reply("Bob", "{MegaMud 1.03u}");

        Assert.Equal("MegaMud 1.03u", h.Players.Find("Bob")!.Version);
    }

    [Fact]
    public void LevelReply_NotMistakenForVersion()
    {
        var h = new Harness();
        h.AddMember("Bob");

        // The member answers @level first — must not be recorded as a version, and
        // the expectation stays armed for the real @version line that follows.
        h.Reply("Bob", "{Level 12, 1,234 exp, 500 to next level}");
        Assert.Null(h.Players.Find("Bob")?.Version);

        h.Reply("Bob", "{MudPlay 2.37.0}");
        Assert.Equal("MudPlay 2.37.0", h.Players.Find("Bob")!.Version);
    }

    [Fact]
    public void VersionReply_AfterWindow_Ignored()
    {
        var h = new Harness();
        h.AddMember("Bob");

        h.Now += TimeSpan.FromMinutes(1);   // past the version window
        h.Reply("Bob", "{MudPlay 2.37.0}");

        Assert.Null(h.Players.Find("Bob")?.Version);
    }

    [Fact]
    public void UnexpectedVersionLine_Ignored()
    {
        var h = new Harness();
        // No probe sent to Carol — a version-shaped line from her isn't recorded.
        h.Reply("Carol", "{MudPlay 2.37.0}");
        Assert.Null(h.Players.Find("Carol")?.Version);
    }

    // ----- version parse unit ---------------------------------------------

    [Theory]
    [InlineData("{MudPlay 2.37.0}", true, "MudPlay 2.37.0")]
    [InlineData("{MegaMud 1.03u}", true, "MegaMud 1.03u")]
    [InlineData("MudPlay 2.37.0", false, "")]          // not brace-wrapped
    [InlineData("{Permission denied}", false, "")]        // no digit → denial/chat
    [InlineData("{Level 12, 1 exp}", false, "")]          // level reply
    [InlineData("{HP=10/20,MA=5/5}", false, "")]          // health reply
    [InlineData("{}", false, "")]                          // empty
    [InlineData("{2.37.0}", false, "")]                    // no leading letter
    public void TryParseVersion_Cases(string message, bool ok, string expected)
    {
        bool parsed = PartyProbeManager.TryParseVersion(message, out string version);
        Assert.Equal(ok, parsed);
        if (ok) Assert.Equal(expected, version);
    }
}
