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

    // Our own alignment word: our row in the realm's players list, which every `who`
    // that shows us rewrites. The list is kept per realm, so a same-named character
    // of ours on another realm of the board can't overwrite it (it once did: a Good
    // paladin read as Villain and its Good-only gear blocked, report
    // paradigm-20260927-134201). null until a `who` has shown us on this realm.
    // Whatever gates on our alignment reads this.
    public string? SelfAlignment => _players.Find(_stats.Name)?.Alignment;

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
        SetStale(false);
    }

    // A new profile is a new character: its alignment isn't stale from the last one's.
    public void ResetForProfile() => SetStale(false);

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
