using MudPlay.Game.Spells;
using MudPlay.Services;
using MudPlay.Services.Patterns;

namespace MudPlay.Game.Combat;

// Aggregates our own combat into the CombatSessionStats the Session Stats panel
// displays: our swing accuracy (hit / miss / crit), the damage of our swings, procs
// and spells, the blows that hit us and how hard, what we avoided, and the per-round
// damage spread. Pure downstream subscriber — it never sends to the wire.
//
// Damage comes off the same round ledger that prints the combat round totals
// (RoundDamageTracker.Attributed), so the two always agree on whose damage a line
// was. Of the lines the ledger credits to us, one matching the caster message of a
// spell we know is that spell's (CasterMessageMatcher, refreshed via
// RefreshMatchers); one the ledger read as a weapon proc, or phrased "Your …", is a
// proc; the rest are our swings, which OnUserHits classifies. A spell or proc line
// never counts as a swing, so it never moves the hit / miss / crit denominators or
// the swing extents. Every damage line the ledger credits to someone hitting us is a
// blow taken; misses and dodges come from the fixed patterns.
//
// Only the local player's own swings count toward the offensive figures, so
// OnUserHits requires the first-person "You" source (the same convention
// conversation.local uses to tell our own speech from another player's).
// Classification is keyword-driven on the raw line, per the MajorMUD line
// vocabulary: "surprise" ⇒ backstab, "critically" ⇒ crit (physical only — bash
// / smash / backstab / spells never crit), otherwise a plain hit.
//
// Threading: every mutation runs on the router's marshalled (UI) dispatch
// thread, as do RoundDamageTracker's events; the window VM reads Snapshot on the
// same thread. The counters are therefore lock-free, matching RoundDamageTracker.
public sealed class CombatSessionTracker : IDisposable
{
    private readonly IDisposable _userHitsSub;
    private readonly IDisposable _userMissesSub;
    private readonly IDisposable _userDodgesSub;
    private readonly IDisposable _mobMissesSub;
    private readonly IDisposable _combatStatusSub;
    private readonly RoundDamageTracker _rounds;

    // Our known spells' caster-message matchers, configured attack slots first,
    // refreshed on the data boundaries (see RefreshMatchers). A null resolver means no
    // spell recognition — our spell lines then count as procs or swings.
    private readonly Func<IReadOnlyList<(string Name, CasterMessageMatcher Matcher)>>? _resolveSpellMatchers;
    private IReadOnlyList<(string Name, CasterMessageMatcher Matcher)> _spellMatchers =
        Array.Empty<(string, CasterMessageMatcher)>();

    // Per-spell landed damage + resisted-cast count, keyed by spell name. Landed = a
    // recognised damage line; Misses = a resisted cast (see ResolvePendingSpellMiss).
    // Ordered by first appearance for a stable display.
    private sealed class SpellAccum { public DamageTally Dmg; public int Misses; }
    private readonly Dictionary<string, SpellAccum> _perSpell = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _spellOrder = new();
    private string? _lastSpell;

    private DamageTally _hit;
    private DamageTally _crit;
    private DamageTally _backstab;
    private DamageTally _round;
    private DamageTally _proc;
    private DamageTally _spell;
    private DamageTally _hitTaken;
    // The last counted miss may actually be a spell-cast EMOTE ("You scatter some
    // ashes in a sweeping motion!") — a self-emote ending in "!" the miss skeleton
    // can't tell from a real whiff. Set when a miss is counted; a spell that lands
    // right after retracts it (OnAttributed), and a physical hit / combat-off
    // resolves it (weapon whiff or resisted cast).
    private bool _emoteMissCandidate;
    // Any physical swing landed this combat — distinguishes a weapon whiff (keep the
    // miss) from a resisted spell cast (reattribute) at combat-off. Reset on Engaged.
    private bool _physicalHitThisCombat;
    private int _misses;
    private int _mobMisses;
    private int _dodges;
    // True between "*Combat Engaged*" and "*Combat Off*" (re-asserted by any
    // hit / mob line). The UserMisses skeleton also matches self-emotes ending
    // in "!", so a miss is only counted while this is set — outside combat a
    // "You feel much better!" can't be a swing whiff. Any real swing-miss is
    // bracketed by combat, so this never suppresses a genuine miss.
    private bool _engaged;
    // The line the ledger pass just claimed as one of our spells or procs, so the
    // UserHits pass that follows for the same line doesn't count it as a swing too.
    private string? _recognizedLine;
    private bool _disposed;

    // Raised after any observation updates the tallies, so the Session Stats VM
    // can refresh its bound figures. Fires on the dispatch thread; debouncing
    // (if wanted) is the subscriber's concern.
    public event Action? Changed;

    public CombatSessionTracker(
        MessageRouter router,
        RoundDamageTracker rounds,
        Func<IReadOnlyList<(string Name, CasterMessageMatcher Matcher)>>? resolveSpellMatchers = null)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(rounds);
        _rounds = rounds;
        _resolveSpellMatchers = resolveSpellMatchers;

        _userHitsSub     = router.Subscribe(KnownPatterns.UserHits,     OnUserHits);
        _userMissesSub   = router.Subscribe(KnownPatterns.UserMisses,   OnUserMisses);
        _userDodgesSub   = router.Subscribe(KnownPatterns.UserDodges,   OnUserDodges);
        _mobMissesSub    = router.Subscribe(KnownPatterns.MobMisses,    OnMobMisses);
        _combatStatusSub = router.Subscribe(KnownPatterns.CombatStatus, OnCombatStatus);
        // The ledger reads a line on MessageRouter.LineDispatched, before the fixed
        // patterns, so a spell / proc line is claimed before OnUserHits sees it.
        _rounds.Attributed += OnAttributed;
        _rounds.RoundComplete += OnRoundComplete;

        RefreshMatchers();
    }

    // Re-resolve our spells' matchers from live game data. Called on the boundaries
    // that move them: connect / character switch, a Combat-tab edit, a game-data set
    // swap, and a spellbook change.
    public void RefreshMatchers()
        => _spellMatchers = _resolveSpellMatchers?.Invoke() ?? Array.Empty<(string, CasterMessageMatcher)>();

    // The spells whose damage lines we recognise, configured attack slots first.
    public IReadOnlyList<string> RecognisedSpells => _spellMatchers.Select(m => m.Name).ToList();

    // True when line is the caster's-eye damage line of one of our spells.
    // RoundDamageTracker uses it to own a spell line that names no caster.
    public bool MatchesOwnSpell(string line) => SpellOf(line, out _) is not null;

    private string? SpellOf(string line, out int damage)
    {
        foreach ((string name, CasterMessageMatcher m) in _spellMatchers)
            if (m.TryMatchDamage(line, out damage)) return name;
        damage = 0;
        return null;
    }

    // Per-spell accumulator, created on first sight of a spell so the display keeps
    // configured-but-never-cast spells out until they actually fire.
    private SpellAccum Spell(string name)
    {
        if (!_perSpell.TryGetValue(name, out SpellAccum? a))
        {
            a = new SpellAccum();
            _perSpell[name] = a;
            _spellOrder.Add(name);
        }
        return a;
    }

    // A miss left un-retracted (no spell landed after it) in a combat with no
    // physical swing, right after we cast, was a RESISTED cast, not a whiff — move it
    // from the physical-miss bucket to the resist count of the spell we last landed,
    // else our first configured attack spell.
    private void ResolvePendingSpellMiss()
    {
        if (_emoteMissCandidate && !_physicalHitThisCombat && _rounds.CastLately
            && (_lastSpell ?? (_spellMatchers.Count > 0 ? _spellMatchers[0].Name : null)) is { } spell
            && _misses > 0)
        {
            _misses--;
            Spell(spell).Misses++;
            Changed?.Invoke();
        }
        _emoteMissCandidate = false;
    }

    private IReadOnlyList<SpellCombatStat> BuildSpellStats()
    {
        if (_spellOrder.Count == 0) return Array.Empty<SpellCombatStat>();
        List<SpellCombatStat> list = new(_spellOrder.Count);
        foreach (string name in _spellOrder)
        {
            SpellAccum a = _perSpell[name];
            if (a.Dmg.Count == 0 && a.Misses == 0) continue;
            list.Add(new SpellCombatStat(
                name, a.Dmg.Count, a.Misses,
                a.Dmg.Count == 0 ? 0 : a.Dmg.Min, a.Dmg.Max, a.Dmg.Sum));
        }
        return list;
    }

    // Point-in-time copy of the session's combat figures. The physical extent is
    // the min/max across whichever swing categories have landed; an empty
    // category contributes nothing.
    public CombatSessionStats Snapshot()
        => new(
            Hits:                _hit.Count,
            Crits:               _crit.Count,
            Backstabs:           _backstab.Count,
            Misses:              _misses,
            HitMinDamage:        _hit.Count == 0 ? 0 : _hit.Min,
            HitMaxDamage:        _hit.Max,
            HitTotalDamage:      _hit.Sum,
            CritMinDamage:       _crit.Count == 0 ? 0 : _crit.Min,
            CritMaxDamage:       _crit.Max,
            CritTotalDamage:     _crit.Sum,
            BackstabMinDamage:   _backstab.Count == 0 ? 0 : _backstab.Min,
            BackstabMaxDamage:   _backstab.Max,
            BackstabTotalDamage: _backstab.Sum,
            MobHits:             _hitTaken.Count,
            MobMisses:           _mobMisses,
            Dodges:              _dodges,
            HitTakenMinDamage:   _hitTaken.Count == 0 ? 0 : _hitTaken.Min,
            HitTakenMaxDamage:   _hitTaken.Max,
            HitTakenTotalDamage: _hitTaken.Sum,
            RoundsWithDamage:    _round.Count,
            RoundMinDamage:      _round.Count == 0 ? 0 : _round.Min,
            RoundMaxDamage:      _round.Max,
            RoundTotalDamage:    _round.Sum,
            ProcHits:            _proc.Count,
            ProcMinDamage:       _proc.Count == 0 ? 0 : _proc.Min,
            ProcMaxDamage:       _proc.Max,
            ProcTotalDamage:     _proc.Sum,
            SpellHits:           _spell.Count,
            SpellMinDamage:      _spell.Count == 0 ? 0 : _spell.Min,
            SpellMaxDamage:      _spell.Max,
            SpellTotalDamage:    _spell.Sum,
            Spells:              BuildSpellStats());

    // Zero every counter — called on the session boundary (connect / character
    // switch), matching RoundDamageTracker.Reset.
    public void Reset()
    {
        _hit = default;
        _crit = default;
        _backstab = default;
        _round = default;
        _proc = default;
        _spell = default;
        _hitTaken = default;
        _perSpell.Clear();
        _spellOrder.Clear();
        _lastSpell = null;
        _misses = 0;
        _mobMisses = 0;
        _dodges = 0;
        _emoteMissCandidate = false;
        _physicalHitThisCombat = false;
        _recognizedLine = null;
        _engaged = false;
        Changed?.Invoke();
    }

    private void OnAttributed(AttributedLine line)
    {
        _recognizedLine = null;
        DamageAttribution sides = line.Sides;
        if (sides.NoDealer) return;

        if (sides.Target == DamageLineAttributor.Self)
        {
            _engaged = true; // an incoming blow means we're mid-combat
            _hitTaken.Add(sides.Amount);
            Changed?.Invoke();
            return;
        }
        if (sides.Source != DamageLineAttributor.Self) return;

        // One of our spells. Its cast line ("You cast … for N damage!") has the
        // first-person "You" source UserHits also matches, so it's claimed here.
        if (SpellOf(line.Text, out int dmg) is { } spell)
        {
            _spell.Add(dmg);
            Spell(spell).Dmg.Add(dmg);
            _lastSpell = spell;
            // The cast's emote was just counted as a physical miss; a landed spell
            // means that "miss" was the emote — retract it so spell combat doesn't
            // inflate the miss count.
            if (_emoteMissCandidate && _misses > 0) _misses--;
            _emoteMissCandidate = false;
            _recognizedLine = line.Text;
            Changed?.Invoke();
            return;
        }

        // Our swings all read "You …"; anything else of ours — a weapon's "Your weapon
        // sears …", or a proc naming only its victim — is a proc.
        if (line.Proc || line.Text.StartsWith("Your ", StringComparison.Ordinal))
        {
            _proc.Add(sides.Amount);
            _recognizedLine = line.Text;
            Changed?.Invoke();
        }
    }

    private void OnUserHits(MatchResult match)
    {
        // A line the ledger pass already claimed as our spell or proc isn't a swing.
        if (string.Equals(match.Text, _recognizedLine, StringComparison.Ordinal)) return;

        // KnownPatterns.UserHits also fires on "The {mob} {verb} you for N
        // damage!" and on another player's swing. Only OUR own first-person
        // "You" swings feed the player's accuracy figures.
        if (match.Groups.Count < 3 ||
            !string.Equals(match.Groups[0], "You", StringComparison.OrdinalIgnoreCase))
            return;

        if (!int.TryParse(match.Groups[2], out int dmg)) return;

        _engaged = true; // a landed swing means we're mid-combat
        string line = match.Text;
        if (line.Contains("surprise", StringComparison.OrdinalIgnoreCase))
            _backstab.Add(dmg);
        else if (line.Contains("critically", StringComparison.OrdinalIgnoreCase))
            _crit.Add(dmg);
        else
            _hit.Add(dmg);
        _emoteMissCandidate = false; // a real physical swing landed — not a spell round
        _physicalHitThisCombat = true;
        Changed?.Invoke();
    }

    private void OnUserMisses(MatchResult _)
    {
        // The miss skeleton also matches self-emotes ending in "!", so only a
        // line seen while combat is engaged is a real swing whiff.
        if (!_engaged) return;
        _misses++;
        // This miss might be a spell-cast emote, not a whiff — a spell landing right
        // after retracts it (OnAttributed); an un-retracted one is resolved at
        // combat-off (weapon whiff kept, resisted cast reattributed).
        _emoteMissCandidate = true;
        Changed?.Invoke();
    }

    private void OnUserDodges(MatchResult _)
    {
        _engaged = true; // an incoming attack means we're mid-combat
        _dodges++;
        Changed?.Invoke();
    }

    private void OnMobMisses(MatchResult match)
    {
        // A dodge line satisfies MobMisses too (its "{mob} … at you" shape);
        // it's already counted by OnUserDodges, so skip it here to keep the
        // plain-miss tally and the dodge denominator from double-counting.
        if (match.Text.Contains("dodge", StringComparison.OrdinalIgnoreCase)) return;
        _engaged = true; // an incoming attack means we're mid-combat
        _mobMisses++;
        Changed?.Invoke();
    }

    private void OnCombatStatus(MatchResult match)
    {
        // (?<status>Engaged|Off) — arm the miss gate while engaged, disarm when
        // the server reports combat ended. Hits / mob lines re-arm it, so a
        // spurious mid-round "*Combat Off*" (the server emits one when we cast)
        // doesn't strand a following real swing-miss uncounted.
        if (match.Groups.Count == 0) return;
        bool engaged = string.Equals(match.Groups[0], "Engaged", StringComparison.OrdinalIgnoreCase);
        if (engaged)
            _physicalHitThisCombat = false; // fresh combat — no swing has landed yet
        else
            ResolvePendingSpellMiss();      // combat ended — reclassify a stuck emote-miss
        _engaged = engaged;
    }

    private void OnRoundComplete(RoundSummary summary)
    {
        // "Characters per round" damage = total damage WE dealt in the round;
        // skip rounds where we dealt nothing (pure-defence rounds) so they
        // don't drag the min to 0.
        if (summary.DamageDealt <= 0) return;
        _round.Add(summary.DamageDealt);
        Changed?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _userHitsSub.Dispose();
        _userMissesSub.Dispose();
        _userDodgesSub.Dispose();
        _mobMissesSub.Dispose();
        _combatStatusSub.Dispose();
        _rounds.Attributed -= OnAttributed;
        _rounds.RoundComplete -= OnRoundComplete;
    }

    // Running count + damage extent for one swing category. Min/Max are only
    // meaningful once Count > 0; callers guard the empty case.
    private struct DamageTally
    {
        public int Count;
        public int Min;
        public int Max;
        public long Sum;

        public void Add(int damage)
        {
            if (Count == 0) { Min = damage; Max = damage; }
            else
            {
                if (damage < Min) Min = damage;
                if (damage > Max) Max = damage;
            }
            Count++;
            Sum += damage;
        }
    }
}
