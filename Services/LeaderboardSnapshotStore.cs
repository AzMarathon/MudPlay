using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using FujinTerm.Game.Leaderboard;

namespace FujinTerm.Services;

// Per-BBS store of captured "top N" leaderboard snapshots. Mirrors
// RoomBlacklistStore: loads Data/BBS/{bbs}/leaderboard.json on every BBS pin, and
// exposes a read-only, newest-first history the XP/HR calculator diffs across.
//
// History is bounded (MaxSnapshots): XP/HR only needs the two most recent
// appearances of each character, and unbounded growth on an actively-captured
// board would bloat the file for no gain. The oldest snapshots fall off the tail.
public sealed class LeaderboardSnapshotStore
{
    // Enough captures to give every listed character a prior reading to diff
    // against (a character can be absent from a few captures if the user varied
    // the requested count), without letting the file grow without limit.
    private const int MaxSnapshots = 50;

    private readonly LogService? _log;
    private string? _activeBbs;
    private readonly List<LeaderboardSnapshot> _snapshots = new();

    // Fires on load against a new BBS and on every capture / clear. The
    // Calculators tab rebuilds its table from this.
    public event Action? Changed;

    public LeaderboardSnapshotStore(LogService? log = null)
    {
        _log = log;
    }

    // Captured listings, newest first. Empty until the first capture on this BBS.
    public IReadOnlyList<LeaderboardSnapshot> Snapshots => _snapshots;

    // Record a fresh capture at the front and trim the tail past MaxSnapshots.
    //
    // A capture only earns a slot when it differs from the most recent one in a way
    // that matters for XP/HR: someone's experience moved, or the roster changed (a
    // name dropped off and another took its place). Re-running "top N" a few times
    // on a quiet board otherwise piles up identical snapshots that can never yield a
    // rate — so an unchanged capture is discarded, and the next real change diffs
    // against the still-standing earlier one over its true elapsed time.
    public void Add(LeaderboardSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (_snapshots.Count > 0 && !DiffersFrom(_snapshots[0], snapshot))
        {
            _log?.Log(LogSeverity.Info, "Leaderboard",
                $"Captured top {snapshot.RequestedCount} — no experience or roster change "
                + "since the last capture; discarded.");
            return;
        }
        _snapshots.Insert(0, snapshot);
        if (_snapshots.Count > MaxSnapshots)
            _snapshots.RemoveRange(MaxSnapshots, _snapshots.Count - MaxSnapshots);
        Persist();
        _log?.Log(LogSeverity.Info, "Leaderboard",
            $"Captured top {snapshot.RequestedCount} — {snapshot.Entries.Count} heroes; "
            + $"{_snapshots.Count} snapshot(s) stored.");
        Changed?.Invoke();
    }

    // True when candidate carries a change worth keeping vs the previous capture:
    // a different roster (identities added / removed / swapped) or any shared hero
    // whose experience moved — or whose class changed, the fingerprint of a reroll
    // reusing the name. Identity is by first name, matching the XP/HR calculator.
    private static bool DiffersFrom(LeaderboardSnapshot previous, LeaderboardSnapshot candidate)
    {
        // A first name shared by two heroes in one listing is vanishingly rare on a
        // top board; last write wins here, which is fine for a change test.
        Dictionary<string, (long Exp, string Class)> before = new(StringComparer.OrdinalIgnoreCase);
        foreach (LeaderboardEntry e in previous.Entries) before[e.FirstName] = (e.Experience, e.Class);

        Dictionary<string, (long Exp, string Class)> now = new(StringComparer.OrdinalIgnoreCase);
        foreach (LeaderboardEntry e in candidate.Entries) now[e.FirstName] = (e.Experience, e.Class);

        if (before.Count != now.Count) return true;
        foreach (KeyValuePair<string, (long Exp, string Class)> hero in now)
        {
            if (!before.TryGetValue(hero.Key, out (long Exp, string Class) was)) return true;
            if (was.Exp != hero.Value.Exp) return true;
            if (!string.Equals(was.Class, hero.Value.Class, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    // Wipe the history for the active BBS (the tab's "Clear history" button).
    public void Clear()
    {
        if (_snapshots.Count == 0) return;
        _snapshots.Clear();
        Persist();
        Changed?.Invoke();
    }

    // Load the history for the active BBS. Wired to ProfileService.ProfileLoaded /
    // BbsPinApplied through AppServices, same as RoomBlacklistStore.
    public void OnBbsPinApplied(string? bbs)
    {
        if (string.IsNullOrWhiteSpace(bbs))
        {
            if (_activeBbs is not null)
            {
                _activeBbs = null;
                _snapshots.Clear();
                Changed?.Invoke();
            }
            return;
        }

        _activeBbs = bbs;
        _snapshots.Clear();

        string path = AppPaths.BbsLeaderboardFile(bbs);
        if (File.Exists(path))
        {
            try
            {
                string raw = File.ReadAllText(path);
                List<LeaderboardSnapshot>? loaded =
                    JsonSerializer.Deserialize<List<LeaderboardSnapshot>>(raw);
                if (loaded is not null)
                    _snapshots.AddRange(loaded);
                _log?.Log(LogSeverity.Info, "Leaderboard",
                    $"Loaded {_snapshots.Count} snapshot(s) from '{path}'.");
            }
            catch (JsonException ex)
            {
                _log?.Log(LogSeverity.Warn, "Leaderboard",
                    $"Failed to parse '{path}': {ex.Message}");
            }
        }

        Changed?.Invoke();
    }

    private void Persist()
    {
        if (_activeBbs is null) return;
        string path = AppPaths.BbsLeaderboardFile(_activeBbs);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var opts = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(path, JsonSerializer.Serialize(_snapshots, opts));
    }
}
