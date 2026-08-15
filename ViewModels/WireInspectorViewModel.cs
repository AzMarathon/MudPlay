using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Game.Combat;
using MudPlay.Services;

namespace MudPlay.ViewModels;

// View-model behind Views.WireInspectorWindow. Polls the shared WireBuffer
// on a low-frequency UI tick (200 ms) and exposes the rendered Raw +
// Stripped text the two panes bind to.
//
// We poll rather than subscribe to WireBuffer.BufferChanged because incoming
// bytes arrive in tiny chunks (one per Telnet read) and repainting the
// entire 64 KB pane on every byte would melt the UI. 200 ms is responsive
// enough for an at-a-glance debugger and keeps the dispatcher idle the rest
// of the time.
//
// The ViewModel implements IDisposable — the hosting window disposes it on
// close so the timer stops cleanly.
public sealed partial class WireInspectorViewModel : ObservableObject, IDisposable
{
    private readonly WireBuffer _buffer;
    private readonly CombatLineClassifier? _classifier;
    private readonly WireInspectorVisibility? _visibility;
    private readonly DispatcherTimer _timer;

    [ObservableProperty]
    private string _rawText = string.Empty;

    [ObservableProperty]
    private string _strippedText = string.Empty;

    // Full-ANSI stream of combat-window lines with a [Combat: <kind>] tag appended
    // to each classified line — how the recognizer read each line.
    [ObservableProperty]
    private string _classifiedText = string.Empty;

    // The three panes' visibility toggles (checkboxes in the control strip). All on
    // by default; unchecking a pane collapses its column so the others fill.
    [ObservableProperty]
    private bool _showRaw = true;

    [ObservableProperty]
    private bool _showStripped = true;

    [ObservableProperty]
    private bool _showClassified = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PauseLabel))]
    private bool _isPaused;

    [ObservableProperty]
    private bool _syncScroll = true;

    // When true (default), each refresh tick scrolls both panes to the bottom
    // so the freshest bytes are always visible. The window's code-behind does
    // the actual ScrollToEnd call in response to RefreshCompleted.
    [ObservableProperty]
    private bool _autoScroll = true;

    // Fires after each non-paused Refresh on the UI thread.
    public event Action? RefreshCompleted;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string _statusText = "Listening…";

    public string PauseLabel => IsPaused ? "Resume" : "Pause";

    // Parameterless ctor for the XAML design-time DataContext only.
    public WireInspectorViewModel() : this(new WireBuffer(), null, null) { }

    public WireInspectorViewModel(WireBuffer buffer, CombatLineClassifier? classifier,
                                  WireInspectorVisibility? visibility)
    {
        _buffer = buffer;
        _classifier = classifier;
        _visibility = visibility;
        PushVisibility();
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(200),
        };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();

        Refresh();
    }

    private void Refresh()
    {
        if (IsPaused) return;

        byte[] snapshot = _buffer.Snapshot();
        RawText = WireFormatter.RenderRaw(snapshot);
        StrippedText = WireFormatter.RenderStripped(snapshot);
        ClassifiedText = _classifier?.RenderLog() ?? string.Empty;
        StatusText = $"{snapshot.Length:N0} / {_buffer.Capacity:N0} bytes  •  {_buffer.TotalBytes:N0} total";

        RefreshCompleted?.Invoke();
    }

    // Toggle the live refresh. When paused the buffer keeps growing.
    [RelayCommand]
    private void TogglePause()
    {
        IsPaused = !IsPaused;
        if (!IsPaused) Refresh();
    }

    // Drop every byte from the buffer and wipe both panes.
    [RelayCommand]
    private void Clear()
    {
        _buffer.Clear();
        _classifier?.Clear();
        RawText = string.Empty;
        StrippedText = string.Empty;
        ClassifiedText = string.Empty;
    }

    // Mirror the current pane visibility into the shared holder the bug report reads.
    private void PushVisibility()
    {
        if (_visibility is null) return;
        _visibility.RawVisible = ShowRaw;
        _visibility.ClassifiedVisible = ShowClassified;
    }

    partial void OnShowRawChanged(bool value) => PushVisibility();
    partial void OnShowClassifiedChanged(bool value) => PushVisibility();

    public void Dispose()
    {
        _timer.Stop();
        // The inspector is closing — nothing is visible anymore.
        if (_visibility is not null)
        {
            _visibility.RawVisible = false;
            _visibility.ClassifiedVisible = false;
        }
    }
}
