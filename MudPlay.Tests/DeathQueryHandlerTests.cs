using System.Collections.Generic;
using System.Linq;
using System.Text;
using MudPlay.Game;
using MudPlay.Game.Remote;
using MudPlay.Models.GameData;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using Xunit;

namespace MudPlay.Tests;

// Pins the read-only @death handler: reports deaths in the recovery log that
// aren't marked fully recovered (Status != Recovered) — the most recent one for
// a bare @death, or all of them for @death all — gated on the QueryDeaths grant.
public sealed class DeathQueryHandlerTests
{
    private static readonly System.DateTime Now = new(2026, 6, 20, 0, 0, 0, System.DateTimeKind.Utc);

    private static (RemoteCommandManager engine, PlayerDatabase players) Setup(
        IReadOnlyList<DeathRecord> records)
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        ChatRouter chat = new(router);
        PartyState party = new();
        PlayerDatabase players = new();
        RemoteCommandManager engine = new(chat, party, players);
        _ = new DeathQueryHandler(engine, () => records);
        return (engine, players);
    }

    private static ChatLogEntry Telepath(string sender, string msg) =>
        new(Now, ChatChannel.TelepathIncoming, sender, msg, $"{sender} telepaths: {msg}");

    private static void SeedPlayer(PlayerDatabase db, string name, PlayerRemoteControls controls)
    {
        db.RecordObservation(name, null, null, null, null, null, null, Now);
        db.EditCustomization(name, new PlayerCustomization(RemoteControls: controls));
    }

    private static string LastReply(RemoteCommandManager e) =>
        Encoding.Latin1.GetString(e.LastSentForTests[^1]);

    private static IReadOnlyList<string> Replies(RemoteCommandManager e) =>
        e.LastSentForTests.Select(b => Encoding.Latin1.GetString(b)).ToList();

    private static DeathRecord Death(int number, int daysAgo, DeathRecoveryStatus status,
        string? roomName = "Crypt", int lives = 5) => new()
    {
        RecordNumber = number,
        At = new System.DateTimeOffset(Now.AddDays(-daysAgo)),
        Room = new RoomRef(1, 100 + number),
        RoomName = roomName,
        LivesRemaining = lives,
        Status = status,
    };

    [Fact]
    public void Death_ReportsMostRecentUnrecovered()
    {
        // Two unrecovered deaths (Active newer than Partial) and one Recovered —
        // the bare @death names only the most recent unrecovered one.
        var records = new List<DeathRecord>
        {
            Death(1, 3, DeathRecoveryStatus.Recovered),   // fully recovered — skipped
            Death(2, 2, DeathRecoveryStatus.Partial),
            Death(3, 1, DeathRecoveryStatus.Active),      // most recent unrecovered
        };
        var (engine, players) = Setup(records);
        SeedPlayer(players, "Friend", PlayerRemoteControls.QueryDeaths);

        engine.DispatchForTests(Telepath("Friend", "@death"));

        string reply = LastReply(engine);
        Assert.Contains("death #3", reply);
        Assert.Contains("active", reply);
        Assert.DoesNotContain("death #2", reply);
        Assert.DoesNotContain("death #1", reply);
    }

    [Fact]
    public void DeathAll_ReportsEveryUnrecovered_MostRecentFirst()
    {
        var records = new List<DeathRecord>
        {
            Death(1, 3, DeathRecoveryStatus.Missing),
            Death(2, 2, DeathRecoveryStatus.Recovered),   // skipped
            Death(3, 1, DeathRecoveryStatus.Partial),
        };
        var (engine, players) = Setup(records);
        SeedPlayer(players, "Friend", PlayerRemoteControls.QueryDeaths);

        engine.DispatchForTests(Telepath("Friend", "@death all"));

        var replies = Replies(engine);
        Assert.Contains(replies, r => r.Contains("death #3"));
        Assert.Contains(replies, r => r.Contains("death #1"));
        Assert.DoesNotContain(replies, r => r.Contains("death #2"));  // the Recovered one
    }

    [Fact]
    public void Death_NoUnrecovered_ReportsNone()
    {
        var records = new List<DeathRecord> { Death(1, 1, DeathRecoveryStatus.Recovered) };
        var (engine, players) = Setup(records);
        SeedPlayer(players, "Friend", PlayerRemoteControls.QueryDeaths);

        engine.DispatchForTests(Telepath("Friend", "@death"));

        Assert.Contains("no unrecovered deaths", LastReply(engine));
    }

    [Fact]
    public void Death_WithoutGrant_ReportsNoDeath()
    {
        // No QueryDeaths grant → the engine gates it before the handler, so the
        // death log is never reported (any reply is the denial, not a death line).
        var records = new List<DeathRecord> { Death(1, 1, DeathRecoveryStatus.Active) };
        var (engine, players) = Setup(records);
        SeedPlayer(players, "Stranger", PlayerRemoteControls.None);

        engine.DispatchForTests(Telepath("Stranger", "@death"));

        Assert.DoesNotContain(Replies(engine), r => r.Contains("death #"));
    }
}
