using System.Collections.Generic;
using System.Linq;
using MudPlay.Game.Map;
using MudPlay.Game.Spells;
using MudPlay.Services;

namespace MudPlay.Game.Combat;

// Narrows an unrecognized wire line captured in a room to the spell that probably
// produced it — "this line is likely this spell's message, which we're missing."
//
// The attribution is derived from the LINE, not just the room. Listing every spell
// every monster in the room can cast made the hint useless: a Darkwood row answered
// "bites (#80)" (a forest spider's on-hit proc) for a captured line about a twig
// snapping, and answered it identically for all thirty-nine lines in the same export.
// A hint that never varies carries no information.
//
// Two signals actually tie a line to a spell:
//   * the line NAMES a monster the room hosts, which points at that monster's own
//     spells and nothing else; or
//   * the room carries a Rooms.Spell of its own, which fires on entry — the source
//     of the atmosphere lines that read like scenery ("An ominous wind blows through
//     the trees" is the darkwood forest spell, #915).
// A named monster wins outright: the line says who acted, so the room's ambient spell
// is irrelevant to it. With neither signal there is no honest answer, and the hint is
// left empty rather than guessing.
//
// Each spell is tagged with the path it fires through, because that says which
// perspective slot the missing message belongs in — an on-hit proc reads as the
// target's line, an on-death spell can only fire as the monster dies.
public static class RoomSpellAttributor
{
    // A spell that could have produced the line, and why we think so.
    public readonly record struct SpellSource(int Number, string Name, string Origin);

    // Ordered, deduped attributions for lineText seen at key. Empty when nothing ties
    // the line to a spell. Pass a null / blank lineText to get the room's own spell
    // alone (no monster can be named without text to name it in).
    public static IReadOnlyList<SpellSource> Attribute(
        RoomKey key,
        string? lineText,
        RoomGraphManager? rooms,
        GameDataCache? data,
        MonsterSpawnIndex? spawns,
        MonsterCatalog? monsters,
        KnownSpellCatalog? spells)
    {
        if (rooms is null || spells is null) return System.Array.Empty<SpellSource>();
        Room? room = rooms.GetRoom(key);
        if (room is null) return System.Array.Empty<SpellSource>();

        List<SpellSource> result = new();
        HashSet<int> seen = new();

        if (monsters is not null
            && NamedMonsterIn(lineText, room, data, spawns) is { } actor
            && monsters.Get(actor.Id) is { } entry)
        {
            foreach ((int number, string path) in CastSpells(entry))
            {
                if (number <= 0 || !seen.Add(number)) continue;
                result.Add(new SpellSource(
                    number, NameOf(spells, number), $"{actor.Name}, {path}"));
            }
            // The line already told us who acted; the room's ambient spell isn't it.
            if (result.Count > 0) return result;
        }

        if (room.Spell > 0)
            result.Add(new SpellSource(room.Spell, NameOf(spells, room.Spell), "room spell"));

        return result;
    }

    // The room monster whose name appears in the line, longest name first so "dark
    // goblin archer" wins over a bare "goblin" that is also in the room. Null when the
    // line names none of them.
    private static RoomTooltipBuilder.RoomMonsterRef? NamedMonsterIn(
        string? lineText, Room room, GameDataCache? data, MonsterSpawnIndex? spawns)
    {
        if (string.IsNullOrWhiteSpace(lineText)) return null;

        RoomTooltipBuilder.RoomMonsters rm =
            RoomTooltipBuilder.ResolveRoomMonsters(room, data, spawns);

        RoomTooltipBuilder.RoomMonsterRef? best = null;
        foreach (RoomTooltipBuilder.RoomMonsterRef mref in
                 rm.Placed.Concat(rm.Assigned).Concat(rm.Lair))
        {
            if (mref.Name.Length == 0) continue;
            if (!lineText.Contains(mref.Name, System.StringComparison.OrdinalIgnoreCase)) continue;
            if (best is null || mref.Name.Length > best.Value.Name.Length) best = mref;
        }
        return best;
    }

    private static string NameOf(KnownSpellCatalog spells, int number) =>
        spells.GetSpellNameByNumber(number) ?? $"Spell #{number}";

    // Every spell a monster can fire, paired with the path it fires through. A slot
    // with no chance to land can't be the source of anything, so it's skipped — the
    // same Att% gate the monster info panel applies.
    private static IEnumerable<(int Number, string Path)> CastSpells(MonsterCatalogEntry mc)
    {
        foreach (MonsterAttackSlot a in mc.Attacks)
        {
            if (a.Percent <= 0) continue;
            // On a spell slot (AttType 2) the accuracy field carries the spell number.
            if (a.Type == 2 && a.Accuracy > 0) yield return (a.Accuracy, "casts");
            if (a.HitSpell > 0) yield return (a.HitSpell, "on hit");
        }
        foreach (MonsterMidSpellSlot m in mc.MidSpells)
            if (m.SpellId > 0) yield return (m.SpellId, "between rounds");
        if (mc.CreateSpell > 0) yield return (mc.CreateSpell, "on spawn");
        if (mc.DeathSpell > 0) yield return (mc.DeathSpell, "on death");
    }

    // Compact label for the Unrecognized Lines "Likely source" column and its export:
    // "bites (#80) — forest spider, on hit". Empty when nothing ties the line to a
    // spell, which leaves the column blank rather than showing a guess.
    public static string LikelySource(
        RoomKey key,
        string? lineText,
        RoomGraphManager? rooms,
        GameDataCache? data,
        MonsterSpawnIndex? spawns,
        MonsterCatalog? monsters,
        KnownSpellCatalog? spells,
        int maxSpells = 4)
    {
        IReadOnlyList<SpellSource> list =
            Attribute(key, lineText, rooms, data, spawns, monsters, spells);
        if (list.Count == 0) return string.Empty;

        string text = string.Join("; ",
            list.Take(maxSpells).Select(s => $"{s.Name} (#{s.Number}) — {s.Origin}"));
        if (list.Count > maxSpells) text += $"; +{list.Count - maxSpells} more";
        return text;
    }
}
