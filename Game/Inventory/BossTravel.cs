namespace MudPlay.Game.Inventory;

// Whether the trip under way leads to another boss room, for the Bossing set's
// "keep on between bosses" option.
public enum BossTravel
{
    // Not heading for a boss room (or not travelling at all).
    No,
    // Our own walk-to is heading for one.
    Yes,
    // Following a party leader: its client has been asked where it's going, and the
    // answer arrives through AutoEquipCoordinator.OnBossTravelResolved.
    Asking,
}
