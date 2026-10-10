using System.Collections.Generic;

namespace MudPlay.Game.Map;

// What RoomSpellDamageClassifier read of one room spell: how its damage comes, how
// much, and what it hangs on. Settings → Periodic Damage Room Spells lists these.
//
// MinDamage / MaxDamage span the damage spells behind Kind, as their records give
// them (a room's spell is rolled between the record's two base values, GAME_MECHANICS
// "Resting in a room whose spell does damage"); both 0 when the damage is not in
// the data. For an EveryTick spell that is what each cast does and nothing later:
// what a timer brings afterwards is in Timed, soonest first, so freezing water reads
// 1 to 4 a tick and not "1 to 9999" for the drowning its held breath ends in. For an
// AfterATimer spell the range is the first of those stages. GrowsWithLevel says one
// of the records behind the range also has a per-level step, which a spell cast from
// a textblock may add.
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
    // The damage a timer started by the spell ends in, soonest first; empty for a
    // spell with no such timer.
    public IReadOnlyList<RoomSpellDamageStage> Timed { get; init; } = [];

    public static RoomSpellDamageReading NoDamage(string? gap = null) =>
        new(RoomSpellDamage.None, 0, 0, false, 0, false, [], gap);
}
