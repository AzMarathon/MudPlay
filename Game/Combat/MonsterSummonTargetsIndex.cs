using System.Collections.Generic;
using System.Text.Json;
using MudPlay.Services;

namespace MudPlay.Game.Combat;

// Maps a monster Number to the monster Numbers it can summon into its room — the
// "summoned from a monster there" side of a room's monster set. A monster's
// CreateSpell (on spawn) and DeathSpell (on death) name a Spells row; a Summon
// ability (MajorMUD code 12) on that spell carries the summoned monster's Number
// in its paired AbilVal (falling back to the spell's MinBase when no ability names
// a target). Widens the room-aware monster-name resolver's candidate set so a
// summoned minion resolves to the record its summoner actually spawns rather than
// a same-named record in another zone.
//
// The AbilVal/MinBase extraction mirrors the Game-Data browser's outgoing-summon
// reader (MonstersSectionViewModel.SummonTargets); it's duplicated here rather
// than shared so the Game layer stays independent of the ViewModels layer. Built
// lazily by joining the raw Monsters table to the raw Spells table, cached, and
// dropped on game-data set switch — mirrors MonsterDeathSummonIndex.
public sealed class MonsterSummonTargetsIndex
{
    // MajorMUD ability code for Summon.
    private const int SummonAbilityCode = 12;
    // Number of Abil-N slots on a Spells row.
    private const int AbilitySlots = 10;

    private readonly GameDataCache _cache;
    private Dictionary<int, IReadOnlyList<int>>? _byMonster;

    public MonsterSummonTargetsIndex(GameDataCache cache)
    {
        ArgumentNullException.ThrowIfNull(cache);
        _cache = cache;
        _cache.ActiveSetChanged += _ => _byMonster = null;
    }

    // Monster Numbers this monster's spawn/death spells summon; empty when it
    // summons nothing.
    public IReadOnlyList<int> SummonedBy(int monsterNumber)
        => Build().TryGetValue(monsterNumber, out IReadOnlyList<int>? ids)
            ? ids
            : Array.Empty<int>();

    private Dictionary<int, IReadOnlyList<int>> Build()
    {
        if (_byMonster is { } cached) return cached;
        var map = new Dictionary<int, IReadOnlyList<int>>();

        // Spell Number → summoned monster Numbers.
        var spellTargets = new Dictionary<int, IReadOnlyList<int>>();
        if (_cache.GetRawTable("Spells") is { } spells)
        {
            foreach (JsonElement row in spells.RootElement.EnumerateArray())
            {
                if (!TryInt(row, "Number", out int num)) continue;
                IReadOnlyList<int> targets = SummonTargets(row);
                if (targets.Count > 0) spellTargets[num] = targets;
            }
        }

        if (spellTargets.Count > 0 && _cache.GetRawTable("Monsters") is { } monsters)
        {
            foreach (JsonElement row in monsters.RootElement.EnumerateArray())
            {
                if (!TryInt(row, "Number", out int number)) continue;
                List<int>? children = null;
                // On-spawn and on-death are the summons that seed a room with the
                // summoner's minions; combat / between-round summons aren't expanded.
                if (TryInt(row, "CreateSpell", out int create) && create != 0
                    && spellTargets.TryGetValue(create, out IReadOnlyList<int>? cs))
                    (children ??= new List<int>()).AddRange(cs);
                if (TryInt(row, "DeathSpell", out int death) && death != 0
                    && spellTargets.TryGetValue(death, out IReadOnlyList<int>? ds))
                    foreach (int id in ds)
                        if (!(children ??= new List<int>()).Contains(id)) children.Add(id);
                if (children is not null) map[number] = children;
            }
            _cache.EvictTable("Monsters");
        }

        _cache.EvictTable("Spells");
        return _byMonster = map;
    }

    // Monster Number(s) a spell summons, or empty when it isn't a summon spell.
    // The summon abilities' own positive values (AbilVal) win; only when none name
    // a target does the spell's MinBase stand in.
    private static IReadOnlyList<int> SummonTargets(JsonElement spell)
    {
        List<int>? targets = null;
        bool isSummon = false;
        for (int i = 0; i < AbilitySlots; i++)
        {
            if (!TryInt(spell, $"Abil-{i}", out int code) || code != SummonAbilityCode) continue;
            isSummon = true;
            if (TryInt(spell, $"AbilVal-{i}", out int t) && t > 0
                && !(targets ??= new List<int>()).Contains(t))
                targets.Add(t);
        }
        if (!isSummon) return Array.Empty<int>();
        if (targets is null && TryInt(spell, "MinBase", out int minBase) && minBase > 0)
            targets = new List<int> { minBase };
        return targets ?? (IReadOnlyList<int>)Array.Empty<int>();
    }

    private static bool TryInt(JsonElement row, string prop, out int value)
    {
        value = 0;
        return row.TryGetProperty(prop, out JsonElement el)
            && el.ValueKind == JsonValueKind.Number
            && el.TryGetInt32(out value);
    }
}
