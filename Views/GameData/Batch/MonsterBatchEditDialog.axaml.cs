using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace MudPlay.Views.GameData.Batch;

public partial class MonsterBatchEditDialog : Window
{
    public MonsterBatchEditDialog()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
