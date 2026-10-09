namespace MudPlay.Game.Quests;

// One way to set a script line off. Command is what the player does ("throw egg",
// "ask archmage valduin crystal", "kill dread mystic"). Monster is the NPC or the kill
// target; Map / Room the room of a room command. OtherKeywords are the further keywords an
// NPC answers with the same reply.
public sealed record QuestTrigger(
    QuestTriggerKind Kind, string Command, int Monster = 0, int Map = 0, int Room = 0)
{
    public IReadOnlyList<string> OtherKeywords { get; init; } = Array.Empty<string>();
}
