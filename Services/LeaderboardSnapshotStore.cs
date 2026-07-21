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
    public void Add(LeaderboardSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _snapshots.Insert(0, snapshot);
        if (_snapshots.Count > MaxSnapshots)
            _snapshots.RemoveRange(MaxSnapshots, _snapshots.Count - MaxSnapshots);
        Persist();
        _log?.Log(LogSeverity.Info, "Leaderboard",
            $"Captured top {snapshot.RequestedCount} — {snapshot.Entries.Count} heroes; "
            + $"{_snapshots.Count} snapshot(s) stored.");
        Changed?.Invoke();
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
