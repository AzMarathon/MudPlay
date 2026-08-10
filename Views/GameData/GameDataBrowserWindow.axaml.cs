using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace MudPlay.Views.GameData;

public partial class GameDataBrowserWindow : Window
{
    public GameDataBrowserWindow()
    {
        InitializeComponent();
        // Install the store-driven window-toggle hotkeys so re-pressing the
        // browser's chord (default F3) while this window has focus closes it —
        // without this the keypress lands on this window instead of MainWindow
        // and the toggle command never fires.
        GlobalHotkeys.Attach(this);
        // Browser VMs subscribe to long-lived AppServices events
        // (GameDataCache.ActiveSetChanged, engine CollectionChanged).
        // Dispose detaches them so the VM tree — plus every cached
        // row collection and lazy-built per-section View — can GC.
        // Without this each reopen leaked ~90 MB of heap.
        Closed += (_, _) => (DataContext as IDisposable)?.Dispose();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
