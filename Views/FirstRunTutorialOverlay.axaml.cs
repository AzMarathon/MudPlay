using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using MudPlay.ViewModels;

namespace MudPlay.Views;

// The first-run setup highlight layer. Given the FirstRunTutorialViewModel as its
// DataContext, it draws a ring around the menu the current step points at
// (resolved by x:Name against the host window). The step card itself lives in a
// docked left panel in MainWindow, so nothing is drawn over the terminal; this
// control is hit-test-transparent throughout.
public partial class FirstRunTutorialOverlay : UserControl
{
    private FirstRunTutorialViewModel? _vm;

    // Resolved via FindControl, NOT the generated x:Name field: this control's own
    // InitializeComponent (=> AvaloniaXamlLoader.Load) doesn't populate those
    // fields, so a direct `Ring` reference would be null (same gotcha the
    // SpellBook / Wire Inspector windows document).
    private readonly Border? _ring;

    public FirstRunTutorialOverlay()
    {
        InitializeComponent();
        _ring = this.FindControl<Border>("Ring");
        SizeChanged += (_, _) => Reposition();
        DataContextChanged += OnDataContextChanged;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm is not null) _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm = DataContext as FirstRunTutorialViewModel;
        if (_vm is not null) _vm.PropertyChanged += OnVmPropertyChanged;
        RepositionDeferred();
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Re-place the ring whenever the active step (or activation) changes.
        if (e.PropertyName is nameof(FirstRunTutorialViewModel.IsActive)
            or nameof(FirstRunTutorialViewModel.CurrentIndex)
            or nameof(FirstRunTutorialViewModel.CurrentTargetName))
            RepositionDeferred();
    }

    // Layout of the target menu may lag the property change (a fresh window still
    // settling), so defer a beat and let it settle before measuring.
    private void RepositionDeferred() => Dispatcher.UIThread.Post(Reposition, DispatcherPriority.Background);

    private void Reposition()
    {
        if (_vm is not { IsActive: true } || _ring is null) return;
        if (Bounds.Width <= 0 || Bounds.Height <= 0) return;

        Rect? holeOpt = ResolveTargetRect(_vm.CurrentTargetName);
        if (holeOpt is { } h)
        {
            Rect ring = h.Inflate(5);
            _ring.IsVisible = true;
            Canvas.SetLeft(_ring, ring.X);
            Canvas.SetTop(_ring, ring.Y);
            _ring.Width = ring.Width;
            _ring.Height = ring.Height;
        }
        else
        {
            _ring.IsVisible = false;
        }
    }

    // The bounds (in this overlay's coordinates) of the named control in the host
    // window, or null when there's no target, it isn't laid out, or it's hidden.
    private Rect? ResolveTargetRect(string? name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        if (TopLevel.GetTopLevel(this) is not Window w) return null;
        if (w.FindControl<Control>(name) is not { } target) return null;
        if (!target.IsVisible || target.Bounds.Width <= 0 || target.Bounds.Height <= 0) return null;
        if (target.TransformToVisual(this) is not { } m) return null;
        Point topLeft = m.Transform(new Point(0, 0));
        return new Rect(topLeft, target.Bounds.Size);
    }
}
