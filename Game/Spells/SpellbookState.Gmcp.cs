using System.Text.Json;

namespace MudPlay.Game.Spells;

// The server's learned spells (Anomaly.Spells.List): while it is applied, the
// learned spells are the server's, each over its game-data record with the
// server's cost, energy, level and delivery.
public sealed partial class SpellbookState
{
    private JsonElement? _gmcpSpells;

    // The server's reading of a learned spell, or null.
    public JsonElement? ServerSpell(int id)
    {
        if (_gmcpSpells is not { } snapshot) return null;
        foreach (JsonElement spell in snapshot.GetProperty("spells").EnumerateArray())
            if (spell.GetProperty("id").GetInt32() == id) return spell;
        return null;
    }

    // null hands the list back to the text's obtained spells.
    public void ApplyGmcpSpells(JsonElement? value)
    {
        if (value is null && _gmcpSpells is null) return;
        _gmcpSpells = value;
        if (value is null) RebuildAvailable();
        ComposeAvailable();
        Changed?.Invoke();
    }

    private void ComposeGmcpSpells()
    {
        Dictionary<int, KnownSpell> catalog = _catalog.Query(ClassNumber, level: 0, charAlign: 0).ToDictionary(s => s.Number);
        List<KnownSpell> known = new();
        _obtained.Clear();
        _obtainedNames.Clear();
        foreach (JsonElement row in _gmcpSpells!.Value.GetProperty("spells").EnumerateArray())
        {
            int id = row.GetProperty("id").GetInt32();
            catalog.TryGetValue(id, out KnownSpell prior);
            SpellFormulaInput formula = (prior.Number == 0 ? new SpellFormulaInput() : prior.Formula) with
            {
                Number = id,
                ManaCost = row.GetProperty("cost").GetInt32(),
                EnergyCost = row.GetProperty("energy").GetInt32(),
            };
            // The delivery as the game data's Targets code.
            int targets = row.GetProperty("delivery").GetString() switch
            {
                "self" => 1,
                "self-or-ally" => 2,
                "single" => 8,
                "room" => 12,
                "party" => 13,
                _ => prior.Targets,
            };
            KnownSpell spell = new(id, row.GetProperty("short").GetString()!, row.GetProperty("name").GetString()!,
                prior.Magery, prior.MageryLvl, row.GetProperty("level").GetInt32(), targets, formula);
            known.Add(spell);
            catalog[id] = spell;
            _obtained.Add(id);
            _obtainedNames.Add(spell.Name);
        }
        // The server names only what is learned; what the class can still learn stays
        // listed as the text's book lists it, so the shop trip and the unlearned
        // reports still see it. A cast code a learned spell owns rules its twins out.
        HashSet<int> learned = new(_obtained);
        Dictionary<string, string> learnedByCode = new(StringComparer.OrdinalIgnoreCase);
        foreach (KnownSpell s in known)
            if (!string.IsNullOrWhiteSpace(s.Short)) learnedByCode.TryAdd(s.Short.Trim(), s.Name);
        foreach (KnownSpell s in _alignedSpells)
        {
            if (learned.Contains(s.Number)) continue;
            if (learnedByCode.TryGetValue(s.Short.Trim(), out string? owner)
                && !string.Equals(owner, s.Name, StringComparison.OrdinalIgnoreCase)) continue;
            known.Add(s);
        }
        known.Sort(KnownSpellCatalog.CompareByReqLevelThenName);
        _available = known;
        _classSpells = catalog.Values.OrderBy(s => s.ReqLevel).ThenBy(s => s.Number).ToArray();
        RebuildAvailablePicks();
    }
}
