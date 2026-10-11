namespace MudPlay.Services;

// Remembers how long each engine has been in its present state, and its last
// few changes of state. A bug report could say which state a walk, a loop or a
// door handler was in, but not for how long: "Pending for 4 minutes" and
// "Pending for half a second" are different bugs.
//
// The engines aren't touched. Each state is read once a second through a probe
// and compared with what it was; a change is stamped with the time it was seen,
// so a "since" is good to the second. Snapshot samples again first, so what a
// report prints is the state at the click.
public sealed class EngineStateWatch
{
    public static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(1);

    public readonly record struct Change(DateTimeOffset At, string From, string To);

    // SinceWatchStart: the state hasn't changed since the watch began, so it is
    // at least this old, not exactly.
    public readonly record struct Reading(
        string Name, string Value, DateTimeOffset Since, bool SinceWatchStart, IReadOnlyList<Change> Recent);

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

    private readonly List<Probe> _probes = new();

    // keep: how many of the probe's last changes are remembered.
    public void Add(string name, Func<string> read, int keep = 6)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(read);
        _probes.Add(new Probe(name, read, Math.Max(1, keep)));
    }

    public void Sample(DateTimeOffset now)
    {
        foreach (Probe probe in _probes)
        {
            string value;
            // A probe reads another engine's live state: one that throws must
            // not stop the rest from being sampled, and is itself worth seeing.
            try { value = probe.Read() ?? "(null)"; }
            catch (Exception ex) { value = $"(unreadable: {ex.GetType().Name})"; }

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

    public IReadOnlyList<Reading> Snapshot(DateTimeOffset now)
    {
        Sample(now);
        List<Reading> readings = new(_probes.Count);
        foreach (Probe probe in _probes)
            readings.Add(new Reading(probe.Name, probe.Value ?? "(not read)", probe.Since, !probe.Changed, probe.Recent.ToArray()));
        return readings;
    }
}
