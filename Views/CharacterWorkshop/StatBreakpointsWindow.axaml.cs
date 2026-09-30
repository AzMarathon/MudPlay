using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MudPlay.ViewModels.CharacterWorkshop;

namespace MudPlay.Views.CharacterWorkshop;

// Modeless Stat Breakpoints window, opened from a base-stat label on Character Info.
// Code-behind only scrolls each column to the character's row and disposes the VM.
public partial class StatBreakpointsWindow : Window
{
    private StatBreakpointsViewModel? _wired;

    public StatBreakpointsWindow()
    {
        InitializeComponent();
        GlobalHotkeys.Attach(this);
        MudPlay.Services.AppServices.Current.WindowLayouts.AttachWindow(this, "statbreakpoints");
        DataContextChanged += (_, _) => Rewire();
        Opened += (_, _) => ScrollToCurrentRows();
        Closed += OnClosed;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void Rewire()
    {
        if (_wired is not null) _wired.Rebuilt -= ScrollToCurrentRows;
        _wired = DataContext as StatBreakpointsViewModel;
        if (_wired is not null) _wired.Rebuilt += ScrollToCurrentRows;
    }

    // A column can run to a hundred rows or more; open each one on the character's
    // row, centred, instead of at the top. Posted so the new rows are laid out first,
    // and done per column so the outer sideways scroll stays put.
    private void ScrollToCurrentRows() => Dispatcher.UIThread.Post(() =>
    {
        foreach (ScrollViewer rows in this.GetVisualDescendants().OfType<ScrollViewer>()
                     .Where(s => s.Classes.Contains("ColumnRows")))
        {
            Border? current = rows.GetVisualDescendants().OfType<Border>()
                .FirstOrDefault(b => b.Classes.Contains("current"));
            if (current is null || rows.Content is not Visual content) continue;
            Point? top = current.TranslatePoint(new Point(0, 0), content);
            if (top is null) continue;
            double y = Math.Max(0, top.Value.Y - (rows.Viewport.Height - current.Bounds.Height) / 2);
            rows.Offset = new Vector(0, y);
        }
    }, DispatcherPriority.Loaded);

    private void OnClosed(object? sender, EventArgs e)
    {
        if (_wired is not null) _wired.Rebuilt -= ScrollToCurrentRows;
        (DataContext as IDisposable)?.Dispose();
    }
}
