using MudPlay.Game;
using MudPlay.Game.Inventory;
using Xunit;

namespace MudPlay.Tests;

// Pins the shared encumbrance-band thresholds (None ≤16 / Light ≤33 / Medium ≤66 /
// Heavy) used by both the live parse and the Item Finder trial overlay.
public sealed class EncumbranceCategoryTests
{
    [Theory]
    [InlineData(0, EncumbranceLevel.None)]
    [InlineData(16, EncumbranceLevel.None)]
    [InlineData(17, EncumbranceLevel.Light)]
    [InlineData(33, EncumbranceLevel.Light)]
    [InlineData(34, EncumbranceLevel.Medium)]
    [InlineData(66, EncumbranceLevel.Medium)]
    [InlineData(67, EncumbranceLevel.Heavy)]
    [InlineData(150, EncumbranceLevel.Heavy)]
    public void ForPercent_MapsBands(int pct, EncumbranceLevel expected)
        => Assert.Equal(expected, EncumbranceCategory.ForPercent(pct));

    [Theory]
    [InlineData(EncumbranceLevel.None, 16)]
    [InlineData(EncumbranceLevel.Light, 33)]
    [InlineData(EncumbranceLevel.Medium, 66)]
    [InlineData(EncumbranceLevel.Heavy, 100)]
    [InlineData(EncumbranceLevel.Unknown, 100)]
    [InlineData(EncumbranceLevel.Encumbered, 100)]
    public void CeilingPercent_IsInverseOfForPercent(EncumbranceLevel level, int expected)
        => Assert.Equal(expected, EncumbranceCategory.CeilingPercent(level));
}
