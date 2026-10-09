using CommunityToolkit.Mvvm.Input;

namespace MudPlay.ViewModels.GameData;

// A link to another flag in the Quest Flag Steps window; activating it shows that flag there.
public sealed class QuestFlagLinkRow
{
    public QuestFlagLinkRow(int flag, string label, Action<int> open)
    {
        ArgumentNullException.ThrowIfNull(open);
        Label = label;
        OpenCommand = new RelayCommand(() => open(flag));
    }

    public string Label { get; }
    public IRelayCommand OpenCommand { get; }
}
