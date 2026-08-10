using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using MudPlay.Game.Leaderboard;

namespace MudPlay.Services;

// Per-BBS store of captured "top N" leaderboard snapshots. Mirrors
// RoomBlacklistStore: loads Data/BBS/{bbs}/leaderboard.json on every BBS pin, and
// exposes a read-only, newest-first history the XP/HR calculator diffs across.
//
// History is bounded (MaxSnapshots): XP/HR only reaches back to a character's
// previous changed reading, and unbounded growth on an actively-captured board
// would bloat the file for no gain. The oldest snapshots fall off the tail.
public sealed class LeaderboardSnapshotStore
{
    // A small ring is enough: the rate diffs the latest capture against the most
    // recent prior one that showed change, so a handful of captures covers every
    // listed character's baseline. Older captures give only staler baselines —
    // there's nothing to gain from hoarding them, so the oldest fall off the tail.
    private const int MaxSnapshots = 5;

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
                $"Captured {Describe(snapshot)} — no experience or roster change "
                + "since the last capture; discarded.");
            return;
        }
        _snapshots.Insert(0, snapshot);
        TrimHistory();
        Persist();
        _log?.Log(LogSeverity.Info, "Leaderboard",
            $"Captured {Describe(snapshot)} — {snapshot.Entries.Count} heroes; "
            + $"{_snapshots.Count} snapshot(s) stored.");
        LogRerollHints();
        Changed?.Invoke();
    }

    // Bound the history at MaxSnapshots, but never evict the snapshot that defines
    // the widest known board (the largest real list captured). The XP/HR table shows
    // the union of readings across the ring; if the widest capture fell off the tail,
    // a run of smaller "top 10" views would shrink the displayed board back down —
    // the very prune this store exists to prevent. So the oldest snapshot dropped is
    // always one that isn't the board-of-record.
    private void TrimHistory()
    {
        while (_snapshots.Count > MaxSnapshots)
        {
            int widest = 0;
            for (int i = 1; i < _snapshots.Count; i++)
                if (_snapshots[i].Entries.Count > _snapshots[widest].Entries.Count)
                    widest = i;

            int removeAt = _snapshots.Count - 1;
            if (removeAt == widest) removeAt--; // pin the widest; take the next-oldest
            _snapshots.RemoveAt(removeAt);
        }
    }

    // A bare "top" (no number) has no requested count; render it as "top" rather
    // than the misleading "top 0" in the log an operator reads.
    private static string Describe(LeaderboardSnapshot snapshot)
        => snapshot.RequestedCount > 0 ? $"top {snapshot.RequestedCount}" : "top";

    // After a kept capture, surface likely rerolls — a listed hero whose class
    // changed or whose experience fell versus their prior appearance — to the
    // program log. This replaces the table's old Note column: the hint now lives
    // where an operator watches. First appearances ("new") are skipped so the
    // opening capture of a full board doesn't spray a line per hero.
    private void LogRerollHints()
    {
        if (_log is null || _snapshots.Count < 2) return;
        LeaderboardReport report = LeaderboardXpRateCalculator.Build(_snapshots);
        foreach (LeaderboardRankRow row in report.Rows)
            if (row.Note.StartsWith("reroll", StringComparison.OrdinalIgnoreCase))
                _log.Log(LogSeverity.Info, "Leaderboard", $"{row.Name} ({row.Class}) — {row.Note}");
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
