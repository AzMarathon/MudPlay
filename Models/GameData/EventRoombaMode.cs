namespace MudPlay.Models.GameData;

// Which Roomba sweep a Roomba event starts: a full sort, or the inventory-only
// pass that walks the circuit to refresh the item log without moving anything.
public enum EventRoombaMode
{
    Sort = 0,
    InventoryOnly = 1,
}
