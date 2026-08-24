using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MudPlay.ViewModels;

namespace MudPlay.Views;

// Modeless Conversation panel. Bound to ConversationViewModel;
// code-behind handles Enter-to-send in the input field and scroll-to-newest
// when AutoScroll is on.
public partial class ConversationWindow : Window
{
    private ListBox? _rowsList;
    private ScrollViewer? _rowsScroll;

    // Click-drag selection paint: on press we pick a direction (select an unselected row,
    // deselect a selected one) and dragging over further rows applies that same state.
    private bool _dragging;
    private bool _dragValue;
    private int _dragLastIndex = -1;

    public ConversationWindow()
    {
        InitializeComponent();
        GlobalHotkeys.Attach(this);
        MudPlay.Services.AppServices.Current.WindowLayouts.AttachWindow(this, "conversation");
        Opened += OnOpened;
        Closed += OnClosed;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnOpened(object? sender, EventArgs e)
    {
        _rowsList = this.FindControl<ListBox>("RowsList");
        if (_rowsList is not null)
        {
            // Tunnel the press so we set the selection ourselves (and suppress the
            // ListBox's own click-toggle) before it acts; capture drives the move/release
            // even when the pointer leaves a row's bounds mid-drag.
            _rowsList.AddHandler(InputElement.PointerPressedEvent, OnRowsPointerPressed,
                RoutingStrategies.Tunnel);
            _rowsList.AddHandler(InputElement.PointerMovedEvent, OnRowsPointerMoved,
                RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
            _rowsList.AddHandler(InputElement.PointerReleasedEvent, OnRowsPointerReleased,
                RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        }
        if (DataContext is ConversationViewModel vm)
        {
            vm.ScrollToRowRequested += OnScrollToRow;
            // Land on the freshest row.
            if (vm.Rows.Count > 0) PinToBottomOnOpen();
            this.FindControl<TextBox>("InputBox")?.Focus();
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (DataContext is ConversationViewModel vm)
        {
            vm.ScrollToRowRequested -= OnScrollToRow;
            vm.Dispose();
        }
    }

    private void OnRecallSelected(object? sender, SelectionChangedEventArgs e)
    {
        // Dropdown pick fills the box, then hands focus back to the input
        // with the caret at the end so the user can edit / Enter at once.
        if (sender is not ListBox { SelectedItem: string command } list) return;
        if (DataContext is ConversationViewModel vm) vm.InputText = command;
        // Clear the selection so the same entry can be re-picked next open.
        list.SelectedIndex = -1;
        this.FindControl<Button>("RecallButton")?.Flyout?.Hide();
        if (this.FindControl<TextBox>("InputBox") is { } box)
        {
            box.Focus();
            box.CaretIndex = box.Text?.Length ?? 0;
        }
    }

    private void OnScrollToRow(ConversationRowViewModel row)
    {
        if (_rowsList is null) return;
        if (DataContext is not ConversationViewModel { AutoScroll: true }) return;
        // Defer the scroll. Calling ScrollIntoView synchronously while the
        // virtualizing panel is mid-update (a chat line arriving as the row is
        // added, or the panel still materialising on open) re-enters the layout
        // pass before the new container is measured and throws "Invalid Arrange
        // rectangle" — the same crash that hit the log pane. Posting lets the
        // panel finish its layout first.
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _rowsList?.ScrollIntoView(row);
            // ScrollIntoView only scrolls far enough to reveal the row, leaving
            // the ListBox's bottom padding (and any extent growth from a line
            // that landed mid-layout) between the last message and the viewport
            // edge — so "auto-scroll" visibly stopped short of the true bottom.
            // Pin the inner viewport to its end to close that gap.
            ResolveRowsScroll()?.ScrollToEnd();
        });
    }

    private ScrollViewer? ResolveRowsScroll()
        => _rowsScroll ??= _rowsList?.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();

    // Scroll-to-bottom on open. Unlike a live update (where the rows are already
    // realized so one ScrollToEnd lands true), on first open the virtualizing
    // panel only measures the handful of visible rows and estimates the rest, so
    // its extent keeps growing as the bottom rows realize — a single ScrollToEnd
    // stops short. Re-pin across the following layout passes until the offset
    // stops moving (or a small safety cap), so the newest line ends flush at the
    // viewport bottom every time the window opens.
    private void PinToBottomOnOpen()
    {
        if (DataContext is not ConversationViewModel { AutoScroll: true }) return;

        int attempts = 0;
        void Pin()
        {
            ScrollViewer? sv = ResolveRowsScroll();
            if (sv is null) return;
            double before = sv.Offset.Y;
            sv.ScrollToEnd();
            // Offset unchanged → extent settled, we're truly at the bottom.
            // Otherwise the extent grew this pass; try once more next pass.
            if (System.Math.Abs(sv.Offset.Y - before) < 0.5 || attempts++ >= 8) return;
            Dispatcher.UIThread.Post(Pin, DispatcherPriority.Background);
        }

        // First pass at Loaded priority: runs after the initial layout so the
        // ScrollViewer exists and has an estimated extent to pin against.
        Dispatcher.UIThread.Post(Pin, DispatcherPriority.Loaded);
    }

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        // Macro lookup first — same dispatch path the terminal canvas
        // uses, so a user-bound chord (F1, numpad direction, Ctrl+letter)
        // fires its command at the wire instead of typing characters
        // into this input field.
        if (MudPlay.Services.AppServices.Current.MacroDispatcher
                .TryHandleKey(e.Key, e.KeyModifiers))
        {
            e.Handled = true;
            return;
        }

        if (DataContext is not ConversationViewModel vm) return;

        // Up / Down recall previously-sent commands into the box, same as
        // the terminal canvas. The input is single-line, so the arrows
        // have no native job here to clobber.
        if (e.Key == Key.Up || e.Key == Key.Down)
        {
            if (e.Key == Key.Up) vm.RecallPrevious();
            else vm.RecallNext();
            if (sender is TextBox tb) tb.CaretIndex = tb.Text?.Length ?? 0;
            e.Handled = true;
            return;
        }

        if (e.Key != Key.Enter && e.Key != Key.Return) return;
        if (vm.SendInputCommand.CanExecute(null))
        {
            vm.SendInputCommand.Execute(null);
            e.Handled = true;
        }
    }

    // Row-list keys: Escape clears the selection (so a clicked line doesn't stay
    // highlighted until it scrolls off), Ctrl+C copies the selected line(s) as text.
    private void OnRowsKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            _rowsList?.UnselectAll();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.C && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            CopyRows(SelectedRows());
            e.Handled = true;
        }
    }

    // Press a line to begin a drag-select: pick a direction from the pressed row (select
    // it if it wasn't, deselect it if it was) and paint that state across every row the
    // drag passes over. Suppresses the list's own click-toggle so there's no double flip.
    private void OnRowsPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_rowsList is null) return;
        if (!e.GetCurrentPoint(_rowsList).Properties.IsLeftButtonPressed) return;   // left only
        // A press on an inline link (or any button) activates it — don't hijack for a drag.
        if (e.Source is Visual src && src.FindAncestorOfType<Button>(includeSelf: true) is not null) return;

        ListBoxItem? container = ContainerAt(e.GetPosition(_rowsList));
        if (container is null) return;   // background press — leave it to the list
        int index = _rowsList.IndexFromContainer(container);
        if (index < 0 || index >= _rowsList.Items.Count) return;

        object? row = _rowsList.Items[index];
        bool selected = row is not null && (_rowsList.SelectedItems?.Contains(row) ?? false);
        _dragValue = !selected;
        SetSelected(row, _dragValue);
        _dragging = true;
        _dragLastIndex = index;
        e.Pointer.Capture(_rowsList);
        _rowsList.Focus();               // so Ctrl+C copies the selection
        e.Handled = true;                // suppress the list's own click-toggle
    }

    private void OnRowsPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_dragging || _rowsList is null) return;
        ListBoxItem? container = ContainerAt(e.GetPosition(_rowsList));
        if (container is null) return;
        int index = _rowsList.IndexFromContainer(container);
        if (index < 0 || index == _dragLastIndex) return;
        // Fill rows skipped between the last hit and this one so a fast drag leaves no gaps.
        int lo = Math.Min(index, _dragLastIndex), hi = Math.Max(index, _dragLastIndex);
        for (int i = lo; i <= hi; i++)
            if (i >= 0 && i < _rowsList.Items.Count) SetSelected(_rowsList.Items[i], _dragValue);
        _dragLastIndex = index;
    }

    private void OnRowsPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        _dragLastIndex = -1;
        e.Pointer.Capture(null);
    }

    private ListBoxItem? ContainerAt(Point p)
    {
        if (_rowsList is null) return null;
        foreach (Visual v in _rowsList.GetVisualsAt(p))
            if (v.FindAncestorOfType<ListBoxItem>(includeSelf: true) is { } item)
                return item;
        return null;
    }

    private void SetSelected(object? row, bool value)
    {
        if (row is null || _rowsList?.SelectedItems is not { } sel) return;
        if (value) { if (!sel.Contains(row)) sel.Add(row); }
        else sel.Remove(row);
    }

    // Right-click → Copy. Copies the whole selection when the clicked row is part of
    // it, else just the row under the cursor — the intuitive "copy what I clicked".
    private void OnCopyRow(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { DataContext: ConversationRowViewModel row }) return;
        List<ConversationRowViewModel> selected = SelectedRows().ToList();
        CopyRows(selected.Contains(row) ? selected : new List<ConversationRowViewModel> { row });
    }

    private IEnumerable<ConversationRowViewModel> SelectedRows()
        => _rowsList?.SelectedItems?.OfType<ConversationRowViewModel>()
           ?? Enumerable.Empty<ConversationRowViewModel>();

    // Copy rows in on-screen order (SelectedItems is in click order), newest-first
    // stays as displayed — one line per entry.
    private void CopyRows(IEnumerable<ConversationRowViewModel> rows)
    {
        if (DataContext is not ConversationViewModel vm) return;
        List<ConversationRowViewModel> ordered = rows.Distinct().OrderBy(vm.Rows.IndexOf).ToList();
        if (ordered.Count == 0) return;
        string text = string.Join(System.Environment.NewLine, ordered.Select(r => r.CopyText));
        if (text.Length == 0) return;

        TopLevel? top = TopLevel.GetTopLevel(this);
        if (top?.Clipboard is not { } cb)
        {
            MudPlay.Services.AppServices.Current.Log.Warn("Conversation",
                "Clipboard unavailable; nothing copied.");
            return;
        }
        _ = CopyAsync(cb, text);
    }

    private static async Task CopyAsync(Avalonia.Input.Platform.IClipboard cb, string text)
    {
        try
        {
            await cb.SetTextAsync(text).ConfigureAwait(false);
        }
        catch (System.Exception ex)
        {
            MudPlay.Services.AppServices.Current.Log.Warn("Conversation",
                $"Clipboard copy failed: {ex.Message}");
        }
    }
}
