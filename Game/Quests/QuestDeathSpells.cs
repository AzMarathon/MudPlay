using System.Globalization;
using System.Text.Json;
using MudPlay.Services;

namespace MudPlay.Game.Quests;

// Spell number → the monsters whose death casts it, for telling "kill this monster" script
// lines from the rest. A monster's DeathSpell is cast when it dies, and a spell's EndCast
// ability (151) names the spell that follows it — the quest monsters die into a short
// "<monster> temp" spell that ends by casting the "<monster> text" spell whose textblock holds
// the script — so the follow-on spells count as well (GAME_MECHANICS "Quest kill steps &
// monster placement"). Built once per game-data set.
internal static class QuestDeathSpells
{
    private const int EndCastAbility = 151;
    private const int AbilitySlots = 10;
    private const int MaxChain = 16;

    public static IReadOnlyDictionary<int, IReadOnlyList<int>> For(GameDataCache cache)
    {
        ArgumentNullException.ThrowIfNull(cache);
        return cache.Derived("quest death spells", () => Build(cache));
    }

    private static IReadOnlyDictionary<int, IReadOnlyList<int>> Build(GameDataCache cache)
    {
        Dictionary<int, List<int>> bySpell = new();
        JsonDocument? monsters = cache.GetRawTable("Monsters");
        JsonDocument? spells = cache.GetRawTable("Spells");
        if (monsters is null || spells is null) return Publish(bySpell);

        Dictionary<int, JsonElement> spellRows = new();
        foreach (JsonElement row in spells.RootElement.EnumerateArray())
            if (row.ValueKind == JsonValueKind.Object && Int(row, "Number") is int n and > 0)
                spellRows.TryAdd(n, row);

        foreach (JsonElement row in monsters.RootElement.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object) continue;
            if (Int(row, "Number") is not int monster || monster <= 0) continue;
            if (Int(row, "DeathSpell") is not int death || death <= 0) continue;
            if (!spellRows.TryGetValue(death, out JsonElement deathRow)) continue;
            // A death spell that targets no one (0) or a user (8) never fires on death.
            if (Int(deathRow, "Targets") is null or 0 or 8) continue;

            HashSet<int> seen = new();
            Queue<int> pending = new();
            pending.Enqueue(death);
            while (pending.Count > 0 && seen.Count < MaxChain)
            {
                int spell = pending.Dequeue();
                if (!seen.Add(spell) || !spellRows.TryGetValue(spell, out JsonElement spellRow)) continue;
                if (!bySpell.TryGetValue(spell, out List<int>? owners)) bySpell[spell] = owners = new List<int>();
                if (!owners.Contains(monster)) owners.Add(monster);

                for (int slot = 0; slot < AbilitySlots; slot++)
                {
                    string s = slot.ToString(CultureInfo.InvariantCulture);
                    if (Int(spellRow, "Abil-" + s) == EndCastAbility && Int(spellRow, "AbilVal-" + s) is int next and > 0)
                        pending.Enqueue(next);
                }
            }
        }
        return Publish(bySpell);
    }

    private static IReadOnlyDictionary<int, IReadOnlyList<int>> Publish(Dictionary<int, List<int>> bySpell)
        => bySpell.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<int>)kv.Value);

    private static int? Int(JsonElement row, string property)
        => row.TryGetProperty(property, out JsonElement el)
           && el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out int value)
            ? value : null;
}
