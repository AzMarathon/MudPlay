namespace MudPlay.Game;

// What one `disarm trap <dir>` is likely to do, from the disarm skill. The engine
// rolls 0–100 against the skill: under it disarms, the next 10 points are a safe
// miss, and anything higher springs the trap (GAME_MECHANICS "Exit traps — search
// and disarm"; read from the Stock engine and assumed for Paradigm until confirmed).
// Percentages are whole numbers that add up to 100.
public readonly record struct TrapDisarmOdds(int Skill, int Disarm, int SafeMiss, int Springs)
{
    // The miss window above the skill that fails without setting the trap off.
    private const int SafeMissBand = 10;

    public static TrapDisarmOdds For(int skill)
    {
        int disarm = Math.Clamp(skill, 0, 100);
        int safeMiss = Math.Min(SafeMissBand, 100 - disarm);
        return new TrapDisarmOdds(skill, disarm, safeMiss, 100 - disarm - safeMiss);
    }

    // "disarm ~71%, failure (no dmg) 10%, failure (dmg) 19%" — the route-details and
    // map-tooltip suffix: the safe miss and the miss that sets the trap off.
    public string Summary =>
        $"disarm ~{Disarm}%, failure (no dmg) {SafeMiss}%, failure (dmg) {Springs}%";
}
