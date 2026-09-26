namespace MudPlay.Game.Inventory;

// EquipmentManager.UpdateSetFromWorn's answer: the outcome, the set's display name
// (null when none matched) and how many worn slots it now holds.
public readonly record struct EquipUpdateResult(EquipUpdateOutcome Outcome, string? SetName, int Slots);
