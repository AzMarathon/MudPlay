using MudPlay.Game.Map;

namespace MudPlay.ViewModels.GameData.Edit;

// What the Doors tab's edit dialog asked for: a kind the user sets on the side, or
// the side put back to the seed.
public sealed record DoorEdit(BareLockBash? Kind, bool ResetToSeed);
