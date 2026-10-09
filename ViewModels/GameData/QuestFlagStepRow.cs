using MudPlay.Game.Quests;

namespace MudPlay.ViewModels.GameData;

// One walkthrough entry laid out for the Quest Flag Steps window: each part's lines joined
// into one block of selectable text, and a flag saying whether the part has anything to show.
public sealed class QuestFlagStepRow
{
    public QuestFlagStepRow(int number, QuestFlagStepEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        Heading = $"{number}. {entry.Heading}";
        Do = string.Join('\n', entry.Do);
        Needs = string.Join('\n', entry.Needs);
        Gives = string.Join('\n', entry.Gives);
        Also = string.Join('\n', entry.Also);
        Script = string.Join('\n', entry.Script);
    }

    public string Heading { get; }
    public string Do { get; }
    public string Needs { get; }
    public string Gives { get; }
    public string Also { get; }
    public string Script { get; }

    public bool HasDo => Do.Length > 0;
    public bool HasNeeds => Needs.Length > 0;
    public bool HasGives => Gives.Length > 0;
    public bool HasAlso => Also.Length > 0;
}
