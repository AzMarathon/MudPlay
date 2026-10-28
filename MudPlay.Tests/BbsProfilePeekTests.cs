using System.IO;
using MudPlay.Models.Settings;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// BbsProfileStore.Peek: the BBS as its bbs.json stands, one copy shared by the
// readers that only read a setting off it (the PvP checks at each room arrival, the
// bash rule a route search reads), read again only when the file is not as it was.
// Get read, parsed and re-serialized the file on every call; on Windows a route
// search and each room arrival paid that many times over (report
// paradigm-20261026-201217).
public sealed class BbsProfilePeekTests : IDisposable
{
    private readonly string _bbs = "peek-test-" + Path.GetRandomFileName();

    public BbsProfilePeekTests() => ScratchRoot.Require();

    public void Dispose()
    {
        try { if (Directory.Exists(AppPaths.BbsFolder(_bbs))) Directory.Delete(AppPaths.BbsFolder(_bbs), recursive: true); }
        catch { /* best-effort */ }
    }

    private BbsProfile Board(bool pvp) => new()
    {
        Name = _bbs,
        Host = "example.org",
        Realms = { new RealmProfile { Name = "Main", PvpEnabled = pvp } },
    };

    [Fact]
    public void Nothing_changed_hands_back_the_copy_already_read()
    {
        BbsProfileStore store = new();
        store.Save(Board(pvp: true));

        BbsProfile first = store.Peek(_bbs)!;
        BbsProfile again = store.Peek(_bbs)!;

        Assert.Same(first, again);
        Assert.True(first.Realms[0].PvpEnabled);
        // Get still hands each caller a copy of its own.
        Assert.NotSame(store.Get(_bbs), store.Get(_bbs));
    }

    [Fact]
    public void Another_clients_write_is_seen_on_the_next_read()
    {
        BbsProfileStore mine = new();
        mine.Save(Board(pvp: false));
        Assert.False(mine.Peek(_bbs)!.Realms[0].PvpEnabled);

        // Another client's store saves over it: a new file, with a later write time.
        string path = AppPaths.BbsProfileFile(_bbs);
        DateTime before = File.GetLastWriteTimeUtc(path);
        new BbsProfileStore().Save(Board(pvp: true));
        File.SetLastWriteTimeUtc(path, before.AddSeconds(2));

        Assert.True(mine.Peek(_bbs)!.Realms[0].PvpEnabled);
    }

    [Fact]
    public void A_file_rewritten_by_hand_to_the_same_length_is_seen_by_its_write_time()
    {
        BbsProfileStore store = new();
        store.Save(Board(pvp: false));
        string path = AppPaths.BbsProfileFile(_bbs);
        Assert.False(store.Peek(_bbs)!.Realms[0].PvpEnabled);

        DateTime before = File.GetLastWriteTimeUtc(path);
        string text = File.ReadAllText(path);
        // "false" to "true " keeps the length: only the write time tells them apart.
        File.WriteAllText(path, text.Replace("\"PvpEnabled\": false", "\"PvpEnabled\": true "));
        File.SetLastWriteTimeUtc(path, before.AddSeconds(2));

        Assert.True(store.Peek(_bbs)!.Realms[0].PvpEnabled);
    }

    [Fact]
    public void A_save_through_the_store_is_seen_at_once()
    {
        BbsProfileStore store = new();
        store.Save(Board(pvp: false));
        BbsProfile peeked = store.Peek(_bbs)!;

        BbsProfile edit = store.Get(_bbs)!;
        edit.Realms[0].PvpEnabled = true;
        store.Save(edit);

        BbsProfile after = store.Peek(_bbs)!;
        Assert.NotSame(peeked, after);
        Assert.True(after.Realms[0].PvpEnabled);
    }

    [Fact]
    public void A_deleted_board_reads_as_none()
    {
        BbsProfileStore store = new();
        store.Save(Board(pvp: false));
        Assert.NotNull(store.Peek(_bbs));

        store.Delete(_bbs);

        Assert.Null(store.Peek(_bbs));
    }
}
