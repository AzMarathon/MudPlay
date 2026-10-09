using System.Globalization;

namespace MudPlay.Terminal;

// The colour schemes offered for the 16 base ANSI colours (Settings → General), and
// the reading and writing of the user's own.
//
// The three schemes for colour blindness were worked out, not picked by eye. Each
// colour was held near its usual hue (within 22 degrees, so red still reads as a
// red to anyone else looking) while its hue, saturation and brightness were moved
// to push the closest two of the fifteen visible colours as far apart as they
// would go as that kind of colour blindness sees them (Machado, Oliveira and
// Fernandes' 2009 model at full severity, distance measured in CIE L*a*b*), with
// every colour kept readable on black (L* 32 or more, both as that kind of colour
// blindness sees it and to anyone else) and each bright colour kept brighter than
// its normal one. Under the standard colours the closest pair sits
// about 4 apart for protanopia (green and yellow), 12 for deuteranopia and 8 for
// tritanopia (green and cyan); under these it is about 27.
public static class AnsiColorSchemes
{
    private static readonly uint[] s_deuteranopia =
    {
        // Normal intensity:
        0xFF000000, 0xFFBB8363, 0xFF0A6B00, 0xFF8F9416,
        0xFF3530D1, 0xFF7A13A2, 0xFF005B76, 0xFFB4B4B4,
        // Bright:
        0xFF6C6C6C, 0xFFFFCDAF, 0xFF79F66C, 0xFFF6FF25,
        0xFFCECCFF, 0xFFC229FE, 0xFF0EA1CD, 0xFFFFFFFF,
    };

    private static readonly uint[] s_protanopia =
    {
        // Normal intensity:
        0xFF000000, 0xFF884F2E, 0xFF36B828, 0xFF557514,
        0xFF483FA2, 0xFF8400D1, 0xFF0E5178, 0xFFB2B2B2,
        // Bright:
        0xFF6A6A6A, 0xFFE79A6D, 0xFFA4FF9B, 0xFFB9FF2C,
        0xFF8B85CC, 0xFFC767FF, 0xFF87D3FF, 0xFFFFFFFF,
    };

    private static readonly uint[] s_tritanopia =
    {
        // Normal intensity:
        0xFF000000, 0xFF9E2E12, 0xFF42BB49, 0xFF9D7A19,
        0xFF2734CE, 0xFF84227A, 0xFF007149, 0xFFA6A6A6,
        // Bright:
        0xFF555555, 0xFFFF4B1E, 0xFFB1FAB6, 0xFFF7D474,
        0xFF838DFF, 0xFFF047DF, 0xFF0BFFA7, 0xFFFFFFFF,
    };

    private static readonly string[] s_names =
    {
        "Black", "Red", "Green", "Yellow", "Blue", "Magenta", "Cyan", "White",
        "Bright black", "Bright red", "Bright green", "Bright yellow",
        "Bright blue", "Bright magenta", "Bright cyan", "Bright white",
    };

    // The 16 colours of a scheme. Custom has none of its own here: the user's
    // colours live in their settings (CustomColors).
    public static IReadOnlyList<uint> For(AnsiColorScheme scheme) => scheme switch
    {
        AnsiColorScheme.Deuteranopia => s_deuteranopia,
        AnsiColorScheme.Protanopia   => s_protanopia,
        AnsiColorScheme.Tritanopia   => s_tritanopia,
        _                            => AnsiPalette.Default16,
    };

    // The scheme a saved name stands for. Null, empty or a name this build doesn't
    // know is the standard scheme.
    public static AnsiColorScheme Parse(string? name) =>
        Enum.TryParse(name, ignoreCase: true, out AnsiColorScheme scheme) && Enum.IsDefined(scheme)
            ? scheme
            : AnsiColorScheme.Standard;

    public static string NameOf(int index) =>
        (uint)index < (uint)s_names.Length ? s_names[index] : $"Colour {index}";

    // The colours to draw for a saved choice: the named scheme's, or for Custom the
    // user's own.
    public static IReadOnlyList<uint> Resolve(string? schemeName, IReadOnlyDictionary<int, string>? custom)
    {
        AnsiColorScheme scheme = Parse(schemeName);
        return scheme == AnsiColorScheme.Custom ? CustomColors(custom) : For(scheme);
    }

    // The user's own 16: the standard colours with each saved "#RRGGBB" laid over
    // its index. A value that isn't a colour, or an index outside the 16, is left
    // out rather than guessed at.
    public static uint[] CustomColors(IReadOnlyDictionary<int, string>? saved)
    {
        uint[] colors = (uint[])AnsiPalette.Default16.Clone();
        if (saved is null) return colors;
        foreach ((int index, string hex) in saved)
            if ((uint)index < AnsiPalette.BaseColorCount && TryParseHex(hex, out uint rgb))
                colors[index] = 0xFF000000u | rgb;
        return colors;
    }

    // What to save of the user's own 16: only the ones that differ from the
    // standard colour, or null when none does.
    public static Dictionary<int, string>? CustomDelta(IReadOnlyList<uint> colors)
    {
        Dictionary<int, string>? delta = null;
        for (int i = 0; i < AnsiPalette.BaseColorCount && i < colors.Count; i++)
            if ((colors[i] & 0xFFFFFF) != (AnsiPalette.Default16[i] & 0xFFFFFF))
                (delta ??= new Dictionary<int, string>())[i] = ToHex(colors[i]);
        return delta;
    }

    public static bool TryParseHex(string? hex, out uint rgb)
    {
        rgb = 0;
        return hex is { Length: 7 } && hex[0] == '#'
            && uint.TryParse(hex.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out rgb);
    }

    public static string ToHex(uint argb) => $"#{argb & 0xFFFFFF:X6}";
}
