namespace MudPlay.Game.Quests;

// One TBInfo script line that touches an ability flag: where it sits, how it is set off, and
// the requirement columns the Quest Flags table shows for it.
//
// Order is the line's position in its textblock (lines are tried top to bottom). RollChance is
// the share of draws that land on this line when it is one band of a random table, else null.
// Sources names the monster / room / spell records the line's Called-From chain starts at.
// Callers are the lines that draw from this line's textblock with a `random` step — the
// conditions on those lines stand in front of this one. A caller carries no callers of its own.
public sealed record QuestFlagLine(
    int Textblock,
    int Order,
    QuestScriptLine Script,
    string CalledFrom,
    IReadOnlyList<QuestTrigger> Triggers,
    double? RollChance,
    string Sources,
    string Level,
    string Classes,
    string Races,
    string Items,
    IReadOnlyList<QuestFlagLine> Callers);
