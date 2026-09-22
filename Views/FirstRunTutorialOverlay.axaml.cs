using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using MudPlay.ViewModels;

namespace MudPlay.Views;

// The first-run setup overlay. Given the FirstRunTutorialViewModel as its
// DataContext, it dims the window and spotlights the menu the current step points
// at (resolved by x:Name against the host window), placing a callout card beside
// it. Pure view geometry — all step logic lives in the view-model.
public partial class FirstRunTutorialOverlay : UserControl
{
    private FirstRunTutorialViewModel? _vm;

    // Resolved via FindControl, NOT the generated x:Name fields: this control's
    // own InitializeComponent (=> AvaloniaXamlLoader.Load) doesn't populate those
    // fields, so a direct `Scrim` reference would be null (same gotcha the
    // SpellBook / Wire Inspector windows document).
    private readonly Avalonia.Controls.Shapes.Path? _scrim;
    private readonly Border? _ring;
    private readonly Border? _card;

    public FirstRunTutorialOverlay()
    {
        InitializeComponent();
        _scrim = this.FindControl<Avalonia.Controls.Shapes.Path>("Scrim");
        _ring = this.FindControl<Border>("Ring");
        _card = this.FindControl<Border>("Card");
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
        // Re-place the spotlight whenever the active step (or activation) changes.
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
        if (_vm is not { IsActive: true }) return;
        if (_scrim is null || _ring is null || _card is null) return;
        if (Bounds.Width <= 0 || Bounds.Height <= 0) return;

        Rect overlay = new(Bounds.Size);
        Rect? holeOpt = ResolveTargetRect(_vm.CurrentTargetName);

        // Scrim = the whole overlay minus the spotlight hole (EvenOdd punches it out).
        GeometryGroup group = new() { FillRule = FillRule.EvenOdd };
        group.Children.Add(new RectangleGeometry(overlay));
        Rect hole = default;
        if (holeOpt is { } h)
        {
            hole = h.Inflate(5);
            group.Children.Add(new RectangleGeometry(hole));
        }
        _scrim.Data = group;

        // Highlight ring on the hole.
        if (holeOpt is not null)
        {
            _ring.IsVisible = true;
            Canvas.SetLeft(_ring, hole.X);
            Canvas.SetTop(_ring, hole.Y);
            _ring.Width = hole.Width;
            _ring.Height = hole.Height;
        }
        else
        {
            _ring.IsVisible = false;
        }

        // Callout card: below the hole if it fits, else above, else centred.
        _card.Measure(new Size(overlay.Width, overlay.Height));
        Size cs = _card.DesiredSize;
        double cx, cy;
        if (holeOpt is { } hr)
        {
            cx = Math.Clamp(hr.X, 8, Math.Max(8, overlay.Width - cs.Width - 8));
            double below = hr.Bottom + 10;
            cy = below + cs.Height + 8 <= overlay.Height
                ? below
                : Math.Max(8, hr.Y - cs.Height - 10);
        }
        else
        {
            cx = Math.Max(8, (overlay.Width - cs.Width) / 2);
            cy = Math.Max(8, (overlay.Height - cs.Height) / 2);
        }
        Canvas.SetLeft(_card, cx);
        Canvas.SetTop(_card, cy);
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
