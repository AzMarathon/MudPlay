using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace MudPlay.Views.GameData.Batch;

public partial class ItemBatchEditDialog : Window
{
    public ItemBatchEditDialog()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
