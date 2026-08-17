using System.Collections.Generic;

namespace MudPlay.Game.Spells;

// In-memory model of the local character's spell book: the full set of spells
// the current class can ever learn (Available, from KnownSpellCatalog) paired
// with the subset the character has actually obtained (keyed by Spells.Number).
//
// Available is the class list with the level gate ignored (queried at level 0)
// — every spell the class can reach, each row carrying its own ReqLevel; the
// obtained checkmark and the per-spell formula results scale to Level.
//
// The obtained set is fed by the live spells / pow list (a full snapshot via
// SetObtainedByNames) and the learn-scroll line (an incremental add via
// MarkObtainedByName). Both report the spell's full Name, so resolution matches
// against Available by name. Reroll clears it (ClearObtained).
//
// The authoritative obtained set is kept as spell NAMES (_obtainedNames), not
// Spells.Number values: numbers are per-data-set, so swapping the active set (or
// reimporting) renumbers the rows and a number-keyed set silently empties itself
// the moment the set changes under it. _obtained is a number cache re-derived
// from those names against the current class list on every list rebuild
// (Reseed / a class change) — so obtained spells survive a data-set swap, which
// previously only a full profile reload could repair.
public sealed class SpellbookState
{
    private readonly KnownSpellCatalog _catalog;
    // Authoritative: obtained spells by canonical Name (survives data-set renumber).
    private readonly HashSet<string> _obtainedNames = new(StringComparer.OrdinalIgnoreCase);
    // Derived cache: the same spells' current Spells.Number values, re-resolved
    // from _obtainedNames whenever the class list rebuilds. Backs IsObtained.
    private readonly HashSet<int> _obtained = new();
    private List<KnownSpell> _available = new();
    private SpellPick[] _availablePicks = Array.Empty<SpellPick>();

    public SpellbookState(KnownSpellCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _catalog = catalog;
    }

    // The active Classes.Number the book is built for (0 = none / no class observed yet).
    public int ClassNumber { get; private set; }

    // The character level the per-spell formula results scale to. Does not gate Available.
    public int Level { get; private set; }

    // Character alignment used by the eligibility filter (0 = unknown / unrestricted).
    public int CharAlign { get; private set; }

    // Every spell the current class can learn, sorted by ReqLevel then Name. Empty for non-magery classes.
    public IReadOnlyList<KnownSpell> Available => _available;

    // The Available spells as distinct (by cast-code) SpellPick entries, ordered
    // by name — the suggestion source for the Settings spell-picker typeahead
    // boxes. Each carries the 4-letter Short cast-code (the value the box commits)
    // alongside the full name. Empty for non-magery classes. Rebuilt only when the
    // class list rebuilds; a bare level change leaves it unchanged.
    public IReadOnlyList<SpellPick> AvailablePicks => _availablePicks;

    // Fires when the available list or the obtained set changes.
    public event Action? Changed;

    // Resolve a Spells.Number to its full Name across the whole table. The Spell
    // Book's per-spell effect rollup uses this to render a RemovesSpell (Abil 122)
    // target, which can point at any spell rather than only the current class's
    // learnable list.
    public string? ResolveSpellName(int spellNumber) => _catalog.GetSpellNameByNumber(spellNumber);

    // Build the textblock → cast-spell reverse index across the whole Spells
    // table. The Spell Book's per-spell effect rollup uses it to expand an
    // Abil-148 (TextBlock) reference into the real effect the textblock casts,
    // which can point at any spell rather than only the current class's learnable
    // list.
    public IReadOnlyDictionary<int, IReadOnlyList<KnownSpell>> BuildCastByTextblockIndex()
        => _catalog.BuildCastByTextblockIndex();

    // The per-round mana cost of the spell with this Spells.Short cast-code, or
    // null when no available spell matches. Level-independent — the energy
    // multiplier folds in, the player level does not. The casting engine uses it
    // to skip a survival cast we can't actually pay for. Case-insensitive on the
    // trimmed cast-code; first match wins (duplicate Shorts share a cost).
    public int? ManaCostOf(string castCode)
        => FindByCastCode(castCode) is { } s ? (int)SpellCalculator.ManaCost(s.Formula) : null;

    // The available spell whose Spells.Short cast-code matches castCode
    // (case-insensitive, trimmed), or null when none does. First match wins —
    // duplicate Shorts share a formula. The Settings spell-picker preview uses it
    // to pull a pick's level-scaled numbers (mana cost, roll range) without
    // re-walking the list itself.
    public KnownSpell? FindByCastCode(string castCode)
    {
        if (string.IsNullOrWhiteSpace(castCode)) return null;
        string target = castCode.Trim();
        foreach (KnownSpell s in _available)
            if (string.Equals(s.Short.Trim(), target, StringComparison.OrdinalIgnoreCase))
                return s;
        return null;
    }

    // The cast-on-use items the active class can use (wands / scrolls / potions
    // carrying an Items code-43 CastsSp ability). The Spell Book lists these
    // alongside learnable spells. Empty when no class is set yet.
    public IReadOnlyList<ClassCastItem> GetCastItems() => _catalog.GetClassCastItems(ClassNumber);

    // Items.Number of the first item that teaches the given spell (LearnSp), or 0
    // when none does. Backs the Spell Book's double-click-to-item-record.
    public int GetTeachingItemNumber(int spellNumber) => _catalog.GetTeachingItemNumber(spellNumber);

    // Monsters.Number of the NPC that teaches the given spell (from "Learned From"),
    // or 0 when it isn't NPC-taught. Backs the double-click for trainer-taught spells.
    public int GetTeachingNpcNumber(int spellNumber) => _catalog.GetTeachingNpcNumber(spellNumber);

    // spellNumber → the trainer LEARN-level gate for the current class (from TBInfo),
    // when higher than the spell's ReqLevel. Backs the Spell Book's unlock-level
    // column so it shows when THIS class can actually learn each spell.
    public IReadOnlyDictionary<int, int> GetTeachLevels() => _catalog.BuildTeachLevelsForClass(ClassNumber);

    // True when the character has learned the spell with this Spells.Number.
    public bool IsObtained(int spellNumber) => _obtained.Contains(spellNumber);

    // How many spells the character has obtained.
    public int ObtainedCount => _obtained.Count;

    // The full names of every obtained spell — the persistence snapshot. Read
    // from the authoritative name set (not the number cache) so a save taken
    // while the active set can't currently resolve a spell — mid data-set swap —
    // still persists it. Resolved names come first in the stable Available order;
    // any the current set can't resolve are appended so persistence never drops a
    // learned spell just because the "wrong" set is loaded. Empty when nothing is
    // obtained.
    public IReadOnlyList<string> ObtainedNames
    {
        get
        {
            List<string> names = new(_obtainedNames.Count);
            HashSet<string> emitted = new(StringComparer.OrdinalIgnoreCase);
            foreach (KnownSpell s in _available)
                if (_obtainedNames.Contains(s.Name) && emitted.Add(s.Name)) names.Add(s.Name);
            foreach (string n in _obtainedNames)
                if (emitted.Add(n)) names.Add(n);
            return names;
        }
    }

    // Rebuild Available for a new class+level. The available list only depends on
    // class+alignment (the level gate is ignored), so it's recomputed only when
    // those change; a bare level change still fires Changed so formula displays
    // rescale. On a class-list rebuild the obtained number cache is re-derived
    // from the obtained NAMES against the new list, so obtained spells re-resolve
    // to the current data-set's numbers; names the new class can't learn (a
    // genuine reroll) simply don't resolve. Reroll clears the set outright via
    // ClearObtained, so a shared spell name can't linger as obtained.
    public void Refresh(int classNumber, int level, int charAlign = 0)
    {
        bool classChanged = classNumber != ClassNumber || charAlign != CharAlign;
        bool levelChanged = level != Level;
        ClassNumber = classNumber;
        Level = level;
        CharAlign = charAlign;

        if (classChanged) RebuildAvailable();
        if (classChanged || levelChanged) Changed?.Invoke();
    }

    // The active game-data set changed under the current class: the Spells /
    // Classes tables were replaced (and may have renumbered rows), so
    // unconditionally re-query Available and re-resolve the obtained numbers by
    // name even when the class number is unchanged. Without this a set swap
    // leaves Available (and the obtained cache) holding the old set's rows — the
    // Spell Book / Buff Watchdog blank until a stat re-parse or profile reload.
    public void Reseed(int classNumber, int level, int charAlign = 0)
    {
        ClassNumber = classNumber;
        Level = level;
        CharAlign = charAlign;
        RebuildAvailable();
        Changed?.Invoke();
    }

    private void RebuildAvailable()
    {
        _available = new List<KnownSpell>(_catalog.Query(ClassNumber, level: 0, CharAlign));
        ResolveObtainedFromNames();
        RebuildAvailablePicks();
    }

    // Re-derive the obtained number cache from the authoritative obtained names
    // against the current Available list — the step that lets obtained spells
    // survive a data-set renumber (their names re-resolve to the new numbers).
    private void ResolveObtainedFromNames()
    {
        _obtained.Clear();
        foreach (string name in _obtainedNames)
            if (FindAvailableByName(name) is { } s) _obtained.Add(s.Number);
    }

    // Rebuild the distinct-by-cast-code pick list, stamping each with whether the
    // character has obtained it. Runs on a class change (new _available) AND on
    // every obtained-set change (learn line / spells poll / reroll), so the
    // picker's learned flag tracks live. When nothing is obtained yet (unknown —
    // the spell list hasn't been parsed) every pick is Learned so the guard stays
    // silent rather than striking through the whole class.
    private void RebuildAvailablePicks()
    {
        bool obtainedKnown = _obtained.Count > 0;
        _availablePicks = _available
            .Where(s => !string.IsNullOrWhiteSpace(s.Short))
            .GroupBy(s => s.Short.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => new SpellPick(
                g.Key, g.First().Name.Trim(),
                Learned: !obtainedKnown || g.Any(s => _obtained.Contains(s.Number))))
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    // Replace the obtained set with exactly the spells named in names (the
    // authoritative snapshot from a spells / pow block). Names that don't resolve
    // to an available spell are ignored. No-ops (no event) when the resolved set
    // matches the current one. The canonical names are retained as the source of
    // truth so the set survives a later data-set renumber (see Reseed).
    public void SetObtainedByNames(IEnumerable<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        HashSet<string> nextNames = new(StringComparer.OrdinalIgnoreCase);
        HashSet<int> nextNums = new();
        foreach (string name in names)
            if (FindAvailableByName(name) is { } s)
            {
                nextNames.Add(s.Name);
                nextNums.Add(s.Number);
            }

        if (nextNames.SetEquals(_obtainedNames)) return;
        _obtainedNames.Clear();
        _obtainedNames.UnionWith(nextNames);
        _obtained.Clear();
        _obtained.UnionWith(nextNums);
        RebuildAvailablePicks();
        Changed?.Invoke();
    }

    // Mark a single spell obtained by its full Name — the learn-scroll signal
    // ("…learn the spell harm."). Returns the resolved spell (so a caller can
    // surface it), or null when the name doesn't match an available spell. Fires
    // Changed only when the spell was newly added.
    public KnownSpell? MarkObtainedByName(string name)
    {
        if (FindAvailableByName(name) is not { } match) return null;
        _obtainedNames.Add(match.Name);
        if (_obtained.Add(match.Number))
        {
            RebuildAvailablePicks();
            Changed?.Invoke();
        }
        return match;
    }

    // Drop every obtained spell (reroll / "you have no spells"). Clears the
    // authoritative name set too, so a later list rebuild can't re-resolve a
    // stale spell back into the book.
    public void ClearObtained()
    {
        if (_obtained.Count == 0 && _obtainedNames.Count == 0) return;
        _obtainedNames.Clear();
        _obtained.Clear();
        RebuildAvailablePicks();
        Changed?.Invoke();
    }

    private KnownSpell? FindAvailableByName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        string target = name.Trim();
        foreach (KnownSpell s in _available)
            if (string.Equals(s.Name.Trim(), target, StringComparison.OrdinalIgnoreCase))
                return s;
        return null;
    }
}
