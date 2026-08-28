using System.Linq;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.Services.Patterns;

namespace MudPlay.Game.Combat;

// Per-character log of actual combat outcomes observed against specific
// monsters, surfaced by Monster Intel's "Your Observations" section — the
// personal counterpart to MonsterCatalog's authoritative MDB facts, kept
// visibly separate rather than blended into them.
//
// Attribution: UserHits carries the target's name in its own capture group,
// resolved to a MonsterNumber via the current room's RoomEntityClassifier
// snapshot (RoomEntity.RawName, already room-aware-resolved). UserMisses and
// the no-effect lines carry no name at all on the wire, so those are
// attributed to the live combat target instead — the same name→number
// resolution CombatManager itself does internally (it isn't public, so this
// mirrors the pattern rather than reusing CombatManager's private method).
//
// Mirrors PlayerSightingTracker's persistence shape: an in-memory dictionary
// (here keyed by MonsterNumber) is authoritative during a session, hydrates
// from CharacterProfile.MonsterObservations on profile load, and snapshots
// back on ProfileSaving (write-on-next-save, not a disk write per swing).
public sealed class MonsterObservationTracker : IDisposable
{
    private readonly RoomEntityClassifier _roomClassifier;
    private readonly Func<string?> _currentTarget;
    private readonly ProfileService? _profile;
    private readonly Func<DateTimeOffset> _clock;

    private readonly Dictionary<int, MonsterObservation> _observations = new();

    private readonly IDisposable _userHitsSub;
    private readonly IDisposable _userMissesSub;
    private readonly IDisposable _weaponNoEffectSub;
    private readonly IDisposable _fistsNoEffectSub;
    private readonly IDisposable _spellNoEffectSub;
    private readonly IDisposable _combatStatusSub;

    // UserMisses also matches self-emotes ending in "!" (its skeleton has no
    // real target anchor) — the same false-positive risk CombatSessionTracker
    // guards against with this identical engagement gate. Duplicated rather
    // than shared because CombatSessionTracker keeps its _engaged flag
    // private and the gate is only a few lines.
    private bool _engaged;

    private bool _disposed;

    // Raised after any observation is recorded or cleared, so the Monster
    // Intel VM can refresh. Fires on the dispatch thread (router lines are
    // already marshalled upstream).
    public event Action? Changed;

    public MonsterObservationTracker(
        MessageRouter router,
        RoomEntityClassifier roomClassifier,
        Func<string?> currentTarget,
        ProfileService? profile,
        Func<DateTimeOffset>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(roomClassifier);
        ArgumentNullException.ThrowIfNull(currentTarget);
        _roomClassifier = roomClassifier;
        _currentTarget = currentTarget;
        _profile = profile;
        _clock = clock ?? (static () => DateTimeOffset.Now);

        _userHitsSub = router.Subscribe(KnownPatterns.UserHits, OnUserHits);
        _userMissesSub = router.Subscribe(KnownPatterns.UserMisses, OnUserMisses);
        _weaponNoEffectSub = router.Subscribe(KnownPatterns.WeaponNoEffect, OnPhysicalNoEffect);
        _fistsNoEffectSub = router.Subscribe(KnownPatterns.FistsNoEffect, OnPhysicalNoEffect);
        _spellNoEffectSub = router.Subscribe(KnownPatterns.SpellNoEffect, OnSpellNoEffect);
        _combatStatusSub = router.Subscribe(KnownPatterns.CombatStatus, OnCombatStatus);

        if (_profile is not null)
        {
            _profile.ProfileLoaded += OnProfileLoaded;
            _profile.ProfileClosed += OnProfileClosed;
            _profile.ProfileSaving += OnProfileSaving;
            Hydrate(_profile.Current);
        }
    }

    // Point-in-time copy of the logged observations.
    public IReadOnlyList<MonsterObservation> Snapshot() => _observations.Values.ToArray();

    public MonsterObservation? For(int monsterNumber) =>
        _observations.TryGetValue(monsterNumber, out MonsterObservation? o) ? o : null;

    // User-driven wipe. Saves immediately (matching PlayerSightingTracker's
    // Clear) so the wipe survives a restart even with no later profile save.
    public void Clear()
    {
        _observations.Clear();
        if (_profile?.Current is not null)
        {
            _profile.Current.MonsterObservations = null;
            _profile.Save();
        }
        Changed?.Invoke();
    }

    private void OnUserHits(MatchResult match)
    {
        if (match.Groups.Count < 3
            || !string.Equals(match.Groups[0], "You", StringComparison.OrdinalIgnoreCase)) return;
        if (!int.TryParse(match.Groups[2], out int dmg)) return;
        if (ResolveNumber(match.Groups[1]) is not { } number) return;

        _engaged = true; // a landed swing means we're mid-combat, same as CombatSessionTracker
        MonsterObservation o = GetOrCreate(number);
        if (o.HitCount == 0) { o.HitDamageMin = dmg; o.HitDamageMax = dmg; }
        else
        {
            if (dmg < o.HitDamageMin) o.HitDamageMin = dmg;
            if (dmg > o.HitDamageMax) o.HitDamageMax = dmg;
        }
        o.HitCount++;
        o.HitDamageSum += dmg;
        Touch(o);
    }

    private void OnUserMisses(MatchResult _)
    {
        if (!_engaged) return;
        if (ResolveCurrentTargetNumber() is not { } number) return;
        MonsterObservation o = GetOrCreate(number);
        o.MissCount++;
        Touch(o);
    }

    private void OnPhysicalNoEffect(MatchResult _)
    {
        if (ResolveCurrentTargetNumber() is not { } number) return;
        MonsterObservation o = GetOrCreate(number);
        o.PhysicalNoEffectCount++;
        Touch(o);
    }

    private void OnSpellNoEffect(MatchResult match)
    {
        int? number = match.Groups.Count > 0 ? ResolveNumber(match.Groups[0]) : null;
        number ??= ResolveCurrentTargetNumber();
        if (number is not { } n) return;
        MonsterObservation o = GetOrCreate(n);
        o.SpellNoEffectCount++;
        Touch(o);
    }

    private void OnCombatStatus(MatchResult match)
    {
        if (match.Groups.Count == 0) return;
        _engaged = string.Equals(match.Groups[0], "Engaged", StringComparison.OrdinalIgnoreCase);
    }

    private int? ResolveCurrentTargetNumber()
    {
        string? target = _currentTarget();
        return string.IsNullOrEmpty(target) ? null : ResolveNumber(target);
    }

    private int? ResolveNumber(string name)
    {
        if (_roomClassifier.Current is not { } obs) return null;
        foreach (RoomEntity e in obs.Entities)
        {
            if (e.Kind != EntityKind.Monster || e.MonsterNumber is not { } number) continue;
            if (string.Equals(e.RawName, name, StringComparison.OrdinalIgnoreCase)) return number;
        }
        return null;
    }

    private MonsterObservation GetOrCreate(int number)
    {
        if (_observations.TryGetValue(number, out MonsterObservation? existing)) return existing;
        MonsterObservation o = new() { MonsterNumber = number, FirstObservedAt = _clock() };
        _observations[number] = o;
        return o;
    }

    private void Touch(MonsterObservation o)
    {
        o.LastObservedAt = _clock();
        Changed?.Invoke();
    }

    private void OnProfileLoaded(CharacterProfile profile) => Hydrate(profile);

    private void OnProfileClosed()
    {
        _observations.Clear();
        Changed?.Invoke();
    }

    private void OnProfileSaving(CharacterProfile profile)
    {
        profile.MonsterObservations = _observations.Count == 0
            ? null
            : _observations.Values.OrderByDescending(o => o.LastObservedAt).ToList();
    }

    private void Hydrate(CharacterProfile? profile)
    {
        _observations.Clear();
        if (profile?.MonsterObservations is { } rows)
            foreach (MonsterObservation o in rows)
                if (o.MonsterNumber > 0) _observations[o.MonsterNumber] = o;
        Changed?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _userHitsSub.Dispose();
        _userMissesSub.Dispose();
        _weaponNoEffectSub.Dispose();
        _fistsNoEffectSub.Dispose();
        _spellNoEffectSub.Dispose();
        _combatStatusSub.Dispose();
        if (_profile is not null)
        {
            _profile.ProfileLoaded -= OnProfileLoaded;
            _profile.ProfileClosed -= OnProfileClosed;
            _profile.ProfileSaving -= OnProfileSaving;
        }
    }
}
