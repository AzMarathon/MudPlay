using System.Collections.Generic;
using System.IO;
using System.Linq;
using MudPlay.Models.Profile;
using MudPlay.Services;

namespace MudPlay.Game.Map;

// Per-set Auto-Lair setup catalogue. Round-trips LairSetups under the
// shared GameDataSetLoopsFolder — same folder as loops, with the
// .lair filename suffix as the schema discriminator. Mirrors
// LoopManager's shape (same load-on-pin, save-fires-Changed,
// delete-on-name lifecycle) so the UI rail can render Loops + Auto-Lair
// Setups side by side with identical wiring.
//
// One-shot migration: earlier exports lived under LegacyBbsLairsFolder as
// plain .json files. The first LoadAll for a given set scans that folder,
// moves each setup into the shared Loops folder with the .lair suffix,
// and removes the legacy directory once empty.
public sealed class LairManager
{
    // Suffix that flags a file in the shared Loops folder as a lair setup.
    public const string LairFileSuffix = ".lair";

    // Intermediate suffix used briefly between the original
    // Lairs/{name}.json layout and the current {name}.lair layout. LoadAll
    // picks these up and renames them to LairFileSuffix so users who
    // shipped a build during that transition window keep their setups.
    public const string LegacyLairFileSuffix = ".lair.json";

    private readonly LogService? _log;
    private readonly Dictionary<string, LairSetup> _setups
        = new(StringComparer.OrdinalIgnoreCase);
    private string? _setName;

    public LairManager(LogService? log = null)
    {
        _log = log;
    }

    // Game-data set the catalogue is bound to, or null when no set is active.
    public string? SetName => _setName;

    // Saved setups for the active set, sorted by name (case-insensitive).
    public IReadOnlyList<LairSetup> Setups =>
        _setups.Values
               .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
               .ToArray();

    // Fired after every mutation (load / save / delete / an outside change taken in).
    public event Action? SetupsChanged;

    private readonly SharedFolderStamp _stamp = new();
    private readonly HashSet<string> _unreadable = new(StringComparer.Ordinal);

    // When the folder was last re-read because another client had changed it; null
    // until it has been.
    public DateTimeOffset? LastOutsideReadAt { get; private set; }

    // The loaded character's favourites, kept on its profile and plugged in the
    // same way LoopManager's are.
    private Func<string, bool>? _isFavorite;
    private Action<string, bool>? _setFavorite;

    public void AttachFavorites(Func<string, bool> isFavorite, Action<string, bool> setFavorite)
    {
        _isFavorite = isFavorite ?? throw new ArgumentNullException(nameof(isFavorite));
        _setFavorite = setFavorite ?? throw new ArgumentNullException(nameof(setFavorite));
        RefreshFavorites();
    }

    public void RefreshFavorites()
    {
        foreach (LairSetup setup in _setups.Values) Stamp(setup);
        SetupsChanged?.Invoke();
    }

    public void SetFavorite(string name, bool favorite)
    {
        if (_setFavorite is null) return;
        _setFavorite(name, favorite);
        RefreshFavorites();
    }

    private void Stamp(LairSetup setup)
    {
        if (_isFavorite is { } isFavorite) setup.Favorite = isFavorite(setup.Name);
    }

    public LairSetup? Get(string name) =>
        _setups.TryGetValue(name, out LairSetup? setup) ? setup : null;

    // Rebuild the in-memory cache from disk for setName. Pass null to clear.
    public void LoadAll(string? setName)
    {
        _setups.Clear();
        _unreadable.Clear();
        // A name that can't be a set's folder is no set: nothing is read for it, and
        // nothing saved while it stands goes anywhere.
        if (!string.IsNullOrWhiteSpace(setName) && !PathGuard.Usable(AppPaths.GameDataRoot, setName))
        {
            PathGuard.Refuse("Loading a set's lair setups", "game-data set name", setName);
            setName = null;
        }
        // Blank is no set either; held as such, a save went to a folder of no name.
        _setName = string.IsNullOrWhiteSpace(setName) ? null : setName;

        if (string.IsNullOrWhiteSpace(setName))
        {
            _stamp.Accept(new Dictionary<string, SharedFolderStamp.FileMark>());
            SetupsChanged?.Invoke();
            return;
        }

        // One-shot migration step A: drain the legacy Lairs/ folder
        // into the shared Loops/ folder. Step B (right after) renames
        // any intermediate .lair.json files to the final .lair suffix.
        // Both steps are safe to re-run.
        MigrateLegacyFolderIfPresent(setName);

        string folder = AppPaths.GameDataSetLoopsFolder(setName);
        MigrateIntermediateSuffixIfPresent(folder);

        if (!Directory.Exists(folder))
        {
            _stamp.Accept(new Dictionary<string, SharedFolderStamp.FileMark>());
            _log?.Info("Lairs", $"no loops folder for set '{setName}'; empty lair catalogue.");
            SetupsChanged?.Invoke();
            return;
        }
        // Marked before the files are read, so a file another client writes meanwhile
        // shows on the next poll instead of being taken as already read.
        IReadOnlyDictionary<string, SharedFolderStamp.FileMark> seen =
            NavTreeScan.Walk(folder, LairFileSuffix).FilesWith(LairFileSuffix);
        _stamp.Accept(seen);

        int loaded = 0;
        int failed = 0;
        // Recurse: setups may live in user-created sub-folders under the
        // shared Loops root. The relative directory is the setup's Folder.
        foreach (string path in Directory.EnumerateFiles(folder, "*" + LairFileSuffix, SearchOption.AllDirectories))
        {
            try
            {
                LairSetup? setup = JsonStore.Load<LairSetup>(path);
                // Schema-validate: a .lair file the user drops in by
                // hand that doesn't match the LairSetup shape (or
                // can't be deserialised) is silently skipped — no-op
                // per user direction.
                if (setup is null || string.IsNullOrWhiteSpace(setup.Name))
                {
                    failed++;
                    NavCatalogueReread.NoteSkipped(_unreadable, seen, path);
                    continue;
                }
                setup.Folder = NavFolders.RelativeFolder(folder, path);
                Stamp(setup);
                _setups[setup.Name] = setup;
                loaded++;
            }
            catch (Exception ex)
            {
                _log?.Warn("Lairs", $"failed to load '{path}': {ex.Message}");
                failed++;
                NavCatalogueReread.NoteSkipped(_unreadable, seen, path);
            }
        }
        _log?.Info("Lairs",
            $"loaded {loaded} setup(s) for set '{setName}'"
          + (failed > 0 ? $" ({failed} failed)" : string.Empty));
        SetupsChanged?.Invoke();
    }

    // Bumped by every read or write of the folder this client makes (see LoopManager).
    internal long Generation => _stamp.Generation;

    // True when the folder now holds lair files other than the ones this client holds.
    internal bool DiffersFrom(NavTreeScan scan) =>
        _setName is not null && _stamp.Differs(scan.FilesWith(LairFileSuffix));

    // Take in at once what another client did to the Loops folder, with no waiting
    // for it to settle (see LoopManager.TakeInOutsideChanges).
    public bool TakeInOutsideChanges()
    {
        if (_setName is null) return false;
        NavTreeScan scan = NavTreeScan.Walk(AppPaths.GameDataSetLoopsFolder(_setName), LairFileSuffix);
        if (!scan.Complete || PlanOutside(scan) is not { } plan) return false;
        plan()();
        return true;
    }

    // Plan the re-read of the lair files in scan, or null when they are the ones held;
    // two steps, as LoopManager.PlanOutside: read off any thread, swap on the UI thread.
    internal Func<Action>? PlanOutside(NavTreeScan scan)
    {
        if (_setName is null || !DiffersFrom(scan)) return null;
        string root = AppPaths.GameDataSetLoopsFolder(_setName);
        IReadOnlyDictionary<string, SharedFolderStamp.FileMark> files = scan.FilesWith(LairFileSuffix);
        Dictionary<string, SharedFolderStamp.FileMark> known = new(_stamp.Known);
        Dictionary<string, LairSetup> heldByPath = new(StringComparer.Ordinal);
        foreach (LairSetup s in _setups.Values) heldByPath[PathOf(root, s)] = s;
        HashSet<string> heldPaths = new(heldByPath.Keys, StringComparer.Ordinal);
        HashSet<string> reported = new(_unreadable);
        return () =>
        {
            Dictionary<string, NavCatalogueReread.Outcome<LairSetup>> outcomes =
                NavCatalogueReread.Preread(files, known, heldPaths, reported, path => ReadOutsideFile(root, path));
            return () => ApplyOutside(root, files, heldByPath, outcomes);
        };
    }

    private void ApplyOutside(string root, IReadOnlyDictionary<string, SharedFolderStamp.FileMark> files,
        IReadOnlyDictionary<string, LairSetup> heldByPath, IReadOnlyDictionary<string, NavCatalogueReread.Outcome<LairSetup>> outcomes)
    {
        NavReread<LairSetup> found = NavCatalogueReread.Run(_stamp.Known, files, _setups, heldByPath,
            s => s.Name,
            (a, b) => NavCatalogueReread.SameRecord(a, a.Folder, b, b.Folder),
            outcomes,
            path => ReadOutsideFile(root, path),
            _unreadable,
            (path, why) => _log?.Warn("Lairs", $"setup file '{path}' changed on disk but can't be read ({why}); keeping the copy held."));
        _stamp.Accept(files);
        if (!found.AnyItemChanged) return;

        foreach (string name in found.Added.Concat(found.Changed)) Stamp(found.Fresh[name]);
        _setups.Clear();
        foreach ((string name, LairSetup setup) in found.Fresh) _setups[name] = setup;
        LastOutsideReadAt = DateTimeOffset.UtcNow;
        _log?.Info("Lairs", NavCatalogueReread.Summary("lair setups", found));
        SetupsChanged?.Invoke();
    }

    private static string PathOf(string root, LairSetup setup) =>
        Path.GetFullPath(Path.Combine(NavFolders.ToDirectory(root, setup.Folder), SafeFileName(setup.Name)));

    // Runs on a worker thread: it touches nothing of the catalogue.
    private static LairSetup? ReadOutsideFile(string root, string path)
    {
        LairSetup? setup = JsonStore.Load<LairSetup>(path);
        if (setup is null || string.IsNullOrWhiteSpace(setup.Name)) return null;
        setup.Folder = NavFolders.RelativeFolder(root, path);
        return setup;
    }

    // Persist setup under GameDataSetLoopsFolder with the LairFileSuffix
    // filename extension. False when nothing was saved: no set active, or a folder
    // that can't be one (NavFolders.WhyNotFolder), refused with the reason in the log.
    public bool Save(LairSetup setup)
    {
        ArgumentNullException.ThrowIfNull(setup);
        if (string.IsNullOrWhiteSpace(setup.Name))
            throw new ArgumentException("Setup name is required.", nameof(setup));
        if (_setName is null) return false;

        string root = AppPaths.GameDataSetLoopsFolder(_setName);
        if (NavFolders.WhyNotFolder(root, setup.Folder) is { } whyNot)
        {
            _log?.Warn("Lairs", $"lair setup '{setup.Name}' not saved: {whyNot}");
            return false;
        }
        setup.SchemaVersion = 1;
        setup.Folder = NavFolders.Normalize(setup.Folder);
        string dir = NavFolders.ToDirectory(root, setup.Folder);
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, SafeFileName(setup.Name));
        // A setup name is unique set-wide, so a folder change is a move: the stale
        // file for this name (in any folder) goes after the write, so another client
        // reading the folder between the two sees the setup twice, never not at all.
        JsonStore.Save(path, setup);
        _stamp.NoteWritten(path);
        DeleteFileForName(root, setup.Name, keep: path);
        Stamp(setup);
        _setups[setup.Name] = setup;
        SetupsChanged?.Invoke();
        return true;
    }

    // Move the setup named name into folder (empty = Loops root). No-op
    // when the setup isn't in the catalogue or no set is active.
    public bool Move(string name, string? folder)
    {
        if (Get(name) is not { } setup) return false;
        string target = NavFolders.Normalize(folder);
        if (string.Equals(NavFolders.Normalize(setup.Folder), target, StringComparison.OrdinalIgnoreCase))
            return false;
        // The catalogue's own object: a refused move leaves it where it was.
        string previous = setup.Folder;
        setup.Folder = target;
        if (Save(setup)) return true;
        setup.Folder = previous;
        return false;
    }

    // Delete the setup named name. No-op when not present or no set is active.
    public bool Delete(string name)
    {
        if (_setName is null) return false;
        if (!_setups.Remove(name)) return false;

        _stamp.NoteHeldChanged();
        DeleteFileForName(AppPaths.GameDataSetLoopsFolder(_setName), name);
        _setFavorite?.Invoke(name, false);
        SetupsChanged?.Invoke();
        return true;
    }

    // Delete the on-disk .lair file for name wherever it lives under root
    // (the name is unique set-wide). Best-effort — failures are logged.
    private void DeleteFileForName(string root, string name, string? keep = null)
    {
        if (!Directory.Exists(root)) return;
        string fileName = SafeFileName(name);
        foreach (string path in Directory.EnumerateFiles(root, fileName, SearchOption.AllDirectories))
        {
            if (keep is not null && string.Equals(Path.GetFullPath(path), Path.GetFullPath(keep), StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                File.Delete(path);
                _stamp.NoteGone(path);
            }
            catch (Exception ex)
            {
                _log?.Warn("Lairs", $"failed to delete setup file '{path}': {ex.Message}");
            }
        }
    }

    // ----- internals -----------------------------------------------

    private void MigrateLegacyFolderIfPresent(string setName)
    {
        string legacy = AppPaths.LegacyBbsLairsFolder(setName);
        if (!Directory.Exists(legacy)) return;

        string target = AppPaths.GameDataSetLoopsFolder(setName);
        Directory.CreateDirectory(target);

        int moved = 0;
        foreach (string srcPath in Directory.EnumerateFiles(legacy, "*.json"))
        {
            string baseName = Path.GetFileNameWithoutExtension(srcPath);
            if (string.IsNullOrWhiteSpace(baseName)) continue;
            // Land on the FINAL suffix (.lair) — skip the intermediate
            // .lair.json stage entirely for legacy-folder migration so
            // users don't see two renames in their git diff.
            string destPath = Path.Combine(target, baseName + LairFileSuffix);
            if (File.Exists(destPath)) continue; // already migrated; user-edited copy wins

            try
            {
                File.Move(srcPath, destPath);
                moved++;
            }
            catch (Exception ex)
            {
                _log?.Warn("Lairs",
                    $"migration: failed to move '{srcPath}' → '{destPath}': {ex.Message}");
            }
        }

        // Remove the empty legacy folder so subsequent loads short-
        // circuit at the Directory.Exists check.
        try
        {
            if (!Directory.EnumerateFileSystemEntries(legacy).Any())
                Directory.Delete(legacy, recursive: false);
        }
        catch (Exception ex)
        {
            _log?.Warn("Lairs", $"migration: failed to remove legacy folder '{legacy}': {ex.Message}");
        }

        if (moved > 0)
            _log?.Info("Lairs",
                $"migrated {moved} setup(s) from legacy Lairs/ → Loops/{LairFileSuffix}.");
    }

    // Rename intermediate {name}.lair.json files to the final {name}.lair
    // suffix. Covers the very short window in which the unification build
    // shipped with the longer suffix.
    private void MigrateIntermediateSuffixIfPresent(string folder)
    {
        if (!Directory.Exists(folder)) return;
        int moved = 0;
        foreach (string srcPath in Directory.EnumerateFiles(folder, "*" + LegacyLairFileSuffix))
        {
            // Strip the FULL legacy suffix to get the user's chosen
            // name back — Path.GetFileNameWithoutExtension would only
            // peel ".json".
            string fileName = Path.GetFileName(srcPath);
            string baseName = fileName.Substring(0,
                fileName.Length - LegacyLairFileSuffix.Length);
            if (string.IsNullOrWhiteSpace(baseName)) continue;
            string destPath = Path.Combine(folder, baseName + LairFileSuffix);
            if (File.Exists(destPath)) continue;
            try
            {
                File.Move(srcPath, destPath);
                moved++;
            }
            catch (Exception ex)
            {
                _log?.Warn("Lairs",
                    $"migration: failed to rename '{srcPath}' → '{destPath}': {ex.Message}");
            }
        }
        if (moved > 0)
            _log?.Info("Lairs",
                $"migrated {moved} setup(s) from {LegacyLairFileSuffix} → {LairFileSuffix}.");
    }

    private static string SafeFileName(string name)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (char c in name)
            sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        return sb.ToString() + LairFileSuffix;
    }
}
