namespace MudPlay.Game.Quests;

// One entry of a quest flag's walkthrough: a script line (or several that differ only in their
// class step or in the wording of the typed command) described for a player.
//
// Heading says what the line does to the flag. Do is how to set it off and where; Needs and
// Gives are the steps whose meaning is established; Also holds every other step exactly as the
// data writes it; Script is each raw line with its textblock number. OtherFlags are the other
// abilities the line checks or changes.
public sealed record QuestFlagStepEntry(
    string Heading,
    IReadOnlyList<string> Do,
    IReadOnlyList<string> Needs,
    IReadOnlyList<string> Gives,
    IReadOnlyList<string> Also,
    IReadOnlyList<string> Script,
    IReadOnlyList<int> OtherFlags);
