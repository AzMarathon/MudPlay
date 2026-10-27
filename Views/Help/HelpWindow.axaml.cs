using Avalonia.Controls;
using MudPlay.Views;

namespace MudPlay.Views.Help;

public partial class HelpWindow : Window, MudPlay.Services.IShowsNoGameData
{
    public HelpWindow()
    {
        InitializeComponent();
        GlobalHotkeys.Attach(this);
    }
}
