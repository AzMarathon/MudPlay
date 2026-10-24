using MudPlay.Game.GameData;
using MudPlay.Game.Spells;
using MudPlay.Models.GameData;

namespace MudPlay.Game.Conditions;

// Compiles the cure confirmations PartyAilmentTracker and SelfAilmentProof read, from
// the game data (CureSpellIndex) and each cure's own message record, NOT the local
// character's configured cure slots: a party-mate cures with their own class's spells,
// which the local character may never learn, so keying recognition on local config
// missed every cross-caster cure (a Priest's cure poison never cleared the member's
// chip until the next `par`).
public static class CureCastMatcherBuilder
{
    // findMessage: a spell's message record by its number, else its name.
    public static List<CureCastMatcher> Build(
        IEnumerable<CureSpellIndex.CureSpell> cures, Func<int, string, MessageRecord?> findMessage)
    {
        ArgumentNullException.ThrowIfNull(cures);
        ArgumentNullException.ThrowIfNull(findMessage);
        List<CureCastMatcher> list = new();
        foreach (CureSpellIndex.CureSpell cure in cures)
        {
            if (findMessage(cure.Number, cure.Name) is not { } rec) continue;
            // Needs a caster matcher that pins {spellname}+{target}; a record whose
            // cast line has no placeholder (a bare NPC-healer variant) is skipped.
            if (CasterMessageMatcher.TryCreate(rec.CasterMessage) is not { } caster) continue;
            CasterMessageMatcher? witness = CasterMessageMatcher.TryCreate(rec.WitnessMessage);
            // The line the one cured is shown names no target ("{source} casts
            // {spellname} upon you!"). A template with a {target} slot is a line about
            // someone, and can't be read as one at us.
            CasterMessageMatcher? atUs = CasterMessageMatcher.TryCreate(rec.TargetMessage) is { PinsTarget: false } t
                ? t : null;
            foreach (MessageFlags ailment in CuredAilmentBits(cure.Cures))
                list.Add(new CureCastMatcher(ailment, cure.Name, caster, witness, atUs));
        }
        return list;
    }

    // Split a spell's combined cured-flags value into the individual ailment bits the
    // trackers clear — a heal+cure removes several at once, each its own matcher.
    private static IEnumerable<MessageFlags> CuredAilmentBits(MessageFlags cured)
    {
        if ((cured & MessageFlags.Poisoned) != 0) yield return MessageFlags.Poisoned;
        if ((cured & MessageFlags.Diseased) != 0) yield return MessageFlags.Diseased;
        if ((cured & MessageFlags.Blinded) != 0) yield return MessageFlags.Blinded;
        if ((cured & MessageFlags.MovementPrevented) != 0) yield return MessageFlags.MovementPrevented;
    }
}
