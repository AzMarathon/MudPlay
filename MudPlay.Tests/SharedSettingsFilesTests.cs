using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using MudPlay.Game.Map;
using MudPlay.Models.Settings;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// The settings files several clients share (global.json, a BBS's bbs.json) and the
// realm's room blacklist. A client holds a copy and used to save the whole of it,
// undoing whatever another client had saved since.
// In the EmoteRuntime collection for the per-BBS emote library test, which commits to an EmoteStore.
[Collection(EmoteRuntimeCollection.Name)]
public sealed class SharedSettingsFilesTests : IDisposable
{
    private readonly string _bbs = "test-sharedbbs-" + Guid.NewGuid().ToString("N").Substring(0, 12);

    public void Dispose()
    {
        try { if (Directory.Exists(AppPaths.BbsFolder(_bbs))) Directory.Delete(AppPaths.BbsFolder(_bbs), true); }
        catch { /* best-effort */ }
    }

    private static JsonNode Node(string json) => JsonNode.Parse(json)!;

    [Fact]
    public void Merge_AppliesOnlyWhatChangedHere_OntoTheFileAsItStands()
    {
        JsonNode baseline = Node("""{ "a": 1, "b": 1, "tab": { "x": 1, "y": 1 }, "gone": 1 }""");
        JsonNode mine     = Node("""{ "a": 2, "b": 1, "tab": { "x": 1, "y": 2 }, "gone": 1, "added": 5 }""");
        JsonNode theirs   = Node("""{ "a": 1, "b": 3, "tab": { "x": 9, "y": 1 }, "new": 7 }""");

        JsonNode merged = JsonThreeWayMerge.Merge(baseline, mine, theirs)!;

        Assert.True(JsonNode.DeepEquals(merged,
            Node("""{ "a": 2, "b": 3, "tab": { "x": 9, "y": 2 }, "new": 7, "added": 5 }""")));
    }

    [Fact]
    public void Merge_NothingChangedHere_IsTheirs()
    {
        JsonNode same = Node("""{ "a": 1, "list": [1, 2] }""");
        JsonNode theirs = Node("""{ "a": 2, "list": [3] }""");

        Assert.True(JsonNode.DeepEquals(JsonThreeWayMerge.Merge(same, same.DeepClone(), theirs), theirs));
    }

    // Two holders of one BBS (a Settings window and the running client, or two
    // clients) each change something and save their whole copy.
    [Fact]
    public void BbsSave_KeepsWhatAnotherHolderSavedSince()
    {
        BbsProfileStore store = new();
        store.Save(new BbsProfile { Name = _bbs, Host = "old.example", Port = 23 });
        BbsProfile settingsWindow = store.Get(_bbs)!;
        BbsProfile runningClient = store.Get(_bbs)!;

        runningClient.Realms[0].PlayerDiesAtHp = -40;      // refined during play
        store.Save(runningClient);
        settingsWindow.Host = "new.example";               // edited in the window, saved later
        store.Save(settingsWindow);

        BbsProfile onFile = store.Get(_bbs)!;
        Assert.Equal("new.example", onFile.Host);
        Assert.Equal(-40, onFile.Realms[0].PlayerDiesAtHp);
    }

    // A copy nothing was changed in is not written over a newer file.
    [Fact]
    public void BbsSave_OfAnUnchangedCopy_LeavesANewerFileAlone()
    {
        BbsProfileStore store = new();
        store.Save(new BbsProfile { Name = _bbs, Host = "old.example", Port = 23 });
        BbsProfile idle = store.Get(_bbs)!;
        BbsProfile edited = store.Get(_bbs)!;

        edited.Host = "new.example";
        store.Save(edited);
        store.Save(idle);

        Assert.Equal("new.example", store.Get(_bbs)!.Host);
    }

    [Fact]
    public void RoomBlacklist_EntriesFromTwoClients_BothStay()
    {
        string realm = Path.Combine(AppPaths.BbsFolder(_bbs), "Realms", "A");
        Directory.CreateDirectory(realm);
        RoomBlacklistStore first = new(); first.OnRealmChanged(realm);
        RoomBlacklistStore second = new(); second.OnRealmChanged(realm);

        first.Add(new RoomKey(1, 10), "Pit");
        second.Add(new RoomKey(1, 20), "Trap");

        RoomBlacklistStore restarted = new(); restarted.OnRealmChanged(realm);
        Assert.True(restarted.IsBlacklisted(new RoomKey(1, 10)));
        Assert.True(restarted.IsBlacklisted(new RoomKey(1, 20)));
        Assert.True(first.TakeInOutsideChanges());
        Assert.True(first.IsBlacklisted(new RoomKey(1, 20)));
    }

    // The emote library is the BBS's. A BBS with none takes a copy of the one every
    // BBS used before, then keeps its own.
    [Fact]
    public void Emotes_ABbsTakesACopyOfTheSharedLibrary_ThenKeepsItsOwn()
    {
        string shared = Path.Combine(AppPaths.BbsFolder(_bbs), "shared-emotes");
        EmoteStore store = new(dir: shared);
        store.Commit(new[] { new EmoteDraft { Shortcode = "wave", Emoji = "👋" } }, Array.Empty<string>());

        store.OnBbsChanged(_bbs);
        Assert.Contains(store.Emotes, e => e.Shortcode == "wave");
        store.Commit(new[] { new EmoteDraft { Shortcode = "bbsonly", Emoji = "⭐" } }, Array.Empty<string>());

        store.OnBbsChanged(null);                                   // back to the shared one
        Assert.Equal(new[] { "wave" }, store.Emotes.Select(e => e.Shortcode));
        store.OnBbsChanged(_bbs);
        Assert.Equal(new[] { "bbsonly" }, store.Emotes.Select(e => e.Shortcode));
    }
}
