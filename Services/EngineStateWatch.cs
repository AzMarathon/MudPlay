namespace MudPlay.Services;

// Remembers how long each engine has been in its present state, and its last
// few changes of state. A bug report could say which state a walk, a loop or a
// door handler was in, but not for how long: "Pending for 4 minutes" and
// "Pending for half a second" are different bugs.
//
// The engines aren't touched. Each state is read once a second through a probe
// and compared with what it was; a change is stamped with the time it was seen,
// so a "since" is good to the second.
//
// Sample is the only writer, and runs on the UI thread's timer. Snapshot is the
// reader and may be called from any thread (a crash report is written from the
// thread that crashed): it reads each probe once more, so what a report prints is
// the state at the click, but works that into its own copies and remembers
// nothing. The change it saw is stamped for good by the next Sample.
public sealed class EngineStateWatch
{
    public static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(1);

    public readonly record struct Change(DateTimeOffset At, string From, string To);

    // SinceWatchStart: the state hasn't changed since the watch began, so it is
    // at least this old, not exactly.
    public readonly record struct Reading(
        string Name, string Value, DateTimeOffset Since, bool SinceWatchStart, IReadOnlyList<Change> Recent);

    // Name, Read and Keep never change. The rest is what was last seen, guarded
    // by _lastSeenLock.
    private sealed class Probe(string name, Func<string> read, int keep)
    {
        public readonly string Name = name;
        public readonly Func<string> Read = read;
        public readonly int Keep = keep;
        public string? Value;
        public DateTimeOffset Since;
        public bool Changed;
        public readonly Queue<Change> Recent = new();
    }

    // What a probe was last seen as, copied out under the lock.
    private readonly record struct LastSeen(Probe Probe, string? Value, DateTimeOffset Since, bool Changed, Change[] Recent);

    // Guards the probe list and every probe's last-seen fields. Never held while
    // a probe is read: a probe calls into an engine.
    private readonly object _lastSeenLock = new();
    private readonly List<Probe> _probes = new();

    // keep: how many of the probe's last changes are remembered.
    public void Add(string name, Func<string> read, int keep = 6)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(read);
        Probe probe = new(name, read, Math.Max(1, keep));
        lock (_lastSeenLock) { _probes.Add(probe); }
    }

    public void Sample(DateTimeOffset now)
    {
        Probe[] probes;
        lock (_lastSeenLock) { probes = _probes.ToArray(); }
        string[] values = new string[probes.Length];
        for (int i = 0; i < probes.Length; i++) values[i] = ReadNow(probes[i]);

        lock (_lastSeenLock)
        {
            for (int i = 0; i < probes.Length; i++)
            {
                Probe probe = probes[i];
                string value = values[i];
                if (probe.Value is null)
                {
                    probe.Value = value;
                    probe.Since = now;
                    continue;
                }
                if (string.Equals(probe.Value, value, StringComparison.Ordinal)) continue;
                if (probe.Recent.Count >= probe.Keep) probe.Recent.Dequeue();
                probe.Recent.Enqueue(new Change(now, probe.Value, value));
                probe.Value = value;
                probe.Since = now;
                probe.Changed = true;
            }
        }
    }

    // A probe reads another engine's live state: one that throws must not stop
    // the rest from being read, and is itself worth seeing.
    private static string ReadNow(Probe probe)
    {
        try { return probe.Read() ?? "(null)"; }
        catch (Exception ex) { return $"(unreadable: {ex.GetType().Name})"; }
    }

    public IReadOnlyList<Reading> Snapshot(DateTimeOffset now)
    {
        LastSeen[] seen;
        lock (_lastSeenLock)
        {
            seen = new LastSeen[_probes.Count];
            for (int i = 0; i < seen.Length; i++)
            {
                Probe probe = _probes[i];
                seen[i] = new LastSeen(probe, probe.Value, probe.Since, probe.Changed, probe.Recent.ToArray());
            }
        }

        List<Reading> readings = new(seen.Length);
        foreach (LastSeen last in seen)
        {
            string value = ReadNow(last.Probe);
            if (last.Value is null)
            {
                // Never sampled: all that is known is what it is now.
                readings.Add(new Reading(last.Probe.Name, value, now, true, last.Recent));
                continue;
            }
            if (string.Equals(last.Value, value, StringComparison.Ordinal))
            {
                readings.Add(new Reading(last.Probe.Name, value, last.Since, !last.Changed, last.Recent));
                continue;
            }
            // Changed since the last Sample: shown as that Sample's successor
            // would record it, in a copy.
            int kept = Math.Min(last.Recent.Length, last.Probe.Keep - 1);
            Change[] recent = new Change[kept + 1];
            Array.Copy(last.Recent, last.Recent.Length - kept, recent, 0, kept);
            recent[kept] = new Change(now, last.Value, value);
            readings.Add(new Reading(last.Probe.Name, value, now, false, recent));
        }
        return readings;
    }
}
