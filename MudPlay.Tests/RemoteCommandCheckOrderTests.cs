using System.Text;
using MudPlay.Game;
using MudPlay.Game.Remote;
using MudPlay.Models.GameData;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using Xunit;

namespace MudPlay.Tests;

// The permission check comes before every check that answers with a reason of
// its own, so a sender without the permission learns nothing but the generic
// failure; and the suicide rules match the exact command words.
public sealed class RemoteCommandCheckOrderTests
{
    private static readonly DateTime Now = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

    private static (RemoteCommandManager engine, PlayerDatabase players) Engine()
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        PlayerDatabase players = new();
        RemoteCommandManager engine = new(new ChatRouter(router), new PartyState(), players);
        return (engine, players);
    }

    private static ChatLogEntry Telepath(string sender, string msg) =>
        new(Now, ChatChannel.TelepathIncoming, sender, msg, $"{sender} telepaths: {msg}");

    private static void Seed(PlayerDatabase db, string name, PlayerRemoteControls controls)
    {
        db.RecordObservation(name, @class: null, race: null, alignment: null,
            title: null, gang: null, role: null, nowUtc: Now);
        db.EditCustomization(name, new PlayerCustomization(RemoteControls: controls));
    }

    private static List<string> Sent(RemoteCommandManager engine) =>
        engine.LastSentForTests.Select(b => Encoding.Latin1.GetString(b)).ToList();

    [Fact]
    public void Suicide_FromASenderWithoutThePermission_GetsOnlyTheGenericFailure()
    {
        var (engine, players) = Engine();
        Seed(players, "Stranger", PlayerRemoteControls.All & ~PlayerRemoteControls.SysopCommands);
        engine.LivesProvider = () => 2;
        engine.MaxSuicideLivesThreshold = 5;
        engine.RegisterHandler("@suicide", PlayerRemoteControls.SysopCommands, _ => { });

        engine.DispatchForTests(Telepath("Stranger", "@suicide"));

        Assert.Equal("/Stranger {command invalid or not allowed}\r", Assert.Single(Sent(engine)));
    }

    [Fact]
    public void Suicide_FromAPlayerNeverSeen_NeverNamesTheLivesCount()
    {
        var (engine, _) = Engine();
        engine.LivesProvider = () => 2;
        engine.RegisterHandler("@suicide", PlayerRemoteControls.SysopCommands, _ => { });

        engine.DispatchForTests(Telepath("Nobody", "@suicide"));

        string reply = Assert.Single(Sent(engine));
        Assert.DoesNotContain("lives", reply);
        Assert.DoesNotContain("2", reply);
    }

    [Fact]
    public void Suicide_WithoutThePermission_IsSilentWhenWarnIsOff()
    {
        var (engine, _) = Engine();
        engine.WarnOnDenial = false;
        engine.LivesProvider = () => 2;
        engine.RegisterHandler("@suicide", PlayerRemoteControls.SysopCommands, _ => { });

        engine.DispatchForTests(Telepath("Nobody", "@suicide"));

        Assert.Empty(engine.LastSentForTests);
    }

    [Fact]
    public void DoSuicide_FromASenderWithoutThePermission_GetsOnlyTheGenericFailure()
    {
        var (engine, players) = Engine();
        Seed(players, "Stranger", PlayerRemoteControls.All & ~PlayerRemoteControls.ExecuteCommands);
        bool fired = false;
        engine.RegisterHandler("@do", PlayerRemoteControls.ExecuteCommands, _ => fired = true);

        engine.DispatchForTests(Telepath("Stranger", "@do suicide"));

        Assert.False(fired);
        Assert.Equal("/Stranger {command invalid or not allowed}\r", Assert.Single(Sent(engine)));
    }

    // "@suicidal" in gang chat is a word, not a command: it is neither answered
    // nor held to the lives rule.
    [Theory]
    [InlineData("@suicidal")]
    [InlineData("@suicide-watch tonight")]
    public void AWordThatOnlyContainsSuicide_IsNotACommand(string message)
    {
        var (engine, players) = Engine();
        Seed(players, "Trusted", PlayerRemoteControls.All);
        engine.LivesProvider = () => 1;
        engine.RegisterHandler("@suicide", PlayerRemoteControls.SysopCommands, _ => { });

        engine.DispatchForTests(Telepath("Trusted", message));

        Assert.Empty(engine.LastSentForTests);
    }

    // The lives rule is @suicide's own: another command is not refused for having
    // the word among what it was sent.
    [Fact]
    public void AnotherCommandNamingSuicide_IsNotHeldToTheLivesRule()
    {
        var (engine, players) = Engine();
        Seed(players, "Trusted", PlayerRemoteControls.All);
        engine.LivesProvider = () => 1;
        engine.MaxSuicideLivesThreshold = 5;
        IReadOnlyList<string>? got = null;
        engine.RegisterHandler("@have", PlayerRemoteControls.QueryInventory, ctx => got = ctx.Args);

        engine.DispatchForTests(Telepath("Trusted", "@have suicide note"));

        Assert.Equal(new[] { "suicide", "note" }, got);
        Assert.Empty(engine.LastSentForTests);
    }

    // @do sends its words to the game, so only the game's own word is refused.
    [Fact]
    public void Do_WithAWordThatOnlyContainsSuicide_IsSent()
    {
        var (engine, players) = Engine();
        Seed(players, "Trusted", PlayerRemoteControls.All);
        bool fired = false;
        engine.RegisterHandler("@do", PlayerRemoteControls.ExecuteCommands, _ => fired = true);

        engine.DispatchForTests(Telepath("Trusted", "@do say suicidal"));

        Assert.True(fired);
    }

    [Fact]
    public void Do_WithTheWordSuicide_InAnyCase_IsRefusedForAGrantedSender()
    {
        var (engine, players) = Engine();
        Seed(players, "Trusted", PlayerRemoteControls.All);
        bool fired = false;
        engine.RegisterHandler("@do", PlayerRemoteControls.ExecuteCommands, _ => fired = true);

        engine.DispatchForTests(Telepath("Trusted", "@do SUICIDE"));

        Assert.False(fired);
        Assert.Equal("/Trusted {@do suicide is not allowed, use @suicide}\r", Assert.Single(Sent(engine)));
    }
}
