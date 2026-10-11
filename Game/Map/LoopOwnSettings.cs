namespace MudPlay.Game.Map;

// A loop's two "Entire Loop Settings" as one character has them: only attack in lair
// rooms, and the wait outside a lair for the debuff. Each character's own for each
// loop (Loop.OnlyAttackInLairRooms / Loop.LairEntryDebuff).
public readonly record struct LoopOwnSettings(bool OnlyAttackInLairRooms, LairEntryDebuffMode LairEntryDebuff);
