using Avalonia.Media;

namespace MudPlay.ViewModels;

// Distinct accent colours cycled per combat profile so each profile reads as its
// own at a glance — the chip fill, the Settings-pane "Combat profile" borders, and
// the Workshop weapon-row marker all pull their colour from here by the profile's
// 1-based number. Amber is first so a single-profile setup keeps the original look.
// Each entry is a (solid edge / text, soft fill) pair; the fill carries the same
// ~18% alpha the amber soft brush uses.
public static class CombatProfilePalette
{
    private static readonly (Color Solid, Color Soft)[] _colors =
    {
        (Color.Parse("#FFD4A24C"), Color.Parse("#2ED4A24C")),   // amber (matches AccentAmber)
        (Color.Parse("#FF5FA8E0"), Color.Parse("#2E5FA8E0")),   // sky blue
        (Color.Parse("#FF6FBF73"), Color.Parse("#2E6FBF73")),   // green
        (Color.Parse("#FFB98BD4"), Color.Parse("#2EB98BD4")),   // violet
        (Color.Parse("#FFE07A9A"), Color.Parse("#2EE07A9A")),   // rose
        (Color.Parse("#FF56C0B0"), Color.Parse("#2E56C0B0")),   // teal
        (Color.Parse("#FFE0954C"), Color.Parse("#2EE0954C")),   // orange
        (Color.Parse("#FF8FB0D8"), Color.Parse("#2E8FB0D8")),   // slate
    };

    public static int Count => _colors.Length;

    // 1-based profile number → its colour pair (cycles past the palette length so
    // any number of profiles keeps getting a colour).
    private static (Color Solid, Color Soft) At(int number)
    {
        int i = ((number - 1) % _colors.Length + _colors.Length) % _colors.Length;
        return _colors[i];
    }

    public static IBrush SolidBrush(int number) => new SolidColorBrush(At(number).Solid);
    public static IBrush SoftBrush(int number)  => new SolidColorBrush(At(number).Soft);
}
