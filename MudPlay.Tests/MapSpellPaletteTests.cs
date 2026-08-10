using System;
using System.Globalization;
using MudPlay.Controls;
using Xunit;

namespace MudPlay.Tests;

// The "by name" room-spell overlay hashes each spell into MapControl.SpellCategoryHex.
// Regression guard for the icy-mountain report: a near-neutral swatch (#B0B0B0) sat
// almost on top of the normal room fill (#9B9B9B), so spell rooms disappeared under
// the filter. Every swatch must stay CHROMATIC and clearly off the room-fill grey.
public sealed class MapSpellPaletteTests
{
    // Matches Controls/MapControl.cs RoomFill.
    private const int RoomFillR = 0x9B, RoomFillG = 0x9B, RoomFillB = 0x9B;

    private static (int r, int g, int b) Rgb(string hex)
    {
        string h = hex.TrimStart('#');
        return (
            int.Parse(h.Substring(0, 2), NumberStyles.HexNumber),
            int.Parse(h.Substring(2, 2), NumberStyles.HexNumber),
            int.Parse(h.Substring(4, 2), NumberStyles.HexNumber));
    }

    [Fact]
    public void EverySwatch_IsChromatic_NotNeutralGrey()
    {
        foreach (string hex in MapControl.SpellCategoryHex)
        {
            (int r, int g, int b) = Rgb(hex);
            int chroma = Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b));
            Assert.True(chroma >= 40,
                $"{hex} is too close to neutral grey (chroma {chroma}); it would blend into the room fill under the by-name filter");
        }
    }

    [Fact]
    public void EverySwatch_IsFarFromRoomFillGrey()
    {
        foreach (string hex in MapControl.SpellCategoryHex)
        {
            (int r, int g, int b) = Rgb(hex);
            int dist = Math.Abs(r - RoomFillR) + Math.Abs(g - RoomFillG) + Math.Abs(b - RoomFillB);
            Assert.True(dist >= 60,
                $"{hex} is within {dist} (Manhattan) of the room fill #9B9B9B — spell rooms would be indistinguishable");
        }
    }
}
