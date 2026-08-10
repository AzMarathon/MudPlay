using System;
using System.Collections.Generic;
using MudPlay.Game.Inventory;
using MudPlay.Services;

namespace MudPlay.Game.Spells;

// Issues the wire sequence that casts a spell from an item: the item must be
// equipped to use it, so the engine equips the cast item, uses it, then re-equips
// whatever it displaced. Driven by CastingDirector when a Bless slot holds an
// ItemCastToken (#<item name>).
//
// Only unlimited-use cast items are eligible — a limited-charge item would burn out
// on a recast loop, and the Spell Book only ever offers a token for unlimited items.
//
// Restore is slot-aware. A cast item displaces only what sits in ITS OWN equip slot,
// so we restore that slot from the last i dump's EquippedItems: a 1H weapon restores
// the weapon hand, an off-hand item (a shield, a warhorn) restores the off-hand, a
// worn item restores its worn slot — and it never touches the other hand. Whatever
// isn't currently held simply isn't restored.
//
// Two-handed dance: a two-handed CAST item needs both hands, so it displaces the
// weapon AND the off-hand. When an off-hand is held we remove it first, then eq the
// item, use it, eq the displaced 1H weapon back (which drops the two-hander and frees
// the off-hand slot), and finally eq the off-hand again. (Confirmed MajorMUD equip
// order; see GAME_MECHANICS.md.) eq is the universal equip verb (matches CombatManager).
//
// The commands are sent back-to-back — MajorMUD queues typed input, and an item use
// resolves the cast immediately so the restore can follow without waiting. The buff
// timer (recast scheduling) is owned by CastingDirector, not this sequencer; this
// only puts the equip/use/restore lines on the wire.
public sealed class ItemCastSequencer
{
    // LogService category — appears as [ItemCast] rows.
    public const string LogCategory = "ItemCast";

    // Inventory slot label a wielded weapon occupies (matches EquippedItem.Slot
    // normalization).
    private const string WeaponHandSlot = "Weapon Hand";

    // Inventory slot label a shield / off-hand item occupies.
    private const string OffHandSlot = "Off-Hand";

    private readonly Func<IReadOnlyList<ClassCastItem>> _castItems;
    private readonly Func<InventorySnapshot> _inventory;
    // Given an inventory slot label, the item the equipment manager wants worn there
    // (its Default set), or null. Used as the restore fallback when the live slot
    // can't say what belongs there — see RestoreTarget. Null when unwired (tests).
    private readonly Func<string, string?>? _desiredSlotItem;
    private readonly WireSender _wire = new();
    private readonly LogService? _log;

    public ItemCastSequencer(
        Func<IReadOnlyList<ClassCastItem>> castItems,
        Func<InventorySnapshot> inventory,
        LogService? log = null,
        Func<string, string?>? desiredSlotItem = null)
    {
        ArgumentNullException.ThrowIfNull(castItems);
        ArgumentNullException.ThrowIfNull(inventory);
        _castItems = castItems;
        _inventory = inventory;
        _log = log;
        _desiredSlotItem = desiredSlotItem;
    }

    // Bind the wire sink (the wrapped engine sender).
    public void SetWireSender(Action<byte[]> sender) => _wire.Bind(sender);

    // Test seam — every buffer sent, in order.
    internal List<byte[]> LastSentForTests => _wire.LastSentForTests;

    // Run the equip → use → restore sequence for the cast item named by the token.
    // Returns true only when the lines were sent: the wire must be bound, the token
    // must resolve to an unlimited-use cast item. The weapon restore is skipped when
    // nothing was wielded (or the held weapon IS the cast item); the off-hand is
    // only juggled when the cast item is two-handed and a shield/off-hand is held.
    public bool Execute(string token)
    {
        if (!_wire.IsBound) return false;
        if (!ItemCastToken.TryResolve(token, _castItems(), out ClassCastItem item))
        {
            _log?.Debug(LogCategory, $"unresolved item-cast token: {token}");
            return false;
        }
        if (!item.Unlimited)
        {
            // Limited-charge items aren't safe to recast on a buff loop.
            _log?.Debug(LogCategory, $"skip limited-charge item-cast: {item.ItemName}");
            return false;
        }

        string name = item.ItemName.Trim();
        InventorySnapshot inv = _inventory();

        // A two-handed cast weapon needs BOTH hands, so it displaces the weapon AND
        // the off-hand: free the off-hand first, wield + use, then eq the 1H weapon
        // back (which drops the two-hander and frees the off-hand slot) and finally
        // re-wear the off-hand. Keyed on the CAST item being two-handed.
        if (item.IsTwoHanded)
        {
            string? restoreWeapon = SlotItem(inv, WeaponHandSlot);
            string? restoreOffHand = SlotItem(inv, OffHandSlot);
            bool restoreWeaponDiffers = Differs(restoreWeapon, name);
            bool juggleOffHand = Differs(restoreOffHand, name);

            if (juggleOffHand) _wire.Send($"remove {restoreOffHand}");
            _wire.Send($"eq {name}");
            _wire.Send($"use {name}");
            if (restoreWeaponDiffers) _wire.Send($"eq {restoreWeapon}");
            if (juggleOffHand) _wire.Send($"eq {restoreOffHand}");

            _log?.Info(LogCategory,
                $"item-cast item=\"{name}\" 2h=True casts={item.SpellName} " +
                $"restore-weapon={restoreWeapon ?? "<none>"} restore-offhand={(juggleOffHand ? restoreOffHand : "<none>")}");
            return true;
        }

        // A one-handed cast item displaces only whatever sits in ITS OWN wear slot —
        // the weapon hand for a 1H weapon, the off-hand for a shield / warhorn, a worn
        // slot for a charged amulet. Restore exactly that; never assume the weapon (an
        // off-hand cast item that re-equipped the weapon would strand the buff item in
        // the off-hand and never put the shield back — the reported bug).
        string slot = string.IsNullOrWhiteSpace(item.WearSlot) ? WeaponHandSlot : item.WearSlot.Trim();
        string? restore = RestoreTarget(inv, slot, name);
        // Skip a redundant re-equip when the cast item is already in its slot (left
        // there from a prior session) — the game would just answer "you do not have
        // <item> left unequipped." The use still fires, and the restore below swaps
        // the right gear back in over it.
        bool alreadyWorn = string.Equals(SlotItem(inv, slot), name, StringComparison.OrdinalIgnoreCase);

        if (!alreadyWorn) _wire.Send($"eq {name}");
        _wire.Send($"use {name}");
        if (restore is not null) _wire.Send($"eq {restore}");

        _log?.Info(LogCategory,
            $"item-cast item=\"{name}\" 2h=False slot={slot} casts={item.SpellName} " +
            $"already-worn={alreadyWorn} restore={restore ?? "<none>"}");
        return true;
    }

    // What to put back into `slot` after the cast: the live gear there, unless that
    // slot is empty or still holds the cast item itself (e.g. the buff item was left
    // equipped across a session) — then fall back to what the equipment manager wants
    // in that slot, so the buff still swaps the right gear back in. Null when there's
    // nothing worth restoring.
    private string? RestoreTarget(InventorySnapshot inv, string slot, string castName)
    {
        string? live = SlotItem(inv, slot);
        if (Differs(live, castName)) return live;
        string? desired = _desiredSlotItem?.Invoke(slot);
        return Differs(desired, castName) ? desired : null;
    }

    // A worn item worth restoring: the slot held something, and it isn't the cast
    // item itself (equipping the cast item over its own copy needs no restore).
    private static bool Differs(string? worn, string castName) =>
        !string.IsNullOrWhiteSpace(worn)
        && !string.Equals(worn, castName, StringComparison.OrdinalIgnoreCase);

    // The item name occupying the given slot in the last inventory dump, or null
    // when that slot is empty.
    private string? SlotItem(InventorySnapshot inv, string slot)
    {
        foreach (EquippedItem e in inv.EquippedItems)
            if (string.Equals(e.Slot.Trim(), slot, StringComparison.OrdinalIgnoreCase))
            {
                string n = e.Name.Trim();
                return n.Length > 0 ? n : null;
            }
        return null;
    }
}
