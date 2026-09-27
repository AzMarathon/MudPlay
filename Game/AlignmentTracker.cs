using System;
using MudPlay.Models.GameData;
using MudPlay.Services;

namespace MudPlay.Game;

// Tracks whether the local character's last-observed alignment is stale.
// MajorMUD prints "A dark cloud passes over you" whenever the character's
// alignment shifts toward evil (an evil-point gain) — but the alignment word
// the Character Workshop displays (parsed from `who`) only updates on the next
// `who`. This watcher flags the alignment stale on that line and clears the
// flag once our own row is re-observed in a `who` response.
//
// Long-lived (app lifetime), so the dark-cloud line is caught even while the
// Character Workshop is closed; the Workshop's Character Info tab reads IsStale
// when it opens and tracks it live via StaleChanged.
public sealed class AlignmentTracker : IDisposable
{
    private readonly PlayerStats _stats;
    private readonly PlayerDatabase _players;
    private readonly IDisposable _darkCloudSub;

    // True when a dark-cloud line has fired since the last `who` refresh.
    public bool IsStale { get; private set; }

    // Our own alignment word as a `who` showed it this profile session, or null until
    // one has. The saved players list is per BBS and persists, so our row there can be
    // hours old, or written by a same-named character of ours on another realm of the
    // board: a Good paladin was read as Villain and its Good-only gear blocked (report
    // paradigm-20260927-134201). Whatever gates on our alignment reads this instead.
    public string? SelfAlignment { get; private set; }

    // Raised whenever IsStale changes.
    public event Action? StaleChanged;

    public AlignmentTracker(MessageRouter router, PlayerStats stats, PlayerDatabase players)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(stats);
        ArgumentNullException.ThrowIfNull(players);
        _stats = stats;
        _players = players;

        _darkCloudSub = router.Subscribe(
            Services.Patterns.KnownPatterns.AlignmentDarkCloud, _ => SetStale(true));
        _players.ObservationRecorded += OnObservationRecorded;
    }

    // Our own row re-observed in a `who` → the displayed alignment is fresh
    // again. Names are matched on GIVEN name (the same key the observation was
    // recorded under), case-insensitively.
    private void OnObservationRecorded(string givenName)
    {
        (string self, _) = PlayerObservation.SplitName(_stats.Name);
        if (string.IsNullOrEmpty(self)
            || !string.Equals(self, givenName, StringComparison.OrdinalIgnoreCase))
            return;
        SelfAlignment = _players.Find(_stats.Name)?.Alignment;
        SetStale(false);
    }

    // A new profile is a new character: forget what the last `who` said about us.
    public void ResetForProfile()
    {
        SelfAlignment = null;
        SetStale(false);
    }

    private void SetStale(bool value)
    {
        if (IsStale == value) return;
        IsStale = value;
        StaleChanged?.Invoke();
    }

    public void Dispose()
    {
        _darkCloudSub.Dispose();
        _players.ObservationRecorded -= OnObservationRecorded;
    }
}
