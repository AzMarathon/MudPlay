namespace MudPlay.ViewModels.GameData;

// One run of a walkthrough step in the Quest Flag Steps window — its label ("Needs",
// "Then gives") and its lines joined into one block of selectable text.
public sealed record QuestFlagStepPartRow(string Label, string Text);
