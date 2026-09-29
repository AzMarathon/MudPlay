using Avalonia.Controls;
using MudPlay.Services;

namespace MudPlay.Views.GameData.Edit;

public partial class MonsterEditDialog : Window
{
    public MonsterEditDialog()
    {
        InitializeComponent();
        AppServices.Current.WindowLayouts.AttachWindow(this, "monster-edit");
    }
}
