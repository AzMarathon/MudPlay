using Avalonia.Media;

namespace MudPlay.ViewModels;

// The Conversation window's row typography, handed to every row as it is built. The
// row template binds these off its own row, never off the window's view-model: a
// row bound to the view-model leaves a weak subscription on it each time the list
// realizes the row, and the view-model raises too few changes for Avalonia to sweep
// the dead ones, so they piled up by the hundred per chat line.
public sealed record ConversationRowLook(
    FontFamily FontFamily,
    double MetaFontSize,
    double MessageFontSize,
    double MessageLineHeight,
    bool EmotesEnabled);
