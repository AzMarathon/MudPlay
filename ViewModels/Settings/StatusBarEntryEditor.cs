using CommunityToolkit.Mvvm.ComponentModel;
using MudPlay.Models.Settings;
using MudPlay.Services;

namespace MudPlay.ViewModels.Settings;

// One item placed in a status-bar zone, as the editor shows it: a chip with its
// name, or a text box when it is the user's own text. Clicking the chip opens its
// move / remove menu, which the view builds.
public sealed partial class StatusBarEntryEditor : ObservableObject
{
    public StatusBarZoneEditor Zone { get; }
    public StatusBarItemDef Def { get; }
    public string Label => Def.Label;
    public string Description => Def.Description;
    public bool IsCustomText => Def.Kind == StatusBarItemKind.CustomText;

    // The tooltip on a custom text's box: how braces work and every name they take.
    public string TextHint =>
        "Type your own text. Put an item's name in braces to show its live value, for example: "
        + "Lap {lap} of {loop}\n\n" + StatusBarItemCatalogue.TokenNames;

    [ObservableProperty] private string _text;

    public StatusBarEntryEditor(StatusBarZoneEditor zone, StatusBarItemDef def, string? text)
    {
        Zone = zone;
        Def = def;
        _text = text ?? string.Empty;
    }

    public StatusBarEntry ToEntry() => new(Def.Id, IsCustomText ? Text : null);

    partial void OnTextChanged(string value) => Zone.NotifyChanged();
}
