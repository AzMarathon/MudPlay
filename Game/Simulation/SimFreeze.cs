using MudPlay.Game.Combat;
using MudPlay.Models.GameData;

namespace MudPlay.Game.Simulation;

// Resolves everything a simulation of this lap will look up — each monster's
// record and overlay, and the cast-codes its overrides name — into plain
// dictionaries, and makes the game-data indexes build their tables, all on the
// caller's (UI) thread. The run then happens on a worker thread without touching
// the live settings resolver or a lazily-building index.
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
        foreach (int id in lap.SelectMany(r => r.LairMonsters.Append(r.NpcMonster)).Where(id => id > 0).Distinct())
        {
            monsters[id] = world.Monster(id);
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
        return (frozen, world with { Monster = id => monsters.GetValueOrDefault(id) });
    }
}
