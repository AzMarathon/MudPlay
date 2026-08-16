using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
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

    // Every class in the active set (Number, Name), enumerated once — the source for
    // each row's "Restrict to classes" checklist.
    private readonly List<(int Number, string Name)> _allClasses = new();

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
    // the edits persist to. classId / raceId / align*: the current character's identity
    // and alignment-quest opt-ins, so each row can pre-compute whether this character is
    // ineligible (drives the "Show in quest journal" default). classId null = the no-class
    // default profile, which is never class-gated.
    public QuestEditorViewModel(GameDataCache gameData, QuestStore quests, int? classId,
                                int? raceId = null, bool alignGood = false,
                                bool alignNeutral = false, bool alignEvil = false)
    {
        ArgumentNullException.ThrowIfNull(gameData);
        ArgumentNullException.ThrowIfNull(quests);
        _quests = quests;
        _gameData = gameData;

        Quests.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasQuests));

        // Enumerate the active set's classes once for every row's restriction checklist.
        if (gameData.GetRawTable("Classes") is { } classDoc)
            foreach (JsonElement row in classDoc.RootElement.EnumerateArray())
            {
                if (!row.TryGetProperty("Name", out JsonElement n) || n.ValueKind != JsonValueKind.String) continue;
                string? name = n.GetString();
                int number = row.TryGetProperty("Number", out JsonElement num)
                    && num.ValueKind == JsonValueKind.Number && num.TryGetInt32(out int v) ? v : 0;
                if (!string.IsNullOrEmpty(name) && number > 0) _allClasses.Add((number, name));
            }

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
            bool ineligible = QuestEligibilityResolver.IsIneligible(
                q, classId, raceId, def.ClassRestrict, alignGood, alignNeutral, alignEvil);
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
                ineligible,
                def.ShowIfIneligible,
                BuildClassOptions(def.ClassRestrict))
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
            ineligible: false,
            showIfIneligible: def.ShowIfIneligible,
            classOptions: BuildClassOptions(def.ClassRestrict))
        { Blocked = def.Blocked };

    // A fresh checklist of every class for one row, ticking the ones already in this
    // quest's ClassRestrict. Each row gets its own option instances so their checked
    // state stays independent.
    private IReadOnlyList<ClassRestrictOption> BuildClassOptions(List<int>? restrict)
    {
        HashSet<int>? selected = restrict is { Count: > 0 } ? new HashSet<int>(restrict) : null;
        var options = new List<ClassRestrictOption>(_allClasses.Count);
        foreach ((int Number, string Name) c in _allClasses)
            options.Add(new ClassRestrictOption(c.Number, c.Name, selected?.Contains(c.Number) == true));
        return options;
    }

    [RelayCommand]
    private void Save()
    {
        _quests.Save(Quests.Select(row => row.ToDefinition()));
        CloseRequested?.Invoke(true);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(false);
}
