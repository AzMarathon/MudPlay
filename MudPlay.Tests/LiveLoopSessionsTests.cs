using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MudPlay.Game.Simulation;
using Xunit;

namespace MudPlay.Tests;

// Pins the program-log reader behind the simulator-vs-your-play check: a session
// runs BeginCircle → Stop / Ended, exp and kills count per `Exp +=`, level and
// character carry across logs only when one log began after the last one ended, the
// room sweeper is skipped, midnight rolls the date, and an unreadable log is skipped.
public sealed class LiveLoopSessionsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mudplay-liveloop-" + Path.GetRandomFileName());

    public LiveLoopSessionsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* best-effort cleanup */ }
    }

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
        var sessions = LiveLoopSessions.Parse(lines, LogStart, ref level, ref character, out DateTime lastAt);

        LiveLoopSession s = Assert.Single(sessions);
        Assert.Equal("BigSharks3", s.Loop);
        Assert.Equal("Ermias Asghedom", s.Character);
        Assert.Equal(47, s.Level);
        Assert.Equal(25000, s.Exp);
        Assert.Equal(2, s.Kills);
        Assert.Equal(1, s.Deaths);
        Assert.Equal(1.0, s.Hours, 3);          // 23:51 → 00:51 the next day
        Assert.Equal(47, level);
        Assert.Equal(new DateTime(2026, 9, 29, 0, 53, 0), lastAt);
    }

    [Fact]
    public void AFailedLoopEndsItsSessionAtTheEndedLine()
    {
        string[] lines =
        {
            "23:50:02.000 [Debug]  StatParser: Level = 47",
            "23:51:00.000 [Info]  LoopRunner: BeginCircle: loop='A' start=1/1 steps=4",
            "23:52:00.000 [Debug]  StatParser: Exp += 100 → 1 (gain line).",
            "23:53:00.000 [Info]  LoopRunner: Ended: loop='A' reason=tier3 recovery failed: lost",
            "23:59:00.000 [Debug]  StatParser: Exp += 5000 → 2 (gain line).",
        };
        int? level = null;
        string? character = null;
        LiveLoopSession s = Assert.Single(LiveLoopSessions.Parse(lines, LogStart, ref level, ref character, out _));
        Assert.Equal(100, s.Exp);
        Assert.Equal(new DateTime(2026, 9, 28, 23, 53, 0), s.End);
    }

    [Fact]
    public void ALevelGainedMidLoopStartsANewSessionAtTheNewLevel()
    {
        string[] lines =
        {
            "23:50:02.000 [Debug]  StatParser: Level = 47",
            "23:51:00.000 [Info]  LoopRunner: BeginCircle: loop='A' start=1/1 steps=4",
            "23:52:00.000 [Debug]  StatParser: Exp += 100 → 1 (gain line).",
            "23:53:00.000 [Info]  StatParser: Level → 48 (train-success line).",
            "23:54:00.000 [Debug]  StatParser: Exp += 200 → 2 (gain line).",
            "23:55:00.000 [Debug]  StatParser: Exp = 9  Level = 48  ExpToNext = 1  LevelExpSpan = 2  LevelPercent = 3",
            "23:56:00.000 [Info]  LoopRunner: Stop: loop='A' state=Running reason=user stop",
        };
        int? level = null;
        string? character = null;
        var sessions = LiveLoopSessions.Parse(lines, LogStart, ref level, ref character, out _);
        Assert.Equal(new[] { (47, 100L), (48, 200L) }, sessions.Select(s => (s.Level!.Value, s.Exp)));
    }

    [Fact]
    public void AnotherCharactersNameDropsTheCarriedLevel()
    {
        string[] lines =
        {
            "23:50:01.000 [Debug]  StatParser: Name = \"Other Person\"",
            "23:51:00.000 [Info]  LoopRunner: BeginCircle: loop='A' start=1/1 steps=4",
        };
        int? level = 47;
        string? character = "Ermias Asghedom";
        LiveLoopSession s = Assert.Single(LiveLoopSessions.Parse(lines, LogStart, ref level, ref character, out _));
        Assert.Null(s.Level);
        Assert.Equal("Other Person", s.Character);

        level = 47;
        character = "Ermias";
        LiveLoopSessions.Parse(new[] { "23:50:01.000 [Debug]  StatParser: Name = \"Ermias Asghedom\"" },
            LogStart, ref level, ref character, out _);
        Assert.Equal(47, level);
    }

    [Fact]
    public void StateCarriesOnlyIntoALogThatBeganAfterTheLastOneEnded()
    {
        // Two instances started in the same second: the second log must not inherit
        // the first's character and level. A log opened later inherits them.
        WriteLog("2026-09-28_10-00-00-p1-program.log",
            "10:00:01.000 [Debug]  StatParser: Name = \"Ermias\"",
            "10:00:02.000 [Debug]  StatParser: Level = 47",
            "11:00:00.000 [Info]  Session: closing");
        WriteLog("2026-09-28_10-00-00-p2-program.log",
            "10:05:00.000 [Info]  LoopRunner: BeginCircle: loop='Sibling' start=1/1 steps=4",
            "10:30:00.000 [Info]  LoopRunner: Stop: loop='Sibling' state=Running reason=user stop");
        WriteLog("2026-09-28_12-00-00-p3-program.log",
            "12:05:00.000 [Info]  LoopRunner: BeginCircle: loop='Later' start=1/1 steps=4",
            "12:30:00.000 [Info]  LoopRunner: Stop: loop='Later' state=Running reason=user stop");

        var sessions = LiveLoopSessions.ReadFolder(_dir);
        LiveLoopSession sibling = sessions.Single(s => s.Loop == "Sibling");
        Assert.Null(sibling.Level);
        Assert.Equal("", sibling.Character);
        LiveLoopSession later = sessions.Single(s => s.Loop == "Later");
        Assert.Equal((47, "Ermias"), (later.Level, later.Character));
    }

    [Fact]
    public void ALogThatCantBeOpenedIsSkippedAndReported()
    {
        WriteLog("2026-09-28_10-00-00-p1-program.log",
            "10:00:02.000 [Debug]  StatParser: Level = 47",
            "10:05:00.000 [Info]  LoopRunner: BeginCircle: loop='Locked' start=1/1 steps=4");
        WriteLog("2026-09-28_12-00-00-p2-program.log",
            "12:05:00.000 [Info]  LoopRunner: BeginCircle: loop='Readable' start=1/1 steps=4",
            "12:30:00.000 [Info]  LoopRunner: Stop: loop='Readable' state=Running reason=user stop");
        // A writer that shares only reads (the app's own DebugLogWriter) doesn't block
        // the reader; one that shares nothing does.
        WriteLog("2026-09-28_13-00-00-p3-program.log",
            "13:05:00.000 [Info]  LoopRunner: BeginCircle: loop='Live' start=1/1 steps=4");
        using var live = new FileStream(Path.Combine(_dir, "2026-09-28_13-00-00-p3-program.log"),
            FileMode.Append, FileAccess.Write, FileShare.Read);
        using var locked = new FileStream(Path.Combine(_dir, "2026-09-28_10-00-00-p1-program.log"),
            FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var warnings = new List<string>();
        var sessions = LiveLoopSessions.ReadFolder(_dir, warnings.Add);

        Assert.Equal(new[] { "Readable", "Live" }, sessions.Select(s => s.Loop));
        Assert.Contains("2026-09-28_10-00-00-p1-program.log", Assert.Single(warnings), StringComparison.Ordinal);
        Assert.Null(sessions[0].Level);      // nothing carried out of the skipped log
    }

    private void WriteLog(string name, params string[] lines) =>
        File.WriteAllLines(Path.Combine(_dir, name), lines);

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
