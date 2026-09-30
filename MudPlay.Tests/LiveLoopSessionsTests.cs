using System;
using System.Linq;
using MudPlay.Game.Simulation;
using Xunit;

namespace MudPlay.Tests;

// Pins the program-log reader behind the simulator-vs-your-play check: a session
// runs BeginCircle → Stop, exp and kills count per `Exp +=`, level and character
// carry across logs, the room sweeper is skipped, and midnight rolls the date.
public sealed class LiveLoopSessionsTests
{
    private static readonly DateTime LogStart = new(2026, 9, 28, 23, 50, 0);

    [Fact]
    public void ReadsASessionWithItsExpLevelAndCharacter()
    {
        string[] lines =
        {
            "23:50:01.000 [Debug]  StatParser: Name = \"Ermias Asghedom\"",
            "23:50:02.000 [Debug]  StatParser: Level = 47",
            "23:51:00.000 [Info]  LoopRunner: BeginCircle: loop='BigSharks3' start=16/3761 steps=56",
            "23:52:00.000 [Debug]  StatParser: Exp += 15000 → 1 (gain line).",
            "23:59:59.000 [Debug]  StatParser: Exp += 10000 → 2 (gain line).",
            "00:10:00.000 [Info]  DeathDetector: Death observed",
            "00:51:00.000 [Info]  LoopRunner: Stop: loop='BigSharks3' state=Running reason=user stop",
            "00:52:00.000 [Info]  LoopRunner: BeginCircle: loop='Roomba sweep' start=1/1 steps=3",
            "00:53:00.000 [Debug]  StatParser: Exp += 999 → 3 (gain line).",
        };
        int? level = null;
        string? character = null;
        var sessions = LiveLoopSessions.Parse(lines, LogStart, ref level, ref character);

        LiveLoopSession s = Assert.Single(sessions);
        Assert.Equal("BigSharks3", s.Loop);
        Assert.Equal("Ermias Asghedom", s.Character);
        Assert.Equal(47, s.Level);
        Assert.Equal(25000, s.Exp);
        Assert.Equal(2, s.Kills);
        Assert.Equal(1, s.Deaths);
        Assert.Equal(1.0, s.Hours, 3);          // 23:51 → 00:51 the next day
        Assert.Equal(47, level);
    }

    [Fact]
    public void PoolsOneCharactersSessionsByLoopAndLevelAboveTheFloor()
    {
        var t = new DateTime(2026, 9, 28, 1, 0, 0);
        LiveLoopSession S(string who, string loop, int lvl, double hours, long exp) =>
            new(who, loop, lvl, t, t.AddHours(hours), exp, 1, 0);
        var pooled = LiveLoopSessions.Pool(new[]
        {
            S("Ermias", "A", 47, 0.75, 750), S("Ermias", "A", 47, 0.5, 500),
            S("Ermias", "A", 46, 0.5, 400), S("Someone", "A", 47, 5, 5000),
        }, "ermias", minHours: 1.0);

        LiveLoopRecord r = Assert.Single(pooled);
        Assert.Equal(("A", 47, 2), (r.Loop, r.Level, r.Sessions));
        Assert.Equal(1000.0, r.ExpPerHour, 3);
    }

    [Fact]
    public void MatchesOnTheFirstNameAndKeepsUnnamedSessionsWhenOnlyOneCharacterIsLogged()
    {
        var t = new DateTime(2026, 9, 28, 1, 0, 0);
        LiveLoopSession S(string who) => new(who, "A", 47, t, t.AddHours(1), 1000, 1, 0);

        var solo = LiveLoopSessions.Pool(new[] { S("Ermias Asghedom"), S("Ermias"), S("") }, "Ermias", 1.0);
        Assert.Equal(3, Assert.Single(solo).Sessions);

        var shared = LiveLoopSessions.Pool(new[] { S("Ermias Asghedom"), S("Voice"), S("") }, "Ermias Asghedom", 1.0);
        Assert.Equal(1, Assert.Single(shared).Sessions);
    }
}
