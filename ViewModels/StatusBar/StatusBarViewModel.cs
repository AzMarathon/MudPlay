using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using Avalonia.Threading;
using MudPlay.Models.Settings;
using MudPlay.Services;

namespace MudPlay.ViewModels.StatusBar;

// The status bar under the terminal, built from the user's layout. The same class
// draws the Settings preview: there an item with nothing to show takes its sample
// text, so every item placed on a bar is visible while it is being arranged.
//
// Cost: items fed by the main window's own status properties update when those
// change; the rest are read on one half-second timer, and only the items actually
// on a bar are read. A marquee row adds one string slice per crawl step. Both
// timers stop when no row needs them.
public sealed class StatusBarViewModel : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan CrawlInterval = TimeSpan.FromMilliseconds(220);
    private static readonly TimeSpan PulseLength = TimeSpan.FromMilliseconds(200);
    private const string MarqueeSeparator = "   ·   ";

    private readonly MainWindowViewModel _main;
    private readonly bool _preview;
    private readonly DispatcherTimer _poll;
    private readonly DispatcherTimer _crawl;
    private readonly Dictionary<string, List<StatusBarItemViewModel>> _bySource = new(StringComparer.Ordinal);
    private readonly List<StatusBarItemViewModel> _polled = new();
    private readonly List<StatusBarItemViewModel> _tickItems = new();
    private string? _applied;
    private int _frame;
    private bool _disposed;

    public ObservableCollection<StatusBarRowViewModel> Rows { get; } = new();

    public StatusBarViewModel(MainWindowViewModel main, bool preview)
    {
        ArgumentNullException.ThrowIfNull(main);
        _main = main;
        _preview = preview;
        _poll = new DispatcherTimer(PollInterval, DispatcherPriority.Background, (_, _) => Poll());
        _crawl = new DispatcherTimer(CrawlInterval, DispatcherPriority.Background, (_, _) => Crawl());
        _main.PropertyChanged += OnMainPropertyChanged;
        if (!preview) AppServices.Current.Tick.CombatTickElapsed += OnCombatTick;
    }

    // Rebuild the rows from a layout. An entry naming an item this build doesn't
    // know (a layout saved by a newer one) is skipped.
    // True when the layout differed from the one showing and the rows were rebuilt.
    public bool Apply(StatusBarSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        // Every global-settings save lands here; only a changed layout rebuilds.
        string layout = JsonSerializer.Serialize(settings);
        if (layout == _applied) return false;
        _applied = layout;

        Rows.Clear();
        _bySource.Clear();
        _polled.Clear();
        _tickItems.Clear();

        foreach (StatusBarRow row in settings.Rows.Take(StatusBarSettings.MaxRows))
            Rows.Add(new StatusBarRowViewModel(Build(row.Left), Build(row.Center), Build(row.Right), row.Marquee));

        foreach (StatusBarItemViewModel item in Rows.SelectMany(r => r.Items)) Refresh(item);
        _poll.IsEnabled = _polled.Count > 0;
        _crawl.IsEnabled = Rows.Any(r => r.IsMarquee);
        Crawl();
        return true;
    }

    // "2 rows: engine location | target | tick connection; (marquee) profile hp" —
    // the layout in one line, for the program log.
    public string Describe() =>
        $"{Rows.Count} row{(Rows.Count == 1 ? string.Empty : "s")}: " + string.Join("; ", Rows.Select(r =>
            (r.IsMarquee ? "(marquee) " : string.Empty)
            + string.Join(" | ", new[] { r.Left, r.Center, r.Right }
                .Select(zone => string.Join(' ', zone.Select(i => i.Def.Id))))));

    private List<StatusBarItemViewModel> Build(List<StatusBarEntry> entries)
    {
        List<StatusBarItemViewModel> items = new(entries.Count);
        foreach (StatusBarEntry entry in entries)
        {
            if (StatusBarItemCatalogue.Find(entry.Item) is not { } def) continue;
            StatusBarItemViewModel item = new(def, _main, entry.Text);
            items.Add(item);

            if (StatusBarValues.SourceProperty(def.Id) is { } property)
            {
                Watch(property, item);
                // The reconnect countdown is part of the connection item's text.
                if (def.Id == "connection") Watch(nameof(MainWindowViewModel.ReconnectCountdownText), item);
            }
            else _polled.Add(item);
            if (def.Id == "tick") _tickItems.Add(item);
        }
        return items;
    }

    private void Watch(string property, StatusBarItemViewModel item)
    {
        if (!_bySource.TryGetValue(property, out List<StatusBarItemViewModel>? list))
            _bySource[property] = list = new List<StatusBarItemViewModel>();
        list.Add(item);
    }

    private string ValueOf(string id)
    {
        string value = StatusBarValues.Read(id, _main);
        return _preview && value.Length == 0 ? StatusBarItemCatalogue.Find(id)?.Sample ?? string.Empty : value;
    }

    private void Refresh(StatusBarItemViewModel item)
    {
        string text = item.Def.Kind == StatusBarItemKind.CustomText
            ? StatusBarItemCatalogue.ExpandTokens(item.Template, ValueOf)
            : ValueOf(item.Def.Id);
        if (_preview && item.Def.Kind == StatusBarItemKind.CustomText && string.IsNullOrWhiteSpace(text))
            text = item.Def.Sample;
        item.Text = text;
        item.IsShown = item.Def.Kind switch
        {
            StatusBarItemKind.EngineChip or StatusBarItemKind.ConnectionLight => true,
            StatusBarItemKind.StatlineWarning => _preview || _main.IsStatlineMismatchVisible,
            _ => !string.IsNullOrWhiteSpace(text),
        };
    }

    private void Poll()
    {
        foreach (StatusBarItemViewModel item in _polled) Refresh(item);
    }

    private void OnMainPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is { } name && _bySource.TryGetValue(name, out List<StatusBarItemViewModel>? items))
            foreach (StatusBarItemViewModel item in items) Refresh(item);
    }

    private void Crawl()
    {
        _frame = _frame == int.MaxValue ? 0 : _frame + 1;
        foreach (StatusBarRowViewModel row in Rows)
        {
            if (!row.IsMarquee) continue;
            string line = string.Join(MarqueeSeparator, row.Items.Where(i => i.IsText && i.IsShown).Select(i => i.Text));
            row.MarqueeText = StatusBarMarquee.Frame(line, _frame);
        }
    }

    // The tick engine raises this off the UI thread.
    private void OnCombatTick() => Dispatcher.UIThread.Post(() =>
    {
        if (_disposed || _tickItems.Count == 0) return;
        foreach (StatusBarItemViewModel item in _tickItems) item.IsPulsing = true;
        DispatcherTimer.RunOnce(() =>
        {
            foreach (StatusBarItemViewModel item in _tickItems) item.IsPulsing = false;
        }, PulseLength);
    });

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _poll.Stop();
        _crawl.Stop();
        _main.PropertyChanged -= OnMainPropertyChanged;
        if (!_preview) AppServices.Current.Tick.CombatTickElapsed -= OnCombatTick;
    }
}
