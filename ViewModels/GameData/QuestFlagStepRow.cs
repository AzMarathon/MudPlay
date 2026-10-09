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
        Parts = entry.Parts.Select(p => new Part(p.Label, string.Join('\n', p.Lines))).ToList();
        Also = string.Join('\n', entry.Also);
        LaterLabel = entry.LaterLabel;
        Later = string.Join('\n', entry.Later);
        Script = string.Join('\n', entry.Script);
    }

    public string Heading { get; }
    public string Do { get; }

    // The line's conditions and gives, as runs in the order it goes through them.
    public IReadOnlyList<Part> Parts { get; }
    public string Also { get; }
    public string LaterLabel { get; }
    public string Later { get; }
    public string Script { get; }

    public bool HasDo => Do.Length > 0;
    public bool HasAlso => Also.Length > 0;
    public bool HasLater => LaterLabel.Length > 0;

    public sealed record Part(string Label, string Text);
}
