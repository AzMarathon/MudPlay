namespace MudPlay.ViewModels;

// One line of the round-totals window. Our own row is picked out.
public sealed record RoundTotalsLineViewModel(string Text, bool IsSelf);
