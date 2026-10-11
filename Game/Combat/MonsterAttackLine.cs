using MudPlay.Terminal;

namespace MudPlay.Game.Combat;

// Whether a line is an attack by one named monster: a hit or a miss, on us or on
// anyone else in the room. Asked while the user's break hold stands, where a
// monster that goes a round without attacking is taken for dead (CombatManager;
// user, 2026-10-10).
//
// It has no wording of its own. A hit is a damage line the round ledger's reader
// (DamageLineAttributor) credits to that monster. A miss, a dodge or a blow that
// armour turned is what CombatLineClassifier reads off the line's colour, on a
// line that leads with the monster's name; the engine's "<victim> just dodged an
// attack from <attacker>." names it at the other end.
public static class MonsterAttackLine
{
    private const string DodgedAttackFrom = " just dodged an attack from ";

    // names maps every name the room's occupants go by to the name the roster lists
    // them under, so "orc captain" is never read as "orc".
    public static bool IsBy(LineExtractor.EmittedLine line, string monster, Dictionary<string, string> names)
    {
        if (line.IsPromptLine || line.IsChat) return false;
        string text = line.Text.Trim();
        if (text.Length == 0) return false;

        if (DamageLineAttributor.TryAttribute(text, names.Keys, out DamageAttribution sides))
            return Is(sides.Source);

        if (CombatLineClassifier.ClassifyAsCombat(line) is not (
                CombatLineKind.MonsterMissYou or CombatLineKind.MonsterMissOther
                or CombatLineKind.DodgeYou or CombatLineKind.DodgeOther
                or CombatLineKind.ArmorBlockYou or CombatLineKind.ArmorBlockOther))
            return false;
        if (Is(DamageLineAttributor.LeadingName(text, names.Keys))) return true;
        int from = text.IndexOf(DodgedAttackFrom, StringComparison.Ordinal);
        return from >= 0
            && Is(DamageLineAttributor.LeadingName(text[(from + DodgedAttackFrom.Length)..], names.Keys));

        bool Is(string? name)
            => name is not null
               && names.TryGetValue(name, out string? listedAs)
               && listedAs.Equals(monster, StringComparison.OrdinalIgnoreCase);
    }
}
