namespace MudPlay.Game.Quests;

// One entry of a quest flag's walkthrough: a script line (or several that differ only in their
// class step or in the wording of the typed command) described for a player.
//
// Heading says what the line does to the flag. Do is how to set it off and where; Needs and
// Gives are the steps whose meaning is established; CheckedAfterwards are the conditions the
// line only reaches after it has changed the flag (or, on a line that leaves the flag alone,
// after it has started giving); Also holds every other step exactly as the data writes it.
// Later, under LaterLabel, is the rest of a line that checks an ability it has itself just
// changed — not reached when that check cannot pass, quoted without a verdict otherwise.
// Script is each raw line with its textblock number. OtherFlags are the other abilities the
// line checks or changes.
public sealed record QuestFlagStepEntry(
    string Heading,
    IReadOnlyList<string> Do,
    IReadOnlyList<string> Needs,
    IReadOnlyList<string> Gives,
    IReadOnlyList<string> CheckedAfterwards,
    IReadOnlyList<string> Also,
    string LaterLabel,
    IReadOnlyList<string> Later,
    IReadOnlyList<string> Script,
    IReadOnlyList<int> OtherFlags);
