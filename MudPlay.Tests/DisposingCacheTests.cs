using MudPlay.Controls;
using Xunit;

namespace MudPlay.Tests;

// The cache the map, the mana chart and the terminal's fallback glyphs keep their
// text layouts in. Each layout holds native glyph memory, so the cache must reuse a
// label it already has and dispose every one it lets go.
public sealed class DisposingCacheTests
{
    private sealed class Drawing : IDisposable
    {
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }

    [Fact]
    public void A_label_redrawn_every_frame_is_made_once()
    {
        DisposingCache<string, Drawing> cache = new(16);
        int made = 0;
        Drawing first = cache.GetOrAdd("12", _ => { made++; return new Drawing(); });

        for (int frame = 0; frame < 1000; frame++)
            Assert.Same(first, cache.GetOrAdd("12", _ => { made++; return new Drawing(); }));

        Assert.Equal(1, made);
        Assert.False(first.Disposed);
    }

    // A zoom sweep makes a new size, so a new key, for every label on every step.
    [Fact]
    public void Many_distinct_labels_stay_within_the_cap_and_every_one_let_go_is_disposed()
    {
        const int cap = 64;
        DisposingCache<(string Text, double Size), Drawing> cache = new(cap);
        List<Drawing> made = new();

        for (int step = 0; step < 200; step++)
            for (int label = 1; label <= 30; label++)
            {
                cache.GetOrAdd((label.ToString(), 10 + step * 0.01), _ =>
                {
                    Drawing d = new();
                    made.Add(d);
                    return d;
                });
                Assert.True(cache.Count <= cap);
            }

        Assert.Equal(6000, made.Count);
        Assert.Equal(cache.Count, made.Count(d => !d.Disposed));
    }

    [Fact]
    public void Clear_disposes_everything_it_holds()
    {
        DisposingCache<int, Drawing> cache = new(16);
        List<Drawing> made = new();
        for (int i = 0; i < 10; i++)
            made.Add(cache.GetOrAdd(i, _ => new Drawing()));

        cache.Clear();

        Assert.Equal(0, cache.Count);
        Assert.All(made, d => Assert.True(d.Disposed));
    }
}
