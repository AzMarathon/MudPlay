namespace MudPlay.Game.Quests;

// One entry of a quest flag's walkthrough: a script line (or several that differ only in their
// class step or in the wording of the typed command) described for a player.
//
// Heading says what the line does to the flag. Do is how to set it off and where. Parts are
// the steps whose meaning is established, as alternating runs of conditions and gives in the
// order the line runs them — a line stops at the first condition that fails, so a give only
// depends on the conditions written before it. Also holds every other step exactly as the data
// writes it. Later, under LaterLabel, is the rest of a line that checks an ability it has
// itself just changed — not reached when that check cannot pass, quoted without a verdict
// otherwise. Script is each raw line with its textblock number. OtherFlags are the other
// abilities the line checks or changes.
public sealed record QuestFlagStepEntry(
    string Heading,
    IReadOnlyList<string> Do,
    IReadOnlyList<QuestFlagStepPart> Parts,
    IReadOnlyList<string> Also,
    string LaterLabel,
    IReadOnlyList<string> Later,
    IReadOnlyList<string> Script,
    IReadOnlyList<int> OtherFlags);
