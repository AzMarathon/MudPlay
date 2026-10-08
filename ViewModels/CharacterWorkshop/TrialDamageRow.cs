namespace MudPlay.ViewModels.CharacterWorkshop;

// One line of the Gear Finder's trial damage readout: what the number is, the number,
// and hover text saying how it was reached (null when the label says it all).
public sealed record TrialDamageRow(string Label, string Value, string? Tooltip = null);
