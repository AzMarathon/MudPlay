using MudPlay.Models.Profile;

namespace MudPlay.Game.Inventory;

// One item that negates a room's spell, as RoomSpellCounterWear weighs it: its name
// for the wear command, the slot it goes in, the armour it brings (what tells a
// better piece from a lesser one that negates the same spells), and whether this
// character can wear it at all (class, level and alignment limits in the game data).
public readonly record struct RoomSpellCounterItem(
    int Id, string Name, EquipmentSlot Slot, int ArmourClass, int DamageResist, bool CanWear);
