using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using FujinTerm.Game.GameData;
using FujinTerm.Game.Map;
using FujinTerm.Models.GameData;
using FujinTerm.Services;

namespace FujinTerm.Game.Remote;

// Keeps the party's level bounds warm for path planning. Whenever this
// character leads a party the tracker fires an @level probe on roster change
// (debounced by a roster signature) so PlayerDatabase holds each member's exact
// level, and exposes the synchronous Bounds that
// MovementFilter.PartyLevelBoundsProvider reads at BFS time to route the party
// around gates a member can't clear. Always-on: routing a following party
// around a gate the group can't clear is never wanted OFF (the alternative
// strands a member), so there's no opt-in toggle — the only gate is "am I
// leading a party".
//
// The async probe and the synchronous gate check are decoupled through
// PlayerDatabase: the probe (persisting via PlayerDatabase.RecordLevel)
// refreshes the cache in the background; Bounds only ever reads it. A member
// not yet probed contributes their title-derived band (its LOW end — the
// conservative floor) — or nothing, when even the title is unknown — so
// planning degrades gracefully until the reply lands instead of blocking on a
// round-trip. An exact level older than StalenessHorizon is treated as due for
// a fresh probe (WarmStaleLevels) — the member could have levelled since.
//
// Invited members are never probed: an invited-not-joined row exists only for
// the PartyWindow chip and isn't yet someone we route the party around, so it's
// excluded from the roster signature, the Bounds estimate, and the stale-warm
// scan. The probe re-fires on the invited→joined edge (IsInvited true→false),
// which arrives as a per-member PropertyChanged rather than a collection change.
//
// Read-only on party state: subscribes to PartyState.Members collection changes,
// each member's PropertyChanged (for the invited→joined edge), and the
// PartyState.IsInParty / PartyState.SelfIsLeader property changes; never writes a
// party field.
public sealed class PartyLevelTracker : IDisposable
{
    private readonly PartyState _party;
    private readonly PartyLevelProbe _probe;
    private readonly PlayerDatabase _players;
    private readonly Func<int?> _selfLevel;
    private readonly Func<DateTime> _clock;
    private readonly LogService? _log;
    private string _lastProbedSignature = string.Empty;
    private DateTime _lastStaleProbeAt = DateTime.MinValue;

    // An exact level older than this is treated as due for a fresh @level
    // re-probe: a party member could have levelled since we last asked, so a
    // long-lived reading shouldn't silently drift out of date and gate a route
    // on a level they've outgrown. A day matches how slowly a level changes
    // relative to a play session.
    public TimeSpan StalenessHorizon { get; set; } = TimeSpan.FromHours(24);

    // Debounces WarmStaleLevels so one route plan (many gate-exit evaluations)
    // fires at most one @level round-trip — same shape as PartyWealthTracker's
    // poll debounce.
    public TimeSpan WarmDebounce { get; set; } = TimeSpan.FromSeconds(30);

    public PartyLevelTracker(
        PartyState party,
        PartyLevelProbe probe,
        PlayerDatabase players,
        Func<int?> selfLevel,
        LogService? log = null)
        : this(party, probe, players, selfLevel, clock: null, log: log) { }

    internal PartyLevelTracker(
        PartyState party,
        PartyLevelProbe probe,
        PlayerDatabase players,
        Func<int?> selfLevel,
        Func<DateTime>? clock,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(party);
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(players);
        ArgumentNullException.ThrowIfNull(selfLevel);
        _party = party;
        _probe = probe;
        _players = players;
        _selfLevel = selfLevel;
        _clock = clock ?? (() => DateTime.UtcNow);
        _log = log;

        _party.Members.CollectionChanged += OnMembersChanged;
        _party.PropertyChanged += OnPartyPropertyChanged;
        // Subscribe to any members already present so their invited→joined flip
        // re-probes even if the roster predates this tracker.
        foreach (PartyMember m in _party.Members)
            m.PropertyChanged += OnMemberPropertyChanged;
        MaybeProbe();
    }

    // The party's most-constraining (Low, High) level window, or null when we're
    // not leading a party or nobody's level is known. Synchronous — reads only
    // the PlayerDatabase cache the probe keeps warm; each member contributes
    // their exact level when probed, else their title band.
    public (int Low, int High)? Bounds()
    {
        if (!_party.IsInParty || !_party.SelfIsLeader) return null;

        List<PartyLevelEstimate> estimates = new();
        foreach (PartyMember m in _party.Members)
        {
            if (m.IsSelf) continue;
            // Skip pending invitees — an invited-not-joined row isn't yet a
            // party member we route around, and hasn't been @level-probed.
            if (m.IsInvited) continue;
            if (string.IsNullOrEmpty(m.Name)) continue;
            PlayerRecord? rec = _players.Find(m.Name);
            int? exact = rec?.Level;
            (int Min, int Max)? title = exact is null
                ? ClassTitleTable.LookupLevelRange(rec?.Title)
                : null;
            estimates.Add(new PartyLevelEstimate(exact, title));
        }
        return PartyLevelBounds.Compute(_selfLevel(), estimates);
    }

    // Route-scoped freshness poll: fire an @level round when a member we'd gate
    // on is unknown (no exact level yet) or stale (exact level older than
    // StalenessHorizon). Called at walk-start via MovementFilter.WarmForRoute —
    // and only when the planned route actually crosses a level gate — so the
    // re-probe is demand-driven the same way the party @wealth poll is, rather
    // than firing @level on every hop. Debounced so one plan fires at most one
    // round-trip. Fire-and-forget: the reply refreshes the cache for the next
    // evaluation; the synchronous Bounds never blocks on it.
    public void WarmStaleLevels()
    {
        if (!_party.IsInParty || !_party.SelfIsLeader) return;

        DateTime now = _clock();
        if (now - _lastStaleProbeAt < WarmDebounce) return;

        bool anyStale = false;
        foreach (PartyMember m in _party.Members)
        {
            if (m.IsSelf || m.IsInvited || string.IsNullOrEmpty(m.Name)) continue;
            PlayerRecord? rec = _players.Find(m.Name);
            // Unknown (no exact reading) or aged past the horizon → due a probe.
            if (rec?.Level is null || rec.LevelAt is not { } at || now - at > StalenessHorizon)
            {
                anyStale = true;
                break;
            }
        }
        if (!anyStale) return;

        _lastStaleProbeAt = now;
        _log?.Info("PartyLevel", "Level gate on the planned route with stale/unknown member level — probing @level.");
        _ = _probe.QueryAsync();   // fire-and-forget; the probe persists levels via RecordLevel
    }

    private void OnMembersChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Detach evicted rows so a removed member can be GC'd cleanly.
        if (e.OldItems is not null)
            foreach (object? item in e.OldItems)
                if (item is PartyMember m) m.PropertyChanged -= OnMemberPropertyChanged;

        // Subscribe fresh rows so the later invited→joined flip re-probes.
        if (e.NewItems is not null)
            foreach (object? item in e.NewItems)
                if (item is PartyMember m) m.PropertyChanged += OnMemberPropertyChanged;

        MaybeProbe();
    }

    // Per-row PropertyChanged — a member added as IsInvited only becomes a real
    // party member (and an @level target) when they accept via `follow`, which
    // flips IsInvited true→false without a CollectionChanged event. Re-probe on
    // that transition so the newly joined member's level warms; without this hook
    // the roster signature would never change on acceptance and we'd never ask.
    private void OnMemberPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PartyMember.IsInvited)) return;
        if (sender is not PartyMember m) return;
        if (m.IsInvited) return;   // only the invited→joined edge probes
        MaybeProbe();
    }

    private void OnPartyPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PartyState.IsInParty) or nameof(PartyState.SelfIsLeader))
            MaybeProbe();
    }

    // Fire an @level probe when leading a party whose roster changed since the
    // last probe. The roster signature debounces unrelated party-state churn
    // (HP polls, leader-name refresh) down to one probe per genuine membership
    // change. Clears the signature whenever we stop leading, so re-forming the
    // same party re-probes.
    private void MaybeProbe()
    {
        if (!_party.IsInParty || !_party.SelfIsLeader)
        {
            _lastProbedSignature = string.Empty;
            return;
        }

        string signature = RosterSignature();
        if (signature.Length == 0) { _lastProbedSignature = string.Empty; return; }
        if (signature == _lastProbedSignature) return;
        _lastProbedSignature = signature;

        _log?.Info("PartyLevel", "Roster changed — probing party @level.");
        _ = _probe.QueryAsync();   // fire-and-forget; the probe persists levels via RecordLevel
    }

    private string RosterSignature()
    {
        IEnumerable<string> names = _party.Members
            .Where(m => !m.IsSelf && !m.IsInvited && !string.IsNullOrEmpty(m.Name))
            .Select(m => m.Name)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase);
        return string.Join('\n', names);   // a newline can't appear in a player name
    }

    public void Dispose()
    {
        _party.Members.CollectionChanged -= OnMembersChanged;
        _party.PropertyChanged -= OnPartyPropertyChanged;
        foreach (PartyMember m in _party.Members)
            m.PropertyChanged -= OnMemberPropertyChanged;
    }
}
