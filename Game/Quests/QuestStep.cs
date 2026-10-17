using System.Collections.Generic;

namespace MudPlay.Game.Quests;

// One auto-drafted, followable step in a quest, produced by QuestStepGraph from a
// TBInfo chain that advances the quest flag. This is the baseline checklist a user
// refines into QuestDefinition.Steps markdown — it surfaces the action the game data
// records (where, the command, what changes hands), ordered by the quest's own
// give-step, but not the prose the user adds.
//
// Fields:
//   Order — the chain's give-step, the quest's own progress counter, used to
//     sequence the checklist into a faithful walk order.
//   Command — the player command that fires the step (e.g. "sit throne", "go hole")
//     when the chain leads with a verb; null when the chain is guard-led (reached
//     by dialogue branch), in which case Location is the anchor.
//   Location — provenance from CalledFrom (e.g. "Room 10/245", "Monster #61"); null
//     when absent.
//   RequiredItems — checkitem ids the player must be carrying.
//   TurnInItems — takeitem ids the step takes from the player.
//   GrantedItems — giveitem ids the step hands the player.
//   Gathers — true for a step that only fetches an item a later step of the quest
//     asks for, advancing no flag: a line gated on the flag that hands the item over
//     (the Conquest hands a monster's death gives while the quest is open), or, when
//     nothing in the quest's own lines yields it, where the set gives or drops it
//     (QuestItemGathering). Its Order is that of the step that needs the item, and it
//     sorts just ahead of it.
//   Droppers — the monsters a gather step's item drops from (a kill for the item);
//     null otherwise.
//   Note — a gather step's source as the data names it ("from silverwood chest",
//     "turn in orc-head"); null otherwise.
public sealed record QuestStep(
    int Order,
    string? Command,
    string? Location,
    IReadOnlyList<int> RequiredItems,
    IReadOnlyList<int> TurnInItems,
    IReadOnlyList<int> GrantedItems,
    bool Gathers = false,
    IReadOnlyList<int>? Droppers = null,
    string? Note = null);
