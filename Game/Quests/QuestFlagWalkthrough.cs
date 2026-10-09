namespace MudPlay.Game.Quests;

// Everything the active set's scripts do with one ability flag, ordered so a player can walk
// it: by the value the character must hold going in, then by the value the step leaves.
//
// Quests are the quest crawl's entries for the flag (one per tier; empty when the crawl lists
// none), with CompleteText and RestrictionText its findings in words (RestrictionText is empty
// when the crawl records no class or race limit). WithoutFlagSteps are the lines that only run
// for a character who doesn't have the flag and leave it alone, kept apart from Steps; when
// every line is of that kind they are the Steps instead. OtherFlags are the other abilities
// any of them check or change.
public sealed record QuestFlagWalkthrough(
    int Flag,
    string FlagName,
    IReadOnlyList<CrawledQuest> Quests,
    string CompleteText,
    string RestrictionText,
    IReadOnlyList<QuestFlagStepEntry> Steps,
    IReadOnlyList<QuestFlagStepEntry> WithoutFlagSteps,
    IReadOnlyList<int> OtherFlags);
