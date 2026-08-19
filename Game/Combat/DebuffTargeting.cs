namespace MudPlay.Game.Combat;

// Spells.Targets scope classification for the Settings → Combat debuff slots.
// A debuff slot spell must be a 0-energy (between-round) spell — that's what
// distinguishes a debuff from a combat attack spell (lbol/mmis cost 500-1000
// energy). On top of that, the single-target slot needs a single-enemy scope and
// the area slot an area scope, so a targeted spell isn't slotted as an AoE (or
// vice-versa). Values confirmed against Paradigm game data (see GAME_MECHANICS.md
// "Debuff slot spells"): single-enemy = Monster(4) / Monster-or-User(8); area
// (room / all enemies) = Divided Area not-self(3) / Divided Area incl-self(5) /
// Divided Attack Area(9) / Full Area(11) / Full Attack Area(12). The party-area
// scopes (10 / 13) are buffs/heals, never enemy debuffs.
internal static class DebuffTargeting
{
    // A single-target enemy scope — the single-target debuff slot requires this.
    public static bool IsSingleTargetEnemy(int targets) => targets is 4 or 8;

    // An area / room / all-enemies scope — the AoE debuff slot requires this.
    public static bool IsAreaEnemy(int targets) => targets is 3 or 5 or 9 or 11 or 12;

    // A 0-energy spell is a between-round cast (buff/debuff), not a combat action.
    public static bool IsBetweenRound(int energyCost) => energyCost == 0;
}
