using System.Text;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using MudPlay.Services;

namespace MudPlay.ViewModels;

// Tools → GMCP Monitor: the recording's state and its latest frames, refreshed a
// couple of times a second rather than per frame (a busy room sends dozens), and
// the export the window saves.
public sealed partial class GmcpMonitorViewModel : ObservableObject, IDisposable
{
    // Frames shown; the export has them all.
    private const int Shown = 300;
    private const int BodyPreview = 240;

    private readonly GmcpMonitor _monitor;
    private readonly DispatcherTimer _refresh;
    private volatile bool _dirty = true;

    [ObservableProperty] private string _status = string.Empty;
    [ObservableProperty] private string _frames = string.Empty;
    [ObservableProperty] private bool _canExport;

    public GmcpMonitorViewModel(GmcpMonitor monitor)
    {
        _monitor = monitor;
        _monitor.Changed += OnChanged;
        _refresh = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) => Refresh());
        _refresh.Start();
        Refresh();
    }

    private void OnChanged() => _dirty = true;

    private void Refresh()
    {
        if (!_dirty) return;
        _dirty = false;
        IReadOnlyList<GmcpMonitor.Frame> frames = _monitor.Snapshot();
        int server = frames.Count(f => f.FromServer && f.Package is not null);
        string since = _monitor.Since is { } at ? at.ToString("HH:mm:ss") : string.Empty;
        Status = _monitor.Since is null
            ? "No GMCP handshake yet this session. Recording starts when the server offers GMCP."
            : (_monitor.IsRecording ? $"Recording since {since}" : $"GMCP ended; recorded from {since}")
              + $" · {frames.Count:N0} frames, {server:N0} GMCP from the server"
              + (_monitor.Dropped > 0 ? $" · {_monitor.Dropped:N0} oldest dropped" : string.Empty);
        CanExport = frames.Count > 0;

        StringBuilder text = new();
        foreach (GmcpMonitor.Frame frame in frames.Skip(Math.Max(0, frames.Count - Shown)))
        {
            string body = frame.Body.Replace("\r", "").Replace('\n', ' ');
            if (body.Length > BodyPreview) body = body[..BodyPreview] + "…";
            string what = frame.Package ?? "text";
            text.Append($"{frame.Milliseconds,9} {(frame.FromServer ? "←" : "→")} {what}  {body}\n");
        }
        Frames = text.ToString();
    }

    // The recording with the saved login masked, as a bug report masks it; null with
    // why when it can't be masked, so nothing unmasked is ever written.
    public string? BuildExport(out string? error)
    {
        error = null;
        try
        {
            BugReportScrub.Scrubber scrubber = BugReportBuilder.BuildScrubber(AppServices.Current);
            return _monitor.Export(scrubber.Apply, AppInfo.Version);
        }
        catch (BugReportBuilder.PrivacyException ex)
        {
            error = $"Not exported: {ex.Message}";
            return null;
        }
    }

    public void Dispose()
    {
        _refresh.Stop();
        _monitor.Changed -= OnChanged;
    }
}
