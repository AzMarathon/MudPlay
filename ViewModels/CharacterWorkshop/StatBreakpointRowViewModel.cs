namespace MudPlay.ViewModels.CharacterWorkshop;

// One row of a breakpoint column: a stat value and what that stat adds from there
// until the next row. IsCurrent marks the row the character's own value falls in.
public sealed record StatBreakpointRowViewModel(string StatText, string ValueText, bool IsCurrent, bool IsZero);
