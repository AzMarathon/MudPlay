namespace MudPlay.Game.Quests;

// One TBInfo script line that touches an ability flag: where it sits, how it is set off, and
// the requirement columns the Quest Flags table shows for it.
//
// Order is the line's position in its textblock (lines are tried top to bottom). RollChance is
// the share of draws that land on this line when it is one band of a random table, else null.
// Sources names the monster / room / spell records the line's Called-From chain starts at.
public sealed record QuestFlagLine(
    int Textblock,
    int Order,
    QuestScriptLine Script,
    string CalledFrom,
    IReadOnlyList<QuestTrigger> Triggers,
    double? RollChance,
    string Sources,
    string Command,
    string Level,
    string Classes,
    string Races,
    string Items);
