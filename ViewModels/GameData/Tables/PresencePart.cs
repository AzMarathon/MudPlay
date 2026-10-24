using CommunityToolkit.Mvvm.ComponentModel;

namespace MudPlay.ViewModels.GameData.Tables;

// One column a PresenceFilter can narrow to (a currency under Cash).
public sealed partial class PresencePart : ObservableObject
{
    public string Label { get; }
    public string Column { get; }

    [ObservableProperty] private bool _isChecked;

    public PresencePart(string label, string column)
    {
        Label = label;
        Column = column;
    }
}
