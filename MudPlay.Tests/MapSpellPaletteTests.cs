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

    // The room fills a by-teleport spell room can stand beside, from
    // Controls/MapControl.cs: the plain room, up / down / up+down exit rooms, the
    // current room, lair, shop, and the ten lair-heat stops.
    private static readonly string[] NeighbouringFills =
    {
        "#9B9B9B", "#00C800", "#DCDC00", "#FFB432", "#E0A000", "#8E4F7B", "#4A7791",
        "#E64A4A", "#F07818", "#C8A000", "#A6C82A", "#43B84E", "#22B58E", "#34B9DE", "#3B7FE6", "#6B54DC", "#A24BD6",
    };

    [Fact]
    public void ByTeleportSwatches_AreOneEachPerClass_AndStandOffEveryNeighbouringFill()
    {
        Assert.Equal(Enum.GetValues<MudPlay.Game.Map.RoomSpellTeleport>().Length, MapControl.SpellTeleportHex.Length);

        foreach (string hex in MapControl.SpellTeleportHex)
        {
            (int r, int g, int b) = Rgb(hex);
            foreach (string other in NeighbouringFills)
            {
                (int otherR, int otherG, int otherB) = Rgb(other);
                int dist = Math.Abs(r - otherR) + Math.Abs(g - otherG) + Math.Abs(b - otherB);
                Assert.True(dist >= 80,
                    $"{hex} is within {dist} (Manhattan) of the room fill {other} — the two would be mistaken for each other");
            }
        }

        // Green none, yellow chance, red teleports: each swatch's dominant channels
        // are the ones its meaning names.
        (int nr, int ng, int nb) = Rgb(MapControl.SpellTeleportHex[(int)MudPlay.Game.Map.RoomSpellTeleport.None]);
        (int cr, int cg, int cb) = Rgb(MapControl.SpellTeleportHex[(int)MudPlay.Game.Map.RoomSpellTeleport.Chance]);
        (int ar, int ag, int ab) = Rgb(MapControl.SpellTeleportHex[(int)MudPlay.Game.Map.RoomSpellTeleport.Always]);
        Assert.True(ng > nr && ng > nb, "the no-teleport swatch isn't green");
        Assert.True(cr > cb + 100 && cg > cb + 100, "the may-teleport swatch isn't yellow");
        Assert.True(ar > ag + 100 && ar > ab + 100, "the teleports swatch isn't red");
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
