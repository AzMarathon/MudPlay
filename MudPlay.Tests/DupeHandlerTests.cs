using System.Text;
using MudPlay.Game;
using MudPlay.Game.Remote;
using MudPlay.Models.GameData;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// @dupe <player> — copies the SENDER's permission set onto a known player. Elevated
// Commands senders only, and accepted over telepath and gangpath only; say, gossip
// (which also carries auctions), broadcast and yell must never reach it, and the
// local API must not be able to drive it. The copy is additive and never touches
// the target's other settings.
public sealed class DupeHandlerTests
{
    private static readonly DateTime Now = new(2026, 9, 21, 0, 0, 0, DateTimeKind.Utc);

    private const PlayerRemoteControls Elevated = PlayerRemoteControls.SysopCommands;

    private static LineExtractor.EmittedLine Line(string text) =>
        new(text, new CellAttributes[text.Length], DateTimeOffset.UnixEpoch, IsPromptLine: false);

    private static (MessageRouter router, RemoteCommandManager engine, PlayerDatabase players) Setup(
        string? localCharacter = null)
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        ChatRouter chat = new(router);
        PlayerDatabase players = new();
        RemoteCommandManager engine = new(chat, new PartyState(), players);
        _ = new DupeHandler(engine, players, () => localCharacter);
        return (router, engine, players);
    }

    private static void SeedPlayer(PlayerDatabase db, string name, PlayerRemoteControls controls)
    {
        db.RecordObservation(name, null, null, null, null, null, null, Now);
        db.EditCustomization(name, new PlayerCustomization(RemoteControls: controls));
    }

    private static PlayerRemoteControls Controls(PlayerDatabase db, string name) =>
        db.Find(name)!.RemoteControls;

    private static List<string> Replies(RemoteCommandManager engine) =>
        engine.LastSentForTests.Select(b => Encoding.Latin1.GetString(b).TrimEnd('\r')).ToList();

    private static string Payload(string wire)
    {
        int open = wire.IndexOf('{');
        int close = wire.LastIndexOf('}');
        return open >= 0 && close > open ? wire[(open + 1)..close] : wire;
    }

    // ===== The feature =====

    [Fact]
    public void Telepath_CopiesSenderPermissionsOntoTarget()
    {
        var (router, engine, players) = Setup();
        SeedPlayer(players, "Reveal", PlayerRemoteControls.All);
        SeedPlayer(players, "Moron", PlayerRemoteControls.None);

        router.Dispatch(Line("Reveal telepaths: @dupe Moron"));

        Assert.Equal(PlayerRemoteControls.All, Controls(players, "Moron"));
        string reply = Assert.Single(Replies(engine));
        Assert.StartsWith("/Reveal ", reply);
        Assert.Equal("Moron now has your permissions", Payload(reply));
    }

    [Fact]
    public void Gangpath_CopiesSenderPermissionsOntoTarget_AndRepliesOnGangpath()
    {
        var (router, engine, players) = Setup();
        SeedPlayer(players, "Reveal", PlayerRemoteControls.All);
        SeedPlayer(players, "Moron", PlayerRemoteControls.None);

        router.Dispatch(Line("Reveal gangpaths: @dupe Moron"));

        Assert.Equal(PlayerRemoteControls.All, Controls(players, "Moron"));
        string reply = Assert.Single(Replies(engine));
        Assert.StartsWith("bg ", reply);
    }

    [Fact]
    public void Copy_IsAdditive_TargetKeepsWhatItAlreadyHas()
    {
        var (router, _, players) = Setup();
        SeedPlayer(players, "Reveal", PlayerRemoteControls.QueryVersion | Elevated);
        SeedPlayer(players, "Moron", PlayerRemoteControls.ExecuteCommands);

        router.Dispatch(Line("Reveal telepaths: @dupe Moron"));

        Assert.Equal(
            PlayerRemoteControls.ExecuteCommands | PlayerRemoteControls.QueryVersion | Elevated,
            Controls(players, "Moron"));
    }

    [Fact]
    public void Copy_MovesOnlyThePermissionGrid_NotTheOtherSettings()
    {
        var (router, _, players) = Setup();
        SeedPlayer(players, "Reveal", PlayerRemoteControls.All);
        SeedPlayer(players, "Moron", PlayerRemoteControls.None);
        players.EditCustomization("Moron", new PlayerCustomization(
            InviteToPartyIfSeen: true, DontAutoDelete: true, Notes: "alt of Reveal"));

        router.Dispatch(Line("Reveal telepaths: @dupe Moron"));

        PlayerRecord moron = players.Find("Moron")!;
        Assert.Equal(PlayerRemoteControls.All, moron.RemoteControls);
        Assert.True(moron.InviteToPartyIfSeen);
        Assert.True(moron.DontAutoDelete);
        Assert.Equal("alt of Reveal", moron.Notes);
    }

    [Fact]
    public void TargetAlreadyHoldsEverything_RepliesAndChangesNothing()
    {
        var (router, engine, players) = Setup();
        SeedPlayer(players, "Reveal", PlayerRemoteControls.QueryVersion | Elevated);
        SeedPlayer(players, "Moron", PlayerRemoteControls.All);

        router.Dispatch(Line("Reveal telepaths: @dupe Moron"));

        Assert.Equal(PlayerRemoteControls.All, Controls(players, "Moron"));
        Assert.Equal("Moron already has all your permissions", Payload(Assert.Single(Replies(engine))));
    }

    // ===== Channels: telepath and gangpath only =====

    [Fact]
    public void Say_IsIgnored_NoChange_NoReply()
    {
        var (_, engine, players) = Setup();
        SeedPlayer(players, "Reveal", PlayerRemoteControls.All);
        SeedPlayer(players, "Moron", PlayerRemoteControls.None);

        engine.DispatchForTests(new ChatLogEntry(
            Now, ChatChannel.Local, "Reveal", "@dupe Moron", "Reveal says: @dupe Moron"));

        Assert.Equal(PlayerRemoteControls.None, Controls(players, "Moron"));
        Assert.Empty(engine.LastSentForTests);
    }

    [Fact]
    public void Gossip_IsIgnored()
    {
        var (router, engine, players) = Setup();
        SeedPlayer(players, "Reveal", PlayerRemoteControls.All);
        SeedPlayer(players, "Moron", PlayerRemoteControls.None);

        router.Dispatch(Line("Reveal gossips: @dupe Moron"));

        Assert.Equal(PlayerRemoteControls.None, Controls(players, "Moron"));
        Assert.Empty(engine.LastSentForTests);
    }

    [Fact]
    public void Auction_IsIgnored()
    {
        // Auctions share gossip's shape and ride the Gossip channel.
        var (router, engine, players) = Setup();
        SeedPlayer(players, "Reveal", PlayerRemoteControls.All);
        SeedPlayer(players, "Moron", PlayerRemoteControls.None);

        router.Dispatch(Line("Reveal auctions: @dupe Moron"));

        Assert.Equal(PlayerRemoteControls.None, Controls(players, "Moron"));
        Assert.Empty(engine.LastSentForTests);
    }

    [Fact]
    public void Broadcast_IsIgnored()
    {
        var (_, engine, players) = Setup();
        SeedPlayer(players, "Reveal", PlayerRemoteControls.All);
        SeedPlayer(players, "Moron", PlayerRemoteControls.None);

        engine.DispatchForTests(new ChatLogEntry(
            Now, ChatChannel.Broadcast, "Reveal", "@dupe Moron", "BROADCAST: @dupe Moron"));

        Assert.Equal(PlayerRemoteControls.None, Controls(players, "Moron"));
        Assert.Empty(engine.LastSentForTests);
    }

    [Fact]
    public void Yell_IsIgnored()
    {
        var (_, engine, players) = Setup();
        SeedPlayer(players, "Reveal", PlayerRemoteControls.All);
        SeedPlayer(players, "Moron", PlayerRemoteControls.None);

        engine.DispatchForTests(new ChatLogEntry(
            Now, ChatChannel.Yell, "Reveal", "@dupe Moron", "Reveal yells: @dupe Moron"));

        Assert.Equal(PlayerRemoteControls.None, Controls(players, "Moron"));
        Assert.Empty(engine.LastSentForTests);
    }

    [Fact]
    public void LocalApi_CannotDrive_Dupe()
    {
        // TryInvokeLocal stamps every invocation as a telepath, so without an
        // explicit refusal a channel check inside the handler would wave it through.
        var (_, engine, players) = Setup();
        SeedPlayer(players, "Moron", PlayerRemoteControls.None);
        List<string> replies = [];

        var result = engine.TryInvokeLocal("@dupe", ["Moron"], replies.Add);

        Assert.Equal(RemoteCommandManager.LocalInvokeResult.PathChannelOnly, result);
        Assert.Empty(replies);
        Assert.Equal(PlayerRemoteControls.None, Controls(players, "Moron"));
    }

    [Fact]
    public void Catalog_MarksDupeAsPathChannelOnly_AndNothingElse()
    {
        Assert.True(RemoteCommandCatalog.IsPathChannelOnly("@dupe"));
        Assert.True(RemoteCommandCatalog.IsPathChannelOnly("@DUPE"));
        Assert.False(RemoteCommandCatalog.IsPathChannelOnly("@where"));
        Assert.False(RemoteCommandCatalog.IsPathChannelOnly(""));
    }

    // ===== Who may use it =====

    [Fact]
    public void SenderWithoutElevatedCommands_IsDenied_EvenWithEveryOtherPermission()
    {
        var (router, _, players) = Setup();
        SeedPlayer(players, "Reveal", PlayerRemoteControls.All & ~Elevated);
        SeedPlayer(players, "Moron", PlayerRemoteControls.None);

        router.Dispatch(Line("Reveal telepaths: @dupe Moron"));

        Assert.Equal(PlayerRemoteControls.None, Controls(players, "Moron"));
    }

    [Fact]
    public void UnknownSender_IsDenied()
    {
        var (router, _, players) = Setup();
        SeedPlayer(players, "Moron", PlayerRemoteControls.None);

        router.Dispatch(Line("Stranger telepaths: @dupe Moron"));

        Assert.Equal(PlayerRemoteControls.None, Controls(players, "Moron"));
    }

    [Fact]
    public void Catalog_GatesDupeBehindElevatedCommands()
    {
        // Rewriting other players' permissions must sit behind the same tier as
        // @suicide, not behind an ordinary grant.
        Assert.True(RemoteCommandCatalog.TryGetCategory("@dupe", out PlayerRemoteControls category));
        Assert.Equal(PlayerRemoteControls.SysopCommands, category);
    }

    [Fact]
    public void ExecuteCommandsAlone_DoesNotAllowDupe()
    {
        // ExecuteCommands is the next-highest tier (@do); it must not be enough.
        var (router, _, players) = Setup();
        SeedPlayer(players, "Reveal", PlayerRemoteControls.ExecuteCommands);
        SeedPlayer(players, "Moron", PlayerRemoteControls.None);

        router.Dispatch(Line("Reveal telepaths: @dupe Moron"));

        Assert.Equal(PlayerRemoteControls.None, Controls(players, "Moron"));
    }

    // ===== Refusals =====

    [Fact]
    public void UnknownTarget_IsRefused_AndNoRecordIsCreated()
    {
        var (router, engine, players) = Setup();
        SeedPlayer(players, "Reveal", PlayerRemoteControls.All);

        router.Dispatch(Line("Reveal telepaths: @dupe Nobody"));

        Assert.Null(players.Find("Nobody"));
        Assert.Equal("unknown player Nobody", Payload(Assert.Single(Replies(engine))));
    }

    [Fact]
    public void LocalCharacter_IsRefused()
    {
        var (router, engine, players) = Setup(localCharacter: "Kyau");
        SeedPlayer(players, "Reveal", PlayerRemoteControls.All);
        SeedPlayer(players, "Kyau", PlayerRemoteControls.None);

        router.Dispatch(Line("Reveal telepaths: @dupe Kyau"));

        Assert.Equal(PlayerRemoteControls.None, Controls(players, "Kyau"));
        Assert.Equal("can't change my own permissions", Payload(Assert.Single(Replies(engine))));
    }

    [Fact]
    public void DupingYourself_IsRefused()
    {
        var (router, engine, players) = Setup();
        SeedPlayer(players, "Reveal", PlayerRemoteControls.All);

        router.Dispatch(Line("Reveal telepaths: @dupe Reveal"));

        Assert.Equal("you already have your own permissions", Payload(Assert.Single(Replies(engine))));
    }

    [Theory]
    [InlineData("@dupe")]
    [InlineData("@dupe Moron Extra")]
    public void WrongArgumentCount_RepliesWithUsage_AndChangesNothing(string message)
    {
        var (router, engine, players) = Setup();
        SeedPlayer(players, "Reveal", PlayerRemoteControls.All);
        SeedPlayer(players, "Moron", PlayerRemoteControls.None);

        router.Dispatch(Line($"Reveal telepaths: {message}"));

        Assert.Equal(PlayerRemoteControls.None, Controls(players, "Moron"));
        Assert.Equal("usage: @dupe <player>", Payload(Assert.Single(Replies(engine))));
    }

    [Fact]
    public void WarnOnDenialOff_SuppressesFailureReplies()
    {
        var (router, engine, players) = Setup();
        engine.WarnOnDenial = false;
        SeedPlayer(players, "Reveal", PlayerRemoteControls.All);

        router.Dispatch(Line("Reveal telepaths: @dupe Nobody"));

        Assert.Empty(engine.LastSentForTests);
    }
}
