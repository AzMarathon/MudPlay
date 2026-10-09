namespace MudPlay.Game.Quests;

// One stretch of a script line, in the order the line runs: a run of conditions ("Needs",
// "Then checks") or a run of things it gives ("First, whatever the checks say", "Gives",
// "Then gives"), with its lines in words.
public sealed record QuestFlagStepPart(string Label, IReadOnlyList<string> Lines);
