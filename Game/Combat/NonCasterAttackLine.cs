using System.Text.RegularExpressions;

namespace MudPlay.Game.Combat;

// Recognizes "<Actor> <verb> [an <ammo>] at <target>!" as a PHYSICAL attack, but only
// when the actor is a player whose class can't cast anything.
//
// The shape alone is ambiguous: "Suijin shoots an arrow at bandit!" and "Suijin hurls
// a fireball at bandit!" are textually the same, so the unrecognized-line capture
// can't suppress it outright — a projectile spell we have no message for is exactly
// what that queue is meant to surface. What disambiguates it is the actor: a Warrior,
// Witchunter, Ninja, or Thief has no magery at all, so nothing they do can be a spell.
//
// This deliberately proves only that the actor didn't CAST. It says nothing about a
// spell aimed AT them — a monster's ailment landing on a non-caster names that player
// as the subject of its own line ("Suijin convulses violently!") and is a genuine
// catalogue gap, which is why this is scoped to the actor-attacks-target shape rather
// than used as a blanket filter on any line naming a non-caster.
public static class NonCasterAttackLine
{
    // One capitalised actor token (board names are a single word), a verb, an optional
    // "a/an <ammo>" noun phrase, then "at <target>" and end of line. Anchored at both
    // ends so it can't reach into a longer line whose tail isn't accounted for, and it
    // requires no weapon-naming tail — that form is already a router pattern and needs
    // no actor check to be safely physical.
    private static readonly Regex ActorAttacksTarget = new(
        @"^(?<actor>[A-Z][A-Za-z'\-]+) \w+(?: an? [\w' -]+?)? at [\w' -]+[.!]\s*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // playerCanCast maps a player's given name to whether their class casts: true for a
    // magery class, false for none, and null when the player or their class is unknown.
    // Only an explicit false suppresses — an unknown actor keeps the line in the queue,
    // so a missing roster entry can never hide a real message.
    public static bool Matches(string? text, Func<string, bool?> playerCanCast)
    {
        ArgumentNullException.ThrowIfNull(playerCanCast);
        if (string.IsNullOrWhiteSpace(text)) return false;

        Match m = ActorAttacksTarget.Match(text);
        if (!m.Success) return false;
        return playerCanCast(m.Groups["actor"].Value) == false;
    }
}
