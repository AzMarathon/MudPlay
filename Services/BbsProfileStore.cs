using System.IO;
using System.Linq;
using MudPlay.Models.Settings;

namespace MudPlay.Services;

// Owns BBS/{name}/ — one folder per BBS, holding the primary bbs.json
// (connection info + the realms it hosts), the character profiles under
// profiles/, and one folder per realm under Realms/ with everything collected
// while playing it (AppPaths.RealmFolder).
public sealed class BbsProfileStore
{
    // The Global fallback game-data set — lets the realm migration find the boss
    // timers of a BBS that never named its own set. null in tests.
    private readonly Func<string?>? _defaultGameDataSet;
    private readonly LogService? _log;

    public BbsProfileStore(Func<string?>? defaultGameDataSet = null, LogService? log = null)
    {
        _defaultGameDataSet = defaultGameDataSet;
        _log = log;
    }

    // Load a single BBS profile by name. Returns null if no bbs.json exists for
    // that name. The folder may exist with only side-files (e.g. mid-migration)
    // — that still counts as "no BBS profile".
    public BbsProfile? Get(string bbsName)
    {
        if (string.IsNullOrWhiteSpace(bbsName)) return null;
        BbsProfile? profile = JsonStore.Load<BbsProfile>(AppPaths.BbsProfileFile(bbsName));

        // The FOLDER is the source of truth for a BBS's identity (ProfileService:
        // "folder location is the source of truth"), and every caller passes the
        // folder name here. Reconcile the loaded Name to it: a bbs.json whose Name
        // drifted from its folder — a folder duplicated to make a same-host sibling
        // realm, its Name left pointing at the original — otherwise mis-keys
        // everything read off BbsProfile.Name. Both the BBS-scoped resource folders
        // (blacklist, leaderboard) AND the per-BBS logon-step lookup key on it, so a
        // "Paradigm PVE" profile whose json still said "Paradigm PVP" ran the PVP
        // realm-select step and logged into the wrong realm (report
        // paradigm-20260825-102259). Rename already keeps them in sync; a hand-copied
        // folder doesn't, so heal it here on load.
        if (profile is not null && !string.Equals(profile.Name, bbsName, StringComparison.Ordinal))
            profile.Name = bbsName;

        // A BBS always has at least one realm. One saved before realms existed gets
        // its first realm (and its collected data) here, once, and is re-saved.
        if (profile is not null && profile.Realms.Count == 0)
        {
            string path = AppPaths.BbsProfileFile(bbsName);
            string? raw = File.Exists(path) ? File.ReadAllText(path) : null;
            RealmProfile realm = RealmMigration.CreateFirstRealm(bbsName, raw, _defaultGameDataSet?.Invoke());
            profile.Realms.Add(realm);
            JsonStore.Save(path, profile);
            _log?.Info("BBS",
                $"'{bbsName}' now has realms: its realm settings and collected data moved into realm '{realm.Name}'.");
        }

        return profile;
    }

    // Persist a BBS profile to Data/BBS/{Name}/bbs.json, creating the folder on
    // first save.
    public void Save(BbsProfile profile)
    {
        if (profile is null) throw new ArgumentNullException(nameof(profile));
        if (string.IsNullOrWhiteSpace(profile.Name))
            throw new ArgumentException("BbsProfile.Name is required for save.", nameof(profile));
        if (profile.Realms.Count == 0) profile.Realms.Add(new RealmProfile { Name = profile.Name });

        Directory.CreateDirectory(AppPaths.BbsFolder(profile.Name));
        JsonStore.Save(AppPaths.BbsProfileFile(profile.Name), profile);
    }

    // True when a BBS folder already occupies this name. Tests the folder, not
    // the bbs.json — Rename moves the whole folder and Directory.Move throws if
    // the destination exists at all, even a stray folder with no bbs.json (a
    // half-deleted BBS, or one holding only nested profiles). Callers gate their
    // rename clash-check on this so they refuse the same cases Move would reject.
    public bool Exists(string bbsName) =>
        !string.IsNullOrWhiteSpace(bbsName) && Directory.Exists(AppPaths.BbsFolder(bbsName));

    // Delete a BBS — removes the entire Data/BBS/{name}/ folder (primary file +
    // all side-files). No-op if the folder doesn't exist.
    public void Delete(string bbsName)
    {
        if (string.IsNullOrWhiteSpace(bbsName)) return;
        string folder = AppPaths.BbsFolder(bbsName);
        if (Directory.Exists(folder))
            Directory.Delete(folder, recursive: true);
    }

    // Rename a BBS in place: move the whole Data/BBS/{old}/ folder — bbs.json,
    // every override side-file, AND every nested character profile — to the new
    // name, then rewrite the primary file's Name field. A folder move keeps the
    // nested profiles intact; a Delete(old)+Save(new) would recursively destroy
    // them. No-op if the source folder is missing; throws if the destination
    // already exists (the caller resolves clashes first). Case-only renames
    // never reach here — the Settings Apply path gates on an OrdinalIgnoreCase
    // inequality, and Directory.Move can't do a case-only move on a
    // case-insensitive filesystem.
    public void Rename(string oldName, string newName)
    {
        if (string.IsNullOrWhiteSpace(oldName) || string.IsNullOrWhiteSpace(newName)) return;
        string oldFolder = AppPaths.BbsFolder(oldName);
        string newFolder = AppPaths.BbsFolder(newName);
        if (!Directory.Exists(oldFolder)) return;
        if (Directory.Exists(newFolder))
            throw new IOException($"A BBS folder already exists at '{newFolder}'.");

        Directory.Move(oldFolder, newFolder);

        // The moved bbs.json still carries the old Name — bring it in line.
        if (Get(newName) is { } profile)
        {
            profile.Name = newName;
            JsonStore.Save(AppPaths.BbsProfileFile(newName), profile);
        }
    }

    // Enumerate every BBS that has a primary bbs.json on disk. The folder name
    // (= BBS name) is yielded, alphabetical order optional at the caller.
    // Folders missing a bbs.json are skipped — they aren't fully initialised yet.
    public IEnumerable<string> ListNames()
    {
        if (!Directory.Exists(AppPaths.BbsDir)) yield break;
        foreach (string folder in Directory.EnumerateDirectories(AppPaths.BbsDir))
        {
            string name = Path.GetFileName(folder);
            if (string.IsNullOrEmpty(name)) continue;
            if (!File.Exists(AppPaths.BbsProfileFile(name))) continue;
            yield return name;
        }
    }
}
