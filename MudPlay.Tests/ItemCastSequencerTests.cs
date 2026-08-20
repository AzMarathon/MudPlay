using System.Collections.Generic;
using System.Linq;
using System.Text;
using MudPlay.Game.Inventory;
using MudPlay.Game.Spells;
using Xunit;

namespace MudPlay.Tests;

/// <summary>
/// Pins <see cref="ItemCastSequencer"/> — the equip → use → re-equip wire
/// sequence that casts a spell from an item. Only unlimited-use items are
/// eligible; the displaced weapon is read from the inventory snapshot and
/// re-wielded afterward.
/// </summary>
public sealed class ItemCastSequencerTests
{
    private static readonly IReadOnlyList<ClassCastItem> Items = new[]
    {
        new ClassCastItem(1, "Emerald Tipped Crozier", 50, "bless", 0, 0),  // unlimited, free, 1H weapon
        new ClassCastItem(2, "Scroll of Light", 60, "light", 0, 3),         // limited
        new ClassCastItem(3, "Shimmering Greatsword", 70, "shield", 8, 0,   // unlimited, two-handed
            IsTwoHanded: true),
        new ClassCastItem(4, "Engraved Warhorn", 80, "warhorn blast", 0, 0, // unlimited, off-hand item
            WearSlot: "Off-Hand"),
        new ClassCastItem(5, "Amulet of Vigor", 90, "bless", 0, 0,          // unlimited, worn on the neck
            WearSlot: "Neck"),
    };

    private static InventorySnapshot InvWith(params EquippedItem[] equipped)
        => InventorySnapshot.Empty with { EquippedItems = equipped };

    private static (ItemCastSequencer Seq, List<byte[]> Sent) NewSeq(InventorySnapshot inv)
    {
        ItemCastSequencer seq = new(() => Items, () => inv);
        seq.SetWireSender(_ => { });
        return (seq, seq.LastSentForTests);
    }

    private static (ItemCastSequencer Seq, List<byte[]> Sent) NewSeqWithDesired(
        InventorySnapshot inv, System.Func<string, string?> desiredSlotItem)
    {
        ItemCastSequencer seq = new(() => Items, () => inv, log: null, desiredSlotItem: desiredSlotItem);
        seq.SetWireSender(_ => { });
        return (seq, seq.LastSentForTests);
    }

    private static (ItemCastSequencer Seq, List<byte[]> Sent) NewSeqWithOffHandNames(
        InventorySnapshot inv, params string[] offHandNames)
    {
        ItemCastSequencer seq = new(() => Items, () => inv, isOffHandItem:
            name => System.Array.Exists(offHandNames, n => string.Equals(n, name, System.StringComparison.OrdinalIgnoreCase)));
        seq.SetWireSender(_ => { });
        return (seq, seq.LastSentForTests);
    }

    private static List<string> Decode(IEnumerable<byte[]> sent)
        => sent.Select(b => Encoding.Latin1.GetString(b).TrimEnd('\r')).ToList();

    [Fact]
    public void Execute_UnlimitedItem_EquipsUsesAndRestoresWeapon()
    {
        (ItemCastSequencer seq, List<byte[]> sent) =
            NewSeq(InvWith(new EquippedItem("long sword", "Weapon Hand")));

        Assert.True(seq.Execute("#emerald tipped crozier"));
        // Commands use the canonical game-data item name, not the lowercase token.
        Assert.Equal(new[]
        {
            "eq Emerald Tipped Crozier",
            "use Emerald Tipped Crozier",
            "eq long sword",
        }, Decode(sent));
    }

    [Fact]
    public void Execute_NoWeaponEquipped_SkipsRestore()
    {
        (ItemCastSequencer seq, List<byte[]> sent) = NewSeq(InventorySnapshot.Empty);

        Assert.True(seq.Execute("#emerald tipped crozier"));
        Assert.Equal(new[]
        {
            "eq Emerald Tipped Crozier",
            "use Emerald Tipped Crozier",
        }, Decode(sent));
    }

    [Fact]
    public void Execute_OneHandedItem_LeavesOffHandAlone()
    {
        // A 1H cast item slots in alongside the shield — no off-hand juggling.
        (ItemCastSequencer seq, List<byte[]> sent) = NewSeq(InvWith(
            new EquippedItem("long sword", "Weapon Hand"),
            new EquippedItem("medium shield", "Off-Hand")));

        Assert.True(seq.Execute("#emerald tipped crozier"));
        Assert.Equal(new[]
        {
            "eq Emerald Tipped Crozier",
            "use Emerald Tipped Crozier",
            "eq long sword",
        }, Decode(sent));
    }

    [Fact]
    public void Execute_TwoHandedItem_RemovesAndRestoresOffHand()
    {
        // Two-handed cast item: drop the shield, wield + use the greatsword,
        // restore the 1H weapon (frees the off-hand), then re-equip the shield.
        (ItemCastSequencer seq, List<byte[]> sent) = NewSeq(InvWith(
            new EquippedItem("long sword", "Weapon Hand"),
            new EquippedItem("medium shield", "Off-Hand")));

        Assert.True(seq.Execute("#shimmering greatsword"));
        Assert.Equal(new[]
        {
            "remove medium shield",
            "eq Shimmering Greatsword",
            "use Shimmering Greatsword",
            "eq long sword",
            "eq medium shield",
        }, Decode(sent));
    }

    [Fact]
    public void Execute_TwoHandedItem_WornSlotBlocksIt_RemovesAndRestoresWornItem()
    {
        // Report paradigm-20260819-234712: a red skull sits in "Worn" (not
        // "Off-Hand") but still occupies the off-hand mechanically — the game
        // refuses the 2H wield until it comes off. isOffHandItem recognizes it.
        (ItemCastSequencer seq, List<byte[]> sent) = NewSeqWithOffHandNames(
            InvWith(
                new EquippedItem("throwing hammers", "Weapon Hand"),
                new EquippedItem("severed head of Goru-Nezar", "Worn"),
                new EquippedItem("red skull", "Worn")),
            "red skull");

        Assert.True(seq.Execute("#shimmering greatsword"));
        Assert.Equal(new[]
        {
            "remove red skull",
            "eq Shimmering Greatsword",
            "use Shimmering Greatsword",
            "eq throwing hammers",
            "eq red skull",
        }, Decode(sent));
    }

    [Fact]
    public void Execute_TwoHandedItem_WornSlotItemNotOffHandBlocking_LeftAlone()
    {
        // A Worn-slot item that ISN'T an off-hand occupant (e.g. a plain trophy)
        // must never be removed just because it shares the generic "Worn" bucket.
        (ItemCastSequencer seq, List<byte[]> sent) = NewSeqWithOffHandNames(
            InvWith(
                new EquippedItem("throwing hammers", "Weapon Hand"),
                new EquippedItem("severed head of Goru-Nezar", "Worn")));

        Assert.True(seq.Execute("#shimmering greatsword"));
        Assert.Equal(new[]
        {
            "eq Shimmering Greatsword",
            "use Shimmering Greatsword",
            "eq throwing hammers",
        }, Decode(sent));
    }

    [Fact]
    public void Execute_TwoHandedItem_NoOffHand_SkipsRemove()
    {
        // No shield held: a two-hander needs no off-hand juggling.
        (ItemCastSequencer seq, List<byte[]> sent) =
            NewSeq(InvWith(new EquippedItem("battle axe", "Weapon Hand")));

        Assert.True(seq.Execute("#shimmering greatsword"));
        Assert.Equal(new[]
        {
            "eq Shimmering Greatsword",
            "use Shimmering Greatsword",
            "eq battle axe",
        }, Decode(sent));
    }

    [Fact]
    public void Execute_OffHandItem_RestoresOffHandNotWeapon()
    {
        // The reported bug: an off-hand cast item (a warhorn) displaces the OFF-HAND,
        // so the off-hand (shield) must come back — the weapon is never touched.
        (ItemCastSequencer seq, List<byte[]> sent) = NewSeq(InvWith(
            new EquippedItem("throwing hammers", "Weapon Hand"),
            new EquippedItem("griffon shield", "Off-Hand")));

        Assert.True(seq.Execute("#engraved warhorn"));
        Assert.Equal(new[]
        {
            "eq Engraved Warhorn",
            "use Engraved Warhorn",
            "eq griffon shield",
        }, Decode(sent));
    }

    [Fact]
    public void Execute_OffHandItem_NoOffHandHeld_SkipsRestore()
    {
        // Off-hand empty: nothing to restore, and the weapon stays put.
        (ItemCastSequencer seq, List<byte[]> sent) =
            NewSeq(InvWith(new EquippedItem("throwing hammers", "Weapon Hand")));

        Assert.True(seq.Execute("#engraved warhorn"));
        Assert.Equal(new[]
        {
            "eq Engraved Warhorn",
            "use Engraved Warhorn",
        }, Decode(sent));
    }

    [Fact]
    public void Execute_OffHandItem_AlreadyEquipped_RestoresFromEquipManager()
    {
        // Report 1: the warhorn is STILL in the off-hand from a prior session, so the
        // live slot can't say what belongs there (it reads back the buff item itself).
        // Fall back to the equipment manager's Default off-hand, and skip the redundant
        // re-equip of the already-worn buff item.
        (ItemCastSequencer seq, List<byte[]> sent) = NewSeqWithDesired(
            InvWith(
                new EquippedItem("throwing hammers", "Weapon Hand"),
                new EquippedItem("Engraved Warhorn", "Off-Hand")),
            slot => slot == "Off-Hand" ? "griffon shield" : null);

        Assert.True(seq.Execute("#engraved warhorn"));
        Assert.Equal(new[]
        {
            "use Engraved Warhorn",
            "eq griffon shield",
        }, Decode(sent));
    }

    [Fact]
    public void Execute_OffHandItem_AlreadyEquipped_NoEquipManagerFallback_SkipsRestore()
    {
        // Buff item stuck in-slot but no configured loadout to consult → fire the buff,
        // restore nothing, and still never touch the weapon.
        (ItemCastSequencer seq, List<byte[]> sent) = NewSeq(InvWith(
            new EquippedItem("throwing hammers", "Weapon Hand"),
            new EquippedItem("Engraved Warhorn", "Off-Hand")));

        Assert.True(seq.Execute("#engraved warhorn"));
        Assert.Equal(new[] { "use Engraved Warhorn" }, Decode(sent));
    }

    [Fact]
    public void Execute_WornItem_RestoresThatSlot()
    {
        // Restore is general across worn slots — a neck cast item restores the neck.
        (ItemCastSequencer seq, List<byte[]> sent) = NewSeq(InvWith(
            new EquippedItem("throwing hammers", "Weapon Hand"),
            new EquippedItem("amethyst pendant", "Neck")));

        Assert.True(seq.Execute("#amulet of vigor"));
        Assert.Equal(new[]
        {
            "eq Amulet of Vigor",
            "use Amulet of Vigor",
            "eq amethyst pendant",
        }, Decode(sent));
    }

    [Fact]
    public void Execute_AlreadyWieldingCastItem_UsesInPlaceNoReequipNoRestore()
    {
        // The cast item is already in its slot — skip the redundant re-equip (the
        // game would just say "you do not have <item> left unequipped"), just use it,
        // and restore nothing (it's the wielded gear).
        (ItemCastSequencer seq, List<byte[]> sent) =
            NewSeq(InvWith(new EquippedItem("Emerald Tipped Crozier", "Weapon Hand")));

        Assert.True(seq.Execute("#emerald tipped crozier"));
        Assert.Equal(new[] { "use Emerald Tipped Crozier" }, Decode(sent));
    }

    [Fact]
    public void Execute_LimitedChargeItem_DoesNothing()
    {
        (ItemCastSequencer seq, List<byte[]> sent) =
            NewSeq(InvWith(new EquippedItem("long sword", "Weapon Hand")));

        Assert.False(seq.Execute("#scroll of light"));
        Assert.Empty(sent);
    }

    [Fact]
    public void Execute_UnknownToken_DoesNothing()
    {
        (ItemCastSequencer seq, List<byte[]> sent) = NewSeq(InventorySnapshot.Empty);

        Assert.False(seq.Execute("#nonexistent"));
        Assert.Empty(sent);
    }

    [Fact]
    public void Execute_WireNotBound_ReturnsFalse()
    {
        ItemCastSequencer seq = new(() => Items, () => InventorySnapshot.Empty);
        // No SetWireSender — must not act.
        Assert.False(seq.Execute("#emerald tipped crozier"));
        Assert.Empty(seq.LastSentForTests);
    }
}
