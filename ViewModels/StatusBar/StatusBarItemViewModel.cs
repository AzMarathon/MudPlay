using CommunityToolkit.Mvvm.ComponentModel;
using MudPlay.Services;

namespace MudPlay.ViewModels.StatusBar;

// One item on a status-bar row. Text items carry their own text; the chip, the
// statline warning and the connection light draw from the main window's state
// through Main.
public sealed partial class StatusBarItemViewModel : ObservableObject
{
    public StatusBarItemDef Def { get; }
    public MainWindowViewModel Main { get; }

    // What the user typed, for a custom text item.
    public string? Template { get; }

    public bool IsText => Def.Kind is StatusBarItemKind.Text or StatusBarItemKind.CustomText;
    public bool IsEngineChip => Def.Kind == StatusBarItemKind.EngineChip;
    public bool IsStatlineWarning => Def.Kind == StatusBarItemKind.StatlineWarning;
    public bool IsConnectionLight => Def.Kind == StatusBarItemKind.ConnectionLight;
    public bool IsMono => Def.Mono;

    // The item's plain-text reading: what a text item shows (and so what it adds
    // to a marquee row's crawl).
    [ObservableProperty] private string _text = string.Empty;

    // False hides the item and closes the gap it would leave: an empty text, or
    // the statline warning while the prompt matches.
    [ObservableProperty] private bool _isShown;

    // The combat-tick item's amber beat on each round.
    [ObservableProperty] private bool _isPulsing;

    public StatusBarItemViewModel(StatusBarItemDef def, MainWindowViewModel main, string? template)
    {
        Def = def;
        Main = main;
        Template = template;
    }
}
