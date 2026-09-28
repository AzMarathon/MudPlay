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
    private readonly LogService? _log;
    private readonly IDisposable _darkCloudSub;
    private readonly IDisposable _evilPointsSub;
    private readonly IDisposable _minEvilPointsSub;

    // True when a dark-cloud line has fired since the last `who` refresh.
    public bool IsStale { get; private set; }

    // Our own alignment word: our row in the realm's players list, which every `who`
    // that shows us rewrites. The list is kept per realm, so a same-named character
    // of ours on another realm of the board can't overwrite it (it once did: a Good
    // paladin read as Villain and its Good-only gear blocked, report
    // paradigm-20260927-134201). null until a `who` has shown us on this realm.
    // After a dark cloud while recorded Good, it reads "Neutral" until the next `who`
    // (see OnDarkCloud). Whatever gates on our alignment reads this.
    // A Paradigm `pro` reading, when newer than our last `who` row, wins: it's the
    // exact number, not just the title.
    public string? SelfAlignment =>
        _leftGood ? "Neutral" : _proTitle ?? _players.Find(_stats.Name)?.Alignment;

    // Paradigm's `pro`: our exact evil points and the `set mineps` floor our drift
    // toward good stops at (null until a `pro` shows them; Stock's `pro` never does).
    public double? EvilPoints { get; private set; }
    public double? MinEvilPoints { get; private set; }

    private string? _proTitle;

    // The lowest evil-only value the game refused us: our evil points are below it.
    // Only an evil gain (a dark cloud) or an exact `pro` reading outdates it.
    private int? _refusedEvilOnly;

    // A dark cloud since the last `pro`: the exact number is now only a floor.
    private bool _gainedSincePro;

    // Our alignment reading changed (a `pro` EPs line, a refused evil-only item, or an
    // evil gain that outdates either) — gear gating re-evaluates.
    public event Action? Refreshed;

    // A dark cloud landed while we were recorded Good: we're Neutral at best now, so
    // Good-only gear can't be worn, even before a `who` says exactly where we are.
    private bool _leftGood;

    // Raised whenever IsStale changes.
    public event Action? StaleChanged;

    // Raised when a dark cloud takes us out of Good — the one shift that changes what
    // we can wear, and worth a `who` to learn exactly where we landed.
    public event Action? LeftGood;

    public AlignmentTracker(MessageRouter router, PlayerStats stats, PlayerDatabase players, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(stats);
        ArgumentNullException.ThrowIfNull(players);
        _stats = stats;
        _players = players;
        _log = log;

        _darkCloudSub = router.Subscribe(
            Services.Patterns.KnownPatterns.AlignmentDarkCloud, _ => OnDarkCloud());
        _evilPointsSub = router.Subscribe(
            Services.Patterns.KnownPatterns.AlignmentEvilPoints, m => OnEvilPoints(m));
        _minEvilPointsSub = router.Subscribe(
            Services.Patterns.KnownPatterns.AlignmentMinEvilPoints, m =>
            {
                if (m.Groups.Count > 0 && TryParse(m.Groups[0], out double floor)) MinEvilPoints = floor;
            });
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
        _leftGood = false;
        _proTitle = null;   // the `who` row is now the newest reading
        SetStale(false);
    }

    // A Paradigm `pro` "EPs:" line: our exact alignment. The line only exists on
    // Paradigm, so its thresholds apply.
    private void OnEvilPoints(MatchResult m)
    {
        if (m.Groups.Count == 0 || !TryParse(m.Groups[0], out double ep)) return;
        EvilPoints = ep;
        _proTitle = Calculators.AlignmentBands.TitleForEvilPoints(ep, RealmType.ParaMud);
        _refusedEvilOnly = null;
        _gainedSincePro = false;
        _leftGood = false;
        SetStale(false);
        Refreshed?.Invoke();
    }

    // Where our evil points can be. The exact `pro` number while it's our newest
    // reading, else the who title's band, narrowed by a refused evil-only item.
    // Null while our alignment is unknown.
    public Calculators.EvilPointRange? SelfEvilPoints(RealmType realm)
    {
        Calculators.EvilPointRange? range = _proTitle is not null && EvilPoints is { } ep
            ? (_gainedSincePro ? new Calculators.EvilPointRange(ep, double.PositiveInfinity) : Calculators.EvilPointRange.Exact(ep))
            : Calculators.EvilPointRange.ForTitle(SelfAlignment, realm);
        if (range is { } r && _refusedEvilOnly is { } refused)
            return r.Below(refused) ?? r;   // a contradicting refusal predates the title
        return range;
    }

    // The game refused an evil-only item with this value while nothing else about us
    // barred it: our evil points are below the value. Stock's `who` shows only the
    // title, so this is how the client narrows a straddling band.
    public void NoteEvilOnlyRefused(int value, string itemName)
    {
        if (value <= 0 || _refusedEvilOnly <= value) return;
        _refusedEvilOnly = value;
        _log?.Info("Alignment", $"refused {itemName} (evil only {value}) — our evil points are below {value}");
        Refreshed?.Invoke();
    }

    private static bool TryParse(string text, out double value) =>
        double.TryParse(text, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out value);

    // An evil shift. From Good it lands at Neutral at best (the alignment ladder in
    // GAME_MECHANICS), so Good-only gear is out from this moment. From Neutral or
    // worse it only goes further evil, which changes nothing we'd act on — no check.
    private void OnDarkCloud()
    {
        SetStale(true);
        // We gained evil points, so a refused evil-only item may fit now.
        bool widens = _refusedEvilOnly is not null || (_proTitle is not null && !_gainedSincePro);
        _refusedEvilOnly = null;
        _gainedSincePro = true;
        if (widens) Refreshed?.Invoke();
        if (_leftGood
            || Inventory.ItemEquipFilter.BucketForWord(SelfAlignment) != Calculators.AlignmentBucket.Good)
            return;
        _leftGood = true;
        LeftGood?.Invoke();
    }

    // A new profile is a new character: its alignment isn't stale from the last one's.
    public void ResetForProfile()
    {
        _leftGood = false;
        _proTitle = null;
        _refusedEvilOnly = null;
        _gainedSincePro = false;
        EvilPoints = null;
        MinEvilPoints = null;
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
        _evilPointsSub.Dispose();
        _minEvilPointsSub.Dispose();
        _players.ObservationRecorded -= OnObservationRecorded;
    }
}
