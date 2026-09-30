using System;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using MudPlay.Controls;
using MudPlay.ViewModels.CharacterWorkshop;

namespace MudPlay.Views.CharacterWorkshop;

// "How did I Die?" viewer. Code-behind feeds the BackscrollView its rows, opens on the
// newest row (the death itself), and hosts the right-click Copy / Select-all menu.
public partial class DeathLogWindow : Window
{
    // Resolved from the name scope: this project hand-rolls InitializeComponent, so the
    // generated x:Name fields are never populated.
    private readonly ScrollViewer _scroll;
    private readonly BackscrollView _view;

    public DeathLogWindow()
    {
        InitializeComponent();
        _scroll = this.FindControl<ScrollViewer>("OuterScroll")!;
        _view = this.FindControl<BackscrollView>("Transcript")!;
        Opened += OnOpened;

        MenuItem copy = new() { Header = "Copy" };
        copy.Click += (_, _) => _view.CopySelectionToClipboard();
        MenuItem selectAll = new() { Header = "Select all" };
        selectAll.Click += (_, _) => _view.SelectAll();
        ContextMenu menu = new();
        menu.Items.Add(copy);
        menu.Items.Add(selectAll);
        menu.Opening += (_, _) => copy.IsEnabled = _view.HasSelection;
        _view.ContextMenu = menu;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnOpened(object? sender, EventArgs e)
    {
        if (DataContext is not DeathLogViewModel vm) return;
        _view.SetRows(vm.Rows);
        Dispatcher.UIThread.Post(() =>
        {
            double maxY = Math.Max(0, _scroll.Extent.Height - _scroll.Viewport.Height);
            _scroll.Offset = _scroll.Offset.WithY(maxY);
            _view.Focus();
        }, DispatcherPriority.Background);
    }
}
