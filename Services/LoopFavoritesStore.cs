using System.Collections.Generic;
using System.Linq;
using MudPlay.Game.Map;
using MudPlay.Models.Profile;

namespace MudPlay.Services;

// Which saved loops and auto-lair setups the loaded character has favourited. The
// loops and setups are files with the game data, shared by every character on it;
// the favourite flag used to ride in those files, so one character's favourites were
// everyone's. It is the character's own now, kept on the profile by name
// (CharacterProfile.FavoriteLoops / FavoriteLairSetups) (user, 2026-10-08).
//
// LoopManager and LairManager still hand out Loop.Favorite / LairSetup.Favorite, so
// nothing that reads a favourite changed: this store plugs into both and they stamp
// the flag from it.
//
// A profile takes each list the first time it loads with its game data's loops in
// hand (the list is null until then): a character from before the move takes the
// flags the files carried, so it keeps the favourites it could see. The default
// profile starts with none, and so does a character made since, whose lists
// ProfileService creates empty.
public sealed class LoopFavoritesStore
{
    private readonly ProfileService _profile;
    private readonly LoopManager _loops;
    private readonly LairManager _lairs;
    private readonly Func<string?> _profileSet;
    private readonly LogService? _log;
    private readonly HashSet<string> _loopNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _lairNames = new(StringComparer.OrdinalIgnoreCase);
    // Set while this store is the one refreshing the managers, whose change
    // events it also listens to.
    private bool _refreshing;

    // profileSet names the game-data set the loaded profile runs on, whose loop
    // files a first list is read from.
    public LoopFavoritesStore(ProfileService profile, LoopManager loops, LairManager lairs,
        Func<string?> profileSet, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(loops);
        ArgumentNullException.ThrowIfNull(lairs);
        ArgumentNullException.ThrowIfNull(profileSet);
        _profile = profile;
        _loops = loops;
        _lairs = lairs;
        _profileSet = profileSet;
        _log = log;

        _loops.AttachFavorites(_loopNames.Contains, SetLoop);
        _lairs.AttachFavorites(_lairNames.Contains, SetLair);
        _profile.ProfileLoaded += Hydrate;
        _profile.ProfileClosed += OnProfileClosed;
        // A profile that loaded before its game data's loops were read takes its
        // first lists once they are.
        _loops.LoopsChanged += OnCatalogueChanged;
        _lairs.SetupsChanged += OnCatalogueChanged;
        if (_profile.Current is { } current) Hydrate(current);
    }

    public IReadOnlyCollection<string> Loops => _loopNames;
    public IReadOnlyCollection<string> LairSetups => _lairNames;

    private void Hydrate(CharacterProfile profile)
    {
        TakeFirstLists(profile);
        _loopNames.Clear();
        _lairNames.Clear();
        _loopNames.UnionWith(profile.FavoriteLoops ?? Enumerable.Empty<string>());
        _lairNames.UnionWith(profile.FavoriteLairSetups ?? Enumerable.Empty<string>());
        Refresh();
    }

    private void OnProfileClosed()
    {
        _loopNames.Clear();
        _lairNames.Clear();
        Refresh();
    }

    private void OnCatalogueChanged()
    {
        if (_refreshing || _profile.Current is not { } p) return;
        bool waitingLoops = p.FavoriteLoops is null, waitingLairs = p.FavoriteLairSetups is null;
        if (!waitingLoops && !waitingLairs) return;
        TakeFirstLists(p);
        // Only a list that just arrived is worth re-stamping for.
        if ((waitingLoops && p.FavoriteLoops is not null) || (waitingLairs && p.FavoriteLairSetups is not null))
            Hydrate(p);
    }

    private void Refresh()
    {
        _refreshing = true;
        try
        {
            _loops.RefreshFavorites();
            _lairs.RefreshFavorites();
        }
        finally { _refreshing = false; }
    }

    // Give a profile the lists it hasn't taken. Only set on the profile here, not
    // saved: this can run inside the profile load, when the other stores still hold
    // the previous profile's state and a save would write theirs into this one. The
    // next save carries it, and until then a reload takes the same lists again.
    private void TakeFirstLists(CharacterProfile profile)
    {
        if (profile.FavoriteLoops is not null && profile.FavoriteLairSetups is not null) return;
        if (_profile.CurrentProfileName is null)
        {
            profile.FavoriteLoops ??= new List<string>();
            profile.FavoriteLairSetups ??= new List<string>();
            return;
        }

        // The catalogue has to be the one for this profile's game data; a profile
        // switch loads the profile before its set, and the last character's loops
        // would be read as this one's.
        string? set = _profileSet();
        if (profile.FavoriteLoops is null && IsFor(_loops.SetName, set))
        {
            profile.FavoriteLoops = _loops.Loops.Where(l => l.SharedFavorite).Select(l => l.Name).ToList();
            _log?.Info("Favorites", $"took {profile.FavoriteLoops.Count} favourite loop(s) from the loop files of '{set}' as this character's own");
        }
        if (profile.FavoriteLairSetups is null && IsFor(_lairs.SetName, set))
        {
            profile.FavoriteLairSetups = _lairs.Setups.Where(s => s.SharedFavorite).Select(s => s.Name).ToList();
            _log?.Info("Favorites", $"took {profile.FavoriteLairSetups.Count} favourite auto-lair setup(s) from the files of '{set}' as this character's own");
        }
    }

    private static bool IsFor(string? loaded, string? wanted) =>
        !string.IsNullOrEmpty(loaded) && string.Equals(loaded, wanted, StringComparison.OrdinalIgnoreCase);

    private void SetLoop(string name, bool favorite) => Set(_loopNames, name, favorite, "loop");
    private void SetLair(string name, bool favorite) => Set(_lairNames, name, favorite, "auto-lair setup");

    private void Set(HashSet<string> names, string name, bool favorite, string kind)
    {
        if (_profile.Current is not { } current) return;
        if (!(favorite ? names.Add(name) : names.Remove(name))) return;
        if (ReferenceEquals(names, _loopNames)) current.FavoriteLoops = _loopNames.ToList();
        else current.FavoriteLairSetups = _lairNames.ToList();
        _profile.Save();
        _log?.Info("Favorites", $"{(favorite ? "favourited" : "unfavourited")} {kind} '{name}' for this character");
    }
}
