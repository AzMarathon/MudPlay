using MudPlay.Game.Map;
using MudPlay.Game.Spells;
using MudPlay.Models.GameData;
using MudPlay.Services;
using MudPlay.Terminal;

namespace MudPlay.Game.Conditions;

// Outside proof that a latched ailment of our own is over when its wear-off line
// never came, or never comes at all (GAME_MECHANICS "Moving while blind"):
//   - a room display: a blind character's move and look both show only
//     "You are blind.", so any room shown proves sight;
//   - a move that lands: a held or knocked-down character can't walk and isn't
//     dragged by a leader;
//   - a cure blindness landing on us: the cure prints its spell lines and no
//     wear-off, on both realms.
// Left latched, each stood until Reset States: the walker sat "Paused by: Held" and
// the hold cure re-cast every window (report paradigm-20261003-161904); a cleric cast
// cure blindness on itself every 15 s while walking a loop it could plainly see
// (report CureBlindlessBugReport, 2026-10-22). A record that also carries another
// condition is left for blindness (ClearFlag's onlyWholly): sight proves nothing
// about a confusion riding the same record. A party member's chip is
// PartyAilmentTracker's (its WitnessCures); this is our own state only.
public static class SelfAilmentProof
{
    public static void Wire(
        RoomTracker tracker,
        ConditionTracker conditions,
        MessageRouter router,
        Func<IReadOnlyList<CureCastMatcher>> readCureMatchers,
        Func<string?> ownName)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        ArgumentNullException.ThrowIfNull(conditions);
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(readCureMatchers);
        ArgumentNullException.ThrowIfNull(ownName);
        tracker.MoveConfirmed += () =>
            conditions.ClearFlag(MessageFlags.MovementPrevented, "a move went through");
        tracker.RoomDisplayRead += () =>
            conditions.ClearFlag(MessageFlags.Blinded, "a room display was seen", onlyWholly: true);
        router.LineDispatched += line =>
        {
            if (!conditions.IsBlinded) return;
            if (CuresUs(line.Text, readCureMatchers(), ownName()) is { } spell)
                conditions.ClearFlag(MessageFlags.Blinded, $"{spell} landed on us", onlyWholly: true);
        };
    }

    // The blindness cure that the line shows landing on us, or null: our own cast
    // naming us ("You cast cure blindness on Francisco!") or another's cast at us
    // ("Kenwood casts cure blindness upon you!").
    private static string? CuresUs(string text, IReadOnlyList<CureCastMatcher> matchers, string? ownName)
    {
        if (text.Length == 0) return null;
        string given = GivenName(ownName);
        foreach (CureCastMatcher cm in matchers)
        {
            if (cm.Ailment != MessageFlags.Blinded) continue;
            if (given.Length > 0
                && (cm.Caster.ConfirmsSpellTarget(text, cm.SpellName, given)
                    || (ownName is { } full && cm.Caster.ConfirmsSpellTarget(text, cm.SpellName, full))))
                return cm.SpellName;
            if (cm.Target is { } atUs
                && atUs.TryMatchCaptures(text, out MessageCaptures caps)
                && string.Equals(caps.Spell, cm.SpellName, StringComparison.OrdinalIgnoreCase))
                return cm.SpellName;
        }
        return null;
    }

    private static string GivenName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        string trimmed = name.Trim();
        int space = trimmed.IndexOf(' ');
        return space >= 0 ? trimmed[..space] : trimmed;
    }
}
