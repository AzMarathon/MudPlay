namespace MudPlay.Game.Stealth;

// The chance a `sn` takes. The engine rolls 0–100 under your Stealth, capped at 95,
// less 10 / 5 over 66% / 33% encumbrance and 1 for each other player and monster in
// the room (GAME_MECHANICS "Sneaking — commands, equip order, and the sneak state
// machine"). The Level Projection shows the empty-room, light-load figure.
public static class SneakChance
{
    private const int SnCap = 95;

    // Ability 186, Perfect Stealth: a sneak always takes and always holds.
    public const int PerfectStealthAbility = 186;

    public static int Percent(int stealth, bool perfectStealth = false) =>
        perfectStealth ? 100 : Math.Clamp(stealth, 0, SnCap);

    // What a load takes off the chance: 10 over 66% encumbrance, 5 over 33%.
    public static int EncumbrancePenalty(int encumbrancePercent) =>
        encumbrancePercent > 66 ? 10 : encumbrancePercent > 33 ? 5 : 0;
}
