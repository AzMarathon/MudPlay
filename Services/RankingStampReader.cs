using MudPlay.Game.Simulation;

namespace MudPlay.Services;

// Reads off disk the parts of what a kept area ranking is compared by that are
// files: the game-data tables' stamps and the monster seed (RankingWorld), and the
// user's record overrides (the "game-data edits" group of RankingCharacter).
//
// A table is stamped by its length and last-write time: the tables run to tens of
// megabytes, and an import rewrites them. The seed and the overrides are small and
// are stamped by content, so a file rewritten with what it already held (a seed
// extracted again at launch, a settings save) is not a change. A content hash is
// remembered against the file's length and time, so a file is read once for as
// long as it is left alone.
public sealed class RankingStampReader
{
    private readonly Dictionary<string, (long Length, DateTime Written, string Hash)> _hashes =
        new(StringComparer.Ordinal);

    // The world a ranking made now on the set in setDir is made in. seedFile is the
    // monster seed in use (null: none loaded).
    public RankingWorld World(string setDir, string? seedFile)
    {
        var tables = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string table in RankingWorld.TableNames)
        {
            FileInfo file = new(Path.Combine(setDir, table + ".json"));
            // A set without the table (Lairs is optional in an export) is stamped as
            // without it: the table arriving later is a change like any other.
            tables[table] = file.Exists ? $"{file.Length}:{file.LastWriteTimeUtc.Ticks}" : "none";
        }
        return new RankingWorld(LoopSimulator.SimulatorVersion, tables, seedFile is null ? "none" : ContentHash(seedFile));
    }

    // The user's record overrides for a set, at every tier that applies to the
    // loaded character: folders is the Global folder, the realm's and the
    // character's, in that order, with a tier that isn't there left out. A file
    // counts by its tier, its name and what it holds.
    public string GameDataEdits(IEnumerable<string> folders, string set)
    {
        ArgumentNullException.ThrowIfNull(folders);
        string suffix = $"_overrides.{set}.json";
        var parts = new List<string>();
        int tier = 0;
        foreach (string folder in folders)
        {
            tier++;
            if (!Directory.Exists(folder)) continue;
            foreach (string file in Directory.EnumerateFiles(folder, "*" + suffix).OrderBy(f => f, StringComparer.Ordinal))
            {
                string hash = ContentHash(file);
                // An override file emptied of its last edit is the same as none.
                if (hash != EmptyObject && hash != EmptyFile)
                    parts.Add($"{tier}:{Path.GetFileName(file).ToLowerInvariant()}:{hash}");
            }
        }
        return RankingCharacter.HashText(string.Join('|', parts));
    }

    private static readonly string EmptyObject = RankingCharacter.HashText("{}");
    private static readonly string EmptyFile = RankingCharacter.HashText("");

    // A hash of what a file holds, "none" when it isn't there or can't be read:
    // then it differs from every real content, and the ranking is dropped or marked.
    private string ContentHash(string path)
    {
        try
        {
            FileInfo file = new(path);
            if (!file.Exists) return "none";
            if (_hashes.TryGetValue(path, out var known) && known.Length == file.Length && known.Written == file.LastWriteTimeUtc)
                return known.Hash;
            string hash = RankingCharacter.HashText(File.ReadAllText(path).Trim());
            _hashes[path] = (file.Length, file.LastWriteTimeUtc, hash);
            return hash;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "none";
        }
    }
}
