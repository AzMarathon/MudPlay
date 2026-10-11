using System.Text.Json;
using System.Text.Json.Serialization;
using MudPlay.Game.Map;
using MudPlay.Game.Simulation;

namespace MudPlay.Services;

// The Simulator's area rankings on disk, so a ranking survives the client being
// closed: one file for each pairing of game-data set and character, kept inside the
// set's folder (AppPaths.SimulatorRankingFile). Kept with the set because it is
// about that set's rooms and goes when the set is deleted; never one of the set's
// tables, and not carried by a copy of the set or of the profile, being one
// character's results.
//
// A file is never trusted. It carries what the ranking was made in (RankingWorld,
// its own format number, the set and character it is for) and is dropped, and
// deleted, when any of that is not what is being asked for, when it can't be parsed,
// or when it is older than MaxAge. What it was made for (RankingCharacter) is handed
// back with it for the reader to compare: a ranking of the character as it was is
// still listed, marked.
//
// Plain JSON, written compact: nothing else in the client keeps compressed data
// files, and a whole realm's ranking is about a megabyte. Every method does its
// file work on the thread that calls it and reports failure in its result, never by
// throwing: AreaRankingStore runs them off the UI thread, in order.
public sealed class AreaRankingFiles
{
    public const int FormatVersion = 1;

    // A ranking older than this is dropped when it is read: the character it was
    // made for is long gone.
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(180);

    private static readonly JsonSerializerOptions Form = new()
    {
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly Func<DateTimeOffset> _now;

    public AreaRankingFiles(Func<DateTimeOffset>? clock = null) => _now = clock ?? (() => DateTimeOffset.Now);

    // The file a pairing's ranking is kept in, or null when it has none: no set, or
    // no character (the default profile is nobody, and its ranking is not kept).
    public static string? FileFor(SimulatorOwner owner) =>
        owner.Set is { Length: > 0 } set && Split(owner.Character) is var (bbs, profile)
            ? AppPaths.SimulatorRankingFile(set, bbs, profile)
            : null;

    // A character key back into its BBS and profile name, which are folder names and
    // hold no separator of their own.
    private static (string Bbs, string Profile)? Split(string? character)
    {
        if (character is null) return null;
        int slash = character.IndexOf('/');
        return slash <= 0 || slash == character.Length - 1 ? null : (character[..slash], character[(slash + 1)..]);
    }

    // What became of a read: the ranking, or nothing, with why a file that was
    // there was dropped (null when there was none).
    public readonly record struct ReadResult(AreaRanking? Ranking, string? Dropped);

    // Write owner's ranking: atomically, a temp file moved over the old one. Error is
    // why it could not be written, or null; Bytes is the file's size, 0 when nothing
    // was written. Nothing is written for a ranking that carries no record of the
    // world it was made in (it could never be read back), or to a set whose folder
    // is gone: that would bring a deleted set back as an empty one.
    public (string? Error, long Bytes) Write(SimulatorOwner owner, AreaRanking ranking)
    {
        ArgumentNullException.ThrowIfNull(ranking);
        if (FileFor(owner) is not { } path || ranking.World is not { } world) return (null, 0);
        try
        {
            if (!Directory.Exists(AppPaths.GameDataSetDir(owner.Set!))) return (null, 0);
            JsonStore.Save(path, ToFile(owner, ranking, world), Form);
            return (null, new FileInfo(path).Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or JsonException)
        {
            return (ex.Message, 0);
        }
    }

    // Read owner's ranking back, checked against the world as it is now. Anything
    // wrong with the file drops it: it is deleted, and Dropped says why.
    public ReadResult Read(SimulatorOwner owner, RankingWorld worldNow)
    {
        ArgumentNullException.ThrowIfNull(worldNow);
        if (FileFor(owner) is not { } path) return default;
        RankingFile? file;
        try
        {
            if (!File.Exists(path)) return default;
            file = JsonStore.Load<RankingFile>(path, Form);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                                       or JsonException or NotSupportedException)
        {
            return Drop(path, $"it could not be read ({ex.Message})");
        }

        if (file is null) return Drop(path, "it is empty");
        if (file.Format != FormatVersion)
            return Drop(path, $"it is in file format {file.Format} and this client reads format {FormatVersion}");
        if (!string.Equals(file.Set, owner.Set, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(file.Character, owner.Character, StringComparison.OrdinalIgnoreCase))
            return Drop(path, $"it says it is for game data '{file.Set}', character '{file.Character}'");
        var made = new RankingWorld(file.SimulatorVersion, file.Tables ?? new(), file.Seed ?? string.Empty);
        if (worldNow.DiffersFrom(made) is { } differs) return Drop(path, differs);
        if (_now() - file.MadeAt > MaxAge)
            return Drop(path, $"it was made {file.MadeAt.ToLocalTime():yyyy-MM-dd}, more than {MaxAge.Days} days ago");
        return FromFile(file, made) is { } ranking
            ? new ReadResult(ranking, null)
            : Drop(path, "its contents are not a ranking");
    }

    private static ReadResult Drop(string path, string why)
    {
        TryDelete(path);
        return new ReadResult(null, why);
    }

    public void Delete(SimulatorOwner owner)
    {
        if (FileFor(owner) is { } path) TryDelete(path);
    }

    // Every ranking kept for a set, whoever's. Returns how many files went.
    public int DeleteSet(string? set)
    {
        if (string.IsNullOrEmpty(set)) return 0;
        try
        {
            string folder = AppPaths.SimulatorRankingsFolder(set);
            if (!Directory.Exists(folder)) return 0;
            int files = Directory.EnumerateFiles(folder, "*" + Suffix, SearchOption.AllDirectories).Count();
            Directory.Delete(folder, recursive: true);
            return files;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A file left behind is dropped when it is read: the tables it names are gone.
            return 0;
        }
    }

    // A character's rankings on every set go with it to a new BBS / profile name
    // (to), or are deleted (to null: the profile was). What was kept under the new
    // name belonged to whoever had it before, and is replaced.
    public void MoveCharacter(string? from, string? to)
    {
        foreach (string set in Sets()) MoveCharacter(from, to, set);
    }

    private static void MoveCharacter(string? from, string? to, string set)
    {
        if (Split(from) is not var (fromBbs, fromProfile)) return;
        string source = AppPaths.SimulatorRankingFile(set, fromBbs, fromProfile);
        try
        {
            if (Split(to) is var (toBbs, toProfile))
            {
                string dest = AppPaths.SimulatorRankingFile(set, toBbs, toProfile);
                if (!File.Exists(source)) { TryDelete(dest); return; }
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Move(source, dest, overwrite: true);
                Rekey(dest, to!);
            }
            else TryDelete(source);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { TryDelete(source); }
    }

    // A BBS was renamed: every character under it is now under the new name. File
    // by file, into a folder that may hold the loaded character's already
    // (MoveCharacter ran for it first).
    public void MoveBbs(string oldBbs, string newBbs)
    {
        foreach (string set in Sets())
        {
            string source = Path.Combine(AppPaths.SimulatorRankingsFolder(set), oldBbs);
            try
            {
                if (!Directory.Exists(source)) continue;
                foreach (string file in Directory.GetFiles(source, "*" + Suffix))
                    MoveCharacter(SimulatorOwner.CharacterKey(oldBbs, ProfileOf(file)), SimulatorOwner.CharacterKey(newBbs, ProfileOf(file)), set);
                Directory.Delete(source, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* a file left under the old name says inside whose it was, and is dropped if ever read as another's */ }
        }
    }

    // A BBS was removed, and every profile under it.
    public void DeleteBbs(string bbs)
    {
        foreach (string set in Sets())
        {
            try
            {
                string folder = Path.Combine(AppPaths.SimulatorRankingsFolder(set), bbs);
                if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* its files say whose they were, and are dropped if ever read as another's */ }
        }
    }

    private const string Suffix = ".ranking.json";

    private static string ProfileOf(string file) => Path.GetFileName(file)[..^Suffix.Length];

    // A moved file says inside whose it is, and is only read back as that
    // character's: bring it in line with where it now sits. One that can't be
    // rewritten is deleted.
    private static void Rekey(string path, string character)
    {
        try
        {
            if (JsonStore.Load<RankingFile>(path, Form) is not { } file) { TryDelete(path); return; }
            file.Character = character;
            JsonStore.Save(path, file, Form);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                                       or JsonException or NotSupportedException)
        {
            TryDelete(path);
        }
    }

    private static IEnumerable<string> Sets()
    {
        try
        {
            return Directory.Exists(AppPaths.GameDataRoot)
                ? Directory.GetDirectories(AppPaths.GameDataRoot).Select(d => Path.GetFileName(d)!).ToArray()
                : Array.Empty<string>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Array.Empty<string>(); }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* left behind, it is checked again the next time it is read */ }
    }

    // ----- The file's form ---------

    private static RankingFile ToFile(SimulatorOwner owner, AreaRanking r, RankingWorld world) => new()
    {
        Format = FormatVersion,
        Set = owner.Set,
        Character = owner.Character,
        SimulatorVersion = world.SimulatorVersion,
        Tables = new Dictionary<string, string>(world.Tables),
        Seed = world.Seed,
        Groups = r.Character is { } c ? new Dictionary<string, string>(c.Groups) : null,
        MadeAt = r.MadeAt,
        Level = r.Level,
        CharacterLevel = r.CharacterLevel,
        Step = r.Step,
        Hours = r.Hours,
        Runs = r.Runs,
        From = r.From.ToString(),
        Realm = r.Realm,
        Summary = r.Summary,
        Rows = r.Rows.Select(row => new RowFile
        {
            Area = row.Area,
            Level = row.Level,
            Tour = string.Join(' ', row.Tour.Select(k => k.ToString())),
            LapRooms = row.LapRooms,
            IsLoop = row.IsLoop,
            Runs = row.Result.Runs.Select(run => new RunFile
            {
                RequestedSeconds = run.RequestedSeconds, Seconds = run.Seconds, Exp = run.Exp, Kills = run.Kills,
                Laps = run.Laps, MovingSeconds = run.MovingSeconds, AttackingSeconds = run.AttackingSeconds,
                RestingSeconds = run.RestingSeconds, MeditatingSeconds = run.MeditatingSeconds,
                WaitingSeconds = run.WaitingSeconds, LowestHpPercent = run.LowestHpPercent,
                LowestManaPercent = run.LowestManaPercent, DiedAtSeconds = run.DiedAtSeconds,
                Casts = new Dictionary<string, int>(run.Casts), Flees = run.Flees,
                HungUpAtSeconds = run.HungUpAtSeconds, DamageTaken = run.DamageTaken,
            }).ToList(),
            Bosses = row.Result.Bosses?.Select(b => new BossFile { Name = b.Name, ExpPerHour = b.ExpPerHour, RegenHours = b.RegenHours }).ToList(),
            Live = row.Live?.Select(l => new LiveFile
            {
                Loop = l.Loop, Level = l.Level, Sessions = l.Sessions, Hours = l.Hours, Exp = l.Exp, Kills = l.Kills, Deaths = l.Deaths,
            }).ToList(),
        }).ToList(),
    };

    // Null when the file's contents don't make a ranking: a room that isn't one, a
    // row with no runs.
    private static AreaRanking? FromFile(RankingFile f, RankingWorld made)
    {
        if (f.Rows is not { Count: > 0 } || !RoomKey.TryParseWire(f.From, out RoomKey from)) return null;
        var rows = new List<AreaRank>(f.Rows.Count);
        foreach (RowFile row in f.Rows)
        {
            if (row.Area is not { Length: > 0 } || row.Runs is not { Count: > 0 } || row.Tour is null) return null;
            var tour = new List<RoomKey>();
            foreach (string room in row.Tour.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!RoomKey.TryParseWire(room, out RoomKey key)) return null;
                tour.Add(key);
            }
            if (tour.Count < 2) return null;
            var runs = row.Runs.Select(run => new LoopSimRun(
                run.RequestedSeconds, run.Seconds, run.Exp, run.Kills, run.Laps, run.MovingSeconds, run.AttackingSeconds,
                run.RestingSeconds, run.MeditatingSeconds, run.WaitingSeconds, run.LowestHpPercent, run.LowestManaPercent,
                run.DiedAtSeconds, run.Casts ?? new Dictionary<string, int>(), run.Flees, run.HungUpAtSeconds, run.DamageTaken)).ToList();
            rows.Add(new AreaRank(row.Area, row.Level, tour, row.LapRooms,
                new LoopSimSummary(runs, row.Bosses?.Select(b => new ExpBossStat(b.Name ?? string.Empty, b.ExpPerHour, b.RegenHours)).ToList()),
                row.IsLoop,
                row.Live?.Select(l => new LiveLoopRecord(l.Loop ?? string.Empty, l.Level, l.Sessions, l.Hours, l.Exp, l.Kills, l.Deaths)).ToList()));
        }
        return new AreaRanking(rows, f.Level, f.Step, f.Hours, f.Runs, from, f.Realm ?? string.Empty, f.Summary ?? string.Empty,
            f.MadeAt, f.CharacterLevel, made, f.Groups is null ? null : new RankingCharacter(f.Groups), FromDisk: true);
    }

    private sealed class RankingFile
    {
        public int Format { get; set; }
        public string? Set { get; set; }
        public string? Character { get; set; }
        public int SimulatorVersion { get; set; }
        public Dictionary<string, string>? Tables { get; set; }
        public string? Seed { get; set; }
        public Dictionary<string, string>? Groups { get; set; }
        public DateTimeOffset MadeAt { get; set; }
        public int Level { get; set; }
        public int CharacterLevel { get; set; }
        public double Step { get; set; }
        public double Hours { get; set; }
        public int Runs { get; set; }
        public string? From { get; set; }
        public string? Realm { get; set; }
        public string? Summary { get; set; }
        public List<RowFile>? Rows { get; set; }
    }

    private sealed class RowFile
    {
        public string? Area { get; set; }
        public int Level { get; set; }
        public string? Tour { get; set; }
        public int LapRooms { get; set; }
        public bool IsLoop { get; set; }
        public List<RunFile>? Runs { get; set; }
        public List<BossFile>? Bosses { get; set; }
        public List<LiveFile>? Live { get; set; }
    }

    private sealed class RunFile
    {
        public double RequestedSeconds { get; set; }
        public double Seconds { get; set; }
        public long Exp { get; set; }
        public int Kills { get; set; }
        public int Laps { get; set; }
        public double MovingSeconds { get; set; }
        public double AttackingSeconds { get; set; }
        public double RestingSeconds { get; set; }
        public double MeditatingSeconds { get; set; }
        public double WaitingSeconds { get; set; }
        public int LowestHpPercent { get; set; }
        public int LowestManaPercent { get; set; }
        public double? DiedAtSeconds { get; set; }
        public Dictionary<string, int>? Casts { get; set; }
        public int Flees { get; set; }
        public double? HungUpAtSeconds { get; set; }
        public long DamageTaken { get; set; }
    }

    private sealed class BossFile
    {
        public string? Name { get; set; }
        public double ExpPerHour { get; set; }
        public int RegenHours { get; set; }
    }

    private sealed class LiveFile
    {
        public string? Loop { get; set; }
        public int Level { get; set; }
        public int Sessions { get; set; }
        public double Hours { get; set; }
        public long Exp { get; set; }
        public int Kills { get; set; }
        public int Deaths { get; set; }
    }
}
