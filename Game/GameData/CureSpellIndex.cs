using System.Text.Json;
using MudPlay.Models.GameData;
using MudPlay.Services;

namespace MudPlay.Game.GameData;

// Maps a Spells-table row to the ailment(s) its cast REMOVES — the inverse of
// SpellAilmentIndex (which maps the spells that APPLY an ailment). Drives the
// party ailment-chip CURE-clear path: a witnessed "X casts <cure spell> on Y!"
// clears Y's chip regardless of whether the local character can cast that spell,
// so a Priest's cure poison clears a Mystic-run client's chip for the poisoned
// member. (The local character's own configured cures are irrelevant — a
// party-mate casts with their own spells, which the local class may never learn.)
//
// Cure encoding, verified against real stock + paradigm Spells data:
//   poison  — CurePoison(20), or DispellMagic(73) targeting the Poison apply code (19)
//   hold    — Freedom(81), or DispellMagic(73) targeting HoldPerson(74)/Paralyze(75)
//   blind   — DispellMagic(73) targeting BlindingLight(53)/BlindUser(107)
//   disease — RemovesSpell(122) targeting a spell that APPLIES disease. Disease has
//             no ability code of its own, so the disease-apply set is supplied by the
//             caller — the Messages catalogue's Diseased-flagged spells, the same
//             source ConditionTracker uses to detect disease in the first place.
// The DispellMagic indirection is what separates cure from apply: a DIRECT
// BlindUser(107) code is the blind APPLY (spell "blind"), while DispellMagic(73)=107
// is the blind CURE (spell "cure blindness"). Keying cures on 73/81/20/122 never
// confuses the two. Combined heal+cures (curing wind, merciful grace) carry
// CurePoison(20) beside Heal(18), so they register as poison cures for free.
//
// Rebuilt lazily when the active set changes (mirrors SpellAilmentIndex); Invalidate()
// drops the cache when the Messages catalogue (the disease-apply source) changes.
public sealed class CureSpellIndex
{
    // Ability codes (AbilityNames.cs). Direct codes mean APPLY; the CURE codes are
    // CurePoison / Freedom and the DispellMagic / RemovesSpell indirections.
    private const int CurePoison = 20;
    private const int DispellMagic = 73;
    private const int HoldPerson = 74;
    private const int Paralyze = 75;
    private const int Freedom = 81;
    private const int BlindingLight = 53;
    private const int BlindUser = 107;
    private const int PoisonApply = 19;
    private const int RemovesSpell = 122;

    private readonly GameDataCache _cache;
    private readonly Func<IReadOnlySet<int>> _diseaseApplyNumbers;
    private List<CureSpell>? _cures;
    private string? _builtForSet;

    public CureSpellIndex(GameDataCache cache, Func<IReadOnlySet<int>>? diseaseApplyNumbers = null)
    {
        ArgumentNullException.ThrowIfNull(cache);
        _cache = cache;
        _diseaseApplyNumbers = diseaseApplyNumbers
            ?? (static () => (IReadOnlySet<int>)System.Collections.Immutable.ImmutableHashSet<int>.Empty);
    }

    // One cure spell: its Spells-table number + name, and the ailments its cast removes.
    public readonly record struct CureSpell(int Number, string Name, MessageFlags Cures);

    // Every cure spell in the active set (empty when no set / no cures).
    public IReadOnlyList<CureSpell> AllCures() => Map();

    // Drop the cache so the next read rebuilds — call when the Messages catalogue
    // (the disease-apply source) changes within a session.
    public void Invalidate() => _cures = null;

    private List<CureSpell> Map()
    {
        if (_cures is not null && _builtForSet == _cache.ActiveSet) return _cures;
        _cures = Build(_cache, _diseaseApplyNumbers());
        _builtForSet = _cache.ActiveSet;
        return _cures;
    }

    private static List<CureSpell> Build(GameDataCache cache, IReadOnlySet<int> diseaseApply)
    {
        List<CureSpell> result = new();
        JsonDocument? doc = cache.GetRawTable("Spells");
        if (doc is null) return result;
        foreach (JsonElement row in doc.RootElement.EnumerateArray())
        {
            int num = ReadInt(row, "Number");
            if (num <= 0) continue;
            List<(int Code, int Val)> codes = new();
            for (int i = 0; i < 10; i++)
            {
                int c = ReadInt(row, $"Abil-{i}");
                if (c != 0) codes.Add((c, ReadInt(row, $"AbilVal-{i}")));
            }
            MessageFlags cured = CuredFlags(codes, diseaseApply);
            if (cured != MessageFlags.None)
                result.Add(new CureSpell(num, ReadString(row, "Name"), cured));
        }
        return result;
    }

    // Pure map from a spell's ability (code, value) pairs to the ailment flags it
    // cures — the testable core.
    public static MessageFlags CuredFlags(
        IReadOnlyList<(int Code, int Val)> abilities, IReadOnlySet<int> diseaseApplyNumbers)
    {
        MessageFlags cured = MessageFlags.None;
        foreach ((int code, int val) in abilities)
        {
            switch (code)
            {
                case CurePoison: cured |= MessageFlags.Poisoned; break;
                case Freedom:    cured |= MessageFlags.MovementPrevented; break;
                case DispellMagic:
                    if (val is BlindingLight or BlindUser) cured |= MessageFlags.Blinded;
                    else if (val is HoldPerson or Paralyze) cured |= MessageFlags.MovementPrevented;
                    else if (val == PoisonApply) cured |= MessageFlags.Poisoned;
                    break;
                case RemovesSpell:
                    if (val > 0 && diseaseApplyNumbers.Contains(val)) cured |= MessageFlags.Diseased;
                    break;
            }
        }
        return cured;
    }

    private static int ReadInt(JsonElement row, string prop)
        => row.TryGetProperty(prop, out JsonElement e)
           && e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out int n) ? n : 0;

    private static string ReadString(JsonElement row, string prop)
        => row.TryGetProperty(prop, out JsonElement e) && e.ValueKind == JsonValueKind.String
           ? (e.GetString() ?? string.Empty) : string.Empty;
}
