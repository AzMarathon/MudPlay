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

    // A ranking older than this is dropped when it is read, and its file deleted by
    // the sweep each client start (SweepOld): the character it was made for is long
    // gone.
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(180);

    private static readonly JsonSerializerOptions Form = new()
    {
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly Func<DateTimeOffset> _now;

    public AreaRankingFiles(Func<DateTimeOffset>? clock = null) => _now = clock ?? (() => DateTimeOffset.Now);

    // ----- Where a file may be ---------
    //
    // A path here is built from three names that come from outside: a game-data set,
    // a BBS and a profile, each a folder's name that the user chose. The rule for
    // such names is PathGuard's and is not repeated here: each of the three must name
    // an entry directly inside the folder above it (PathGuard.Child), so the file is
    // always game data/{set}/Simulator/{bbs}/{profile}.ranking.json and nothing else.
    // The names are an existing set's and an existing character's, so they are held
    // to that and not to the stricter rule for a name being given: a profile made
    // under an odder name before names were checked keeps its rankings like any other.
    //
    // A recursive delete goes only for a folder strictly below a set's Simulator
    // folder (PathGuard.DeleteTree). A refusal is logged through PathGuard, once
    // while it is remembered, and touches nothing on disk.
    //
    // The Simulator folder and the BBS folders in it are made here, as folders. One
    // that is a link was put there by something else and leads wherever it likes:
    // it is not followed, written through or emptied.

    // A set's Simulator folder as a full path, or null when the set's name does not
    // name a folder directly inside the game-data folder.
    private static string? SimulatorFolder(string? set)
    {
        if (PathGuard.Child(AppPaths.GameDataRoot, set) is null) return null;
        string folder = Path.GetFullPath(AppPaths.SimulatorRankingsFolder(set!));
        return IsLink(folder) ? null : folder;
    }

    // A BBS's folder inside a set's Simulator folder, or null.
    private static string? BbsFolder(string? set, string? bbs) =>
        SimulatorFolder(set) is { } simulator && PathGuard.Child(simulator, bbs) is { } folder && !IsLink(folder)
            ? folder
            : null;

    // A character's file on a set, or null. The profile's name is held to the rule
    // by itself first: ".." with the suffix on is a file name like any other.
    private static string? FileIn(string? set, string? bbs, string? profile) =>
        BbsFolder(set, bbs) is { } folder && PathGuard.Child(folder, profile) is not null
            ? PathGuard.Child(folder, profile + Suffix)
            : null;

    private static bool IsLink(string folder)
    {
        try { return new DirectoryInfo(folder).LinkTarget is not null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
    }

    // True when piece could be a set's, a BBS's or a profile's name at all: one path
    // segment, whichever folder it is then looked for in.
    private static bool IsOneName(string? piece) => PathGuard.Child(AppPaths.GameDataRoot, piece) is not null;

    // The file a pairing's ranking is kept in, or null when it has none: no set, no
    // character (the default profile is nobody, and its ranking is not kept), or a
    // name that is not fit to be part of a path.
    public static string? FileFor(SimulatorOwner owner) =>
        Split(owner.Character) is var (bbs, profile) ? FileIn(owner.Set, bbs, profile) : null;

    // The file for owner, with a refusal logged, naming the piece at fault, when
    // owner names a set and character but no file may be kept for them.
    private static string? Resolve(SimulatorOwner owner, string operation)
    {
        if (string.IsNullOrEmpty(owner.Set) || owner.Character is null) return null;
        if (FileFor(owner) is { } path) return path;
        if (!IsOneName(owner.Set)) PathGuard.Refuse(operation, "game-data set name", owner.Set);
        else if (Split(owner.Character) is not var (bbs, profile)) PathGuard.Refuse(operation, "character", owner.Character);
        else if (!IsOneName(bbs)) PathGuard.Refuse(operation, "BBS name", bbs);
        else if (!IsOneName(profile)) PathGuard.Refuse(operation, "profile name", profile);
        else PathGuard.Refuse(operation, "ranking folder", $"{owner.Set}/{bbs}");
        return null;
    }

    // A character key back into its BBS and profile name at the first separator.
    // Whether the two are fit to be path pieces is for FileIn to say.
    private static (string Bbs, string Profile)? Split(string? character)
    {
        if (character is null) return null;
        int slash = character.IndexOf('/');
        return slash < 0 ? null : (character[..slash], character[(slash + 1)..]);
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
        if (ranking.World is not { } world || Resolve(owner, "Saving an area ranking") is not { } path) return (null, 0);
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
        if (Resolve(owner, "Reading a saved area ranking") is not { } path) return default;
        RankingFile? file;
        try
        {
            if (!File.Exists(path)) return default;
            file = JsonStore.LoadStream<RankingFile>(path, Form);
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
        if (Resolve(owner, "Deleting a saved area ranking") is { } path) TryDelete(path);
    }

    // Every ranking kept for a set, whoever's. Returns how many files went. What is
    // in the Simulator folder goes piece by piece, each strictly below it; the
    // folder itself is then removed only if that left it empty.
    public int DeleteSet(string? set)
    {
        if (string.IsNullOrEmpty(set)) return 0;
        const string operation = "Deleting a set's saved area rankings";
        if (SimulatorFolder(set) is not { } folder)
        {
            PathGuard.Refuse(operation, IsOneName(set) ? "ranking folder" : "game-data set name", set);
            return 0;
        }
        try
        {
            if (!Directory.Exists(folder)) return 0;
            // Counted and removed folder by folder, a link among them as the link it
            // is: what it points at is somebody else's.
            int files = Directory.GetFiles(folder, "*" + Suffix).Length;
            foreach (string bbs in Directory.GetDirectories(folder))
            {
                if (!IsLink(bbs)) files += Directory.GetFiles(bbs, "*" + Suffix).Length;
                PathGuard.DeleteTree(bbs, folder, operation);
            }
            foreach (string stray in Directory.GetFiles(folder))
                if (PathGuard.IsInside(stray, folder)) File.Delete(stray);
            Directory.Delete(folder);
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
        if (Split(from) is not var (fromBbs, fromProfile)) return;
        (string Bbs, string Profile)? target = Split(to);
        string operation = to is null ? "Deleting a character's saved area rankings" : "Moving a character's saved area rankings";
        string[] pieces = target is var (toBbs, toProfile)
            ? new[] { fromBbs, fromProfile, toBbs, toProfile }
            : new[] { fromBbs, fromProfile };
        if (Array.Find(pieces, p => !IsOneName(p)) is { } bad)
        {
            PathGuard.Refuse(operation, "BBS or profile name", bad);
            return;
        }
        foreach (string set in Sets()) MoveCharacter(from, to, set);
    }

    private static void MoveCharacter(string? from, string? to, string set)
    {
        if (Split(from) is not var (fromBbs, fromProfile) || FileIn(set, fromBbs, fromProfile) is not { } source) return;
        try
        {
            if (to is null) { TryDelete(source); return; }
            if (Split(to) is not var (toBbs, toProfile) || FileIn(set, toBbs, toProfile) is not { } dest) return;
            // A rename that only changes capitals is a rename where the file system
            // tells the two spellings apart, and the same file where it doesn't: what
            // sits under the new spelling is then this character's own, not another's.
            bool capitalsOnly = string.Equals(from, to, StringComparison.OrdinalIgnoreCase);
            if (string.Equals(source, dest, StringComparison.Ordinal)) return;
            if (!File.Exists(source))
            {
                if (!capitalsOnly) TryDelete(dest);
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Move(source, dest, overwrite: true);
            Rekey(dest, to);
            RemoveIfEmpty(Path.GetDirectoryName(source)!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { TryDelete(source); }
    }

    // A BBS folder the last file was moved out of is not left behind.
    private static void RemoveIfEmpty(string folder)
    {
        try
        {
            if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
                Directory.Delete(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* an empty folder left behind is harmless */ }
    }

    // A BBS was renamed: every character under it is now under the new name. File
    // by file, into a folder that may hold the loaded character's already
    // (MoveCharacter ran for it first).
    public void MoveBbs(string oldBbs, string newBbs)
    {
        const string operation = "Moving a BBS's saved area rankings";
        if (!IsOneName(oldBbs) || !IsOneName(newBbs))
        {
            PathGuard.Refuse(operation, "BBS name", IsOneName(oldBbs) ? newBbs : oldBbs);
            return;
        }
        foreach (string set in Sets())
        {
            if (SimulatorFolder(set) is not { } simulator || BbsFolder(set, oldBbs) is not { } source) continue;
            try
            {
                if (!Directory.Exists(source)) continue;
                foreach (string file in Directory.GetFiles(source, "*" + Suffix))
                    MoveCharacter(SimulatorOwner.CharacterKey(oldBbs, ProfileOf(file)), SimulatorOwner.CharacterKey(newBbs, ProfileOf(file)), set);
                // Where the file system ignores capitals, the old spelling's folder is
                // the new one's, and the files just moved are still in it.
                if (string.Equals(oldBbs, newBbs, StringComparison.OrdinalIgnoreCase)) RemoveIfEmpty(source);
                else PathGuard.DeleteTree(source, simulator, operation);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* a file left under the old name says inside whose it was, and is dropped if ever read as another's */ }
        }
    }

    // A BBS was removed, and every profile under it.
    public void DeleteBbs(string bbs)
    {
        const string operation = "Deleting a BBS's saved area rankings";
        if (!IsOneName(bbs))
        {
            PathGuard.Refuse(operation, "BBS name", bbs);
            return;
        }
        foreach (string set in Sets())
        {
            if (SimulatorFolder(set) is not { } simulator || BbsFolder(set, bbs) is not { } folder) continue;
            try { PathGuard.DeleteTree(folder, simulator, operation); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* its files say whose they were, and are dropped if ever read as another's */ }
        }
    }

    // Delete every ranking file older than MaxAge, on every set and for every BBS
    // and profile: the ones a read would drop, without waiting for a character that
    // may never be loaded again. A BBS folder this leaves empty goes too. Returns
    // how many files went.
    //
    // Age is the read's own: the made-at stamp inside the file against this
    // client's clock, never the file's time (a copied or restored data folder has
    // new file times and old rankings). A file that can't be read, or that carries
    // no stamp, is left for the read to judge; skipped is told why, for the log.
    //
    // held are the files of rankings the client has in memory and are left alone.
    //
    // Other clients share the folder and may sweep, or save a ranking, at the same
    // moment. A file another sweep took first is simply gone. One replaced between
    // the look at its stamp and the delete has another write time by then, and is
    // left: it is a new ranking.
    public int SweepOld(IReadOnlyCollection<string> held, Action<string>? skipped = null)
    {
        ArgumentNullException.ThrowIfNull(held);
        int deleted = 0;
        foreach (string set in Sets())
        {
            // Null for a Simulator folder that is a link: it is not looked into.
            if (SimulatorFolder(set) is not { } simulator) continue;
            string[] folders;
            try
            {
                if (!Directory.Exists(simulator)) continue;
                folders = Directory.GetDirectories(simulator);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                skipped?.Invoke($"game data '{set}': {ex.Message}");
                continue;
            }
            foreach (string listed in folders)
            {
                // Through the same gate every other path here takes: a BBS folder
                // directly inside the Simulator folder, and not a link.
                if (BbsFolder(set, Path.GetFileName(listed)) is not { } bbs) continue;
                int went = SweepFolder(bbs, held, skipped);
                deleted += went;
                // Only a folder this pass took a file from. One found empty may have
                // been made a moment ago by a client about to move a file into it.
                if (went > 0) RemoveIfEmpty(bbs);
            }
        }
        return deleted;
    }

    private int SweepFolder(string bbs, IReadOnlyCollection<string> held, Action<string>? skipped)
    {
        string[] files;
        try { files = Directory.GetFiles(bbs, "*" + Suffix); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            skipped?.Invoke($"{bbs}: {ex.Message}");
            return 0;
        }
        int deleted = 0;
        foreach (string file in files)
        {
            try
            {
                if (!file.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase) || !PathGuard.IsInside(file, bbs)) continue;
                // Without regard to capitals: a file system that ignores them lists a
                // held file under whatever spelling it was first written with.
                if (held.Contains(file, StringComparer.OrdinalIgnoreCase)) continue;
                var info = new FileInfo(file);
                // A link is somebody else's file under one of our names.
                if (!info.Exists || info.LinkTarget is not null) continue;
                DateTime writtenAt = info.LastWriteTimeUtc;
                if (JsonStore.LoadStream<StampOnly>(file, Form)?.MadeAt is not { } madeAt)
                {
                    skipped?.Invoke($"{file}: it carries no made-at stamp");
                    continue;
                }
                if (_now() - madeAt <= MaxAge) continue;
                if (File.GetLastWriteTimeUtc(file) != writtenAt) continue;
                File.Delete(file);
                deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                                           or JsonException or NotSupportedException)
            {
                skipped?.Invoke($"{file}: {ex.Message}");
            }
        }
        return deleted;
    }

    // The one field of a ranking file the sweep reads; the rows are skipped over.
    private sealed class StampOnly
    {
        public DateTimeOffset? MadeAt { get; set; }
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
            if (JsonStore.LoadStream<RankingFile>(path, Form) is not { } file) { TryDelete(path); return; }
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
        Loops = r.Loops is { } loops ? new Dictionary<string, string>(loops) : null,
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
            f.MadeAt, f.CharacterLevel, made, f.Groups is null ? null : new RankingCharacter(f.Groups), FromDisk: true,
            Loops: LoopsOf(f.Loops));
    }

    // A loop is looked up by name, whatever capitals it has come to have. Two names
    // in a file that differ only by those, or a signature that is null, are a file
    // someone has been at: the first stands, and a missing signature matches none.
    private static Dictionary<string, string>? LoopsOf(Dictionary<string, string>? file)
    {
        if (file is null) return null;
        var loops = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string name, string? signature) in file) loops.TryAdd(name, signature ?? string.Empty);
        return loops;
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
        public Dictionary<string, string>? Loops { get; set; }
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
