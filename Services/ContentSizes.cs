using System.Globalization;
using System.Text;

namespace MudPlay.Services;

// How much the open windows hold of what grows with play: the Conversation's
// rows, the terminal's scrollback against its cap, the rooms the Navigation map
// has placed, the Backscroll's lines. Each is memory a long session piles up, and
// a report that names them tells a full window from a leak.
//
// Read off the UI thread (the memory log's timer, the hang report's writer), so
// each count is read without a lock: a collection's Count or a field, which may
// be a row stale while the UI thread changes it but never throws or waits. The
// registry's own lock is held only to add, drop or copy an entry.
public sealed class ContentSizes
{
    private readonly object _gate = new();
    private readonly List<Entry> _entries = new();

    private sealed class Entry(string key, Func<int> count, Func<int>? capacity) : IDisposable
    {
        public string Key { get; } = key;
        public Func<int> Count { get; } = count;
        public Func<int>? Capacity { get; } = capacity;
        public ContentSizes? Owner { get; set; }

        public void Dispose()
        {
            if (Owner is not { } owner) return;
            lock (owner._gate) owner._entries.Remove(this);
            Owner = null;
        }
    }

    // Keeps key's count in the reports until the returned handle is disposed (the
    // window closing). count and capacity must be lock-free reads: see above.
    public IDisposable Track(string key, Func<int> count, Func<int>? capacity = null)
    {
        Entry entry = new(key, count, capacity) { Owner = this };
        lock (_gate) _entries.Add(entry);
        return entry;
    }

    // "convo=1234 scrollback=4000/4000 map=812", in the order they were tracked;
    // "none" with nothing open. Two of a key (two maps open) read "map=812+40".
    public string Describe()
    {
        Entry[] entries;
        lock (_gate) entries = _entries.ToArray();
        if (entries.Length == 0) return "none";
        StringBuilder sb = new();
        foreach (IGrouping<string, Entry> group in entries.GroupBy(e => e.Key, StringComparer.Ordinal))
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(group.Key).Append('=').Append(string.Join("+", group.Select(Read)));
        }
        return sb.ToString();
    }

    private static string Read(Entry entry)
    {
        try
        {
            string count = entry.Count().ToString(CultureInfo.InvariantCulture);
            return entry.Capacity is { } cap ? count + "/" + cap().ToString(CultureInfo.InvariantCulture) : count;
        }
        catch (Exception ex)
        {
            // A window torn down between the copy and the read: say so, and let the rest be read.
            return "?(" + ex.GetType().Name + ")";
        }
    }
}
