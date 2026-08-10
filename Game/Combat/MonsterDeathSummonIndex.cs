using System.Collections.Generic;
using System.Text.Json;
using MudPlay.Services;

namespace MudPlay.Game.Combat;

// Fast lookup of whether a monster summons another monster when it dies, by
// monster Number in the active game-data set. A monster's Monsters.DeathSpell is
// cast on its death; if that Spells row carries the Summon ability (MajorMUD code
// 12), the kill spawns a fresh monster in the room. The auto-combat/walker must
// re-scan the room (send a bare CR) before moving on such a kill — otherwise it
// clears the combat gate and steps the instant the summoner dies, dragging the
// newly-summoned monster into the next room and stacking rooms (report
// paradigm-20260729-211336).
//
// Mirrors MonsterMagicIndex: built lazily by joining the raw Monsters table's
// DeathSpell to the raw Spells table's Abil-0..9 == 12 check, cached, and dropped
// on game-data set switch so the next query rebuilds against the new set.
public sealed class MonsterDeathSummonIndex
{
    // MajorMUD ability code for Summon (per GameData.AbilityNames).
    private const int SummonAbilityCode = 12;

    // Number of Abil-N slots on a Spells row.
    private const int AbilitySlots = 10;

    private readonly GameDataCache _cache;
    private HashSet<int>? _summoners;

    public MonsterDeathSummonIndex(GameDataCache cache)
    {
        ArgumentNullException.ThrowIfNull(cache);
        _cache = cache;
        _cache.ActiveSetChanged += _ => _summoners = null;
    }

    // True when killing this monster casts a death spell that summons another
    // monster — i.e. the room must be re-scanned before movement resumes.
    public bool SummonsOnDeath(int monsterNumber) => Build().Contains(monsterNumber);

    private HashSet<int> Build()
    {
        if (_summoners is { } cached) return cached;

        // Spell Numbers that summon (carry any Abil-i == 12).
        HashSet<int> summonSpells = new();
        JsonDocument? spells = _cache.GetRawTable("Spells");
        if (spells is not null)
        {
            foreach (JsonElement row in spells.RootElement.EnumerateArray())
            {
                if (!TryInt(row, "Number", out int spellNum)) continue;
                for (int i = 0; i < AbilitySlots; i++)
                {
                    if (TryInt(row, $"Abil-{i}", out int code) && code == SummonAbilityCode)
                    {
                        summonSpells.Add(spellNum);
                        break;
                    }
                }
            }
        }

        // Monsters whose DeathSpell is one of those summon spells.
        HashSet<int> summoners = new();
        if (summonSpells.Count > 0)
        {
            JsonDocument? monsters = _cache.GetRawTable("Monsters");
            if (monsters is not null)
            {
                foreach (JsonElement row in monsters.RootElement.EnumerateArray())
                {
                    if (!TryInt(row, "Number", out int number)) continue;
                    if (TryInt(row, "DeathSpell", out int deathSpell)
                        && deathSpell != 0
                        && summonSpells.Contains(deathSpell))
                        summoners.Add(number);
                }
            }
            _cache.EvictTable("Monsters");
        }

        // Folded into the set — release the pinned raw JsonDocuments.
        _cache.EvictTable("Spells");
        _summoners = summoners;
        return summoners;
    }

    private static bool TryInt(JsonElement row, string prop, out int value)
    {
        value = 0;
        return row.TryGetProperty(prop, out JsonElement el)
            && el.ValueKind == JsonValueKind.Number
            && el.TryGetInt32(out value);
    }
}
