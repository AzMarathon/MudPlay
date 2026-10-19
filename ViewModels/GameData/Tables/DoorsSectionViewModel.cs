using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Game.Map;
using MudPlay.Services;
using MudPlay.ViewModels.GameData.Edit;

namespace MudPlay.ViewModels.GameData.Tables;

// Game Data Browser → Doors tab. Every bare `(Key: N)` lock on the map, with what the
// client takes it for (BareKeyLockBook): a door or gate it may bash, a key exit it
// never bashes, or not known. Double-click a side to set its kind by hand or put it
// back to the seed; "Forget learned…" drops the active realm's whole record. Edits go
// to the realm's file (BareKeyLockStore) and reach routing and the door handler at
// once; the tab refreshes whenever the client learns something.
public sealed class DoorsSectionViewModel : GameDataTableSectionViewModel, IEditableTableSectionViewModel
{
    private readonly BareKeyLockBook _book;
    private readonly RoomGraphManager _graph;
    private readonly Func<int, string?> _itemName;
    private readonly Func<string?> _realmFolder;
    private readonly DialogService? _dialogs;
    private readonly Func<string, string, Task<bool>>? _confirm;
    private readonly Dictionary<GameDataRow, DoorsRowInfo> _rowToSide = new();
    private bool _reloadPosted;

    public override string Id => "doors";
    public override string Title => "Doors";

    public override IReadOnlyList<string> Columns { get; } = new[]
    {
        "Room", "Name", "Dir", "Key", "Kind", "From", "Answers", "Last answered",
    };

    public override bool ShowUseColumn => false;

    public override IEnumerable<string> SearchableLabels => new[]
    {
        Title, "door", "gate", "key", "bash", "lock", "key exit",
    };

    public override string? BannerText =>
        "Bare (Key: N) locks: the game data can't say whether each is a door or gate (bashable) or a key exit "
        + "(never bashed: there, bash <dir> is an attack). The shipped seed says for Stock's map; the game's own "
        + "answers on this realm overturn it after two agreeing ones. Double-click a side to set its kind yourself "
        + "or put it back to the seed. "
        + (_realmFolder() is { } folder
            ? $"Edits are kept for this realm ({System.IO.Path.GetFileName(folder)})."
            : "No realm is active: edits last until the client closes.")
        + (_note is { } note ? " " + note : "");

    // A passing word for the banner: "nothing learned to forget".
    private string? _note;

    public IAsyncRelayCommand<GameDataRow?> OpenEditAsyncCommand { get; }
    public IAsyncRelayCommand ForgetLearnedCommand { get; }

    ICommand IEditableTableSectionViewModel.OpenEditCommand => OpenEditAsyncCommand;
    ICommand? IEditableTableSectionViewModel.ExportCommand => ForgetLearnedCommand;
    string? IEditableTableSectionViewModel.ExportLabel => "Forget learned…";

    // confirm: (title, body) → whether the user agreed.
    public DoorsSectionViewModel(
        BareKeyLockBook book,
        RoomGraphManager graph,
        Func<int, string?> itemName,
        Func<string?> realmFolder,
        DialogService? dialogs = null,
        Func<string, string, Task<bool>>? confirm = null)
    {
        ArgumentNullException.ThrowIfNull(book);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(itemName);
        ArgumentNullException.ThrowIfNull(realmFolder);
        _book = book;
        _graph = graph;
        _itemName = itemName;
        _realmFolder = realmFolder;
        _dialogs = dialogs;
        _confirm = confirm;
        OpenEditAsyncCommand = new AsyncRelayCommand<GameDataRow?>(OpenEditAsync);
        ForgetLearnedCommand = new AsyncRelayCommand(ForgetLearnedAsync);
        _book.Changed += OnSourceChanged;
        _graph.GraphReloaded += OnSourceChanged;
        Reload();
    }

    // Another client on the realm may have changed the record since.
    public override void OnActivated() => _book.SyncWithRecord();

    public override void Dispose()
    {
        _book.Changed -= OnSourceChanged;
        _graph.GraphReloaded -= OnSourceChanged;
        base.Dispose();
    }

    // The book changes from the door handler and the store as well as from this tab:
    // one rebuild per burst, on the UI thread.
    private void OnSourceChanged()
    {
        if (_reloadPosted) return;
        _reloadPosted = true;
        Dispatcher.UIThread.Post(() =>
        {
            _reloadPosted = false;
            Reload();
            OnPropertyChanged(nameof(BannerText));
        });
    }

    protected override void PopulateRows(IList<GameDataRow> rows)
    {
        _rowToSide.Clear();
        foreach (DoorsRowInfo side in DoorsRowInfo.AllOn(_graph.Rooms.ToList(), _book, _itemName))
        {
            var cells = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["Room"] = side.Room.ToString(),
                ["Name"] = side.RoomName,
                ["Dir"] = side.Dir.ToString(),
                ["Key"] = side.KeyName,
                ["Kind"] = side.KindText,
                ["From"] = side.Source,
                ["Answers"] = side.Answers,
                ["Last answered"] = side.LastAnswered,
            };
            GameDataRow row = GameDataRow.FromDictionary(cells, Columns);
            _rowToSide[row] = side;
            rows.Add(row);
        }
    }

    private async Task OpenEditAsync(GameDataRow? row)
    {
        if (row is null || _dialogs is null || !_rowToSide.TryGetValue(row, out DoorsRowInfo? side)) return;
        DoorEdit? edit = await _dialogs.OpenWindowAsync<DoorEditDialogViewModel, DoorEdit>(new DoorEditDialogViewModel(side, _book));
        if (edit is not null) Apply(side, edit);
    }

    // Makes the dialog's edit to the side.
    public void Apply(DoorsRowInfo side, DoorEdit edit)
    {
        ArgumentNullException.ThrowIfNull(side);
        ArgumentNullException.ThrowIfNull(edit);
        if (edit.ResetToSeed) _book.ResetToSeed(side.Room, side.Dir);
        else if (edit.Kind is { } kind) _book.SetByUser(side.Room, side.Dir, kind);
    }

    private async Task ForgetLearnedAsync()
    {
        if (_book.LearnedCount == 0)
        {
            _note = "Nothing learned to forget for this realm.";
            OnPropertyChanged(nameof(BannerText));
            return;
        }
        _note = null;
        if (_confirm is not null && !await _confirm("Forget learned doors",
                $"Forget all {_book.LearnedCount} side(s) this realm has recorded — the game's answers and your own edits? "
                + "Every bare key lock goes back to what the seed says."))
            return;
        _book.ForgetLearned();
    }
}
