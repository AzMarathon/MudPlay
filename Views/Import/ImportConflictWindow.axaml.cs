using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace MudPlay.Views.Import;

public partial class ImportConflictWindow : Window
{
    public ImportConflictWindow()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
