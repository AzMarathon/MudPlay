using System.Text.Json;

namespace MudPlay.Game.Spells;

public sealed partial class SpellbookState
{
    private JsonElement? _gmcpSpells;
    public JsonElement? ServerSpell(int id) => _gmcpSpells is { } snapshot
        ? snapshot.GetProperty("spells").EnumerateArray().Where(s => s.GetProperty("id").GetInt32() == id).Select(s => (JsonElement?)s).FirstOrDefault() : null;

    public void ApplyGmcpSpells(JsonElement? value)
    {
        if (value is null && _gmcpSpells is null) return;
        _gmcpSpells = value;
        if (value is null) RebuildAvailable();
        ComposeAvailable(); Changed?.Invoke();
    }

    private void ComposeGmcpSpells()
    {
        Dictionary<int, KnownSpell> catalog = _catalog.Query(ClassNumber, level: 0, charAlign: 0).ToDictionary(s => s.Number);
        List<KnownSpell> known = new();
        _obtained.Clear(); _obtainedNames.Clear();
        foreach (JsonElement row in _gmcpSpells!.Value.GetProperty("spells").EnumerateArray())
        {
            int id = row.GetProperty("id").GetInt32();
            catalog.TryGetValue(id, out KnownSpell prior);
            SpellFormulaInput formula = (prior.Number == 0 ? new SpellFormulaInput() : prior.Formula) with { Number = id, ManaCost = row.GetProperty("cost").GetInt32(), EnergyCost = row.GetProperty("energy").GetInt32() };
            int targets = row.GetProperty("delivery").GetString() switch { "self" => 1, "self-or-ally" => 2, "single" => 8, "room" => 12, "party" => 13, _ => prior.Targets };
            KnownSpell spell = new(id, row.GetProperty("short").GetString()!, row.GetProperty("name").GetString()!,
                prior.Magery, prior.MageryLvl, row.GetProperty("level").GetInt32(), targets, formula);
            known.Add(spell); catalog[id] = spell; _obtained.Add(id); _obtainedNames.Add(spell.Name);
        }
        _available = known;
        _classSpells = catalog.Values.OrderBy(s => s.ReqLevel).ThenBy(s => s.Number).ToArray();
        RebuildAvailablePicks();
    }
}
