using System.Collections.Generic;
using System.IO;
using System.Linq;
using MudPlay.Game.Map;
using MudPlay.Models.Profile;

namespace MudPlay.Services;

// Per-character favourite-room bookmarks for the Navigation GOTO pane, kept on the
// profile (CharacterProfile.Favorites). They were kept per game-data set, shared by
// every character on it, and two realms on the same game data then shared one
// list; they are the character's own now, like its macros and aliases.
//
// A profile takes its list the first time it loads (Favorites is null until then):
// a character from before the move takes a copy of the list its game data carried,
// and a character made since, or the default profile, takes the bundled starters
// for its realm. That needs the profile's game-data set, so a profile loaded with
// none waits for one.
//
// Singleton in AppServices. Consumers (Navigation view-model) subscribe to
// Changed for refresh; the store doesn't push a sorted view itself — sort order
// is a UI concern.
//
// The label stored per entry is the user's chosen text from the "Add to
// favorites" prompt. When the label is null/empty, callers fall back to the
// room's graph display name (so the GOTO row still reads sensibly).
public sealed class FavoritesStore
{
    // How many favourites may be starred (shown in the terminal right-click
    // Favorites flyout) at once. Enforced on the write side by SetStarred.
    public const int MaxStarred = 10;

    private readonly ProfileService _profile;
    private readonly Func<string?> _profileSet;
    private readonly LogService? _log;
    private readonly Dictionary<RoomKey, FavoriteRoom> _favorites = new();

    // Empty folders the user created but hasn't filled yet.
    private readonly HashSet<string> _emptyFolders = new(StringComparer.OrdinalIgnoreCase);

    // profileSet names the game-data set the loaded profile runs on (its realm's,
    // else the default), which is where a first list is copied from.
    public FavoritesStore(ProfileService profile, GameDataCache cache, Func<string?> profileSet,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(profileSet);
        _profile = profile;
        _profileSet = profileSet;
        _log = log;

        _profile.ProfileLoaded += Hydrate;
        _profile.ProfileClosed += OnProfileClosed;
        // A profile that loaded before any game data was there takes its list once
        // a set is.
        cache.ActiveSetChanged += _ =>
        {
            if (_profile.Current is { Favorites: null } waiting) Hydrate(waiting);
        };
        if (_profile.Current is { } current) Hydrate(current);
    }

    // Read-only snapshot of every favourite the loaded character has.
    public IReadOnlyCollection<FavoriteRoom> All => _favorites.Values;

    // True when key is currently bookmarked.
    public bool IsFavorite(RoomKey key) => _favorites.ContainsKey(key);

    // True when key is a starred quick-access favourite.
    public bool IsStarred(RoomKey key) => _favorites.TryGetValue(key, out FavoriteRoom? f) && f.Starred;

    // How many favourites are starred right now.
    public int StarredCount => _favorites.Values.Count(f => f.Starred);

    // The starred favourites (uncapped; the flyout caps the display at MaxStarred
    // as a safety net for a hand-edited Favorites.json). Order is unspecified —
    // the flyout resolves display names and sorts.
    public IReadOnlyList<FavoriteRoom> StarredFavorites() =>
        _favorites.Values.Where(f => f.Starred).ToList();

    // Toggle key's quick-access star. Persists + fires Changed on a real change.
    // Returns false without changing anything when turning the star ON would push
    // past MaxStarred (the write-side cap), when the key isn't bookmarked, or when
    // no profile is loaded. Turning OFF and no-op re-sets (already in the wanted state)
    // return true.
    public bool SetStarred(RoomKey key, bool starred)
    {
        if (_profile.Current is null) return false;
        if (!_favorites.TryGetValue(key, out FavoriteRoom? entry)) return false;
        if (entry.Starred == starred) return true;
        if (starred && StarredCount >= MaxStarred) return false;

        entry.Starred = starred;
        Persist();
        _log?.Info("Favorites", $"{(starred ? "starred" : "unstarred")} {key}");
        Changed?.Invoke();
        return true;
    }

    // Normalised folder path of key, or string.Empty (root / not a favourite).
    public string FolderOf(RoomKey key) =>
        _favorites.TryGetValue(key, out FavoriteRoom? f) ? NavFolders.Normalize(f.Folder) : string.Empty;

    // Every folder node the GOTO tree must render — the ancestors of each
    // favourite's folder plus any remembered empty folders. Excludes the root.
    // Order is unspecified; the UI sorts.
    public IReadOnlyCollection<string> AllFolders
    {
        get
        {
            var paths = new List<string?>(_emptyFolders);
            foreach (FavoriteRoom f in _favorites.Values) paths.Add(f.Folder);
            return NavFolders.ExpandAncestors(paths);
        }
    }

    // Fires after every mutation (add / rename / remove / move / folder op / profile swap).
    public event Action? Changed;

    // Bookmark key with an optional user-typed label and target folder. No-op
    // when the key is already in the list (rename via Rename or remove + add) or
    // no profile is loaded. Persists immediately.
    public void Add(RoomKey key, string? label = null, string? folder = null)
    {
        if (_profile.Current is null) return;
        if (_favorites.ContainsKey(key)) return;

        string norm = NavFolders.Normalize(folder);
        FavoriteRoom entry = new(key.Map, key.Room, label, norm.Length == 0 ? null : norm);
        _favorites[key] = entry;
        // The folder is now non-empty — drop any empty-folder record for it.
        if (norm.Length != 0) _emptyFolders.Remove(norm);
        Persist();
        _log?.Info("Favorites", $"added {key}" + (label is null ? string.Empty : $" ('{label}')"));
        Changed?.Invoke();
    }

    // Update an existing favourite's label. No-op when not bookmarked or no profile is loaded.
    public void Rename(RoomKey key, string? newLabel)
    {
        if (_profile.Current is null) return;
        if (!_favorites.TryGetValue(key, out FavoriteRoom? entry)) return;

        entry.Label = newLabel;
        Persist();
        _log?.Info("Favorites", $"renamed {key} → '{newLabel}'");
        Changed?.Invoke();
    }

    // Re-point a favourite at a different room and/or relabel it. Favourites are
    // keyed by room, so a coordinate change removes the old entry and adds one at
    // the new key carrying the folder over; a same-coordinate edit is a relabel.
    public void Edit(RoomKey oldKey, RoomKey newKey, string? newLabel)
    {
        if (oldKey.Equals(newKey))
        {
            Rename(oldKey, newLabel);
            return;
        }
        string? folder = FolderOf(oldKey);
        Remove(oldKey);
        Add(newKey, newLabel, folder);
    }

    // Remove the favourite. No-op when not bookmarked or no profile is loaded.
    public void Remove(RoomKey key)
    {
        if (_profile.Current is null) return;
        if (!_favorites.Remove(key)) return;

        Persist();
        _log?.Info("Favorites", $"removed {key}");
        Changed?.Invoke();
    }

    // Move a bookmarked room into folder (empty = root). No-op when not
    // bookmarked, no profile loaded, or already there. Persists immediately.
    public void MoveFavorite(RoomKey key, string? folder)
    {
        if (_profile.Current is null) return;
        if (!_favorites.TryGetValue(key, out FavoriteRoom? entry)) return;

        string norm = NavFolders.Normalize(folder);
        string from = NavFolders.Normalize(entry.Folder);
        if (string.Equals(from, norm, StringComparison.OrdinalIgnoreCase)) return;

        entry.Folder = norm.Length == 0 ? null : norm;
        if (norm.Length != 0) _emptyFolders.Remove(norm);
        Persist();
        _log?.Info("Favorites", $"moved {key} → '{(norm.Length == 0 ? "(root)" : norm)}'");
        Changed?.Invoke();
    }

    // Create an empty folder so it shows in the tree before any favourite is
    // filed under it. No-op when no profile is loaded or the folder already exists
    // (as an empty record or via a favourite).
    public void AddFolder(string path)
    {
        if (_profile.Current is null) return;
        string norm = NavFolders.Normalize(path);
        if (norm.Length == 0) return;
        if (AllFolders.Any(f => string.Equals(f, norm, StringComparison.OrdinalIgnoreCase))) return;

        _emptyFolders.Add(norm);
        Persist();
        Changed?.Invoke();
    }

    // Rename folder oldPath (and every sub-folder / favourite beneath it) to
    // newPath. No-op when no profile is loaded or the path is the root.
    public void RenameFolder(string oldPath, string newPath)
    {
        if (_profile.Current is null) return;
        string from = NavFolders.Normalize(oldPath);
        string to = NavFolders.Normalize(newPath);
        if (from.Length == 0 || to.Length == 0) return;
        if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase)) return;

        foreach (FavoriteRoom f in _favorites.Values)
        {
            if (!NavFolders.IsSelfOrDescendant(from, f.Folder)) continue;
            string rebased = NavFolders.Rebase(from, to, NavFolders.Normalize(f.Folder));
            f.Folder = rebased.Length == 0 ? null : rebased;
        }
        RebaseEmptyFolders(from, to);
        Persist();
        _log?.Info("Favorites", $"renamed folder '{from}' → '{to}'");
        Changed?.Invoke();
    }

    // Remove folder path. When moveContentsToParent is true, favourites and
    // sub-folders beneath it are re-parented one level up; otherwise the caller
    // must have emptied it first (anything still inside is also re-parented to
    // keep favourites from being orphaned). No-op at the root or with no profile loaded.
    public void RemoveFolder(string path, bool moveContentsToParent = true)
    {
        if (_profile.Current is null) return;
        string from = NavFolders.Normalize(path);
        if (from.Length == 0) return;
        string parent = NavFolders.Parent(from);

        foreach (FavoriteRoom f in _favorites.Values)
        {
            if (!NavFolders.IsSelfOrDescendant(from, f.Folder)) continue;
            string rebased = moveContentsToParent
                ? NavFolders.Rebase(from, parent, NavFolders.Normalize(f.Folder))
                : parent;
            f.Folder = rebased.Length == 0 ? null : rebased;
        }

        _emptyFolders.RemoveWhere(e => NavFolders.IsSelfOrDescendant(from, e));

        Persist();
        _log?.Info("Favorites", $"removed folder '{from}'");
        Changed?.Invoke();
    }

    private void RebaseEmptyFolders(string from, string to)
    {
        var rebased = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string e in _emptyFolders)
        {
            string norm = NavFolders.Normalize(e);
            if (NavFolders.IsSelfOrDescendant(from, norm)) norm = NavFolders.Rebase(from, to, norm);
            if (norm.Length != 0) rebased.Add(norm);
        }
        _emptyFolders.Clear();
        foreach (string r in rebased) _emptyFolders.Add(r);
    }

    // Put the whole list back on the profile and save it.
    private void Persist()
    {
        if (_profile.Current is not { } current) return;
        current.Favorites = _favorites.Values.ToList();
        current.FavoriteFolders = _emptyFolders.ToList();
        _profile.Save();
    }

    private void OnProfileClosed()
    {
        _favorites.Clear();
        _emptyFolders.Clear();
        Changed?.Invoke();
    }

    private void Hydrate(CharacterProfile profile)
    {
        _favorites.Clear();
        _emptyFolders.Clear();
        if (profile.Favorites is null) TakeFirstList(profile);

        if (profile.Favorites is { } list)
            foreach (FavoriteRoom f in list) _favorites[new RoomKey(f.Map, f.Room)] = f;
        if (profile.FavoriteFolders is { } folders)
            foreach (string e in folders)
            {
                string norm = NavFolders.Normalize(e);
                if (norm.Length != 0) _emptyFolders.Add(norm);
            }
        Changed?.Invoke();
    }

    // Give a profile its first list. Only set on the profile here, not saved: this
    // runs inside the profile load, when the other stores still hold the previous
    // profile's state and a save would write theirs into this one. The next save
    // carries it, and until then a reload takes the same list again.
    private void TakeFirstList(CharacterProfile profile)
    {
        if (_profileSet() is not { Length: > 0 } set) return;

        bool shared = profile.PredatesOwnLists && _profile.CurrentProfileName is not null;
        string source = shared
            ? AppPaths.GameDataSetFavoritesFile(set)
            : Path.Combine(AppPaths.BundledNavSeedDir(GameDataRealm.Resolve(set)), "Favorites.json");

        FavoritesFile? file = null;
        try { file = JsonStore.Load<FavoritesFile>(source); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            // Unreadable — start empty rather than fail the load.
            _log?.Warn("Favorites", $"couldn't read '{source}' for a first list ({ex.Message}); starting empty");
        }

        profile.Favorites = file?.Favorites ?? new List<FavoriteRoom>();
        profile.FavoriteFolders = file?.FavoriteFolders ?? new List<string>();
        _log?.Info("Favorites", shared
            ? $"took a copy of the {profile.Favorites.Count} favourite(s) shared on game data '{set}'"
            : $"started with the {profile.Favorites.Count} bundled favourite(s)");
    }
}

// On-disk shape of a Favorites.json (a set's old shared list, or the bundled
// starters) — favourites plus any empty folders.
internal sealed class FavoritesFile
{
    public List<FavoriteRoom>? Favorites { get; set; }
    public List<string>? FavoriteFolders { get; set; }
}
