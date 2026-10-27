using System.IO;
using System.Linq;
using MudPlay.Models.Profile;
using MudPlay.Models.Settings;

namespace MudPlay.Services;

// The structural operations on a BBS's realms — add, rename, remove, pick the game
// data — shared by Profile Management (which commits each one at once) and the
// Settings BBS tab (which stages them until OK and saves bbs.json itself, so it
// calls only the data halves: MoveData / DeleteContents). A realm's name is also
// its data folder's, so a rename moves the folder and re-points the characters
// assigned to it. Removing a realm removes it entirely, like removing a BBS: its
// character profiles and its data folder are deleted (callers confirm first,
// naming the characters).
public sealed class RealmCatalog
{
    private readonly BbsProfileStore _bbs;
    private readonly ProfileService _profiles;
    private readonly LogService? _log;

    public RealmCatalog(BbsProfileStore bbs, ProfileService profiles, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(bbs);
        ArgumentNullException.ThrowIfNull(profiles);
        _bbs = bbs;
        _profiles = profiles;
        _log = log;
    }

    // Why name can't be used for a realm whose siblings are named taken (the realm
    // being renamed left out), or null when it can.
    public static string? NameProblem(string name, IEnumerable<string> taken)
    {
        if (name.Length == 0) return "A realm needs a name.";
        // A realm's name is its data folder's.
        if (PathGuard.WhyNotPlain(name, "realm name") is { } notPlain) return notPlain;
        if (taken.Any(t => string.Equals(t, name, StringComparison.OrdinalIgnoreCase)))
            return $"This BBS already has a realm named “{name}”.";
        return null;
    }

    // "Realm N" for the first N not in use.
    public static string NextFreeName(BbsProfile bbs)
    {
        int n = bbs.Realms.Count + 1;
        while (bbs.Realms.Any(r => string.Equals(r.Name, $"Realm {n}", StringComparison.OrdinalIgnoreCase))) n++;
        return $"Realm {n}";
    }

    // Add an empty realm (default settings, no data) and return its name.
    public string? Add(string bbsName)
    {
        if (_bbs.Get(bbsName) is not { } bbs) return null;
        RealmProfile realm = new() { Name = NextFreeName(bbs) };
        bbs.Realms.Add(realm);
        _bbs.Save(bbs);
        _log?.Info("BBS", $"Added realm '{realm.Name}' to '{bbsName}'.");
        return realm.Name;
    }

    // Rename a realm; returns why it couldn't, or null once done.
    public string? Rename(string bbsName, string oldName, string newName)
    {
        if (_bbs.Get(bbsName) is not { } bbs
            || bbs.Realms.FirstOrDefault(r => string.Equals(r.Name, oldName, StringComparison.OrdinalIgnoreCase))
                is not { } realm)
            return "That realm no longer exists.";
        newName = newName.Trim();
        if (string.Equals(newName, realm.Name, StringComparison.Ordinal)) return null;
        if (NameProblem(newName, bbs.Realms.Where(r => r != realm).Select(r => r.Name)) is { } problem)
            return problem;
        string previous = realm.Name;
        realm.Name = newName;
        _bbs.Save(bbs, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [previous] = newName });
        MoveData(bbsName, previous, newName);
        return null;
    }

    // Remove a realm (a BBS keeps at least one), deleting the characters that play
    // it and its data folder.
    public bool Remove(string bbsName, string name)
    {
        if (_bbs.Get(bbsName) is not { Realms.Count: > 1 } bbs) return false;
        IReadOnlyList<ProfileRef> characters = CharactersOn(bbsName, name);   // before the realm goes
        int removed = bbs.Realms.RemoveAll(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
        if (removed == 0) return false;
        _bbs.Save(bbs);
        DeleteContents(bbsName, name, characters);
        return true;
    }

    // Point a realm at a game-data set (null = the Global default).
    public void SetGameDataSet(string bbsName, string realmName, string? set)
    {
        if (_bbs.Get(bbsName) is not { } bbs || bbs.RealmFor(realmName) is not { } realm) return;
        if (string.Equals(realm.ActiveGameDataSet, set, StringComparison.OrdinalIgnoreCase)) return;
        realm.ActiveGameDataSet = set;
        _bbs.Save(bbs);
        _log?.Info("BBS", $"Realm '{realm.Name}' on '{bbsName}' now uses game data '{set ?? "global default"}'.");
    }

    // A realm's name is its data folder's, under the BBS's Realms folder, and a
    // bbs.json can hold any text for one. A realm that is there may have no folder
    // yet (nothing collected) and a name from before names were checked, so it only
    // has to name a folder directly inside Realms; a name being given to one has to
    // be usable (PathGuard.Usable). Otherwise the refusal is logged and nothing is
    // touched.
    private static bool RealmNamed(string operation, string bbsName, string? name, bool given = false)
    {
        string realms = AppPaths.RealmsDir(bbsName);
        if (PathGuard.Usable(AppPaths.BbsDir, bbsName)
            && (given ? PathGuard.Usable(realms, name) : PathGuard.Child(realms, name) is not null))
            return true;
        PathGuard.Refuse(operation, "realm name", name);
        return false;
    }

    // A realm's name changed: move its data folder and re-point its characters.
    public void MoveData(string bbsName, string oldName, string newName)
    {
        const string operation = "Renaming a realm";
        if (!RealmNamed(operation, bbsName, oldName) || !RealmNamed(operation, bbsName, newName, given: true)) return;
        string from = AppPaths.RealmFolder(bbsName, oldName);
        string to = AppPaths.RealmFolder(bbsName, newName);
        if (Directory.Exists(from) && !Directory.Exists(to)) Directory.Move(from, to);
        _profiles.RenameRealm(bbsName, oldName, newName);
        _log?.Info("BBS", $"Renamed realm '{oldName}' → '{newName}' on '{bbsName}'.");
    }

    // A realm was removed: delete the characters that played it (taken before it
    // went, since a character with no realm plays the first) and its data folder.
    public void DeleteContents(string bbsName, string name, IReadOnlyList<ProfileRef> characters)
    {
        const string operation = "Removing a realm";
        if (!RealmNamed(operation, bbsName, name)) return;
        foreach (ProfileRef character in characters)
            _profiles.DeleteProfile(character.Bbs, character.Name);
        // Only ever a folder inside this BBS's Realms folder.
        PathGuard.DeleteTree(AppPaths.RealmFolder(bbsName, name), AppPaths.RealmsDir(bbsName), operation);
        _log?.Info("BBS", $"Removed realm '{name}' from '{bbsName}' with its {characters.Count} character(s) "
            + (characters.Count > 0 ? $"({string.Join(", ", characters.Select(c => c.Name))}) " : "")
            + "and its collected data.");
    }

    // The saved characters on bbsName playing realmName (null / unknown count as the
    // first realm).
    public IReadOnlyList<ProfileRef> CharactersOn(string bbsName, string realmName)
    {
        BbsProfile? bbs = _bbs.Get(bbsName);
        return _profiles.ListAll()
            .Where(r => string.Equals(r.Bbs, bbsName, StringComparison.OrdinalIgnoreCase))
            .Where(r => string.Equals(bbs?.RealmFor(_profiles.RealmOf(r))?.Name, realmName,
                StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // The confirmation for removing a realm: plain when it's empty, and when it has
    // characters, spelled out that they're deleted for good.
    public static (string Body, string YesLabel) RemovalPrompt(
        string bbsName, string realmName, IReadOnlyList<ProfileRef> characters)
    {
        const string data = "Its collected data (players seen, blacklist, leaderboard, roomba, quests, boss timers) is deleted too.";
        if (characters.Count == 0)
            return ($"Remove the realm “{realmName}” from “{bbsName}”? No characters play it. {data}", "Remove realm");
        string names = string.Join(", ", characters.Select(c => c.Name));
        return ($"Remove the realm “{realmName}” from “{bbsName}”?\n\n"
                + $"This PERMANENTLY DELETES the {characters.Count} character profile{(characters.Count == 1 ? "" : "s")} "
                + $"on this realm: {names}.\n\n{data}\n\nThis can't be undone.",
            characters.Count == 1 ? "Delete realm and its character" : "Delete realm and characters");
    }
}
