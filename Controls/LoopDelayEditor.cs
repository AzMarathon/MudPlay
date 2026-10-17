using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using MudPlay.Game.Map;

namespace MudPlay.Controls;

// Minutes and seconds boxes for a loop room command's delay, bound two-way to the
// delay in milliseconds the loop stores. Each box takes up to LoopDelay.MaxField; the
// boxes are split from the stored value only when it changes from outside, so typing
// 90 into seconds stays 90 rather than jumping to 1 min 30 s mid-edit.
public sealed class LoopDelayEditor : UserControl
{
    public static readonly StyledProperty<long> DelayMsProperty =
        AvaloniaProperty.Register<LoopDelayEditor, long>(
            nameof(DelayMs), 0L, defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<bool> ShowSpinnersProperty =
        AvaloniaProperty.Register<LoopDelayEditor, bool>(nameof(ShowSpinners), true);

    public long DelayMs { get => GetValue(DelayMsProperty); set => SetValue(DelayMsProperty, value); }
    public bool ShowSpinners { get => GetValue(ShowSpinnersProperty); set => SetValue(ShowSpinnersProperty, value); }

    private readonly NumericUpDown _minutes;
    private readonly NumericUpDown _seconds;
    private bool _writing;

    public LoopDelayEditor()
    {
        _minutes = Box("Minutes to wait (up to 99999)");
        _seconds = Box("Seconds to wait, added to the minutes (up to 99999)");
        Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 3,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                _minutes, Unit("m"), _seconds, Unit("s"),
            },
        };
        _minutes.ValueChanged += (_, _) => WriteBack();
        _seconds.ValueChanged += (_, _) => WriteBack();
        ShowFrom(DelayMs);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == DelayMsProperty && !_writing) ShowFrom(DelayMs);
        else if (change.Property == ShowSpinnersProperty)
            _minutes.ShowButtonSpinner = _seconds.ShowButtonSpinner = ShowSpinners;
    }

    private void ShowFrom(long ms)
    {
        (long minutes, long seconds) = LoopDelay.Split(ms);
        _writing = true;
        try
        {
            _minutes.Value = minutes;
            _seconds.Value = seconds;
        }
        finally { _writing = false; }
    }

    private void WriteBack()
    {
        if (_writing) return;
        long ms = LoopDelay.ToMs((long)(_minutes.Value ?? 0), (long)(_seconds.Value ?? 0));
        _writing = true;
        try { DelayMs = ms; }
        finally { _writing = false; }
    }

    private static NumericUpDown Box(string tip)
    {
        NumericUpDown box = new()
        {
            Minimum = 0,
            Maximum = LoopDelay.MaxField,
            Increment = 1,
            FormatString = "0",
            MinWidth = 52,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(box, tip);
        return box;
    }

    private static TextBlock Unit(string text) => new()
    {
        Text = text,
        VerticalAlignment = VerticalAlignment.Center,
    };
}
