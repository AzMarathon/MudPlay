using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using MudPlay.ViewModels.CharacterWorkshop;

namespace MudPlay.Views.CharacterWorkshop;

public partial class CalculatorsSectionView : UserControl
{
    private CalculatorsSectionViewModel? _vm;

    public CalculatorsSectionView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm is not null)
        {
            _vm.ScrollToCalculatorRequested -= CenterCalculator;
        }
        _vm = DataContext as CalculatorsSectionViewModel;
        if (_vm is not null)
        {
            _vm.ScrollToCalculatorRequested += CenterCalculator;
        }
    }

    // Deep-link: expand + center a calculator. The IsExpanded two-way binding has
    // already opened the target (the VM set its flag); defer the centering to
    // Loaded priority so the newly-revealed content is laid out first. BringIntoView
    // only reveals to the nearest edge, so compute the offset that centers it in the
    // scroll viewport ourselves. (This works here — unlike the Navigation folder
    // trees — because these Expanders sit in a plain, non-virtualized ScrollViewer.)
    private void CenterCalculator(CalculatorsSectionViewModel.CalculatorId id)
    {
        Control? target = ExpanderFor(id);
        ScrollViewer? sv = CalcScroll ?? this.FindControl<ScrollViewer>("CalcScroll");
        if (target is null || sv is null) return;

        Dispatcher.UIThread.Post(() =>
        {
            Point? rel = target.TranslatePoint(new Point(0, 0), sv);
            if (rel is not { } p) return;
            double targetTop = sv.Offset.Y + p.Y;
            double centered = targetTop - ((sv.Viewport.Height - target.Bounds.Height) / 2);
            double max = Math.Max(0, sv.Extent.Height - sv.Viewport.Height);
            sv.Offset = new Vector(sv.Offset.X, Math.Clamp(centered, 0, max));
            _vm?.ClearPendingScrollTarget();
        }, DispatcherPriority.Loaded);
    }

    private Control? ExpanderFor(CalculatorsSectionViewModel.CalculatorId id) => id switch
    {
        CalculatorsSectionViewModel.CalculatorId.Hit => HitCalc ?? this.FindControl<Expander>("HitCalc"),
        CalculatorsSectionViewModel.CalculatorId.Movement => MovementCalc ?? this.FindControl<Expander>("MovementCalc"),
        CalculatorsSectionViewModel.CalculatorId.Swing => SwingCalc ?? this.FindControl<Expander>("SwingCalc"),
        CalculatorsSectionViewModel.CalculatorId.Backstab => BackstabCalc ?? this.FindControl<Expander>("BackstabCalc"),
        CalculatorsSectionViewModel.CalculatorId.ManaRegen => ManaRegenCalc ?? this.FindControl<Expander>("ManaRegenCalc"),
        CalculatorsSectionViewModel.CalculatorId.MonsterAggro => MonsterAggroCalc ?? this.FindControl<Expander>("MonsterAggroCalc"),
        _ => null,
    };

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        // A calculator deep-link can arrive before this view is built (the tab is
        // lazy). NavigateToCalculator armed a pending target; center it now that
        // we're attached and laid out.
        if (_vm?.PendingScrollTarget is { } pending) CenterCalculator(pending);
    }
}
