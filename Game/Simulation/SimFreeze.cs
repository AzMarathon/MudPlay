using MudPlay.Game.Combat;
using MudPlay.Models.GameData;

namespace MudPlay.Game.Simulation;

// Resolves everything a simulation of this lap will look up — each monster's
// record, overlay, death summons and attack hit spells, and the cast-codes its
// overrides name — into plain dictionaries, and makes the game-data indexes build
// their tables, all on the caller's (UI) thread. The run then happens on a worker
// thread without touching the live settings resolver or a lazily-building index.
public static class SimFreeze
{
    public static (SimCharacter Character, SimWorld World) For(
        SimCharacter character, SimWorld world, IReadOnlyList<SimRoom> lap)
    {
        ArgumentNullException.ThrowIfNull(character);
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(lap);

        var monsters = new Dictionary<int, MonsterCatalogEntry?>();
        var overlays = new Dictionary<int, MonsterOverlay>();
        var shorts = new Dictionary<int, string?>();
        var summons = new Dictionary<int, IReadOnlyList<int>?>();
        var procs = new Dictionary<int, SimProc?>();
        foreach (int id in MonstersOn(lap, world, summons))
        {
            monsters[id] = world.Monster(id);
            foreach (MonsterAttackSlot a in monsters[id]?.Attacks ?? Array.Empty<MonsterAttackSlot>())
                if (a.HitSpell > 0 && !procs.ContainsKey(a.HitSpell)) procs[a.HitSpell] = world.HitSpell?.Invoke(a.HitSpell);
            MonsterOverlay o = character.Overlay(id);
            overlays[id] = o;
            foreach (int? spell in new[] { o.OverrideAttackSpellId, o.OverrideAltAttackSpellId, o.OverridePreAttackSpellId })
                if (spell is > 0 && !shorts.ContainsKey(spell.Value)) shorts[spell.Value] = character.SpellShortByNumber(spell.Value);
            world.MonsterMagic?.SpellImmunity(id);
            world.MonsterResist?.ResistPercent(id, 0);
            world.MonsterLife?.CanDrain(id);
        }
        world.SpellReqLevel?.ReqLevel(string.Empty);
        world.SpellAttackType?.AttackType(string.Empty);
        world.SpellTargetType?.TargetType(string.Empty);

        SimCharacter frozen = character with
        {
            Overlay = id => overlays.TryGetValue(id, out MonsterOverlay? o) ? o : new MonsterOverlay(),
            SpellShortByNumber = n => shorts.GetValueOrDefault(n),
        };
        return (frozen, world with
        {
            Monster = id => monsters.GetValueOrDefault(id),
            DeathSummons = id => summons.GetValueOrDefault(id),
            HitSpell = n => procs.GetValueOrDefault(n),
        });
    }

    // Every monster that can appear on the lap: placed, lair, boss, room-summoned, and each
    // tier of a death-summon chain below them (walked to the end). Records each
    // monster's death-summon list as it goes.
    private static HashSet<int> MonstersOn(
        IReadOnlyList<SimRoom> lap, SimWorld world, Dictionary<int, IReadOnlyList<int>?> summons)
    {
        var seen = new HashSet<int>();
        var queue = new Queue<int>(lap.SelectMany(r => r.LairMonsters
            .Append(r.NpcMonster)
            .Concat(r.Bosses ?? Array.Empty<int>())
            .Concat(r.Summon?.Entries.Select(e => e.Monster) ?? Enumerable.Empty<int>())));
        while (queue.Count > 0)
        {
            int id = queue.Dequeue();
            if (id <= 0 || !seen.Add(id)) continue;
            IReadOnlyList<int>? tier = world.DeathSummons?.Invoke(id);
            summons[id] = tier;
            if (tier is not null) foreach (int next in tier) queue.Enqueue(next);
        }
        return seen;
    }
}
