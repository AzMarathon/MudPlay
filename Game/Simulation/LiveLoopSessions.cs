using System.Globalization;
using System.Text.RegularExpressions;

namespace MudPlay.Game.Simulation;

// One loop the character actually ran, read back from a program log: from
// LoopRunner's BeginCircle to that loop's Stop or Ended (or the next BeginCircle, a
// level change, or the end of the log). Exp and kills come from StatParser's
// per-kill `Exp +=` lines, the level and character from the last level / `Name =`
// line the log recorded. Rates are wall-clock.
public sealed record LiveLoopSession(
    string Character, string Loop, int? Level, DateTime Start, DateTime End, long Exp, int Kills, int Deaths)
{
    public double Hours => (End - Start).TotalHours;
}

// Every session one character played on one loop at one level, pooled.
public sealed record LiveLoopRecord(string Loop, int Level, int Sessions, double Hours, long Exp, int Kills, int Deaths)
{
    public double ExpPerHour => Hours > 0 ? Exp / Hours : 0;
    public double KillsPerHour => Hours > 0 ? Kills / Hours : 0;
}

public static class LiveLoopSessions
{
    private static readonly Regex Time = new(@"^(\d\d):(\d\d):(\d\d)\.(\d{3}) ", RegexOptions.Compiled);
    private static readonly Regex Begin = new(@"LoopRunner: BeginCircle: loop='(?<loop>[^']*)'", RegexOptions.Compiled);
    // Stop is a clean stop; Ended is every failure that resets the runner without one.
    private static readonly Regex End = new(@"LoopRunner: (?:Stop|Ended): loop='(?<loop>[^']*)'", RegexOptions.Compiled);
    private static readonly Regex Exp = new(@"StatParser: Exp \+= (\d+)", RegexOptions.Compiled);
    // The stat screen's `Level =`, the exp screen's `Exp = N  Level = N`, and a train's `Level → N`.
    private static readonly Regex Level = new(@"StatParser: (?:Level = |Level → |Exp = \d+  Level = )(\d+)", RegexOptions.Compiled);
    private static readonly Regex Name = new("StatParser: Name = \"(?<name>[^\"]*)\"", RegexOptions.Compiled);
    private const string Death = "DeathDetector: Death observed";

    // The room sweeper runs through LoopRunner too, but it isn't a hunting loop.
    private const string RoomSweep = "Roomba sweep";

    // Program logs are named "yyyy-MM-dd_HH-mm-ss-p<pid>-program.log"; the name
    // carries the date every line's time of day belongs to.
    public static DateTime? LogStart(string fileName) =>
        fileName.Length >= 19 && DateTime.TryParseExact(fileName[..19], "yyyy-MM-dd_HH-mm-ss",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime start) ? start : null;

    // Sessions in one log. Level and character carry in (a log opened mid-character
    // hasn't re-read the stat screen yet) and back out; lastAt is the log's last
    // timestamp, so the caller can tell whether the next log began after it.
    public static IReadOnlyList<LiveLoopSession> Parse(
        IEnumerable<string> lines, DateTime logStart, ref int? level, ref string? character, out DateTime lastAt)
    {
        var sessions = new List<LiveLoopSession>();
        DateTime day = logStart.Date;
        int? lastSeconds = null;
        DateTime at = logStart;
        (string Loop, int? Level, string Character, DateTime Start, long Exp, int Kills, int Deaths)? cur = null;

        void Close()
        {
            if (cur is { } c)
                sessions.Add(new LiveLoopSession(c.Character, c.Loop, c.Level, c.Start, at, c.Exp, c.Kills, c.Deaths));
            cur = null;
        }

        foreach (string line in lines)
        {
            Match t = Time.Match(line);
            if (!t.Success) continue;
            int seconds = int.Parse(t.Groups[1].Value) * 3600 + int.Parse(t.Groups[2].Value) * 60 + int.Parse(t.Groups[3].Value);
            if (lastSeconds is { } last && seconds < last - 3600) day = day.AddDays(1);   // past midnight
            lastSeconds = seconds;
            at = day.AddSeconds(seconds).AddMilliseconds(int.Parse(t.Groups[4].Value));

            if (line.Contains("StatParser: ", StringComparison.Ordinal))
            {
                if (Level.Match(line) is { Success: true } lm)
                {
                    int n = int.Parse(lm.Groups[1].Value);
                    level = n > 0 ? n : null;
                    if (cur is { Level: null } c) cur = c with { Level = level };
                    // A level gained mid-loop starts a new session, so the exp after the
                    // train counts toward the new level.
                    else if (cur is { } open && level is { } l && l != open.Level)
                    {
                        Close();
                        cur = (open.Loop, level, open.Character, at, 0, 0, 0);
                    }
                }
                else if (Name.Match(line) is { Success: true } nm)
                {
                    string name = nm.Groups["name"].Value;
                    // Another character's name means the level carried so far was theirs.
                    if (character is not null && !SameCharacter(character, name)) level = null;
                    character = name;
                }
                else if (cur is { } c && Exp.Match(line) is { Success: true } em)
                {
                    cur = c with { Exp = c.Exp + long.Parse(em.Groups[1].Value), Kills = c.Kills + 1 };
                }
                continue;
            }
            if (line.Contains("LoopRunner: ", StringComparison.Ordinal))
            {
                if (Begin.Match(line) is { Success: true } bm)
                {
                    Close();
                    string loop = bm.Groups["loop"].Value;
                    if (loop != RoomSweep) cur = (loop, level, character ?? string.Empty, at, 0, 0, 0);
                }
                else if (cur is { } c && End.Match(line) is { Success: true } sm && sm.Groups["loop"].Value == c.Loop)
                {
                    Close();
                }
                continue;
            }
            if (cur is { } d && line.Contains(Death, StringComparison.Ordinal)) cur = d with { Deaths = d.Deaths + 1 };
        }
        Close();
        lastAt = at;
        return sessions;
    }

    // Every program log in the folder, oldest first. Level and character carry into
    // a log only from the log that last wrote before it began: instances running
    // side by side (a party on one machine) start in the same second, and one
    // character's state must not leak into another's log. A log that can't be read
    // (pruned mid-scan, locked) is skipped and reported through warn.
    public static IReadOnlyList<LiveLoopSession> ReadFolder(string logsDir, Action<string>? warn = null)
    {
        if (!Directory.Exists(logsDir)) return Array.Empty<LiveLoopSession>();
        var logs = Directory.EnumerateFiles(logsDir, "*-program.log")
            .Select(p => (Path: p, Start: LogStart(Path.GetFileName(p))))
            .Where(x => x.Start is not null)
            .OrderBy(x => x.Start)
            .ToList();
        (int? Level, string? Character, DateTime End)? latest = null;
        var all = new List<LiveLoopSession>();
        foreach ((string path, DateTime? logStart) in logs)
        {
            DateTime start = logStart!.Value;
            bool carry = latest is { } p && p.End < start;
            int? level = carry ? latest!.Value.Level : null;
            string? character = carry ? latest!.Value.Character : null;
            try
            {
                all.AddRange(Parse(ReadShared(path), start, ref level, ref character, out DateTime end));
                if (latest is not { } q || end > q.End) latest = (level, character, end);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warn?.Invoke($"skipped {Path.GetFileName(path)}: {ex.Message}");
            }
        }
        return all;
    }

    // The running instance's own log is still open for writing (and a sibling's may
    // be pruned under us); File.ReadLines' FileShare.Read refuses the first on Windows.
    private static IEnumerable<string> ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line) yield return line;
    }

    // One character's sessions pooled by loop and level, keeping each pool of at
    // least minHours — shorter samples swing too far on luck to check against. The
    // logs name a character by the full name on some screens and the first name on
    // others, so they're matched on the first name, which is unique (GAME_MECHANICS
    // "Character names — the first name is unique"); a session logged before any
    // name counts only when the logs never name anyone else.
    public static IReadOnlyList<LiveLoopRecord> Pool(
        IEnumerable<LiveLoopSession> sessions, string character, double minHours)
    {
        var list = sessions.ToList();
        bool onlyMe = list.All(s => s.Character.Length == 0 || SameCharacter(s.Character, character));
        return list
            .Where(s => s.Level is not null && s.Hours > 0
                && (s.Character.Length == 0 ? onlyMe : SameCharacter(s.Character, character)))
            .GroupBy(s => (Loop: s.Loop, Level: s.Level!.Value))
            .Select(g => new LiveLoopRecord(g.Key.Loop, g.Key.Level, g.Count(), g.Sum(s => s.Hours),
                g.Sum(s => s.Exp), g.Sum(s => s.Kills), g.Sum(s => s.Deaths)))
            .Where(r => r.Hours >= minHours)
            .OrderBy(r => r.Loop, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Level)
            .ToList();
    }

    private static bool SameCharacter(string a, string b) =>
        FirstName(a).Equals(FirstName(b), StringComparison.OrdinalIgnoreCase);

    private static string FirstName(string name) =>
        name.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries) is { Length: > 0 } parts ? parts[0] : string.Empty;
}
