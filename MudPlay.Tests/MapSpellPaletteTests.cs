using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Media;
using MudPlay.Controls;
using MudPlay.Game.Map;
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

    // The room fills a by-teleport spell room can stand beside, read off the map
    // itself: the plain room, the current room, lair, shop, the flat spell purple
    // (an unknown spell keeps it), up / down / up+down exit rooms, and the lair-heat
    // stops.
    private static IEnumerable<(string Name, int R, int G, int B)> NeighbouringFills()
    {
        (string, IBrush)[] brushes =
        {
            ("room", MapControl.RoomFill), ("current room", MapControl.CurrentFill), ("lair", MapControl.LairFill),
            ("shop", MapControl.ShopFill), ("spell", MapControl.SpellFill), ("up exit", MapControl.UpFill),
            ("down exit", MapControl.DownFill), ("up+down exit", MapControl.UpDownFill),
        };
        foreach ((string name, IBrush brush) in brushes)
        {
            Color c = Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color;
            yield return (name, c.R, c.G, c.B);
        }
        foreach (string hex in MapControl.HeatFixedHex)
        {
            (int r, int g, int b) = Rgb(hex);
            yield return ("lair heat " + hex, r, g, b);
        }
    }

    [Fact]
    public void ByTeleportSwatches_AreOneEachPerPaintedClass_AndStandOffEveryNeighbouringFill()
    {
        // Unknown is the one class with no swatch: it keeps the flat spell purple.
        Assert.Equal(Enum.GetValues<RoomSpellTeleport>().Length - 1, MapControl.SpellTeleportHex.Length);
        Assert.Equal((int)RoomSpellTeleport.Unknown, MapControl.SpellTeleportHex.Length);

        foreach (string hex in MapControl.SpellTeleportHex)
        {
            (int r, int g, int b) = Rgb(hex);
            foreach ((string name, int otherR, int otherG, int otherB) in NeighbouringFills())
            {
                int dist = Math.Abs(r - otherR) + Math.Abs(g - otherG) + Math.Abs(b - otherB);
                Assert.True(dist >= 80,
                    $"{hex} is within {dist} (Manhattan) of the {name} fill — the two would be mistaken for each other");
            }
        }

        // Green none, yellow conditional, red sudden: each swatch's dominant
        // channels are the ones its colour names.
        (int nr, int ng, int nb) = Rgb(MapControl.SpellTeleportHex[(int)RoomSpellTeleport.None]);
        (int cr, int cg, int cb) = Rgb(MapControl.SpellTeleportHex[(int)RoomSpellTeleport.Conditional]);
        (int sr, int sg, int sb) = Rgb(MapControl.SpellTeleportHex[(int)RoomSpellTeleport.Sudden]);
        Assert.True(ng > nr && ng > nb, "the no-teleport swatch isn't green");
        Assert.True(cr > cb + 100 && cg > cb + 100, "the conditional swatch isn't yellow");
        Assert.True(sr > sg + 100 && sr > sb + 100, "the sudden swatch isn't red");
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
