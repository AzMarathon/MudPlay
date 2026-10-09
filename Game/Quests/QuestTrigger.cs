namespace MudPlay.Game.Quests;

// One way a script line is set off. Command is what the player does ("throw egg",
// "ask archmage valduin crystal", "kill dread mystic"), or for an auto-shown keyword the
// keyword itself. Monster is the NPC or the kill target; Map / Room the room of a room
// command; Spell the spell a kill line hangs off; Textblock the NPC's keyword block.
// OtherKeywords are the further keywords the NPC lists for the same reply.
public sealed record QuestTrigger(
    QuestTriggerKind Kind, string Command, int Monster = 0, int Map = 0, int Room = 0,
    int Spell = 0, int Textblock = 0)
{
    public IReadOnlyList<string> OtherKeywords { get; init; } = Array.Empty<string>();
}
