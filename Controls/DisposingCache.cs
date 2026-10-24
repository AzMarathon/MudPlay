namespace MudPlay.Controls;

// A bounded cache of disposable drawing objects, for text a control redraws
// unchanged frame after frame.
//
// It exists for text: a FormattedText lays its text out again on every draw and
// never disposes those lines, so each draw leaves native glyph memory behind for
// the finalizer, and native memory alone never makes the GC run. A TextLayout
// keeps its lines and frees them on Dispose. Cached, a label is laid out once and
// redrawn as often as it shows. Uncached, every redraw would be a new allocation.
//
// Every value is disposed when it leaves: on Clear, and when an add finds the
// cache full, which empties it first, so the count never passes the cap. A
// value handed out may be disposed by the next call, so draw it before asking
// again. That is safe for a frame already recorded: the renderer keeps its own
// reference to the native glyphs it draws.
//
// UI-thread only, like the rendering that uses it.
internal sealed class DisposingCache<TKey, TValue>(int capacity)
    where TKey : notnull
    where TValue : class, IDisposable
{
    private readonly Dictionary<TKey, TValue> _items = new();

    public int Count => _items.Count;

    public TValue GetOrAdd(TKey key, Func<TKey, TValue> create)
    {
        if (_items.TryGetValue(key, out TValue? cached)) return cached;
        if (_items.Count >= capacity) Clear();
        TValue value = create(key);
        _items[key] = value;
        return value;
    }

    public void Clear()
    {
        foreach (TValue value in _items.Values) value.Dispose();
        _items.Clear();
    }
}
