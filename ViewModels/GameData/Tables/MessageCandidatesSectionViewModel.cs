using System.Collections.Generic;
using System.Collections.Specialized;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Models.GameData;
using MudPlay.Services;
using MudPlay.ViewModels.GameData.Edit;

namespace MudPlay.ViewModels.GameData.Tables;

// Game Data Browser → Candidates tab. Surfaces MessageCandidateStore's staged,
// unrecognized wire lines for batch review — the same records the LogPane's
// "double-click to review" flow (App.axaml.cs) resolves one at a time as they
// arrive. Both surfaces commit through the shared MessageCandidateCommit
// helper so neither duplicates the seed/commit logic.
public sealed class MessageCandidatesSectionViewModel : GameDataTableSectionViewModel, IEditableTableSectionViewModel
{
    private readonly MessageCandidateStore _candidates;
    private readonly MessageStore _messages;
    private readonly DialogService? _dialogs;
    private readonly GameDataCache? _cache;

    public override string Id => "message-candidates";
    public override string Title => "Candidates";

    public override IReadOnlyList<string> Columns { get; } = new[]
    {
        "Raw Text", "Occurrences", "First Seen", "Last Seen", "Status",
    };

    public override string SearchKeyColumn => "Raw Text";

    public override IEnumerable<string> SearchableLabels => new[]
    {
        Title, "unrecognized", "candidate", "message",
    };

    // Open the same edit dialog the Messages tab uses, pre-seeded with the raw text.
    public IRelayCommand<GameDataRow?> OpenEditAsyncCommand { get; }
    public IRelayCommand RemoveSelectedCommand { get; }

    ICommand IEditableTableSectionViewModel.OpenEditCommand => OpenEditAsyncCommand;
    // No Add button — a candidate only ever arrives from live capture
    // (Game.MessageCandidateWatcher); there's nothing to hand-add here.
    ICommand? IEditableTableSectionViewModel.AddCommand     => null;
    ICommand? IEditableTableSectionViewModel.RemoveCommand  => RemoveSelectedCommand;

    private readonly NotifyCollectionChangedEventHandler _handler;

    public MessageCandidatesSectionViewModel(
        MessageCandidateStore candidates,
        MessageStore messages,
        DialogService? dialogs = null,
        GameDataCache? cache = null)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(messages);
        _candidates = candidates;
        _messages = messages;
        _dialogs = dialogs;
        _cache = cache;
        _handler = (_, _) => Reload();
        _candidates.Candidates.CollectionChanged += _handler;
        OpenEditAsyncCommand  = new AsyncRelayCommand<GameDataRow?>(OpenEditAsync);
        RemoveSelectedCommand = new RelayCommand(RemoveSelected, () => SelectedRow is not null);

        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SelectedRow))
                RemoveSelectedCommand.NotifyCanExecuteChanged();
        };

        Reload();
    }

    public override void Dispose()
    {
        _candidates.Candidates.CollectionChanged -= _handler;
        base.Dispose();
    }

    protected override void PopulateRows(IList<GameDataRow> rows)
    {
        foreach (MessageCandidateRecord c in _candidates.Candidates)
        {
            var dict = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["Raw Text"]    = c.RawText,
                ["Occurrences"] = c.Occurrences.ToString(),
                ["First Seen"]  = c.FirstSeenAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
                ["Last Seen"]   = c.LastSeenAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
                ["Status"]      = c.Dismissed ? "Dismissed" : "Pending",
            };
            GameDataRow row = GameDataRow.FromDictionary(dict, Columns);
            row.Tag = c;
            rows.Add(row);
        }
    }

    private async Task OpenEditAsync(GameDataRow? row)
    {
        if (row is null || _dialogs is null) return;
        if (row.Tag is not MessageCandidateRecord candidate) return;

        MessageRecord seed = MessageCandidateCommit.BuildSeedRecord(candidate);
        MessageEditDialogViewModel vm = new(
            seed, SettingsTier.Defaults, _messages.Messages, isNew: true, cache: _cache);
        MessageEditResult? result =
            await _dialogs.OpenWindowAsync<MessageEditDialogViewModel, MessageEditResult>(vm);
        if (result is null) return;

        MessageCandidateCommit.Commit(_messages, _candidates, result, candidate.Id);
    }

    // Dismiss the selected row(s) — sticky, not a hard delete
    // (MessageCandidateStore.Dismiss): a dismissed candidate keeps counting
    // occurrences in the background, so a genuinely boring recurring line
    // doesn't quietly resurface and re-alert as "new" in a later session.
    // That's a real deviation from every other table's Remove (hard-delete),
    // so unlike a typical RemoveSelectedAsync this deliberately skips a
    // Confirm.ConfirmDeleteAsync prompt — nothing is actually being deleted,
    // and a "Delete this?" dialog would misleadingly imply otherwise.
    private void RemoveSelected()
    {
        IReadOnlyList<GameDataRow> selection = SelectedRows.Count > 0
            ? new List<GameDataRow>(SelectedRows)
            : (SelectedRow is null ? Array.Empty<GameDataRow>() : new[] { SelectedRow });
        if (selection.Count == 0) return;

        foreach (GameDataRow row in selection)
            if (row.Tag is MessageCandidateRecord candidate)
                _candidates.Dismiss(candidate.Id);
    }
}
