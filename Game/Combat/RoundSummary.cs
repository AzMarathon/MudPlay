namespace MudPlay.Game.Combat;

// One closed combat round emitted by RoundDamageTracker: who dealt and took how
// much damage in it, as far as the lines name them.
//
// RoundNumber counts every round since the last RoundDamageTracker.Reset (new BBS
// connection / character switch); FightRound counts rounds within the current fight,
// from 1. StartedAt is the first damage line of the round, EndedAt when it closed.
// Combatants holds one row per named combatant, the local player as
// DamageLineAttributor.Self. UnknownDealt is damage whose dealer no line named;
// UnknownTaken is damage whose victim none named (an area effect). HpBefore/HpAfter
// and MaBefore/MaAfter snapshot PlayerState at StartedAt and EndedAt.
public readonly record struct RoundSummary(
    int RoundNumber,
    int FightRound,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    IReadOnlyList<CombatantDamage> Combatants,
    int UnknownDealt,
    int UnknownTaken,
    int HpBefore,
    int HpAfter,
    int MaBefore,
    int MaAfter)
{
    // The local player's own row.
    public int DamageDealt => Self.Dealt;
    public int DamageTaken => Self.Taken;

    private CombatantDamage Self
    {
        get
        {
            foreach (CombatantDamage c in Combatants)
                if (c.Name == DamageLineAttributor.Self) return c;
            return default;
        }
    }
}
