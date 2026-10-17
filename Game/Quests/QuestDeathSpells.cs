using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using MudPlay.Services;

namespace MudPlay.Game.Quests;

// Spell number → the monsters whose death casts it, for telling "kill this monster" script
// lines from the rest. A monster's DeathSpell is cast when it dies, and a spell's EndCast
// ability (151) names the spell that follows it — the quest monsters die into a short
// "<monster> temp" spell that ends by casting the "<monster> text" spell whose textblock holds
// the script — so the follow-on spells count as well (GAME_MECHANICS "Quest kill steps &
// monster placement"). Built once per game-data set.
internal static partial class QuestDeathSpells
{
    private const int EndCastAbility = 151;
    private const int AbilitySlots = 10;
    private const int MaxChain = 16;

    public static IReadOnlyDictionary<int, IReadOnlyList<int>> For(GameDataCache cache)
    {
        ArgumentNullException.ThrowIfNull(cache);
        return cache.Derived("quest death spells", () => Build(cache));
    }

    // The monsters whose death runs a script called from location: its Called-From names
    // the spell at the end of their death-spell chain. Empty for any other script — one
    // that hangs off a monster itself is that NPC's dialogue, not its death
    // (GAME_MECHANICS "Quest kill steps & monster placement").
    public static List<int> KilledAt(GameDataCache cache, string? location)
    {
        List<int> targets = new();
        if (string.IsNullOrWhiteSpace(location)) return targets;
        IReadOnlyDictionary<int, IReadOnlyList<int>> deaths = For(cache);
        foreach (Match m in SpellRef().Matches(location))
            if (int.TryParse(m.Groups[1].Value, out int spell) && deaths.TryGetValue(spell, out IReadOnlyList<int>? monsters))
                foreach (int id in monsters)
                    if (!targets.Contains(id)) targets.Add(id);
        return targets;
    }

    // "Spell #604" inside a Called-From string — a script a spell runs.
    [GeneratedRegex(@"Spell\s+#(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex SpellRef();

    private static IReadOnlyDictionary<int, IReadOnlyList<int>> Build(GameDataCache cache)
    {
        Dictionary<int, List<int>> bySpell = new();
        GameTable? monsters = cache.GetTable("Monsters");
        GameTable? spells = cache.GetTable("Spells");
        if (monsters is null || spells is null) return Publish(bySpell);

        Dictionary<int, GameRow> spellRows = new();
        foreach (GameRow row in spells.Rows)
            if (Int(row, "Number") is int n and > 0)
                spellRows.TryAdd(n, row);

        foreach (GameRow row in monsters.Rows)
        {
            if (Int(row, "Number") is not int monster || monster <= 0) continue;
            if (Int(row, "DeathSpell") is not int death || death <= 0) continue;
            if (!spellRows.TryGetValue(death, out GameRow deathRow)) continue;
            // A death spell that targets no one (0) or a user (8) never fires on death.
            if (Int(deathRow, "Targets") is null or 0 or 8) continue;

            HashSet<int> seen = new();
            Queue<int> pending = new();
            pending.Enqueue(death);
            while (pending.Count > 0 && seen.Count < MaxChain)
            {
                int spell = pending.Dequeue();
                if (!seen.Add(spell) || !spellRows.TryGetValue(spell, out GameRow spellRow)) continue;
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

    private static int? Int(GameRow row, string property)
        => row.TryGetProperty(property, out GameValue el)
           && el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out int value)
            ? value : null;
}
