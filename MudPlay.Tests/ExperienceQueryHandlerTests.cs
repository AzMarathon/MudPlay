using System.Text;
using MudPlay.Game;
using MudPlay.Game.Combat;
using MudPlay.Game.Remote;
using MudPlay.Models.GameData;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using Xunit;

namespace MudPlay.Tests;

/// <summary>
/// Pins the read-only <see cref="ExperienceQueryHandler"/> replies
/// (<c>@exp</c> / <c>@level</c>): progression numbers come from the
/// <see cref="PlayerStats"/> snapshot, the session rate + earned total
/// from a clock-driven <see cref="SessionActivityTracker"/>. Both gate on
/// "not parsed yet" and reply on the sender's channel — never touch the wire.
/// </summary>
public sealed class ExperienceQueryHandlerTests
{
    private static readonly DateTime Now = new(2026, 6, 20, 0, 0, 0, DateTimeKind.Utc);

    private sealed class Clock
    {
        public DateTimeOffset NowUtc = new(2026, 6, 20, 0, 0, 0, TimeSpan.Zero);
        public void Advance(double minutes) => NowUtc += TimeSpan.FromMinutes(minutes);
    }

    private static (RemoteCommandManager engine, PlayerStats stats, SessionActivityTracker activity, Clock clock, PlayerDatabase players)
        Setup()
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        ChatRouter chat = new(router);
        PartyState party = new();
        PlayerDatabase players = new();
        RemoteCommandManager engine = new(chat, party, players);
        PlayerStats stats = new();
        Clock clock = new();
        SessionActivityTracker activity = new(() => clock.NowUtc);
        _ = new ExperienceQueryHandler(engine, stats, activity);
        return (engine, stats, activity, clock, players);
    }

    private static ChatLogEntry Telepath(string sender, string msg) =>
        new(Now, ChatChannel.TelepathIncoming, sender, msg, $"{sender} telepaths: {msg}");

    private static void SeedPlayer(PlayerDatabase db, string name, PlayerRemoteControls controls)
    {
        db.RecordObservation(name, null, null, null, null, null, null, Now);
        db.EditCustomization(name, new PlayerCustomization(RemoteControls: controls));
    }

    private static List<string> Replies(RemoteCommandManager engine) =>
        engine.LastSentForTests
            .Select(b => Encoding.Latin1.GetString(b))
            .Select(StripWire)
            .ToList();

    private static string StripWire(string wire)
    {
        string s = wire.TrimEnd('\r');
        int open = s.IndexOf('{');
        int close = s.LastIndexOf('}');
        return open >= 0 && close > open ? s[(open + 1)..close] : s;
    }

    // ----- @level ------------------------------------------------------

    [Fact]
    public void Level_BeforeStatScreen_PointsAtStat()
    {
        var (engine, _, _, _, players) = Setup();
        SeedPlayer(players, "Bob", PlayerRemoteControls.QueryExperience);

        engine.DispatchForTests(Telepath("Bob", "@level"));

        Assert.Contains("parse a stat screen", string.Join(" ", Replies(engine)));
    }

    [Fact]
    public void Level_WithExpSpan_ReportsToNext()
    {
        var (engine, stats, _, _, players) = Setup();
        SeedPlayer(players, "Bob", PlayerRemoteControls.QueryExperience);
        stats.Level = 12;
        stats.Exp = 1_500_000;
        stats.LevelExpSpan = 500_000;
        stats.ExpToNext = 120_000;

        engine.DispatchForTests(Telepath("Bob", "@level"));

        string reply = Assert.Single(Replies(engine));
        Assert.Equal("Level 12, 1,500,000 exp, 120,000 to next level", reply);
    }

    [Fact]
    public void Level_WithoutExpSpan_FlagsExpToNextUnknown()
    {
        var (engine, stats, _, _, players) = Setup();
        SeedPlayer(players, "Bob", PlayerRemoteControls.QueryExperience);
        stats.Level = 12;
        stats.Exp = 1_500_000;
        // LevelExpSpan left 0 — the exp line hasn't been parsed yet.

        engine.DispatchForTests(Telepath("Bob", "@level"));

        string reply = Assert.Single(Replies(engine));
        Assert.Equal("Level 12, 1,500,000 exp, exp-to-next unknown (type exp)", reply);
    }

    // ----- @exp --------------------------------------------------------

    [Fact]
    public void Exp_BeforeAnyGain_ReportsRateUnknown()
    {
        var (engine, _, _, clock, players) = Setup();
        SeedPlayer(players, "Bob", PlayerRemoteControls.QueryExperience);
        clock.Advance(30); // time online, but no exp booked

        engine.DispatchForTests(Telepath("Bob", "@exp"));

        string reply = Assert.Single(Replies(engine));
        Assert.Equal("exp rate + time to level unknown (type exp)", reply);
    }

    [Fact]
    public void Exp_WithRateAndSpan_ReportsEtaToLevel()
    {
        var (engine, stats, activity, clock, players) = Setup();
        SeedPlayer(players, "Bob", PlayerRemoteControls.QueryExperience);
        activity.NoteExperience(16_200);
        clock.Advance(30); // 16,200 / 0.5h = 32,400/hr
        stats.LevelExpSpan = 500_000;
        stats.ExpToNext = 32_400; // one hour of exp remaining → "60m" (under the 90m h/m cutover)

        engine.DispatchForTests(Telepath("Bob", "@exp"));

        string reply = Assert.Single(Replies(engine));
        Assert.Equal("32,400 EXP to level, making 32,400/hr ~60m to level.", reply);
    }

    [Fact]
    public void Exp_RateKnownButNoSpan_PointsAtExp()
    {
        var (engine, stats, activity, clock, players) = Setup();
        SeedPlayer(players, "Bob", PlayerRemoteControls.QueryExperience);
        activity.NoteExperience(6_000);
        clock.Advance(30);
        // LevelExpSpan stays 0 — rate is known, ETA is not.

        engine.DispatchForTests(Telepath("Bob", "@exp"));

        string reply = Assert.Single(Replies(engine));
        Assert.Equal("making 12,000/hr (type exp for time to level)", reply);
    }

    [Fact]
    public void Exp_ReadyToLevel_WhenRemainingIsZero()
    {
        var (engine, stats, activity, clock, players) = Setup();
        SeedPlayer(players, "Bob", PlayerRemoteControls.QueryExperience);
        activity.NoteExperience(6_000);
        clock.Advance(30);
        stats.LevelExpSpan = 500_000;
        stats.ExpToNext = 0; // server clamps to 0 once past the threshold

        engine.DispatchForTests(Telepath("Bob", "@exp"));

        Assert.Equal("0 EXP to level, making 12,000/hr ready to level.", Assert.Single(Replies(engine)));
    }

    // ----- @exp rate abbreviation --------------------------------------

    [Theory]
    [InlineData(0, "0")]                    // guarded upstream (rate>0), but exact below 100k
    [InlineData(999, "999")]
    [InlineData(85_377, "85,377")]          // < 100k → exact, comma-grouped
    [InlineData(99_999, "99,999")]
    [InlineData(100_000, "100k")]           // 100k–999k → whole thousands (floor)
    [InlineData(853_777, "853k")]
    [InlineData(999_999, "999k")]
    [InlineData(1_000_000, "1m")]           // millions → one decimal, trailing .0 dropped
    [InlineData(1_100_000, "1.1m")]
    [InlineData(1_193_744, "1.2m")]
    [InlineData(10_100_000, "10.1m")]
    [InlineData(30_000_000, "30m")]
    public void FormatExpRate_TiersByMagnitude(double rate, string expected)
        => Assert.Equal(expected, ExperienceQueryHandler.FormatExpRate(rate));

    // ----- gating ------------------------------------------------------

    [Fact]
    public void Exp_FromUnauthorisedSender_IsDenied()
    {
        var (engine, _, _, clock, players) = Setup();
        engine.WarnOnDenial = false; // silence the generic denial reply
        SeedPlayer(players, "Stranger",
            PlayerRemoteControls.All & ~PlayerRemoteControls.QueryExperience);
        clock.Advance(30);

        engine.DispatchForTests(Telepath("Stranger", "@exp"));

        Assert.Empty(Replies(engine));
    }
}
