using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// GameDataCache reads a table file once however many threads ask for the table while
// the read runs (a set load starts background builds that want the tables the loading
// thread is reading), tells one load of a set from the next, and remembers what each
// table's file looked like when it was read.
public sealed class GameDataCacheSharedReadTests : IDisposable
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(20);
    private readonly string _root;

    public GameDataCacheSharedReadTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-gdc-shared-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    private string Seed(string set, string table, string json)
    {
        string dir = Path.Combine(_root, set);
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, table + ".json");
        File.WriteAllText(path, json);
        return path;
    }

    private static string Monsters(string name) => "[{\"Number\":1,\"Name\":\"" + name + "\"}]";

    private static string? FirstName(GameTable? table) =>
        table!.Rows[0].TryGetProperty("Name", out GameValue name) ? name.GetString() : null;

    // Holds the first read of a table open until released, and counts every read.
    private sealed class ReadGate
    {
        private readonly ManualResetEventSlim _entered = new(), _release = new();
        private int _reads;
        public int Reads => Volatile.Read(ref _reads);
        // Runs on the reading thread once the held read is let go, before the file is opened.
        public Action? BeforeHeldReadOpens { get; set; }

        public ReadGate(GameDataCache cache) => cache.TableReadStarting = _ =>
        {
            if (Interlocked.Increment(ref _reads) != 1) return;
            _entered.Set();
            Assert.True(_release.Wait(Limit), "the held read was never released");
            BeforeHeldReadOpens?.Invoke();
        };

        public void WaitUntilARead() => Assert.True(_entered.Wait(Limit), "no read started");
        public void Release() => _release.Set();
    }

    // A lookup on its own thread, so the test can see that it is blocked.
    private sealed class Lookup
    {
        private readonly Thread _thread;
        public GameTable? Table { get; private set; }
        public Exception? Failure { get; private set; }

        public Lookup(GameDataCache cache, string table)
        {
            _thread = new Thread(() =>
            {
                try { Table = cache.GetTable(table); }
                catch (Exception ex) { Failure = ex; }
            });
            _thread.Start();
        }

        public void WaitUntilBlocked()
        {
            long deadline = Environment.TickCount64 + (long)Limit.TotalMilliseconds;
            while ((_thread.ThreadState & ThreadState.WaitSleepJoin) == 0)
            {
                Assert.True(_thread.IsAlive, "the lookup returned instead of waiting for the read in flight");
                Assert.True(Environment.TickCount64 < deadline, "the lookup never waited");
                Thread.Sleep(1);
            }
        }

        public void Join() => Assert.True(_thread.Join(Limit), "the lookup never returned");
    }

    [Fact]
    public void ActiveLoad_MovesWithEveryLoadOfASet_AndWithNothingElse()
    {
        Seed("alpha", "Monsters", Monsters("orc"));
        Seed("beta", "Monsters", Monsters("troll"));
        GameDataCache cache = new(_root);
        GameDataCache.Load none = cache.ActiveLoad;
        Assert.Null(none.Set);

        cache.SwitchSet("alpha");
        GameDataCache.Load first = cache.ActiveLoad;
        Assert.Equal("alpha", first.Set);
        Assert.NotEqual(none, first);

        // Reading, marking done and the idle sweep leave the load what it was.
        cache.GetTable("Monsters");
        cache.EvictTable("Monsters");
        cache.EvictIdle(TimeSpan.Zero);
        cache.GetTable("Monsters");
        cache.SwitchSet("alpha");
        Assert.Equal(first, cache.ActiveLoad);

        // A re-import over the active set and a reload keep the name and are new loads.
        cache.ReloadActiveSet();
        GameDataCache.Load reimported = cache.ActiveLoad;
        Assert.Equal("alpha", reimported.Set);
        Assert.NotEqual(first, reimported);
        cache.Reload();
        GameDataCache.Load reloaded = cache.ActiveLoad;
        Assert.NotEqual(reimported, reloaded);

        // Away and back is a new load of the same name.
        cache.SwitchSet("beta");
        cache.SwitchSet("alpha");
        Assert.Equal("alpha", cache.ActiveLoad.Set);
        Assert.NotEqual(reloaded, cache.ActiveLoad);
    }

    [Fact]
    public void LookupsOfATableBeingRead_WaitForThatRead()
    {
        Seed("alpha", "Monsters", Monsters("orc"));
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        ReadGate gate = new(cache);

        Lookup first = new(cache, "Monsters");
        gate.WaitUntilARead();
        Lookup second = new(cache, "Monsters");
        Lookup third = new(cache, "monsters");
        second.WaitUntilBlocked();
        third.WaitUntilBlocked();
        gate.Release();
        first.Join();
        second.Join();
        third.Join();

        Assert.Equal(1, gate.Reads);
        Assert.Equal("orc", FirstName(first.Table));
        Assert.Same(first.Table, second.Table);
        Assert.Same(first.Table, third.Table);
        Assert.Same(first.Table, cache.GetTable("Monsters"));
        Assert.Equal(1, gate.Reads);
    }

    [Fact]
    public async Task LookupsOfATableBeingPrewarmed_AllWaitForThePrewarm()
    {
        Seed("alpha", "Monsters", Monsters("orc"));
        GameDataCache cache = new(_root);
        ReadGate gate = new(cache);

        Task prewarm = cache.PrewarmAsync("alpha", new[] { "Monsters" });
        gate.WaitUntilARead();
        cache.SwitchSet("alpha");
        Lookup first = new(cache, "Monsters");
        Lookup second = new(cache, "Monsters");
        first.WaitUntilBlocked();
        second.WaitUntilBlocked();
        gate.Release();
        first.Join();
        second.Join();
        await prewarm;

        Assert.Equal(1, gate.Reads);
        Assert.Equal("orc", FirstName(first.Table));
        Assert.Same(first.Table, second.Table);
        // The lookups put it in place; the prewarm filed no second copy to be claimed later.
        cache.EvictIdle(TimeSpan.Zero);
        Assert.Equal("orc", FirstName(cache.GetTable("Monsters")));
        Assert.Equal(2, gate.Reads);
    }

    [Fact]
    public void AMalformedFile_IsNoTableForEitherLookup_AndIsReportedOnce()
    {
        Seed("alpha", "Monsters", "[{\"Name\":\"Go" + (char)0x1E + "blin\"}]");
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        int reported = 0;
        cache.TableParseFailed += _ => Interlocked.Increment(ref reported);
        ReadGate gate = new(cache);

        Lookup first = new(cache, "Monsters");
        gate.WaitUntilARead();
        Lookup second = new(cache, "Monsters");
        second.WaitUntilBlocked();
        gate.Release();
        first.Join();
        second.Join();

        Assert.Null(first.Failure);
        Assert.Null(second.Failure);
        Assert.Null(first.Table);
        Assert.Null(second.Table);
        Assert.Equal(1, reported);
        Assert.Null(cache.GetTable("Monsters"));
    }

    // A file that cannot be opened is an exception to the caller. Two callers on one
    // read each get theirs: the one waiting reads the file itself when the read it
    // waited on comes back with nothing.
    [Fact]
    public void AFileThatCannotBeRead_ThrowsToBothLookups()
    {
        string path = Seed("alpha", "Monsters", Monsters("orc"));
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        ReadGate gate = new(cache) { BeforeHeldReadOpens = () => File.Delete(path) };

        Lookup first = new(cache, "Monsters");
        gate.WaitUntilARead();
        Lookup second = new(cache, "Monsters");
        second.WaitUntilBlocked();
        gate.Release();
        first.Join();
        second.Join();

        Assert.IsAssignableFrom<IOException>(first.Failure);
        Assert.IsAssignableFrom<IOException>(second.Failure);
        Assert.Equal(2, gate.Reads);
        // Nothing was remembered as broken: the file put back reads.
        Seed("alpha", "Monsters", Monsters("orc"));
        Assert.Equal("orc", FirstName(cache.GetTable("Monsters")));
    }

    [Fact]
    public void ASwitchDuringARead_NeitherWaitsOnItNorKeepsIt()
    {
        Seed("alpha", "Monsters", Monsters("orc"));
        Seed("beta", "Monsters", Monsters("troll"));
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        ReadGate gate = new(cache);

        Lookup overtaken = new(cache, "Monsters");
        gate.WaitUntilARead();
        cache.SwitchSet("beta");
        // On this thread, with alpha's read still held: it must not wait for it.
        Assert.Equal("troll", FirstName(cache.GetTable("Monsters")));
        gate.Release();
        overtaken.Join();

        // The overtaken lookup gets the table it asked for; the cache keeps beta's.
        Assert.Equal("orc", FirstName(overtaken.Table));
        Assert.Equal("troll", FirstName(cache.GetTable("Monsters")));
        Assert.Equal(2, gate.Reads);
    }

    // Away and back while a read of the first load is still running, the set imported
    // again in between: the new load reads the new file and does not take the old read.
    [Fact]
    public void AReadOfAnEarlierLoadOfTheSameSet_IsNotSharedWithTheNextLoad()
    {
        Seed("alpha", "Monsters", Monsters("orc"));
        Seed("beta", "Monsters", Monsters("troll"));
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        ReadGate gate = new(cache);

        Lookup overtaken = new(cache, "Monsters");
        gate.WaitUntilARead();
        cache.SwitchSet("beta");
        cache.SwitchSet("alpha");
        Assert.Equal("orc", FirstName(cache.GetTable("Monsters")));
        Assert.Equal(2, gate.Reads);
        GameTable? current = cache.GetTable("Monsters");
        gate.Release();
        overtaken.Join();

        Assert.NotSame(current, overtaken.Table);
        Assert.Same(current, cache.GetTable("Monsters"));
        Assert.Equal(2, gate.Reads);
    }

    // The same when the set is imported again over itself while a read is running.
    [Fact]
    public void AReadOvertakenByAReimport_IsNotSharedWithTheNewLoad()
    {
        string path = Seed("alpha", "Monsters", Monsters("orc"));
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        ReadGate gate = new(cache);

        Lookup overtaken = new(cache, "Monsters");
        gate.WaitUntilARead();
        File.WriteAllText(path, Monsters("troll"));
        cache.ReloadActiveSet();
        Assert.Equal("troll", FirstName(cache.GetTable("Monsters")));
        gate.Release();
        overtaken.Join();

        Assert.Equal("troll", FirstName(cache.GetTable("Monsters")));
        Assert.Equal(2, gate.Reads);
    }

    [Fact]
    public void FileStampAsRead_IsTheFileAsTheCacheReadIt()
    {
        string path = Seed("alpha", "Monsters", Monsters("orc"));
        DateTime written = new(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, written);
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");

        // Nothing is known of a table not read in this load, or with no file.
        Assert.Null(cache.FileStampAsRead("Monsters"));
        Assert.Null(cache.FileStampAsRead("Items"));
        cache.GetTable("Items");
        Assert.Null(cache.FileStampAsRead("Items"));

        cache.GetTable("Monsters");
        GameDataCache.TableFileStamp read = new(new FileInfo(path).Length, written);
        Assert.Equal(read, cache.FileStampAsRead("Monsters"));
        Assert.Equal(read, cache.FileStampAsRead("monsters"));

        // The file changing on disk changes nothing: the cache has not read it again.
        File.WriteAllText(path, Monsters("a much longer name"));
        DateTime rewritten = new(2026, 4, 5, 6, 7, 8, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, rewritten);
        Assert.Equal(read, cache.FileStampAsRead("Monsters"));

        // Dropped by the idle sweep: still the file the dropped table came from.
        cache.EvictIdle(TimeSpan.Zero);
        Assert.False(cache.IsTableLoaded("Monsters"));
        Assert.Equal(read, cache.FileStampAsRead("Monsters"));

        // Read again after the drop: the file as this read found it.
        cache.GetTable("Monsters");
        GameDataCache.TableFileStamp reread = new(new FileInfo(path).Length, rewritten);
        Assert.NotEqual(read, reread);
        Assert.Equal(reread, cache.FileStampAsRead("Monsters"));

        // A new load of the set starts with nothing known.
        cache.ReloadActiveSet();
        Assert.Null(cache.FileStampAsRead("Monsters"));
        cache.GetTable("Monsters");
        Assert.Equal(reread, cache.FileStampAsRead("Monsters"));
        cache.SwitchSet(null);
        Assert.Null(cache.FileStampAsRead("Monsters"));
    }

    // A table claimed from a prewarm carries the stamp of the file the prewarm read,
    // not of the file on disk when it was claimed.
    [Fact]
    public async Task FileStampAsRead_OfAPrewarmedTable_IsTheFileThePrewarmRead()
    {
        string path = Seed("alpha", "Monsters", Monsters("orc"));
        DateTime written = new(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, written);
        GameDataCache.TableFileStamp prewarmed = new(new FileInfo(path).Length, written);
        GameDataCache cache = new(_root);

        await cache.PrewarmAsync("alpha", new[] { "Monsters" });
        File.WriteAllText(path, Monsters("a much longer name"));
        cache.SwitchSet("alpha");

        Assert.Equal("orc", FirstName(cache.GetTable("Monsters")));
        Assert.Equal(prewarmed, cache.FileStampAsRead("Monsters"));
    }
}
