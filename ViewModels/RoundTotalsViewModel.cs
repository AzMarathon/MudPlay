using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MudPlay.Game.Combat;
using MudPlay.Models.Profile;
using MudPlay.Services;

namespace MudPlay.ViewModels;

// The Round Totals window: the latest round's damage table, drawn in fixed-width
// type, with its own choice of rows (kept apart from the terminal's table). The
// board says how many rows and how wide a name column to hold; rows the round
// didn't fill are left blank, so the window keeps its size from round to round
// instead of following every change in the room.
public sealed partial class RoundTotalsViewModel : ObservableObject, IDisposable
{
    private readonly RoundTotalsBoard _board;
    private readonly ProfileService _profile;
    private bool _loading;

    [ObservableProperty] private string _roundText = string.Empty;
    [ObservableProperty] private string _header = string.Empty;

    // False until the first round of combat: the window shows a waiting line.
    [ObservableProperty] private bool _hasRound;

    public ObservableCollection<RoundTotalsLineViewModel> Lines { get; } = new();

    // ----- The window's own options (its Rows menu) --------------------
    [ObservableProperty] private bool _showSelf = true;
    [ObservableProperty] private bool _showParty = true;
    [ObservableProperty] private bool _showPlayers = true;
    [ObservableProperty] private bool _showMonsters = true;
    [ObservableProperty] private bool _eachMonster;

    public RoundTotalsViewModel(RoundTotalsBoard board, ProfileService profile)
    {
        ArgumentNullException.ThrowIfNull(board);
        ArgumentNullException.ThrowIfNull(profile);
        _board = board;
        _profile = profile;
        _board.Changed += Refresh;
        _profile.ProfileLoaded += OnProfileLoaded;
        LoadOptions();
        Refresh();
    }

    private void OnProfileLoaded(CharacterProfile _) => LoadOptions();

    private void LoadOptions()
    {
        RoundTotalsWindowSettings saved = new();
        if (_profile.Current?.Settings is { } all
            && all.TryGetValue(RoundTotalsWindowSettings.SectionKey, out System.Text.Json.JsonElement json))
        {
            try
            {
                saved = System.Text.Json.JsonSerializer.Deserialize<RoundTotalsWindowSettings>(json) ?? saved;
            }
            catch (System.Text.Json.JsonException)
            {
                // A malformed entry shows every row rather than none.
            }
        }
        _loading = true;
        ShowSelf = saved.ShowSelf;
        ShowParty = saved.ShowParty;
        ShowPlayers = saved.ShowPlayers;
        ShowMonsters = saved.ShowMonsters;
        EachMonster = saved.EachMonster;
        _loading = false;
    }

    // An option flipped in the Rows menu: save it for this character and redraw the
    // round that is showing.
    private void SaveOptions()
    {
        if (_loading) return;
        _profile.UpdateSection<RoundTotalsWindowSettings>(RoundTotalsWindowSettings.SectionKey, s =>
        {
            s.ShowSelf = ShowSelf;
            s.ShowParty = ShowParty;
            s.ShowPlayers = ShowPlayers;
            s.ShowMonsters = ShowMonsters;
            s.EachMonster = EachMonster;
        });
        _board.OptionsChanged();
    }

    partial void OnShowSelfChanged(bool value) => SaveOptions();
    partial void OnShowPartyChanged(bool value) => SaveOptions();
    partial void OnShowPlayersChanged(bool value) => SaveOptions();
    partial void OnShowMonstersChanged(bool value) => SaveOptions();
    partial void OnEachMonsterChanged(bool value) => SaveOptions();

    private void Refresh()
    {
        HasRound = _board.HasRound;
        Lines.Clear();
        if (!_board.HasRound)
        {
            RoundText = "Round totals";
            Header = string.Empty;
            return;
        }

        int width = _board.NameWidth;
        RoundText = $"Round {_board.Round}";
        Header = RoundTotalsFormatter.HeaderColumns(width);
        foreach (RoundTotalsRow row in _board.Rows)
            Lines.Add(new RoundTotalsLineViewModel(
                RoundTotalsFormatter.Columns(row.Name, row.Dealt.ToString(), row.Taken.ToString(), width),
                row.Kind == CombatantKind.Self));
        // Blank lines of the same width hold the room for a fuller round.
        string blank = RoundTotalsFormatter.Columns(string.Empty, string.Empty, string.Empty, width);
        for (int i = _board.Rows.Count; i < _board.RowSlots; i++)
            Lines.Add(new RoundTotalsLineViewModel(blank, IsSelf: false));
    }

    public void Dispose()
    {
        _board.Changed -= Refresh;
        _profile.ProfileLoaded -= OnProfileLoaded;
    }
}
