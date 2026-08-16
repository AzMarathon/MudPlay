using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Game.Map;
using MudPlay.Game.Quests;
using MudPlay.Models.Profile;
using MudPlay.Services;

namespace MudPlay.ViewModels.CharacterWorkshop;

// Modeless editor for the active set's quest overlay ({set}/quests.json): per
// quest the user sets a display name, show/hide visibility, and amplifying step
// markdown over the crawler's auto-draft. Every crawled quest is listed — hidden
// ones included, so they can be un-hidden. The user can also Add a custom quest
// the crawl never finds (a self-contained manual row in the reserved
// QuestDefinition.ManualFlagBase range — Delete removes it) and Block a crawled
// quest a set surfaces spuriously (a journal-suppressing flag that keeps the row
// here so it can be un-blocked). Save writes the overlay as a delta (untouched
// seed/default rows aren't frozen in, manual rows verbatim) via QuestStore.Save;
// Cancel / title-bar X discard. Standard edit-window contract: Save commits,
// X / Cancel discards.
public sealed partial class QuestEditorViewModel : ObservableObject, IDialogViewModel<bool>
{
    public event Action<bool>? CloseRequested;

    private readonly QuestStore _quests;
    private readonly GameDataCache _gameData;

    // Every crawled quest in crawl order (flag, then band level), editable.
    public ObservableCollection<QuestEditRowViewModel> Quests { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private QuestEditRowViewModel? _selectedQuest;

    // False when the active set crawls no quests — drives the empty-state hint.
    public bool HasQuests => Quests.Count > 0;

    // True when a quest is selected — gates the detail pane.
    public bool HasSelection => SelectedQuest is not null;

    // gameData: active set, source of the crawl + item names. quests: overlay store
    // the edits persist to. classId: character class number for class-resolved
    // bonus labels, or null for the no-class default.
    public QuestEditorViewModel(GameDataCache gameData, QuestStore quests, int? classId)
    {
        ArgumentNullException.ThrowIfNull(gameData);
        ArgumentNullException.ThrowIfNull(quests);
        _quests = quests;
        _gameData = gameData;

        Quests.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasQuests));

        // Kill / ask-NPC target placement, resolved once for the whole draft pass so a
        // crawled quest's auto-draft step can link to the room the quest places its
        // target / NPC in; and the reverse item-acquisition index so a turn-in /
        // prerequisite step can point at where its item comes from.
        IReadOnlyDictionary<int, IReadOnlyList<RoomKey>>? monsterRooms =
            AppServices.Current?.RoomSearch?.QuestKillRooms();
        ItemSourceIndex? itemSources = AppServices.Current?.ItemSources;

        foreach (CrawledQuest q in QuestCrawler.Crawl(gameData, classId))
        {
            QuestDefinition def = quests.Resolve(q.Flag, q.Step);
            Quests.Add(new QuestEditRowViewModel(
                q.Flag, q.Step,
                QuestTextFormatter.FallbackTitle(q),
                string.Join("\n", QuestTextFormatter.StepLines(gameData, q, monsterRooms, itemSources)),
                QuestTextFormatter.Awards(gameData, q),
                QuestTextFormatter.Bonuses(q.Bonuses),
                QuestTextFormatter.Level(q.RequiredLevel),
                q.RequiredLevel,
                QuestTextFormatter.Requirements(gameData, q),
                def.Name,
                def.Visible,
                def.Steps ?? string.Empty,
                def.Rewards ?? string.Empty,
                def.RequiredLevel,
                ClassNamesText(def.ClassRestrict))
            { Blocked = def.Blocked });
        }

        // User-added quests the crawl never produces, listed after the crawled ones.
        foreach (QuestDefinition def in quests.ManualQuests())
            Quests.Add(BuildManualRow(def));

        SelectedQuest = Quests.FirstOrDefault();
    }

    // Add a blank custom quest at the next free manual flag and select it for editing.
    [RelayCommand]
    private void AddQuest()
    {
        int flag = QuestDefinition.ManualFlagBase;
        foreach (QuestEditRowViewModel row in Quests)
            if (row.IsManual && row.Flag >= flag) flag = row.Flag + 1;

        QuestEditRowViewModel added = BuildManualRow(new QuestDefinition(flag, 0));
        Quests.Add(added);
        SelectedQuest = added;
    }

    // Delete the selected manual quest (crawled quests are blocked, not deleted). Keeps a
    // neighbouring row selected so the detail pane doesn't blank out mid-edit.
    [RelayCommand]
    private void DeleteSelected()
    {
        if (SelectedQuest is not { IsManual: true } row) return;
        int index = Quests.IndexOf(row);
        Quests.Remove(row);
        SelectedQuest = Quests.Count == 0 ? null : Quests[Math.Min(index, Quests.Count - 1)];
    }

    // A master-list row for a manual quest: every crawl-baseline field is empty, so the
    // editable boxes pre-fill straight from the definition and persist verbatim.
    private QuestEditRowViewModel BuildManualRow(QuestDefinition def) =>
        new(def.Flag, def.Step,
            fallbackLabel: "(custom quest)",
            autoSteps: string.Empty,
            autoRewards: string.Empty,
            bonusText: string.Empty,
            levelText: QuestTextFormatter.Level(def.RequiredLevel ?? 0),
            autoRequiredLevel: 0,
            requirementsText: string.Empty,
            name: def.Name,
            visible: def.Visible,
            steps: def.Steps ?? string.Empty,
            rewards: def.Rewards ?? string.Empty,
            requiredLevel: def.RequiredLevel,
            classRestrictText: ClassNamesText(def.ClassRestrict))
        { Blocked = def.Blocked };

    // Class Numbers → a comma-separated name list for the editor field ("" when none).
    private string ClassNamesText(System.Collections.Generic.List<int>? nums)
    {
        if (nums is not { Count: > 0 }) return string.Empty;
        return string.Join(", ", nums.Select(n => _gameData.FindNameByNumber("Classes", n) ?? n.ToString()));
    }

    // The editor field's comma-separated class names (or raw numbers) → class Numbers,
    // or null when blank. A token that resolves to neither a known class name nor a
    // positive integer is dropped.
    private System.Collections.Generic.List<int>? ParseClassRestrict(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var ids = new System.Collections.Generic.List<int>();
        foreach (string raw in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (int.TryParse(raw, out int n) && n > 0) { if (!ids.Contains(n)) ids.Add(n); continue; }
            int byName = GetInt(_gameData.FindRowByName("Classes", raw), "Number");
            if (byName > 0 && !ids.Contains(byName)) ids.Add(byName);
        }
        return ids.Count > 0 ? ids : null;
    }

    private static int GetInt(System.Text.Json.JsonElement? rowOpt, string prop)
    {
        if (rowOpt is not System.Text.Json.JsonElement row || row.ValueKind != System.Text.Json.JsonValueKind.Object) return 0;
        if (!row.TryGetProperty(prop, out System.Text.Json.JsonElement v)) return 0;
        return v.ValueKind == System.Text.Json.JsonValueKind.Number && v.TryGetInt32(out int n) ? n : 0;
    }

    [RelayCommand]
    private void Save()
    {
        _quests.Save(Quests.Select(row =>
        {
            QuestDefinition def = row.ToDefinition();
            def.ClassRestrict = ParseClassRestrict(row.ClassRestrictText);
            return def;
        }));
        CloseRequested?.Invoke(true);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(false);
}
