using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Services;
using MudPlay.Terminal;

namespace MudPlay.ViewModels.CharacterWorkshop;

// Read-only "How did I Die?" viewer — the header of the death log and the backscroll
// snapshot captured at the moment of a recorded death, drawn in the colours it had
// (DeathLogFormat) with the terminal's own font. No pending state, so there's only a
// Close path; the standard edit-window Save/Cancel contract doesn't apply.
public sealed partial class DeathLogViewModel : ObservableObject, IDialogViewModel<bool>
{
    public event Action<bool>? CloseRequested;

    public string Title { get; }
    public string Header { get; }
    public IReadOnlyList<ScrollbackBuffer.Row> Rows { get; }
    public FontFamily FontFamily { get; }
    public double FontSize { get; }

    public DeathLogViewModel(string title, string header, IReadOnlyList<ScrollbackBuffer.Row> rows,
        string fontFamily = DisplayConfig.DefaultFontFamily, double fontSize = DisplayConfig.DefaultFontSize)
    {
        Title = title;
        Header = header;
        Rows = rows;
        FontFamily = new FontFamily(fontFamily);
        FontSize = fontSize;
    }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(false);
}
