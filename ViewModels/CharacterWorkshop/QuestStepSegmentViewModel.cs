using System;
using CommunityToolkit.Mvvm.Input;

namespace MudPlay.ViewModels.CharacterWorkshop;

// What a clickable quest-step segment does when clicked.
public enum QuestStepLinkKind
{
    None,   // plain prose — not clickable
    Walk,   // a (map/room) coordinate — walks there
    Send,   // a 'command' — sends the text to the game as if typed
}

// One inline run of a quest step row: either plain prose (Command null) or a
// clickable token — a (map/room) coordinate that walks there (Kind Walk) or a
// single-quoted command that gets typed at the game (Kind Send). The view renders
// link runs as underlined hot-text (cyan for walk, green for send) and plain runs
// as ordinary prose, so a single wrapped line can mix all three.
public sealed class QuestStepSegmentViewModel
{
    // The literal text shown for this run — prose, or the token (with its
    // parentheses / quotes).
    public string Text { get; }

    // Non-null when this run is clickable; null for plain prose.
    public IRelayCommand? Command { get; }

    // Which action the clickable run performs (None for plain prose).
    public QuestStepLinkKind Kind { get; }

    // True when this run is a clickable link (drives the view's link styling).
    public bool IsLink => Command is not null;

    public QuestStepSegmentViewModel(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        Text = text;
        Kind = QuestStepLinkKind.None;
    }

    public QuestStepSegmentViewModel(string text, IRelayCommand command, QuestStepLinkKind kind)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(command);
        Text = text;
        Command = command;
        Kind = kind;
    }
}
