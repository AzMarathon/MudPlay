using System.IO;
using System.Linq;
using MudPlay.Models.Profile;
using MudPlay.Models.Settings;

namespace MudPlay.Services;

// The structural operations on a BBS's realms — add, rename, remove, pick the game
// data — shared by Profile Management (which commits each one at once) and the
// Settings BBS tab (which stages them until OK and saves bbs.json itself, so it
// calls only the data halves: MoveData / ReleaseCharacters). A realm's name is
// also its data folder's, so a rename moves the folder and re-points the
// characters assigned to it; a removal sends them to the first realm and leaves
// the folder on disk.
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
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name is "." or "..")
            return $"“{name}” can't be used as a realm name.";
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
        _bbs.Save(bbs);
        MoveData(bbsName, previous, newName);
        return null;
    }

    // Remove a realm (a BBS keeps at least one); its characters go to the first.
    public bool Remove(string bbsName, string name)
    {
        if (_bbs.Get(bbsName) is not { Realms.Count: > 1 } bbs) return false;
        int removed = bbs.Realms.RemoveAll(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
        if (removed == 0) return false;
        _bbs.Save(bbs);
        ReleaseCharacters(bbsName, name);
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

    // A realm's name changed: move its data folder and re-point its characters.
    public void MoveData(string bbsName, string oldName, string newName)
    {
        string from = AppPaths.RealmFolder(bbsName, oldName);
        string to = AppPaths.RealmFolder(bbsName, newName);
        if (Directory.Exists(from) && !Directory.Exists(to)) Directory.Move(from, to);
        _profiles.RenameRealm(bbsName, oldName, newName);
        _log?.Info("BBS", $"Renamed realm '{oldName}' → '{newName}' on '{bbsName}'.");
    }

    // A realm was removed: its characters go to the BBS's first realm. Its data
    // folder stays on disk.
    public void ReleaseCharacters(string bbsName, string name)
    {
        _profiles.RenameRealm(bbsName, name, null);
        _log?.Info("BBS", $"Removed realm '{name}' from '{bbsName}'; its characters now play the first realm.");
    }

    // How many saved characters on bbsName play realmName (null / unknown count
    // as the first realm).
    public int CharacterCount(string bbsName, string realmName)
    {
        BbsProfile? bbs = _bbs.Get(bbsName);
        return _profiles.ListAll()
            .Where(r => string.Equals(r.Bbs, bbsName, StringComparison.OrdinalIgnoreCase))
            .Count(r => string.Equals(bbs?.RealmFor(_profiles.RealmOf(r))?.Name, realmName,
                StringComparison.OrdinalIgnoreCase));
    }
}
