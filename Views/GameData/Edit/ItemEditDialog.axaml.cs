using Avalonia.Controls;
using MudPlay.Services;

namespace MudPlay.Views.GameData.Edit;

public partial class ItemEditDialog : Window
{
    public ItemEditDialog()
    {
        InitializeComponent();
        AppServices.Current.WindowLayouts.AttachWindow(this, "item-edit");
    }
}
