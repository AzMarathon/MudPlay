using System;
using System.IO;
using System.Linq;
using MudPlay.Game.Map;
using MudPlay.Models.Profile;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// Which loops and auto-lair setups are favourites belongs to the character; the
// loops and setups themselves stay with the game data, shared. Before this the flag
// rode in the shared files, so one character's favourites were everyone's.
public sealed class LoopFavoritesStoreTests : IDisposable
{
    private readonly string _setName;
    private readonly string _bbs;

    public LoopFavoritesStoreTests()
    {
        string id = Guid.NewGuid().ToString("N").Substring(0, 12);
        _setName = "test-loopfav-" + id;
        _bbs = "test-loopfavbbs-" + id;
        Directory.CreateDirectory(AppPaths.GameDataSetLoopsFolder(_setName));
    }

    public void Dispose()
    {
        foreach (string folder in new[] { Path.Combine(AppPaths.GameDataRoot, _setName), AppPaths.BbsFolder(_bbs) })
        {
            try { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
            catch { /* best-effort */ }
        }
    }

    private static readonly RoomKey[] TwoRooms = { new(1, 1), new(1, 2) };

    // A loop or setup file as a version from before the move wrote it, flag and all.
    private void WriteSharedLoop(string name, bool favorite) =>
        File.WriteAllText(Path.Combine(AppPaths.GameDataSetLoopsFolder(_setName), name + LoopManager.LoopFileSuffix),
            $$"""{ "SchemaVersion": 3, "Name": "{{name}}", "Favorite": {{(favorite ? "true" : "false")}}, "Waypoints": [ { "Room": "1/1" }, { "Room": "1/2" } ] }""");

    private void WriteSharedSetup(string name, bool favorite) =>
        File.WriteAllText(Path.Combine(AppPaths.GameDataSetLoopsFolder(_setName), name + LairManager.LairFileSuffix),
            $$"""{ "SchemaVersion": 1, "Name": "{{name}}", "Favorite": {{(favorite ? "true" : "false")}}, "Markers": [ { "Map": 1, "Room": 10 } ] }""");

    private void WriteOlderCharacter(string name)
    {
        Directory.CreateDirectory(AppPaths.ProfileFolder(_bbs, name));
        File.WriteAllText(AppPaths.CharacterProfileFile(_bbs, name), $$"""{ "Name": "{{name}}", "SchemaVersion": 8 }""");
    }

    private sealed record Session(ProfileService Profile, LoopManager Loops, LairManager Lairs, LoopFavoritesStore Store);

    // The managers as the app has them when a character loads: its set's files read.
    private Session Open(string character)
    {
        GameDataCache cache = new();
        RoomGraphManager graph = new(cache);
        LoopManager loops = new(new BfsMapper(graph), graph);
        LairManager lairs = new();
        loops.LoadAll(_setName);
        lairs.LoadAll(_setName);
        ProfileService profile = new();
        LoopFavoritesStore store = new(profile, loops, lairs, () => _setName);
        profile.Load(_bbs, character);
        return new Session(profile, loops, lairs, store);
    }

    [Fact]
    public void OlderCharacter_TakesTheFilesFlags_ThenKeepsItsOwn()
    {
        WriteSharedLoop("Dragons", favorite: true);
        WriteSharedLoop("Orcs", favorite: false);
        WriteSharedSetup("Crypt", favorite: true);
        WriteOlderCharacter("Alpha");
        WriteOlderCharacter("Beta");

        Session alpha = Open("Alpha");
        Assert.True(alpha.Loops.Get("Dragons")!.Favorite);
        Assert.False(alpha.Loops.Get("Orcs")!.Favorite);
        Assert.True(alpha.Lairs.Get("Crypt")!.Favorite);

        alpha.Loops.SetFavorite("Orcs", true);
        alpha.Loops.SetFavorite("Dragons", false);
        alpha.Lairs.SetFavorite("Crypt", false);

        // Beta loads after Alpha's changes and still finds what the files carried.
        Session beta = Open("Beta");
        Assert.True(beta.Loops.Get("Dragons")!.Favorite);
        Assert.False(beta.Loops.Get("Orcs")!.Favorite);
        Assert.True(beta.Lairs.Get("Crypt")!.Favorite);

        // And Alpha, reloaded from its own file, keeps its own.
        Session alphaAgain = Open("Alpha");
        Assert.False(alphaAgain.Loops.Get("Dragons")!.Favorite);
        Assert.True(alphaAgain.Loops.Get("Orcs")!.Favorite);
        Assert.False(alphaAgain.Lairs.Get("Crypt")!.Favorite);
        Assert.Equal(new[] { "Orcs" }, alphaAgain.Profile.Current!.FavoriteLoops);
    }

    [Fact]
    public void NewCharacter_StartsWithNoLoopFavourites()
    {
        WriteSharedLoop("Dragons", favorite: true);
        WriteSharedSetup("Crypt", favorite: true);
        new ProfileService().CreateProfile(_bbs, "Fresh");

        Session fresh = Open("Fresh");

        Assert.False(fresh.Loops.Get("Dragons")!.Favorite);
        Assert.False(fresh.Lairs.Get("Crypt")!.Favorite);
    }

    // A favourite is the character's, so setting one must not rewrite the shared file.
    [Fact]
    public void SettingAFavourite_LeavesTheLoopFileAsItWas()
    {
        WriteSharedLoop("Orcs", favorite: false);
        WriteOlderCharacter("Alpha");
        string file = Path.Combine(AppPaths.GameDataSetLoopsFolder(_setName), "Orcs" + LoopManager.LoopFileSuffix);
        string before = File.ReadAllText(file);

        Session alpha = Open("Alpha");
        alpha.Loops.SetFavorite("Orcs", true);

        Assert.Equal(before, File.ReadAllText(file));
        Assert.True(alpha.Loops.Get("Orcs")!.Favorite);
    }

    // Saving a loop (an edit, a running loop's snapshot) keeps the favourite the
    // character gave it and the flag the file carried for characters yet to load.
    [Fact]
    public void SavingALoop_KeepsTheFavourite_AndTheFilesOwnFlag()
    {
        WriteSharedLoop("Dragons", favorite: true);
        WriteOlderCharacter("Alpha");
        WriteOlderCharacter("Beta");
        Session alpha = Open("Alpha");
        alpha.Loops.SetFavorite("Dragons", false);

        Loop dragons = alpha.Loops.Get("Dragons")!;
        dragons.Notes = "edited";
        alpha.Loops.Save(dragons);
        // A snapshot saved over the same name arrives as a fresh object.
        alpha.Loops.Save(new Loop("Dragons", TwoRooms) { SharedFavorite = dragons.SharedFavorite });

        Assert.False(alpha.Loops.Get("Dragons")!.Favorite);
        Assert.True(Open("Beta").Loops.Get("Dragons")!.Favorite);
    }

    [Fact]
    public void DeletingALoop_ForgetsItsFavourite()
    {
        WriteSharedLoop("Dragons", favorite: true);
        WriteOlderCharacter("Alpha");
        Session alpha = Open("Alpha");

        alpha.Loops.Delete("Dragons");
        alpha.Loops.Save(new Loop("Dragons", TwoRooms));

        Assert.False(alpha.Loops.Get("Dragons")!.Favorite);
        Assert.Empty(alpha.Store.Loops);
    }

    // A profile switch loads the profile before its game data: the catalogue in hand
    // is still the last character's, and its flags mustn't be taken as this one's.
    [Fact]
    public void ProfileLoadedBeforeItsLoops_TakesItsListWhenTheyArrive()
    {
        WriteSharedLoop("Dragons", favorite: true);
        WriteOlderCharacter("Early");
        GameDataCache cache = new();
        RoomGraphManager graph = new(cache);
        LoopManager loops = new(new BfsMapper(graph), graph);
        LairManager lairs = new();
        ProfileService profile = new();
        LoopFavoritesStore store = new(profile, loops, lairs, () => _setName);

        profile.Load(_bbs, "Early");
        Assert.Null(profile.Current!.FavoriteLoops);

        loops.LoadAll(_setName);
        lairs.LoadAll(_setName);

        Assert.Equal(new[] { "Dragons" }, profile.Current.FavoriteLoops);
        Assert.True(loops.Get("Dragons")!.Favorite);
        Assert.Contains("Dragons", store.Loops);
    }
}
