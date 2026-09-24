using Avalonia.Controls;

namespace MudPlay.Views.GameData.Edit;

public partial class TriggerWildcardsDialog : Window
{
    public TriggerWildcardsDialog()
    {
        InitializeComponent();
        // The VM subscribes to TriggerEngine.WildcardsChanged for its whole life;
        // the engine outlives this window, so drop the subscription when the user
        // closes it (including via the title-bar X, which the VM never sees).
        Closed += (_, _) => (DataContext as System.IDisposable)?.Dispose();
    }
}
