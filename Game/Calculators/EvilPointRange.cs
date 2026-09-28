using System;

namespace MudPlay.Game.Calculators;

// What the client knows about the character's evil points, as an inclusive range.
// Paradigm's `pro` pins the exact number; otherwise it's the who title's band,
// narrowed by an evil-only item the game refused. Evil-only (ability 98) gear and
// spells gate on it: value 0 needs Outlaw through Fiend, value N needs at least N
// evil points (GAME_MECHANICS "Item wear restrictions (ability-code flags)").
public readonly record struct EvilPointRange(double Lo, double Hi)
{
    // Where Outlaw starts on both realms — the least an evil-only item accepts.
    public const int OutlawFloor = 40;

    public static EvilPointRange Exact(double evilPoints) => new(evilPoints, evilPoints);

    // The band a who title covers (the same thresholds as AlignmentBands.TitleForEvilPoints),
    // or null for a blank / unknown title. Lawful is Good with a self-imposed flag.
    public static EvilPointRange? ForTitle(string? title, RealmType realm) =>
        title?.Trim().ToLowerInvariant() switch
        {
            "saint" => new EvilPointRange(double.NegativeInfinity, -201),
            "good" or "lawful" => new EvilPointRange(-200, -51),
            "neutral" => new EvilPointRange(-50, 29),
            "seedy" => new EvilPointRange(30, 39),
            "outlaw" => new EvilPointRange(40, 79),
            "criminal" => new EvilPointRange(80, 119),
            "villain" => new EvilPointRange(120, realm == RealmType.Stock ? 209 : 299),
            "fiend" => new EvilPointRange(realm == RealmType.Stock ? 210 : 300, double.PositiveInfinity),
            _ => null,
        };

    // Whether an evil-only item or spell with this ability value is usable: true when
    // the whole range meets it, false when none of it does, null when it straddles —
    // the caller then lets the game decide, and a refusal narrows the range.
    public bool? MeetsEvilOnly(int value)
    {
        double need = Math.Max(OutlawFloor, value);
        if (Lo >= need) return true;
        if (Hi < need) return false;
        return null;
    }

    // Narrowed by a refused evil-only `value` item: our evil points are below it.
    // Null when that contradicts the range (the title moved since), so the caller
    // keeps the range as it was.
    public EvilPointRange? Below(int value) =>
        value <= Lo ? null : this with { Hi = Math.Min(Hi, Math.BitDecrement((double)value)) };

    public override string ToString()
    {
        if (Lo == Hi) return Format(Lo);
        string lo = double.IsNegativeInfinity(Lo) ? "…" : Format(Lo);
        string hi = double.IsPositiveInfinity(Hi) ? "…" : Hi == Math.Round(Hi) ? Format(Hi) : $"under {Format(Math.Ceiling(Hi))}";
        return $"{lo} to {hi}";
    }

    private static string Format(double v) => v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
}
