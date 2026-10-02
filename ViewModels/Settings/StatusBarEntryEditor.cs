using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Models.Settings;
using MudPlay.Services;

namespace MudPlay.ViewModels.Settings;

// One item placed in a status-bar zone, as the editor shows it: its name, a text
// box when it is the user's own text, and the move / remove buttons.
public sealed partial class StatusBarEntryEditor : ObservableObject
{
    private readonly StatusBarZoneEditor _zone;

    public StatusBarItemDef Def { get; }
    public string Label => Def.Label;
    public string Description => Def.Description;
    public bool IsCustomText => Def.Kind == StatusBarItemKind.CustomText;

    [ObservableProperty] private string _text;

    public StatusBarEntryEditor(StatusBarZoneEditor zone, StatusBarItemDef def, string? text)
    {
        _zone = zone;
        Def = def;
        _text = text ?? string.Empty;
    }

    public StatusBarEntry ToEntry() => new(Def.Id, IsCustomText ? Text : null);

    [RelayCommand] private void MoveEarlier() => _zone.Move(this, -1);
    [RelayCommand] private void MoveLater() => _zone.Move(this, +1);
    [RelayCommand] private void Remove() => _zone.Remove(this);

    partial void OnTextChanged(string value) => _zone.NotifyChanged();
}
