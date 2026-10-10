using System.Collections.Generic;

namespace MudPlay.Game.Map;

// What RoomSpellDamageClassifier read of one room spell: how its damage comes, how
// much, and what it hangs on. Settings → Periodic Damage Room Spells lists these.
//
// MinDamage / MaxDamage span the damage spells behind Kind, as their records give
// them (a room's spell is rolled between the record's two base values, GAME_MECHANICS
// "Resting in a room whose spell does damage"); both 0 when the damage is not in
// the data. GrowsWithLevel says one of those records also has a per-level step,
// which a spell cast from a textblock may add.
//
// ChancePercent is the share of the room's casts that damage, when every roll on
// the way is one the data gives a number for (a table band, an EndCast%); 0 when
// there is no roll or a skill test is among them (SkillTest). Conditions are the
// textblock steps the damage is behind, as the data writes them (`maxlevel 19`).
// Gap names the first thing the read couldn't follow, null when it read everything.
public sealed record RoomSpellDamageReading(
    RoomSpellDamage Kind,
    int MinDamage,
    int MaxDamage,
    bool GrowsWithLevel,
    int ChancePercent,
    bool SkillTest,
    IReadOnlyList<string> Conditions,
    string? Gap)
{
    public static RoomSpellDamageReading NoDamage(string? gap = null) =>
        new(RoomSpellDamage.None, 0, 0, false, 0, false, [], gap);
}
