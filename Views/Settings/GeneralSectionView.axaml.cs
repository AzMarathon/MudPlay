using Avalonia.Controls;

namespace MudPlay.Views.Settings;

public partial class GeneralSectionView : UserControl
{
    public GeneralSectionView()
    {
        InitializeComponent();
        Controls.RadioGroupScope.ToThisView(this);
    }
}
