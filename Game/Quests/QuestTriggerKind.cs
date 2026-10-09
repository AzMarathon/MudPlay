namespace MudPlay.Game.Quests;

// How a script line is set off, for the cases the game data establishes.
public enum QuestTriggerKind
{
    // A room command, typed as written in the room that carries it.
    RoomCommand,
    // A keyword of an NPC's dialogue: `ask <npc> <keyword>`.
    Ask,
    // The script hangs off a monster's death spell, so it runs when that monster dies.
    Kill,
}
