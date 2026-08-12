using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using MudPlay.Controls;
using MudPlay.ViewModels;

namespace MudPlay.Views;

// Modeless terminal-history window. Bound to BackscrollViewModel, which holds a
// frozen snapshot captured when the window opened (the window never tracks the
// live terminal). The transcript is a BackscrollView — a virtualized canvas that
// renders only the visible rows and owns its own (row, col) selection AND its own
// Ctrl+C copy — so drag-select and copy stay flat even on a deep history. The
// code-behind feeds it the VM's rows once and drives Find-next / Jump-to-end by
// scrolling the viewer, and hosts the right-click Copy / Select-all menu.
public partial class BackscrollWindow : Window
{
    // Named controls resolved from the XAML name scope after load. This project
    // hand-rolls InitializeComponent, so Avalonia's generated x:Name fields are
    // never populated — every window pulls its controls via FindControl (see
    // LogPaneWindow / ConversationWindow). Accessing the generated fields
    // directly leaves them null and faults on first use.
    private readonly ScrollViewer _scroll;
    private readonly BackscrollView _view;

    public BackscrollWindow()
    {
        InitializeComponent();
        _scroll = this.FindControl<ScrollViewer>("OuterScroll")!;
        _view   = this.FindControl<BackscrollView>("Transcript")!;
        GlobalHotkeys.Attach(this);
        MudPlay.Services.AppServices.Current.WindowLayouts.AttachWindow(this, "backscroll");
        Opened += OnOpened;
        Closed += OnClosed;
        BuildContextMenu();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    // Right-click menu on the transcript. Copy is greyed out with nothing
    // selected; "Select all" grabs the whole history so a single copy takes it
    // all. Both delegate to the view, which owns the selection and the copy.
    private void BuildContextMenu()
    {
        MenuItem copy = new() { Header = "Copy" };
        copy.Click += (_, _) => _view.CopySelectionToClipboard();

        MenuItem selectAll = new() { Header = "Select all" };
        selectAll.Click += (_, _) => _view.SelectAll();

        ContextMenu menu = new();
        menu.Items.Add(copy);
        menu.Items.Add(selectAll);
        // Reflect the live selection state each time the menu opens.
        menu.Opening += (_, _) => copy.IsEnabled = _view.HasSelection;
        _view.ContextMenu = menu;
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        if (DataContext is not BackscrollViewModel vm) return;

        vm.FindMatchRequested += OnFindMatch;
        vm.JumpToEndRequested += OnJumpToEnd;

        _view.SetRows(vm.Rows.Select(r => r.Source).ToArray());

        // Once the first layout pass has sized the viewport: park at the newest
        // captured row (open on the tail) and focus the transcript so Ctrl+C
        // reaches the view's key handler without the user clicking first.
        Dispatcher.UIThread.Post(() =>
        {
            OnJumpToEnd();
            _view.Focus();
        }, DispatcherPriority.Background);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (DataContext is BackscrollViewModel vm)
        {
            vm.FindMatchRequested -= OnFindMatch;
            vm.JumpToEndRequested -= OnJumpToEnd;
        }
    }

    // Highlight a Find-next hit by selecting its (row, col) span and scrolling it
    // into view (~a third down the viewport).
    private void OnFindMatch(int rowIndex, int columnOffset, int length)
    {
        _view.SelectMatch(rowIndex, columnOffset, length);

        double maxY = Math.Max(0, _scroll.Extent.Height - _scroll.Viewport.Height);
        double targetY = Math.Clamp(_view.RowTop(rowIndex) - _scroll.Viewport.Height / 3, 0, maxY);
        _scroll.Offset = _scroll.Offset.WithY(targetY);
    }

    private void OnJumpToEnd()
    {
        double maxY = Math.Max(0, _scroll.Extent.Height - _scroll.Viewport.Height);
        _scroll.Offset = _scroll.Offset.WithY(maxY);
    }
}
