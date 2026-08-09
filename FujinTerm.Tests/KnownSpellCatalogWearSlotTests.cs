using System.Text.Json;
using FujinTerm.Game.Spells;
using Xunit;

namespace FujinTerm.Tests;

// Pins KnownSpellCatalog.WearSlotFor — the equip slot a cast item resolves to,
// which the item-cast buff swap restores. The regression: Worn is a NUMERIC JSON
// field, so reading it as a string handed back null and the warhorn (ItemType=0
// Armour, Worn=12 Off-Hand) mislabeled itself "Weapon Hand", stranding the swap.
public sealed class KnownSpellCatalogWearSlotTests
{
    private static JsonElement Row(int itemType, int worn)
        => JsonDocument.Parse($"{{\"ItemType\":{itemType},\"Worn\":{worn}}}").RootElement;

    [Theory]
    [InlineData(0, 12, "Off-Hand")]    // engraved warhorn: Armour worn off-hand (the bug)
    [InlineData(0, 8, "Neck")]         // an amulet
    [InlineData(0, 2, "Head")]         // a helm
    [InlineData(1, 0, "Weapon Hand")]  // a real weapon rides the weapon hand (Worn Nowhere)
    [InlineData(1, 12, "Off-Hand")]    // a weapon-typed item worn off-hand → prefer its worn slot
    public void WearSlotFor_ResolvesFromNumericWorn(int itemType, int worn, string expected)
        => Assert.Equal(expected, KnownSpellCatalog.WearSlotFor(Row(itemType, worn)));
}
