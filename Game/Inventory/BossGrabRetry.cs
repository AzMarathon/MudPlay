using MudPlay.Services;

namespace MudPlay.Game.Inventory;

// Follows up a boss's Grab All. The grab is sent blind the moment the boss dies, and
// the game can throw those commands away: a boss whose death casts a silent spell on
// the room leaves everyone there unable to act for a moment, and what is sent in
// that moment is echoed and never run (report paradigm-20261007-111929: four `get`s
// after a kill, none answered, the drops still on the floor).
//
// So the room is looked at again after the grab, and any drop the "You notice" list
// still shows is asked for again, a few times at most. A drop that isn't listed was
// taken, or never dropped.
public sealed class BossGrabRetry
{
    public const int MaxTries = 3;

    // How long after the kill the floor is still watched.
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(20);
    // A room display this soon after a `get` may have been drawn before the get ran.
    private static readonly TimeSpan AnswerTime = TimeSpan.FromSeconds(1);
    // When to look at the room again after sending.
    private static readonly TimeSpan LookAfter = TimeSpan.FromMilliseconds(1500);

    private readonly Action<string> _send;
    private readonly Action<TimeSpan, Action> _scheduleAfter;
    private readonly Func<DateTimeOffset> _now;
    private readonly LogService? _log;

    // Item name → how many times it has been asked for again.
    private readonly Dictionary<string, int> _wanted = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _armedAt;
    private DateTimeOffset _lastSent;
    private int _generation;

    public BossGrabRetry(Action<string> send, Action<TimeSpan, Action> scheduleAfter,
        Func<DateTimeOffset>? now = null, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(send);
        ArgumentNullException.ThrowIfNull(scheduleAfter);
        _send = send;
        _scheduleAfter = scheduleAfter;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _log = log;
    }

    public bool IsArmed => _wanted.Count > 0;

    // The grab for these items has just been sent.
    public void Arm(IEnumerable<string> itemNames)
    {
        ArgumentNullException.ThrowIfNull(itemNames);
        _wanted.Clear();
        foreach (string name in itemNames)
            if (!string.IsNullOrWhiteSpace(name)) _wanted[name.Trim()] = 0;
        if (_wanted.Count == 0) return;
        _armedAt = _lastSent = _now();
        ScheduleLook();
    }

    // We left the room: its floor is no longer ours to check.
    public void Clear()
    {
        _wanted.Clear();
        _generation++;
    }

    // A room display's "You notice <list> here." list, as the game printed it.
    public void OnNoticeSurvey(string list)
    {
        if (_wanted.Count == 0) return;
        DateTimeOffset now = _now();
        if (now - _armedAt > Window) { Clear(); return; }
        if (now - _lastSent < AnswerTime) return;

        HashSet<string> onFloor = new(StringComparer.OrdinalIgnoreCase);
        foreach (string entry in list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            onFloor.Add(CountedCommand.SplitLeadingCount(entry).Name);

        int sent = 0;
        foreach (string name in _wanted.Keys.ToList())
        {
            if (!onFloor.Contains(name)) { _wanted.Remove(name); continue; }   // taken
            if (_wanted[name] >= MaxTries)
            {
                _log?.Warn("GrabAll", $"'{name}' is still on the floor after {MaxTries} more tries — leaving it");
                _wanted.Remove(name);
                continue;
            }
            _wanted[name]++;
            _send($"get {name}");
            sent++;
        }
        if (sent == 0) return;
        _log?.Info("GrabAll", $"{sent} drop(s) still on the floor — the grab didn't take, asking again");
        _lastSent = now;
        ScheduleLook();
    }

    // The empty line re-draws the room (as a `look` would, without the description),
    // which is what shows whether the last gets took.
    private void ScheduleLook()
    {
        int generation = ++_generation;
        _scheduleAfter(LookAfter, () =>
        {
            if (generation != _generation || _wanted.Count == 0) return;
            _send(string.Empty);
        });
    }
}
