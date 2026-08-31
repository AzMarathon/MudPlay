using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Services;
using MudPlay.Views.GameData.Tables;

namespace MudPlay.ViewModels.GameData.Tables;

// Shared, source-agnostic base for every per-table tab in the Game Data Browser. Owns the
// column list, the all-rows / filtered-rows observable pair, the selected-row slot, the
// search box, and the DataGrid view. Subclasses supply rows via PopulateRows — JSON-backed
// tabs pull from GameDataCache (see JsonTableSectionViewModel), engine-backed tabs
// (Triggers / Aliases / Players / Macros / Messages) pull from their runtime services.
public abstract partial class GameDataTableSectionViewModel : GameDataSectionViewModel
{
    // Trailing virtual column name shown on every grid — see GameDataRow.SourceTier.
    public const string UseColumnName = "Use";

    private Control? _view;

    // Data columns in display order. Search hits, sort, and the right-pane row view all key
    // off this list. The virtual UseColumnName tier column gets appended automatically by
    // DisplayColumns.
    public abstract IReadOnlyList<string> Columns { get; }

    // Columns rendered in the DataGrid: data columns + the trailing "Use" tier column (when
    // applicable). The view's column builder reads from this.
    public IReadOnlyList<string> DisplayColumns => ShowUseColumn
        ? Columns.Concat(new[] { UseColumnName }).ToArray()
        : Columns;

    // True when the trailing virtual "Use" tier column should render. MDB-overlay tables
    // (Monsters / Items / Spells / Messages) keep it — the tier badge tells the user which
    // layer owns each row. Engine-backed tables (Macros / Triggers / Aliases / Players) hide
    // it: every row lives at one tier so the badge would always read the same value and just
    // adds visual noise.
    public virtual bool ShowUseColumn => true;

    // Optional friendly grid-header overrides, keyed by column name in Columns. When a column
    // is present here the DataGrid renders the mapped label as its header while the column
    // name still drives the value binding, search, sort, and formatters. Lets a table show
    // "AC" / "HP Regen" while keeping the raw MDB keys (ArmourClass / HPRegen) as the data
    // identity. Columns absent from the map fall back to their raw name.
    public virtual IReadOnlyDictionary<string, string>? ColumnHeaders => null;

    // Column the search box filters against by default (kept for status-bar display only).
    public abstract string SearchKeyColumn { get; }

    // Optional tooltip for the row-filter box — a section overrides it to advertise a
    // richer query it accepts (e.g. Items' auto-* / stash flag keywords). Null = no tip.
    public virtual string? FilterHint => null;

    // Optional muted-info banner shown directly under the tab header — used by sections that
    // need a one-liner note for the user (e.g. Aliases: "fires from the Conversation window's
    // input field only"). null hides the banner row.
    public virtual string? BannerText => null;

    // Optional per-column display formatters. Keys are column names in Columns; values
    // transform the raw cell string into the human-readable form rendered in the grid
    // (e.g. 1 → "Weapon", 5 → "Feet"). Subclasses opt in by overriding; the search filter
    // still runs against the raw value so numeric codes are findable both ways.
    protected virtual IReadOnlyDictionary<string, Func<string?, string?>>? ColumnFormatters => null;

    // Every row loaded from the source, original order. Set in one shot on Reload rather than
    // appended row-by-row so the DataGrid sees a single PropertyChanged + re-bind instead of N
    // CollectionChanged events. Critical at 27k-row table sizes (Rooms in MajorMUD v1.11p),
    // where per-row notifications were the hot loop.
    private ObservableCollection<GameDataRow> _allRows = new();
    public ObservableCollection<GameDataRow> AllRows
    {
        get => _allRows;
        private set => SetProperty(ref _allRows, value);
    }

    // Rows that survive the current SearchText filter.
    private ObservableCollection<GameDataRow> _filteredRows = new();
    public ObservableCollection<GameDataRow> FilteredRows
    {
        get => _filteredRows;
        private set => SetProperty(ref _filteredRows, value);
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private GameDataRow? _selectedRow;

    // Live mirror of the DataGrid's multi-selection set. The view's code-behind syncs this
    // whenever RowsGrid.SelectionChanged fires (Avalonia's DataGrid exposes SelectedItems as a
    // non-bindable IList, so the sync is imperative). RemoveSelected handlers iterate this set
    // instead of just SelectedRow so Ctrl-/Shift-selecting multiple rows + clicking Remove
    // drops them all in one go.
    public System.Collections.ObjectModel.ObservableCollection<GameDataRow> SelectedRows { get; } = new();

    [ObservableProperty] private string _searchText = string.Empty;

    public override Control View => _view ??= new GameDataTableSectionView { DataContext = this };

    // Bottom-strip status. Shows the 1-based position of the selected row out of the table
    // total when something is selected ("5 of 240 rows"); otherwise just the row count, with
    // the filtered / unfiltered split ("3 / 240 rows" when a search filter is active,
    // "240 rows" otherwise).
    public string StatusText
    {
        get
        {
            int total = AllRows.Count;
            int visible = FilteredRows.Count;

            if (SelectedRow is not null)
            {
                int index = FilteredRows.IndexOf(SelectedRow);
                if (index >= 0) return $"{index + 1} of {total} rows";
            }

            return total == visible ? $"{total} rows" : $"{visible} / {total} rows";
        }
    }

    // Subclass hook: append every visible row to rows. Called on the first activation (see
    // OnActivated) and on every Reload trigger. Uses IList (not ObservableCollection) so the
    // row-build can run against a plain List — caller wraps the finished list in a fresh
    // ObservableCollection once for the bulk-replace.
    protected abstract void PopulateRows(IList<GameDataRow> rows);

    // Extra columns to carry in each row's value/display maps but NOT render as grid
    // columns — used for filter-only fields (e.g. Monsters' Alignment dropdown reads
    // "Align" without the table showing an Alignment column, and the AC / DR filters
    // read the raw fields even though the grid shows them combined). The grid still
    // builds its columns from Columns alone; these are appended after them so the
    // visible columns keep their cell indices (the sort comparer is index-based).
    protected virtual IReadOnlyList<string> FilterOnlyColumns => System.Array.Empty<string>();

    // Columns whose values are materialised on each row = visible columns plus any
    // filter-only ones. Cached; visible columns come first so their indices are stable.
    private IReadOnlyList<string>? _valueColumns;
    protected IReadOnlyList<string> ValueColumns => _valueColumns ??=
        FilterOnlyColumns.Count == 0
            ? Columns
            : System.Linq.Enumerable.ToList(System.Linq.Enumerable.Concat(Columns, FilterOnlyColumns));

    // Called on the UI thread once AllRows is materialised (both Reload and the async
    // LoadAsync path). Subclasses rebuild data-derived UI state here — e.g. category-filter
    // option lists computed from the loaded rows. PopulateRows itself runs on a worker thread
    // under LoadAsync, so it must never touch observable collections; this hook is where that
    // work belongs.
    protected virtual void OnRowsLoaded() { }

    // Called by GameDataBrowserViewModel whenever this section becomes the selected one. Lets
    // expensive sections (10k+ rows of MDB-derived JSON) defer their parse + row-build work
    // until the user actually opens the tab. Base implementation is a no-op;
    // JsonTableSectionViewModel overrides to trigger the first load.
    public virtual void OnActivated() { }

    // True once Reload or LoadAsync has completed at least once. Subclasses that re-react to
    // source changes (set switch, profile reload) check this to skip work for cold tabs the
    // user has never opened.
    protected bool IsLoaded { get; private set; }

    // Rebuild AllRows + FilteredRows from the source and reapply the filter. Builds a fresh
    // List via PopulateRows, wraps it in a new ObservableCollection, and assigns to the
    // properties — one PropertyChanged each instead of N CollectionChanged events. Subclasses
    // call this when their source changes (set switch, engine CollectionChanged, profile
    // reload).
    protected void Reload()
    {
        SelectedRow = null;
        List<GameDataRow> rows = new();
        PopulateRows(rows);
        AllRows = new ObservableCollection<GameDataRow>(rows);
        OnRowsLoaded();
        ApplyFilter();
        IsLoaded = true;
        OnPropertyChanged(nameof(StatusText));
    }

    // Async variant of Reload: row-build runs on a worker thread, then bulk-replace AllRows
    // back on the UI thread once the parse is done. Keeps the UI responsive on big tables
    // (Rooms reaches 27k+ rows in MajorMUD v1.11p, more on custom-edit realms).
    // JsonTableSectionViewModel.OnActivated fires this through the dispatcher post; tests can
    // await it directly for deterministic completion.
    internal async Task LoadAsync()
    {
        List<GameDataRow> rows = await Task.Run(() =>
        {
            List<GameDataRow> list = new();
            PopulateRows(list);
            return list;
        });
        // Task.Run resumes the continuation on the captured
        // SynchronizationContext (Avalonia dispatcher in app context,
        // none in tests — either way the property writes happen on a
        // single thread per call).
        SelectedRow = null;
        AllRows = new ObservableCollection<GameDataRow>(rows);
        OnRowsLoaded();
        ApplyFilter();
        IsLoaded = true;
        OnPropertyChanged(nameof(StatusText));

        // Cross-section navigation might have queued a selection while
        // rows were still loading (Shops → Rooms double-click hits this
        // path). Apply it now that AllRows is materialised.
        if (_pendingRowSelector is { } selector)
        {
            _pendingRowSelector = null;
            ApplyRowSelector(selector);
        }
    }

    // Pending row predicate from a cross-section navigation request that arrived before this
    // section had loaded its rows. Cleared after LoadAsync applies it.
    private Func<GameDataRow, bool>? _pendingRowSelector;

    // Select the first row matching predicate. Queues the predicate when rows haven't loaded
    // yet — the selection applies as soon as LoadAsync finishes.
    public void SelectRowMatching(Func<GameDataRow, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        if (!IsLoaded || AllRows.Count == 0)
        {
            _pendingRowSelector = predicate;
            return;
        }
        ApplyRowSelector(predicate);
    }

    // Fired after a programmatic SelectRowMatching lands on a row. The view subscribes to
    // bring the row into view (Avalonia DataGrid doesn't auto-scroll on SelectedItem source
    // changes — we have to call ScrollIntoView explicitly).
    public event Action<GameDataRow>? ScrollToRowRequested;

    private void ApplyRowSelector(Func<GameDataRow, bool> predicate)
    {
        // Prefer matches that survive the current filter; fall back to
        // the full row set when none do so the navigation never lands
        // on "no selection" with the row sitting just past the filter.
        // When a match is found in AllRows but the filter is hiding it,
        // clear the filter so the user actually sees the selected row.
        foreach (GameDataRow row in FilteredRows)
        {
            if (predicate(row))
            {
                SelectedRow = row;
                ScrollToRowRequested?.Invoke(row);
                return;
            }
        }
        foreach (GameDataRow row in AllRows)
        {
            if (predicate(row))
            {
                // Filter is hiding the row — clear it so the target is
                // visible. The selection latches on the now-unfiltered
                // row set.
                if (!string.IsNullOrEmpty(SearchText)) SearchText = string.Empty;
                SelectedRow = row;
                ScrollToRowRequested?.Invoke(row);
                return;
            }
        }
    }

    partial void OnSearchTextChanged(string value)
    {
        ApplyFilter();
        OnPropertyChanged(nameof(StatusText));
    }

    // Callable by subclasses so a richer filter surface (e.g. the Monsters
    // multi-field panel) can re-filter when one of its fields changes.
    protected void ApplyFilter()
    {
        string filter = (SearchText ?? string.Empty).Trim();
        if (filter.Length == 0 && !HasExtraFilter)
        {
            // Unfiltered — alias the AllRows collection directly so the
            // DataGrid sees identical content with zero extra allocation.
            // Both collections are treated as immutable snapshots between
            // Reloads, so sharing is safe.
            FilteredRows = AllRows;
            return;
        }

        List<GameDataRow> matched = new();
        foreach (GameDataRow row in AllRows)
        {
            if (RowMatches(row, filter))
                matched.Add(row);
        }
        FilteredRows = new ObservableCollection<GameDataRow>(matched);
    }

    // ----- Curation filter panel (subclasses populate FilterGroups; empty = no panel) -----
    // A LIVE sidebar beside the grid: min/max ranges, checkboxes, and dropdowns,
    // grouped into labelled sections. Editing any control re-filters immediately —
    // there is no Apply step. Distinct from the always-live "Filter…" text box: the
    // panel CURATES the list (which rows qualify); the text box FINDS a specific one
    // within it. Empty by default, so tables that declare no groups render no
    // sidebar. Subclasses build FilterGroups once and call WireLiveFilters.
    public ObservableCollection<FilterGroup> FilterGroups { get; } = new();
    public bool HasFilterPanel => FilterGroups.Count > 0;

    private IEnumerable<RangeFilter> AllRangeFilters => FilterGroups.SelectMany(g => g.Ranges);
    private IEnumerable<BoolFilter> AllBoolFilters => FilterGroups.SelectMany(g => g.Bools);
    private IEnumerable<CategoryFilter> AllCategoryFilters => FilterGroups.SelectMany(g => g.Categories);

    // Suppresses the per-filter live re-filter during a bulk change (Reset), so the
    // whole panel re-filters once at the end rather than once per cleared box.
    private bool _suppressLiveFilter;

    // Subscribe every panel filter's change to a live re-filter. Called by the
    // subclass after it has populated FilterGroups.
    protected void WireLiveFilters()
    {
        foreach (RangeFilter r in AllRangeFilters) r.PropertyChanged += OnPanelFilterChanged;
        foreach (BoolFilter b in AllBoolFilters) b.PropertyChanged += OnPanelFilterChanged;
        foreach (CategoryFilter c in AllCategoryFilters) c.PropertyChanged += OnPanelFilterChanged;
        OnPropertyChanged(nameof(HasFilterPanel));
    }

    private void OnPanelFilterChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_suppressLiveFilter) return;
        ApplyFilter();
        OnPropertyChanged(nameof(StatusText));
    }

    // Drives ApplyFilter's "run even with an empty text box" path (see ApplyFilter).
    protected bool HasExtraFilter
    {
        get
        {
            foreach (RangeFilter r in AllRangeFilters) if (r.IsActive) return true;
            foreach (BoolFilter b in AllBoolFilters) if (b.IsActive) return true;
            foreach (CategoryFilter c in AllCategoryFilters) if (c.IsActive) return true;
            return false;
        }
    }

    // Empties every panel filter (and the text box) and re-filters once.
    [RelayCommand]
    private void ResetFilters()
    {
        _suppressLiveFilter = true;
        foreach (RangeFilter r in AllRangeFilters) r.Clear();
        foreach (BoolFilter b in AllBoolFilters) b.Clear();
        foreach (CategoryFilter c in AllCategoryFilters) c.Clear();
        _suppressLiveFilter = false;
        SearchText = string.Empty;   // OnSearchTextChanged re-applies (no-op if already empty)
        ApplyFilter();
        OnPropertyChanged(nameof(StatusText));
    }

    // Panel filters (range + bool + category), all AND'd together. Empty groups pass
    // everything, so non-panel tables are unaffected. Range filters test the leading
    // integer of the raw cell; bool filters test the raw value via their predicate;
    // category filters match the rendered display value.
    private bool PassesPanelFilters(GameDataRow row)
    {
        foreach (RangeFilter r in AllRangeFilters)
            if (r.IsActive && (!TryLeadingInt(row.Get(r.Column), out int v) || !r.Passes(v)))
                return false;
        foreach (BoolFilter b in AllBoolFilters)
            if (b.IsActive && !b.Passes(row.Get(b.Column)))
                return false;
        foreach (CategoryFilter c in AllCategoryFilters)
            if (c.IsActive && !c.Passes(row.GetDisplay(c.Column)))
                return false;
        return true;
    }

    // A row matches the text filter when any column's raw value contains the substring
    // (case-insensitive), so numeric codes are findable even when the grid renders them
    // via a formatter. Virtual so a tab with a richer match (Rooms' "map,room" query)
    // can intercept before this substring pass.

    // Leading signed integer of a cell string — "12345" → 12345, "2hp@90s" → 2,
    // "10/42/8" → 10, "1–11" → 1. False when there's no leading number.
    private static bool TryLeadingInt(string? s, out int value)
    {
        value = 0;
        if (string.IsNullOrEmpty(s)) return false;
        int i = s[0] == '-' ? 1 : 0;
        int start = i;
        while (i < s.Length && s[i] is >= '0' and <= '9') i++;
        if (i == start) return false;
        if (!int.TryParse(s.AsSpan(start, i - start), out value)) return false;
        if (s[0] == '-') value = -value;
        return true;
    }

    protected virtual bool RowMatches(GameDataRow row, string filter)
    {
        if (!PassesPanelFilters(row)) return false;
        // Empty text box + active panel: the panel alone decides the match.
        if (filter.Length == 0) return true;
        foreach (string column in Columns)
        {
            string? value = row.Get(column);
            if (value is not null && value.Contains(filter, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        // Also match against the Use-tier short label so the user can
        // filter by tier (e.g. typing "Char" surfaces every overridden row).
        return row.SourceTier.ToShortLabel().Contains(filter, StringComparison.OrdinalIgnoreCase);
    }
}

// Concrete base for MDB-derived tabs. Loads its rows from GameDataCache's active set on
// construction and on every GameDataCache.ActiveSetChanged. Subclasses supply TableName +
// Columns + SearchKeyColumn.
public abstract class JsonTableSectionViewModel : GameDataTableSectionViewModel
{
    private readonly GameDataCache _cache;
    private readonly SettingsResolver? _resolver;

    // JSON-backed sections all belong in the browser's MDB-derived tables group.
    public override bool ShowInTableGroup => true;

    // Underlying table name in the active set (e.g. "Monsters").
    protected abstract string TableName { get; }

    // Row-level filter applied while populating from the raw table. Default keeps every
    // row; a derived view (e.g. Unobtainable = Items with In Game == 0) overrides to
    // include only the rows it wants without duplicating the whole populate loop.
    protected virtual bool IncludeRow(JsonElement element) => true;

    // Column whose value identifies the record for tier-override lookup (default: the
    // primary-key column, typically "Number" on MajorMUD MDB tables). Subclasses can override
    // if the table's natural key isn't "Number".
    protected virtual string OverrideKeyColumn => "Number";

    // Stored as a field so Dispose can unsubscribe — without this the
    // GameDataCache singleton's event roots every JsonTableSectionViewModel
    // ever created (leaking section VMs + their cached row collections +
    // their lazy-built Views across every browser open).
    private readonly Action<string?> _activeSetHandler;

    protected JsonTableSectionViewModel(GameDataCache cache, SettingsResolver? resolver = null)
    {
        ArgumentNullException.ThrowIfNull(cache);
        _cache = cache;
        _resolver = resolver;
        // ActiveSetChanged invalidates whatever was loaded — but we only
        // re-parse if the tab has already been opened. Tabs that have never
        // been activated stay un-loaded until first activation, dodging the
        // upfront 10-tables-times-thousands-of-rows parse on browser open.
        _activeSetHandler = _ =>
        {
            if (IsLoaded) Reload();
        };
        _cache.ActiveSetChanged += _activeSetHandler;
        // NOTE: ctor does NOT call Reload() — that's lazy via OnActivated.
    }

    public override void Dispose()
    {
        _cache.ActiveSetChanged -= _activeSetHandler;
        base.Dispose();
    }

    public override void OnActivated()
    {
        if (IsLoaded) return;
        // Defer to the next dispatcher tick so the parse runs *after* the
        // ContentControl constructs our View and the DataGrid builds its
        // columns (DataContextChanged handler in code-behind). Without
        // the defer, rows would arrive on a 0-column grid and never
        // materialise — the tab would render blank on first activation.
        // LoadAsync flips IsLoaded once the rows land.
        Dispatcher.UIThread.Post(() => _ = LoadAsync());
    }

    protected override void PopulateRows(IList<GameDataRow> rows)
    {
        JsonDocument? doc = _cache.GetRawTable(TableName);
        if (doc is null) return;

        IReadOnlyDictionary<string, Func<string?, string?>>? formatters = ColumnFormatters;
        foreach (JsonElement el in doc.RootElement.EnumerateArray())
        {
            if (!IncludeRow(el)) continue;
            // Sections may inject synthesised cells that aren't backed
            // by a real MDB field (e.g. Races / Classes synthesise an
            // "Abilities" column from Abil-N / AbilVal-N pairs).
            IReadOnlyDictionary<string, string?>? computed = ComputeRowCells(el);
            GameDataRow row = GameDataRow.FromJson(el, ValueColumns, formatters, computed);
            // Per-row tier resolution: look up the record by its primary
            // key column value (typically Number) and ask the resolver
            // which tier owns the highest-priority override, if any.
            if (_resolver is not null)
            {
                string? key = row.Get(OverrideKeyColumn);
                if (!string.IsNullOrEmpty(key))
                    row.SourceTier = _resolver.GetGameDataSourceTier(TableName, key);
            }
            rows.Add(row);
        }
    }

    // Optional per-row computed-cell hook. Returned values are merged into the row, taking
    // precedence over any same-named raw JSON cell. Use this for columns that aren't backed by
    // a real MDB field — e.g. the Race / Class tabs synthesise an "Abilities" column from each
    // row's Abil-N / AbilVal-N pairs. Default implementation returns null (no extras).
    protected virtual IReadOnlyDictionary<string, string?>? ComputeRowCells(JsonElement element) => null;
}

// One row loaded from a game-data source. Holds the column-name → string-rendered-value
// dictionary. Numbers / nulls / nested objects are all collapsed to strings at parse time so
// the view only has to deal with one shape.
public sealed class GameDataRow
{
    private readonly IReadOnlyDictionary<string, string?> _values;

    // Data cells in display order; the trailing "Use" virtual cell is appended by the view.
    public IReadOnlyList<GameDataCell> Cells { get; }

    // Highest-priority tier that owns this record. Drives the Game Data Browser's "Use" column
    // label and the edit dialog's "Use:" dropdown initial value.
    public SettingsTier SourceTier { get; set; } = SettingsTier.Defaults;

    // Short tier label rendered in the virtual "Use" column.
    public string UseLabel => SourceTier.ToShortLabel();

    // Opaque per-section payload — used by sections (e.g. Messages) that need a direct handle
    // back to the source record after the user double-clicks. Lets the section avoid the
    // Id-from-cells dance when display columns don't include the identity fields (the Messages
    // table shows "Lines/Preview" summaries, not the raw message text the Id is hashed from).
    public object? Tag { get; set; }

    private GameDataRow(IReadOnlyDictionary<string, string?> values, IReadOnlyList<GameDataCell> cells)
    {
        _values = values;
        Cells = cells;
    }

    // Read a column value by name. Returns null if the column wasn't in the source row.
    public string? Get(string column)
        => _values.TryGetValue(column, out string? value) ? value : null;

    // Read a column's *display* value (formatter-applied), as shown in the grid.
    // Category filters match on this so their dropdowns show "Living"/"Undead" or
    // "Lawful Good" rather than the raw MDB codes; range filters use Get (raw
    // numeric) instead. Falls back to the raw value when the column has no cell.
    public string? GetDisplay(string column)
    {
        foreach (GameDataCell cell in Cells)
            if (string.Equals(cell.Column, column, StringComparison.OrdinalIgnoreCase))
                return cell.Value;
        return Get(column);
    }

    // Build a row from a JSON element. Columns missing from the source render as null in the
    // resulting row so subclasses see a uniform shape regardless of schema drift. The raw cell
    // value drives Get (so search/filter sees the underlying data) while the optional
    // formatters map shapes the displayed value in Cells.
    public static GameDataRow FromJson(
        JsonElement element,
        IReadOnlyList<string> columns,
        IReadOnlyDictionary<string, Func<string?, string?>>? formatters = null,
        IReadOnlyDictionary<string, string?>? computedCells = null)
    {
        Dictionary<string, string?> values = new(StringComparer.OrdinalIgnoreCase);
        List<GameDataCell> cells = new(columns.Count);

        foreach (string column in columns)
        {
            // Computed cells take precedence over raw JSON when present —
            // sections use this to surface synthesised columns ("Abilities")
            // that aren't backed by a real MDB field.
            string? raw = computedCells is not null
                          && computedCells.TryGetValue(column, out string? computed)
                ? computed
                : ReadValue(element, column);
            values[column] = raw;
            string? display = (formatters is not null && formatters.TryGetValue(column, out Func<string?, string?>? fmt))
                ? fmt(raw)
                : raw;
            cells.Add(new GameDataCell(column, display));
        }
        return new GameDataRow(values, cells);
    }

    // Build a row from an arbitrary column-name → raw-value dictionary (engine-backed tabs
    // that don't have an MDB JSON source). The same formatter contract as FromJson applies —
    // formatted strings render in the grid, raw strings drive search.
    public static GameDataRow FromDictionary(
        IReadOnlyDictionary<string, string?> source,
        IReadOnlyList<string> columns,
        IReadOnlyDictionary<string, Func<string?, string?>>? formatters = null)
    {
        Dictionary<string, string?> values = new(StringComparer.OrdinalIgnoreCase);
        List<GameDataCell> cells = new(columns.Count);

        foreach (string column in columns)
        {
            source.TryGetValue(column, out string? raw);
            values[column] = raw;
            string? display = (formatters is not null && formatters.TryGetValue(column, out Func<string?, string?>? fmt))
                ? fmt(raw)
                : raw;
            cells.Add(new GameDataCell(column, display));
        }
        return new GameDataRow(values, cells);
    }

    private static string? ReadValue(JsonElement row, string column)
    {
        if (!row.TryGetProperty(column, out JsonElement el)) return null;
        return el.ValueKind switch
        {
            JsonValueKind.Null      => null,
            JsonValueKind.Undefined => null,
            JsonValueKind.String    => el.GetString(),
            JsonValueKind.Number    => el.ToString(),
            JsonValueKind.True      => "true",
            JsonValueKind.False     => "false",
            _                        => el.ToString(),
        };
    }
}

// One column / value pair on a GameDataRow.
public sealed record GameDataCell(string Column, string? Value);
