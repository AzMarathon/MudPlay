using System.Text;
using System.Text.Json;
using MudPlay.Game.Cash;
using MudPlay.Game.Inventory;
using MudPlay.Game.Recovery;
using MudPlay.Models.Profile;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// Another player's corpse recovered with their password: the record built from the
// recovery's lines, the gives that hand it back, and the password kept out of
// everything the client stores.
public sealed class CorpseHandoffTests
{
    private const string Password = "hunter2xyz";

    private sealed class Harness
    {
        public ProfileService Profile { get; } = new();
        public LogService Log { get; } = new();
        public CorpseHandoffManager Manager { get; }
        public List<string> Sent { get; } = new();
        public List<(string Command, Func<string, bool> MayGo)> Queue { get; } = new();
        public List<Action> Timers { get; } = new();
        public int Recalled { get; private set; }
        public bool Paradigm { get; set; } = true;
        public DateTimeOffset Now { get; set; } = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);

        public Harness()
        {
            Profile.LoadBlank();
            Profile.Current!.Name = "Fujin";
            CurrencyNaming naming = new();
            Manager = new CorpseHandoffManager(
                Profile,
                selfName: () => "Fujin",
                currentRoom: () => (new RoomRef(1, 224), "Darkwood Forest"),
                canonicalCurrency: naming.Canonicalize,
                isParadigm: () => Paradigm,
                runicName: () => naming.RunicName,
                sendPaced: (commands, _, mayGo) =>
                {
                    foreach (string c in commands) Queue.Add((c, mayGo));
                },
                recallPaced: _ => { Recalled++; Queue.Clear(); },
                scheduleAfter: (_, action) => Timers.Add(action),
                log: Log,
                now: () => Now);
        }

        // The pacer letting every queued give go.
        public void Pump()
        {
            foreach ((string command, Func<string, bool> mayGo) in Queue.ToList())
                if (mayGo(command)) Sent.Add(command);
            Queue.Clear();
        }

        public void FireTimers()
        {
            foreach (Action a in Timers.ToList()) a();
        }

        public void Recover(string owner, params string[] items)
        {
            Manager.ObserveCommand($"recover corpse {owner.ToLowerInvariant()} {Password}");
            Manager.OnLine($"You begin to pick through the corpse of {owner}...");
            Manager.NoteCoinsPicked("platinum", 51);
            Manager.NoteCoinsPicked("gold", 53);
            foreach (string item in items) Manager.OnLine($"You took {item}.");
            Manager.OnLine($"You have recovered the corpse of {owner}.");
        }
    }

    // ----- the recovery lines -------------------------------------------

    [Fact]
    public void Lines_ParseTheRecoverySequence()
    {
        Assert.Equal("Raijin", CorpseRecoveryLines.Begun("You begin to pick through the corpse of Raijin..."));
        Assert.Equal("Raijin", CorpseRecoveryLines.Completed("You have recovered the corpse of Raijin."));
        Assert.Equal("black star key", CorpseRecoveryLines.Took("You took black star key."));
        Assert.Null(CorpseRecoveryLines.Took("You took 12 damage."));
        Assert.Null(CorpseRecoveryLines.Begun("You notice corpse of Raijin here."));
    }

    [Theory]
    [InlineData("recover corpse raijin secret", true, "raijin", "secret")]
    [InlineData("RECOVER CORPSE Raijin two words", true, "Raijin", "two words")]
    [InlineData("recover corpse raijin", false, "", "")]
    [InlineData("recover raijin secret", false, "", "")]
    public void Command_ParsesNameAndPassword(string command, bool ok, string name, string password)
    {
        Assert.Equal(ok, CorpseRecoveryLines.TryParseRecoverWithPassword(command, out string n, out string p));
        Assert.Equal(name, n);
        Assert.Equal(password, p);
    }

    [Fact]
    public void Command_RedactedAndStrippedOfThePassword()
    {
        Assert.Equal("recover corpse raijin [redacted]", CorpseRecoveryLines.Redact($"recover corpse raijin {Password}"));
        Assert.Equal("recover corpse raijin", CorpseRecoveryLines.WithoutPassword($"recover corpse raijin {Password}"));
        Assert.Equal("recover corpse raijin", CorpseRecoveryLines.Redact("recover corpse raijin"));
        Assert.Equal("look", CorpseRecoveryLines.Redact("look"));
    }

    // ----- recording -----------------------------------------------------

    [Fact]
    public void FriendsCorpse_RecordsEveryItemAndTheCoins()
    {
        Harness h = new();
        h.Recover("Raijin", "thorned whip", "rope and grapple", "darkwood ring", "darkwood ring");

        CorpseHandoffRecord r = Assert.Single(h.Manager.Records);
        Assert.Equal("Raijin", r.Owner);
        Assert.Equal("Darkwood Forest", r.RoomName);
        Assert.Equal(new[] { "thorned whip", "rope and grapple", "2 darkwood ring" }, r.Items.Select(i => i.ToString()));
        Assert.Equal(51, r.Coins.Platinum);
        Assert.Equal(53, r.Coins.Gold);
        Assert.Contains(h.Log.Snapshot(), e => e.Message == "recorded 4 item(s) from Raijin's corpse and 51 platinum pieces, 53 gold crowns");
    }

    [Fact]
    public void OwnCorpse_IsNeverRecorded()
    {
        Harness h = new();
        h.Recover("Fujin", "thorned whip");
        Assert.Empty(h.Manager.Records);
    }

    [Fact]
    public void RecoveryWithoutOurCommand_IsNotRecorded()
    {
        Harness h = new();
        h.Manager.OnLine("You begin to pick through the corpse of Raijin...");
        h.Manager.OnLine("You took thorned whip.");
        h.Manager.OnLine("You have recovered the corpse of Raijin.");
        Assert.Empty(h.Manager.Records);
    }

    [Fact]
    public void ArmRunsOut_AndAnotherNamesCorpseIsNotTaken()
    {
        Harness h = new();
        h.Manager.ObserveCommand($"recover corpse raijin {Password}");
        h.Manager.OnLine("You begin to pick through the corpse of Nineteen...");
        h.Manager.OnLine("You took thorned whip.");
        h.Manager.OnLine("You have recovered the corpse of Nineteen.");
        Assert.Empty(h.Manager.Records);

        h.Manager.ObserveCommand($"recover corpse raijin {Password}");
        h.Now += CorpseHandoffManager.ArmWindow + TimeSpan.FromSeconds(1);
        h.Manager.OnLine("You begin to pick through the corpse of Raijin...");
        h.Manager.OnLine("You took thorned whip.");
        h.Manager.OnLine("You have recovered the corpse of Raijin.");
        Assert.Empty(h.Manager.Records);
    }

    // ----- the password ---------------------------------------------------

    [Fact]
    public void Password_NeverReachesTheRecordTheLogOrTheTrail()
    {
        Harness h = new();
        h.Recover("Raijin", "thorned whip");
        h.Manager.StartHandoff(h.Manager.Records[0], "Raijin");

        string stored = JsonSerializer.Serialize(h.Profile.Current);
        Assert.DoesNotContain(Password, stored);
        Assert.DoesNotContain(h.Log.Snapshot(), e => e.Message.Contains(Password));
        Assert.Contains(Password, h.Manager.SessionPasswords);

        SentCommandTrail trail = new();
        string? told = null;
        trail.TypedLineEntered = line => told = line;
        trail.ObserveTyped(Encoding.Latin1.GetBytes($"recover corpse raijin {Password}\r"));
        trail.Record($"recover corpse raijin {Password}", SentCommandTrail.Origin.Engine, "Macro", SentCommandTrail.Outcome.Sent);
        Assert.All(trail.Snapshot(), e => Assert.Equal("recover corpse raijin [redacted]", e.Text));
        Assert.Equal("recover corpse raijin [redacted]", told);

        CommandHistory history = new();
        history.Record($"recover corpse raijin {Password}");
        Assert.Equal("recover corpse raijin", Assert.Single(history.Entries));
    }

    [Fact]
    public void Password_IsMaskedByTheBugReportScrubber()
    {
        BugReportScrub.Scrubber scrubber = BugReportScrub.Build(
            [new(BugReportScrub.CorpsePasswordLabel, Password)], Array.Empty<string?>());
        Assert.DoesNotContain(Password, scrubber.Apply($"[HP=50]:recover corpse raijin {Password}"));
    }

    // ----- the gives -------------------------------------------------------

    [Fact]
    public void Plan_MergesNamesAndCountsOnParadigm()
    {
        IReadOnlyList<CorpseHandoffPlan.Give> gives = CorpseHandoffPlan.Build(
            [new("darkwood ring", 1), new("Darkwood ring", 1), new("thorned whip", 1)],
            CurrencyHoldings.Empty with { Runic = 2, Gold = 53 }, "Raijin", paradigm: true, runicName: "runic");
        Assert.Equal(new[]
        {
            "give 2 darkwood ring to Raijin",
            "give thorned whip to Raijin",
            "give 2 runic to Raijin",
            "give 53 gold to Raijin",
        }, gives.Select(g => g.Command));
    }

    [Fact]
    public void Plan_GivesOneCopyACommandOnStock()
    {
        IReadOnlyList<CorpseHandoffPlan.Give> gives = CorpseHandoffPlan.Build(
            [new("torch", 2)], CurrencyHoldings.Empty, "Bob", paradigm: false, runicName: "runic");
        Assert.Equal(new[] { "give torch to Bob", "give torch to Bob" }, gives.Select(g => g.Command));
    }

    [Fact]
    public void Handoff_ConfirmedGivesComeOffAndTheRecordGoesWhenEmpty()
    {
        Harness h = new();
        h.Recover("Raijin", "thorned whip", "darkwood ring", "darkwood ring");
        Assert.Null(h.Manager.StartHandoff(h.Manager.Records[0], "raij"));
        h.Pump();
        Assert.Equal(4, h.Sent.Count);

        h.Manager.OnItemGivenAway("darkwood ring", 2, "Raijin");
        h.Manager.OnItemGivenAway("thorned whip", 1, "Raijin");
        h.Manager.OnCoinsGivenAway("Raijin", 51 * 10_000);
        Assert.Single(h.Manager.Records);
        h.Manager.OnCoinsGivenAway("Raijin", 53 * 100);

        Assert.Empty(h.Manager.Records);
        Assert.False(h.Manager.HandoffActive);
        Assert.Contains(h.Log.Snapshot(), e => e.Message.Contains("everything handed back"));
    }

    [Fact]
    public void Handoff_RefusedAndUnansweredGivesStayListed()
    {
        Harness h = new();
        h.Recover("Raijin", "thorned whip", "rope and grapple");
        h.Manager.StartHandoff(h.Manager.Records[0], "Raijin");
        h.Pump();

        h.Manager.OnGiveRefused("Raijin");               // the oldest give: thorned whip
        h.Manager.OnItemGivenAway("rope and grapple", 1, "Raijin");
        h.Manager.OnCoinsGivenAway("Raijin", 51 * 10_000);
        h.FireTimers();                                   // the gold give was never answered

        CorpseHandoffRecord r = Assert.Single(h.Manager.Records);
        Assert.Equal(new[] { "thorned whip" }, r.Items.Select(i => i.Name));
        Assert.Equal(0, r.Coins.Platinum);
        Assert.Equal(53, r.Coins.Gold);
        Assert.Contains("1 refused", r.Note);
        Assert.Contains("1 not answered", r.Note);
        Assert.False(h.Manager.HandoffActive);
    }

    [Fact]
    public void Handoff_SomeoneElsesGiveLineIsNotTaken()
    {
        Harness h = new();
        h.Recover("Raijin", "thorned whip");
        h.Manager.StartHandoff(h.Manager.Records[0], "Raijin");
        h.Pump();
        h.Manager.OnItemGivenAway("thorned whip", 1, "Nineteen");
        Assert.Contains(h.Manager.Records[0].Items, i => i.Name == "thorned whip");
    }

    [Fact]
    public void Clear_RemovesTheRecord()
    {
        Harness h = new();
        h.Recover("Raijin", "thorned whip");
        h.Manager.Clear(h.Manager.Records[0]);
        Assert.Empty(h.Manager.Records);
    }
}
