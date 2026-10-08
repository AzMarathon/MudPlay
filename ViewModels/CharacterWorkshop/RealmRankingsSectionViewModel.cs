using System;
using System.Collections.ObjectModel;
using System.Text.Json;
using Avalonia.Collections;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Game.Leaderboard;
using MudPlay.Services;
using MudPlay.Views.CharacterWorkshop;

namespace MudPlay.ViewModels.CharacterWorkshop;

// Record Keeping → Realm Rankings: the realm's top-N experience list as last
// captured, with an XP/HR column worked out between captures and the rank moves and
// reroll suspects since the one before.
public sealed partial class RealmRankingsSectionViewModel : WorkshopSectionViewModel
{
    public const string SectionId = "realmrankings";
    public const string SectionTitle = "Realm Rankings";
    public override string Id => SectionId;
    public override string Title => SectionTitle;

    private Control? _view;
    public override Control View => _view ??= new RealmRankingsSectionView { DataContext = this };

    private readonly GameDataCache _gameData;
    private readonly LeaderboardSnapshotStore _leaderboards;

    public RealmRankingsSectionViewModel(GameDataCache gameData, LeaderboardSnapshotStore leaderboards)
    {
        ArgumentNullException.ThrowIfNull(gameData);
        ArgumentNullException.ThrowIfNull(leaderboards);
        _gameData = gameData;
        _leaderboards = leaderboards;
        LeaderboardView = new DataGridCollectionView(Leaderboard);
        _leaderboards.Changed += OnLeaderboardChanged;
        EnsureClassFilterOptions();
        RebuildLeaderboard();
    }

    public override void Dispose() => _leaderboards.Changed -= OnLeaderboardChanged;

    // The latest capture's ranked rows with a derived XP/HR column. Fed by the
    // per-BBS LeaderboardSnapshotStore — every character on the board shares it —
    // and rebuilt whenever a fresh `top N` block is captured off the terminal.
    public ObservableCollection<LeaderboardRow> Leaderboard { get; } = new();

    // The grid binds this, not the raw collection: a DataGridCollectionView is what
    // makes the columns user-sortable (an ObservableCollection alone is inert to
    // the grid's sort). Each column's SortMemberPath points at a typed sort key on
    // LeaderboardRow so numbers order by magnitude, not formatted text.
    public DataGridCollectionView LeaderboardView { get; }

    // Raised after the rows are rebuilt so the view can size the grid's columns to
    // the whole capture. The DataGrid's Auto columns otherwise measure only the
    // realized rows and creep wider as more scroll into view.
    public event Action? LeaderboardRebuilt;

    // One-line capture context (when, how many shown vs asked, whole-list vs
    // truncated, snapshot count) or the empty-state hint.
    [ObservableProperty] private string _leaderboardStatus = string.Empty;

    // Reroll / dropout suspects since the previous capture (empty when none).
    [ObservableProperty] private string _leaderboardRerolls = string.Empty;

    // True once at least one capture exists — gates the table vs the empty state.
    [ObservableProperty] private bool _hasLeaderboard;

    // True when there are reroll/dropout notices to show.
    [ObservableProperty] private bool _hasLeaderboardNotices;

    // Class-filter choices for the leaderboard grid: a leading "No Filter"
    // sentinel followed by every class name from the active set. Populated once.
    public ObservableCollection<string> ClassFilterOptions { get; } = new();

    private const string NoClassFilter = "No Filter";

    // The chosen class filter. "No Filter" (the default) clears the filter; any
    // real class narrows the grid to rows of that class via the view's filter.
    [ObservableProperty] private string _selectedClassFilter = NoClassFilter;

    partial void OnSelectedClassFilterChanged(string value)
    {
        LeaderboardView.Filter = string.Equals(value, NoClassFilter, StringComparison.Ordinal)
            ? null
            : o => o is LeaderboardRow row
                   && string.Equals(row.Class, value, StringComparison.OrdinalIgnoreCase);
    }

    // Populate the class filter once from the active set, with the "No Filter"
    // sentinel first. Cheap to retry if the set wasn't loaded at construction.
    private void EnsureClassFilterOptions()
    {
        if (ClassFilterOptions.Count > 0) return;
        ClassFilterOptions.Add(NoClassFilter);
        JsonDocument? doc = _gameData.GetRawTable("Classes");
        if (doc is null) return;

        foreach (JsonElement row in doc.RootElement.EnumerateArray())
        {
            if (!row.TryGetProperty("Name", out JsonElement nameEl)) continue;
            if (nameEl.ValueKind != JsonValueKind.String) continue;
            string? name = nameEl.GetString();
            if (string.IsNullOrEmpty(name)) continue;
            ClassFilterOptions.Add(name);
        }
    }

    // Request a fresh listing at the DEEPEST depth we've ever captured (a 300-deep
    // list re-requests top 300), defaulting to 100 before the first capture. Reading
    // the depth from the widest stored capture — not just the newest — means a recent
    // small "top 10" view doesn't shrink the refresh back down. Asking at least 100
    // also keeps the request over-running a small board so its short reply reveals
    // the true cap (IsComplete). The reply is captured automatically off the
    // terminal, so the table refreshes on its own.
    [RelayCommand]
    private void ParseToplist()
    {
        int n = 100;
        foreach (LeaderboardSnapshot snap in _leaderboards.Snapshots)
        {
            if (snap.Entries.Count == 0) continue;
            n = Math.Max(n, Math.Max(snap.Entries.Count, snap.Entries[^1].Rank));
        }
        AppServices.Current.SendGameCommand($"top {n}");
    }

    // Wipe this BBS's capture history.
    [RelayCommand]
    private void ClearLeaderboard() => _leaderboards.Clear();

    private void OnLeaderboardChanged() => RebuildLeaderboard();

    private void RebuildLeaderboard()
    {
        LeaderboardReport report = LeaderboardXpRateCalculator.Build(_leaderboards.Snapshots);

        Leaderboard.Clear();
        foreach (LeaderboardRankRow r in report.Rows)
            Leaderboard.Add(LeaderboardRow.From(r));

        HasLeaderboard = report.Rows.Count > 0;

        if (report.CapturedAtUtc is { } at)
        {
            string asked = report.RequestedCount > 0 ? $" (asked top {report.RequestedCount})" : string.Empty;
            string scope = report.IsComplete ? "whole list visible" : "list may extend below";
            LeaderboardStatus =
                $"Last capture {at.ToLocalTime():yyyy-MM-dd HH:mm} · {report.ShownCount} shown{asked} · "
                + $"{scope} · {report.SnapshotCount} capture(s) stored";
        }
        else
        {
            LeaderboardStatus =
                "No captures yet. Press Parse Toplist (or run `top <N>` in-game) to capture the leaderboard.";
        }

        LeaderboardRerolls = report.Notices.Count > 0
            ? "Reroll suspects since last capture: " + string.Join(", ", report.Notices)
            : string.Empty;
        HasLeaderboardNotices = report.Notices.Count > 0;

        LeaderboardRebuilt?.Invoke();
    }
}
