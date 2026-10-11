using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// SetIndexTimings writes one program-log line per set load naming what each per-set
// index took. The line is one load's: a load cut short by a rebuild that threw must
// leave nothing behind for the next load's line.
public sealed class SetIndexTimingsTests : IDisposable
{
    private readonly string _root;

    public SetIndexTimingsTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-indextimes-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        Directory.CreateDirectory(Path.Combine(_root, "beta"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    private static string[] Lines(LogService log) =>
        log.Snapshot().Select(e => e.Message).Where(m => m.StartsWith("Per-set indexes", StringComparison.Ordinal)).ToArray();

    private static int Mentions(string line, string name) => line.Split(name).Length - 1;

    [Fact]
    public void EachLoadGetsOneLineNamingEachRebuildOnce()
    {
        GameDataCache cache = new(_root);
        LogService log = new();
        SetIndexTimings timings = new(cache, log);
        cache.ActiveSetChanged += timings.Timed("item names", _ => { });
        cache.ActiveSetChanged += timings.Timed("shop stock", _ => { });
        cache.ActiveSetChanged += timings.LogLoad;

        cache.SwitchSet("alpha");
        cache.SwitchSet("beta");
        cache.ReloadActiveSet();
        cache.SwitchSet(null);

        string[] lines = Lines(log);
        Assert.Equal(3, lines.Length);
        Assert.StartsWith("Per-set indexes for 'alpha' rebuilt in ", lines[0]);
        Assert.StartsWith("Per-set indexes for 'beta' rebuilt in ", lines[1]);
        Assert.StartsWith("Per-set indexes for 'beta' rebuilt in ", lines[2]);
        Assert.All(lines, line =>
        {
            Assert.Equal(1, Mentions(line, "item names"));
            Assert.Equal(1, Mentions(line, "shop stock"));
        });
    }

    [Fact]
    public void ALoadCutShortByARebuildThatThrew_LeavesNothingForTheNextLine()
    {
        GameDataCache cache = new(_root);
        LogService log = new();
        SetIndexTimings timings = new(cache, log);
        bool broken = true;
        cache.ActiveSetChanged += timings.Timed("item names", _ => { });
        cache.ActiveSetChanged += timings.Timed("shop stock", _ =>
        {
            if (broken) throw new InvalidOperationException("the table could not be read");
        });
        cache.ActiveSetChanged += timings.Timed("monster drops", _ => { });
        cache.ActiveSetChanged += timings.LogLoad;

        Assert.Throws<InvalidOperationException>(() => cache.SwitchSet("alpha"));
        Assert.Empty(Lines(log));

        broken = false;
        cache.SwitchSet("beta");

        string line = Assert.Single(Lines(log));
        Assert.StartsWith("Per-set indexes for 'beta' rebuilt in ", line);
        Assert.Equal(1, Mentions(line, "item names"));
        Assert.Equal(1, Mentions(line, "shop stock"));
        Assert.Equal(1, Mentions(line, "monster drops"));
    }
}
