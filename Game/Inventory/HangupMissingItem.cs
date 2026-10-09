namespace MudPlay.Game.Inventory;

// An item held before the link dropped and short now: how many copies are
// missing, and the slots of the worn copies among them (empty when none was
// worn), which go back on if they are picked up.
public sealed record HangupMissingItem(string Name, int Count, IReadOnlyList<string> WornSlots);
