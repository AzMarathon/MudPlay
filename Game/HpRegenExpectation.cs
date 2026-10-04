using MudPlay.Game.Calculators;

namespace MudPlay.Game;

// The HP amounts passive regen can pay a character, so RegenTracker can tell a
// regen gain from a heal, a heal over time or a drain by its size: timing alone
// can't, since a heal cast between rounds lands next to the round and some sources
// pay on the round itself.
//
// Each amount is a range. Its low end is the game's formula at the regen percent
// the gear, race, class and quests give; the high end adds PercentHeadroom for a
// buff's percent, which the client doesn't track. Largest is the most one gain can
// be: anything above it is a heal.
//
// Stock pays the idle amount on the 30 s pass, three times it on a rest tick, and
// both in one gain when they coincide. Paradigm pays thirds of the amount before
// the percent, the last of each three with the percent's extra, and the whole
// amount with its extra on a full rest gain (GAME_MECHANICS "Rest and meditate
// tick timing").
public sealed record HpRegenExpectation(IReadOnlyList<(int Min, int Max)> Amounts, int Largest)
{
    public const int PercentHeadroom = 200;

    public bool Matches(int gain)
    {
        foreach ((int min, int max) in Amounts)
            if (gain >= min && gain <= max) return true;
        return false;
    }

    // Null before `stat` has given a level and a Health to work from.
    public static HpRegenExpectation? For(RealmType realm, int level, int health, int regenPercent)
    {
        if (level <= 0 || health <= 0) return null;
        int withBuffs = regenPercent + PercentHeadroom;
        int Idle(int percent) => CharacterCalculator.CalcHpRegen(level, health, percent, isResting: false, realm);

        if (realm == RealmType.ParaMud)
        {
            int amount = Idle(0);
            int third = CharacterCalculator.ParadigmHpRegenThird(amount);
            int extra = Idle(regenPercent) - amount, extraWithBuffs = Idle(withBuffs) - amount;
            return new HpRegenExpectation(new[]
            {
                (third, third),
                Range(third + extra, third + extraWithBuffs),
                Range(amount + extra, amount + extraWithBuffs),
            }, Math.Max(amount + extra, amount + extraWithBuffs));
        }

        int Rest(int percent) => CharacterCalculator.CalcHpRegen(level, health, percent, isResting: true, realm);
        return new HpRegenExpectation(new[]
        {
            Range(Idle(regenPercent), Idle(withBuffs)),
            Range(Rest(regenPercent), Rest(withBuffs)),
            Range(Idle(regenPercent) + Rest(regenPercent), Idle(withBuffs) + Rest(withBuffs)),
        }, Idle(withBuffs) + Rest(withBuffs));
    }

    private static (int Min, int Max) Range(int a, int b) => (Math.Min(a, b), Math.Max(a, b));

    public override string ToString() =>
        string.Join(" / ", Amounts.Select(a => a.Min == a.Max ? $"+{a.Min}" : $"+{a.Min}..+{a.Max}"));
}
