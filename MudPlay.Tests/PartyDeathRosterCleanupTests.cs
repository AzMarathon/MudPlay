using System.Text;
using MudPlay.Game;
using MudPlay.Game.Map;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// A party member's death in our room ("<Name> is dead.") asks one `par`; the first
// reply read afterwards settles it (user, 2026-10-17). Not listed: the roster drops
// them. Listed [Invited]: a leader uninvites them once combat clears. Every line goes
// through the terminal emulator, the line extractor and the router, and every send is
// what the managers put on their wire senders.
public sealed class PartyDeathRosterCleanupTests
{
    private sealed class Harness : IDisposable
    {
        private readonly TerminalEmulator _emulator = new(120, 25);
        private readonly LineExtractor _extractor;
        public MessageRouter Router { get; } = new();
        public PartyState Party { get; } = new();
        public PartyManager Manager { get; }
        public PartyPoller Poller { get; }
        public MovementCoordinator Coord { get; } = new();
        public PartyDeathRosterCleanup Cleanup { get; }
        public LogService Log { get; } = new();
        public List<string> Wire { get; } = new();
        public DateTime Clock { get; set; } = new(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);

        public Harness(bool parOnTimer = false)
        {
            DefaultPatterns.Seed(Router);
            _extractor = new LineExtractor(_emulator);
            _extractor.LineEmitted += Router.Dispatch;
            Manager = new PartyManager(Router, Party) { LocalCharacterName = "MudPlay" };
            Manager.AttachLineExtractor(_extractor);
            Manager.SetWireSender(b => Wire.Add(Encoding.Latin1.GetString(b)));
            Poller = new PartyPoller(new ChatRouter(Router), Party, Manager, useTimer: false)
            {
                HealthNagEnabled = false,
                NowProvider = () => Clock,
            };
            Poller.ApplyParSettings(new PartySettings { ParPollOnTimer = parOnTimer });
            Poller.SetWireSender(b => Wire.Add(Encoding.Latin1.GetString(b)));
            Cleanup = new PartyDeathRosterCleanup(Router, Party, Manager, Coord, Poller, Log)
            {
                NowProvider = () => new DateTimeOffset(Clock),
            };
        }

        public void Line(string text) => _emulator.Feed(Encoding.Latin1.GetBytes(text + "\r\n"));

        // A `par` reply: the follow line when we follow, the header, the member rows,
        // our own row last, and the prompt that ends it.
        public void ParReply(string? following, params string[] rows)
        {
            if (following is not null) Line($"You are following {following}.");
            Line("The following people are in your travel party:");
            foreach (string row in rows) Line(row);
            Line(Row("MudPlay"));
            Line("[HP=100/MA=50]:");
        }

        public static string Row(string name) => $"  {name,-30} {"(Warrior)",-12}          [H:100%]  - Frontrank";
        public static string Invited(string name) => $"  {name,-30} {"(Warrior)",-12} [Invited]";

        public int Pars => Wire.Count(w => w == "par\r");
        public IEnumerable<string> Uninvites => Wire.Where(w => w.StartsWith("uninvite", StringComparison.Ordinal));
        public bool OnRoster(string given) => Party.Members.Any(m => m.Name.StartsWith(given, StringComparison.Ordinal));

        public void Dispose()
        {
            Cleanup.Dispose();
            Poller.Dispose();
            Manager.Dispose();
        }
    }

    // We lead Raijin (and Bob): both follow us.
    private static Harness Leading(bool parOnTimer = false)
    {
        Harness h = new(parOnTimer);
        h.Line("Raijin started to follow you.");
        h.Line("Bob started to follow you.");
        h.Line("Cid started to follow you.");
        return h;
    }

    [Fact]
    public void LeaderStock_DeadMemberNotListed_ParAsked_AndRosterDropsThem()
    {
        using Harness h = Leading();
        h.Line("Raijin is dead.");
        Assert.Equal(1, h.Pars);

        h.ParReply(null, Harness.Row("Bob"), Harness.Row("Cid"));   // Stock: no row for the dead

        Assert.False(h.OnRoster("Raijin"));
        Assert.True(h.OnRoster("Bob"));
        Assert.Empty(h.Uninvites);
        Assert.Contains("no longer lists Raijin", h.Cleanup.LastDeathSummary);
        Assert.Empty(h.Cleanup.AwaitingPar);
        Assert.Empty(h.Cleanup.AwaitingUninvite);
    }

    [Fact]
    public void LeaderParadigm_DeadMemberListedInvited_IsUninvited()
    {
        using Harness h = Leading();
        h.Line("Raijin is dead.");
        h.ParReply(null, Harness.Invited("Raijin"), Harness.Row("Bob"), Harness.Row("Cid"));

        Assert.Equal(["uninvite Raijin\r"], h.Uninvites);
    }

    [Fact]
    public void Uninvite_WaitsForCombatToClear()
    {
        using Harness h = Leading();
        h.Coord.AssertGate(MovementCoordinator.CombatGate);
        h.Line("Raijin is dead.");
        h.ParReply(null, Harness.Invited("Raijin"), Harness.Row("Bob"), Harness.Row("Cid"));
        Assert.Empty(h.Uninvites);
        Assert.Contains("once the room is clear", h.Cleanup.LastDeathSummary);

        h.Coord.ClearGate(MovementCoordinator.CombatGate);
        Assert.Equal(["uninvite Raijin\r"], h.Uninvites);

        // Further gate churn sends nothing more.
        h.Coord.AssertGate(MovementCoordinator.CombatGate);
        h.Coord.ClearGate(MovementCoordinator.CombatGate);
        Assert.Single(h.Uninvites);
    }

    [Fact]
    public void Follower_DeadMemberNotListed_IsDropped_AndListedInvited_IsLeft()
    {
        using Harness h = new();
        h.Line("You are now following Boss.");
        h.ParReply("Boss", Harness.Row("Boss"), Harness.Row("Raijin"), Harness.Row("Bob"));
        Assert.True(h.OnRoster("Raijin"));

        h.Line("Raijin is dead.");
        Assert.Equal(1, h.Pars);
        h.ParReply("Boss", Harness.Row("Boss"), Harness.Row("Bob"));
        Assert.False(h.OnRoster("Raijin"));

        h.Clock += TimeSpan.FromSeconds(10);
        h.Line("Bob is dead.");
        Assert.Equal(2, h.Pars);
        h.ParReply("Boss", Harness.Row("Boss"), Harness.Invited("Bob"));
        Assert.Empty(h.Uninvites);   // only the leader can uninvite
        Assert.Contains("only the leader can uninvite", h.Cleanup.LastDeathSummary);
    }

    // The game ends our follow on the leader's death with its own line.
    [Fact]
    public void Follower_LeaderDies_NoParIsAsked()
    {
        using Harness h = new();
        h.Line("You are now following Boss.");
        h.ParReply("Boss", Harness.Row("Boss"), Harness.Row("Raijin"));

        h.Line("Boss is dead.");

        Assert.Equal(0, h.Pars);
        Assert.Contains("our leader", h.Cleanup.LastDeathSummary);
    }

    [Theory]
    [InlineData("Stranger is dead.")]                                  // not ours
    [InlineData("The roc hatchling lets out a squeal, and is dead.")]  // a monster
    [InlineData("Raijin has died.")]                                   // no game line
    public void ADeathThatIsNotOurMembers_AsksNothing(string line)
    {
        using Harness h = Leading();
        h.Line(line);
        Assert.Equal(0, h.Pars);
        Assert.Null(h.Cleanup.LastDeathSummary);
    }

    // A pending recruit was invited from the start, never active: not ours to act on.
    [Fact]
    public void PendingRecruitDies_AsksNothing_AndIsNotUninvited()
    {
        using Harness h = Leading();
        h.Line("You have invited Newbie to follow you.");
        h.Line("Newbie is dead.");
        h.ParReply(null, Harness.Row("Raijin"), Harness.Invited("Newbie"));

        Assert.Equal(0, h.Pars);
        Assert.Empty(h.Uninvites);
    }

    // One `par` on its way answers every death seen before its reply.
    [Fact]
    public void ParOnItsWay_IsNotSentAgain()
    {
        using Harness h = Leading();
        h.Line("Raijin is dead.");
        h.Line("Bob is dead.");
        Assert.Equal(1, h.Pars);
        Assert.Contains("already sent", h.Cleanup.LastDeathSummary);

        h.ParReply(null, Harness.Row("Cid"));
        Assert.False(h.OnRoster("Raijin"));
        Assert.False(h.OnRoster("Bob"));

        // Answered, a later death asks again.
        h.Line("Cid is dead.");
        Assert.Equal(2, h.Pars);
    }

    // An unanswered `par` stops counting as on its way once its wait is over.
    [Fact]
    public void UnansweredPar_StopsHoldingRequestsBack()
    {
        using Harness h = Leading();
        h.Line("Raijin is dead.");
        h.Clock += PartyPoller.ParReplyWait;
        h.Line("Bob is dead.");
        Assert.Equal(2, h.Pars);
    }

    // With the timer on, its next `par` due within the window answers the death.
    [Fact]
    public void TimerParDueSoon_NoneIsSent()
    {
        using Harness h = Leading(parOnTimer: true);
        h.Line("Raijin is dead.");                    // no `par` yet: its countdown isn't known
        Assert.Equal(1, h.Pars);
        h.ParReply(null, Harness.Row("Bob"), Harness.Row("Cid"));

        h.Clock += TimeSpan.FromSeconds(1);
        h.Line("Bob is dead.");                       // the timer's next is 4 s away
        Assert.Equal(1, h.Pars);
        Assert.Contains("timer's next", h.Cleanup.LastDeathSummary);

        h.ParReply(null, Harness.Row("Cid"));          // the timer's `par` answers it
        Assert.False(h.OnRoster("Bob"));
    }

    // The master switch holds the `par`; the next reply read still settles the death.
    [Fact]
    public void MasterSwitchOff_HoldsThePar_TheNextReplySettlesIt()
    {
        using Harness h = Leading();
        h.Poller.MasterSwitchOff = () => true;
        h.Line("Raijin is dead.");
        Assert.Equal(0, h.Pars);
        Assert.Contains("no `par` could be sent", h.Cleanup.LastDeathSummary);

        h.ParReply(null, Harness.Row("Bob"), Harness.Row("Cid"));   // the user's own `par`
        Assert.False(h.OnRoster("Raijin"));
        Assert.Empty(h.Cleanup.AwaitingPar);
    }

    // The death line carries the given name; par carries the family name too.
    [Fact]
    public void GivenNameMatch_AcrossDeathLineAndParForms()
    {
        using Harness h = new();
        h.Line("Raijin started to follow you.");
        h.ParReply(null, Harness.Row("Raijin WuzHere"));

        h.Line("Raijin is dead.");
        h.ParReply(null, Harness.Invited("Raijin WuzHere"));

        Assert.Equal(["uninvite Raijin\r"], h.Uninvites);
    }

    // A death no reply settles inside the window is dropped, not acted on late.
    [Fact]
    public void ExpiredDeath_IsNotActedOn()
    {
        using Harness h = Leading();
        h.Line("Raijin is dead.");
        h.Clock += h.Cleanup.CleanupWindow + TimeSpan.FromSeconds(1);
        h.ParReply(null, Harness.Invited("Raijin"), Harness.Row("Bob"), Harness.Row("Cid"));

        Assert.Empty(h.Uninvites);
    }

    [Fact]
    public void Dispose_StopsReactingToDeath()
    {
        using Harness h = Leading();
        h.Cleanup.Dispose();
        h.Line("Raijin is dead.");
        Assert.Equal(0, h.Pars);
    }

    [Fact]
    public void Decisions_AreLoggedAtInfo()
    {
        using Harness h = Leading();
        List<LogEntry> rows = new();
        h.Log.EntryAdded += rows.Add;
        h.Line("Raijin is dead.");
        h.ParReply(null, Harness.Row("Bob"), Harness.Row("Cid"));

        Assert.Contains(rows, r => r.Source == PartyDeathRosterCleanup.LogCategory
            && r.Severity == LogSeverity.Info && r.Message.Contains("`par` sent", StringComparison.Ordinal));
        Assert.Contains(rows, r => r.Source == PartyDeathRosterCleanup.LogCategory
            && r.Severity == LogSeverity.Info && r.Message.Contains("no longer lists Raijin", StringComparison.Ordinal));
    }
}
