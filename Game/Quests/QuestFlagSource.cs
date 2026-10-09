namespace MudPlay.Game.Quests;

// One record a script line's Called-From chain starts at: a monster, a room or a spell (or the
// textblock itself when the chain reaches none). Text names it the way the window quotes it,
// e.g. "monster orc warlord (#724)" or "room Volcanic Chamber (7/1358)".
public sealed record QuestFlagSource(QuestFlagSourceKind Kind, int Number, int Map, int Room, string Text);
