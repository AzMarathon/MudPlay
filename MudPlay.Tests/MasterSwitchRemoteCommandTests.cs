using System.Text;
using System.Text.Json;
using MudPlay.Game;
using MudPlay.Game.Remote;
using MudPlay.Models.GameData;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using Xunit;

namespace MudPlay.Tests;

// The master switch and remote commands: with it off nothing in the catalog is
// obeyed or answered except @auto-all, which also has to engage the switch when
// every toggle is already off and fall back to the base modes when switched on
// with nothing remembered (user, 2026-10-09).
public sealed class MasterSwitchRemoteCommandTests
{
    private static readonly DateTime Now = new(2026, 10, 9, 0, 0, 0, DateTimeKind.Utc);
    private const string Sender = "Tank";

    private sealed class Setup : IDisposable
    {
        public RemoteCommandManager Engine { get; }
        public AutoModeRemoteHandler Handler { get; }
        public AutoModeController Controller { get; }
        public ProfileService Profile { get; }
        public List<string> Invoked { get; } = new();

        public Setup()
        {
            MessageRouter router = new();
            DefaultPatterns.Seed(router);
            ChatRouter chat = new(router);
            PartyState party = new();
            party.Members.Add(new PartyMember { Name = Sender });
            PlayerDatabase players = new();
            players.RecordObservation(Sender, null, null, null, null, null, null, Now);
            players.EditCustomization(Sender, new PlayerCustomization(RemoteControls: ~PlayerRemoteControls.None));
            Profile = new ProfileService();
            Profile.LoadBlank();
            Controller = new AutoModeController(Profile);
            Engine = new RemoteCommandManager(chat, party, players)
            {
                BlockedByMasterSwitch = what => Controller.Blocks("Remote commands", what),
                // Lives known and plentiful, so @suicide-shaped commands reach a handler.
                LivesProvider = () => 9,
            };

            // A recording handler for every catalog command; the real @auto-* /
            // @settings handlers then replace theirs.
            foreach (string command in RemoteCommandCatalog.Map.Keys)
                Engine.RegisterHandler(command, RemoteCommandCatalog.Map[command],
                    ctx => { Invoked.Add(command); ctx.Reply("done"); });
            Handler = new AutoModeRemoteHandler(Engine, Profile, Controller);
        }

        public void WriteGeneral(GeneralSettings general)
        {
            Profile.Current!.Settings ??= new();
            Profile.Current.Settings["General"] = JsonSerializer.SerializeToElement(general);
        }

        public AutoActionDefaults Mode =>
            Profile.Current!.Settings is { } settings && settings.TryGetValue("General", out JsonElement json)
                ? JsonSerializer.Deserialize<GeneralSettings>(json.GetRawText())!.AutoMode
                : new AutoActionDefaults();

        public string LastReply => Encoding.Latin1.GetString(Engine.LastSentForTests[^1]);

        public void Dispose() => Handler.Dispose();
    }

    private static ChatLogEntry Telepath(string msg) =>
        new(Now, ChatChannel.TelepathIncoming, Sender, msg, $"{Sender} telepaths: {msg}");

    private static AutoActionDefaults AllOff() => new()
    {
        AutoCombat = false, AutoNuke = false, AutoHeal = false, AutoRest = false,
        AutoBless = false, AutoLight = false, AutoGetItems = false,
        AutoGetCash = false, AutoSneak = false, AutoHide = false, AutoSearch = false,
    };

    // Every catalog command but the switch's own. @auto-all is driven by the real
    // handler in the tests below.
    public static IEnumerable<object[]> CatalogCommands() =>
        RemoteCommandCatalog.Map.Keys
            .Where(c => !c.Equals(RemoteCommandManager.MasterSwitchCommand, StringComparison.OrdinalIgnoreCase))
            .Select(c => new object[] { c });

    [Theory]
    [MemberData(nameof(CatalogCommands))]
    public void SwitchOff_CommandIsNeitherRunNorAnswered(string command)
    {
        using Setup s = new();
        s.Controller.TurnOff("test");
        AutoActionDefaults before = s.Mode;

        s.Engine.DispatchForTests(Telepath(command));

        Assert.Empty(s.Invoked);
        Assert.Empty(s.Engine.LastSentForTests);
        Assert.True(before.SameAs(s.Mode));
        Assert.True(s.Controller.KillSwitchEngaged);
        Assert.Equal(1, s.Controller.SkippedSinceOff["Remote commands"]);
    }

    [Theory]
    [MemberData(nameof(CatalogCommands))]
    public void SwitchOn_CommandIsHandledAsBefore(string command)
    {
        using Setup s = new();

        s.Engine.DispatchForTests(Telepath(command));

        // Handled: it reached a handler (the recorder, or the real @auto-* /
        // @settings one, which answers) and nothing was counted as skipped.
        Assert.True(s.Invoked.Contains(command) || s.Engine.LastSentForTests.Count > 0,
            $"{command} was not handled with the switch on");
        Assert.Empty(s.Controller.SkippedSinceOff);
    }

    [Fact]
    public void SwitchOff_RelayBackIsDropped()
    {
        using Setup s = new();
        s.Controller.TurnOff("test");

        s.Engine.DispatchForTests(Telepath("&@invite"));
        s.Engine.DispatchForTests(Telepath("&@auto-all on"));

        Assert.Empty(s.Engine.LastSentForTests);
        Assert.True(s.Controller.KillSwitchEngaged);
    }

    [Fact]
    public void SwitchOff_OrdinaryChatStartingWithAt_IsNotCountedAsACommand()
    {
        using Setup s = new();
        s.Controller.TurnOff("test");

        s.Engine.DispatchForTests(Telepath("@because I said so"));

        Assert.Empty(s.Controller.SkippedSinceOff);
        Assert.Empty(s.Engine.LastSentForTests);
    }

    [Fact]
    public void AutoAllOff_WithEveryToggleAlreadyOff_EngagesTheSwitch()
    {
        using Setup s = new();
        s.WriteGeneral(new GeneralSettings { AutoMode = AllOff() });

        s.Engine.DispatchForTests(Telepath("@auto-all off"));

        Assert.True(s.Controller.KillSwitchEngaged);
        Assert.Contains("@auto-all: off", s.LastReply);
    }

    [Fact]
    public void AutoAllOn_NothingRemembered_SwitchesOnTheBaseModes()
    {
        using Setup s = new();
        AutoActionDefaults baseModes = AllOff();
        baseModes.AutoCombat = true;
        baseModes.AutoRest = true;
        s.WriteGeneral(new GeneralSettings { AutoMode = AllOff(), AutoModeBase = baseModes });

        s.Engine.DispatchForTests(Telepath("@auto-all on"));

        Assert.False(s.Controller.KillSwitchEngaged);
        Assert.True(s.Mode.AutoCombat);
        Assert.True(s.Mode.AutoRest);
        Assert.False(s.Mode.AutoNuke);
        Assert.Contains("@auto-all: on", s.LastReply);
    }

    [Theory]
    [InlineData("@auto-all on")]
    [InlineData("@auto-all")]
    public void SwitchOff_AutoAllStillWorks_AndRestoresTheToggles(string command)
    {
        using Setup s = new();
        AutoActionDefaults mode = AllOff();
        mode.AutoCombat = true;
        s.WriteGeneral(new GeneralSettings { AutoMode = mode });
        s.Engine.DispatchForTests(Telepath("@auto-all off"));
        Assert.True(s.Controller.KillSwitchEngaged);
        Assert.False(s.Mode.AutoCombat);

        s.Engine.DispatchForTests(Telepath(command));

        Assert.False(s.Controller.KillSwitchEngaged);
        Assert.True(s.Mode.AutoCombat);
        Assert.Contains("@auto-all: on", s.LastReply);
    }

    [Fact]
    public void SwitchOff_AutoAllFromSomeoneWithoutTheGrant_IsStillDenied()
    {
        using Setup s = new();
        s.Controller.TurnOff("test");
        ChatLogEntry stranger = new(Now, ChatChannel.TelepathIncoming, "Stranger", "@auto-all on",
            "Stranger telepaths: @auto-all on");

        s.Engine.DispatchForTests(stranger);

        Assert.True(s.Controller.KillSwitchEngaged);
        // And unanswered: a denial would make @auto-all the one command that tells
        // a stranger the client is there.
        Assert.Empty(s.Engine.LastSentForTests);
    }

    [Fact]
    public void SwitchOn_AutoAllFromSomeoneWithoutTheGrant_GetsTheUsualDenial()
    {
        using Setup s = new();
        ChatLogEntry stranger = new(Now, ChatChannel.TelepathIncoming, "Stranger", "@auto-all off",
            "Stranger telepaths: @auto-all off");

        s.Engine.DispatchForTests(stranger);

        Assert.False(s.Controller.KillSwitchEngaged);
        Assert.Single(s.Engine.LastSentForTests);
    }
}
