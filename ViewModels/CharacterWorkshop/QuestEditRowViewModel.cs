using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using MudPlay.Models.Profile;

namespace MudPlay.ViewModels.CharacterWorkshop;

// One editable quest in the QuestEditorViewModel master list. Holds the user-owned
// overlay fields — Name, Visible, Steps — as live two-way state, both pre-filled
// from the crawl baseline (FallbackLabel / AutoSteps) so the boxes show the
// auto-draft the moment the editor opens. ToDefinition diffs back against that
// baseline so an untouched prefill is never frozen into the overlay. Identity is
// the (Flag, Step) pair.
public sealed partial class QuestEditRowViewModel : ObservableObject
{
    // Quest-flag ability id (the overlay key's flag half).
    public int Flag { get; }

    // Band level for a multi-part quest; 0 for a single-part one.
    public int Step { get; }

    // True when this is a user-added quest (no crawl backing) — fields persist verbatim and the row can be deleted.
    public bool IsManual => QuestDefinition.IsManual(Flag);

    // True for a crawled quest — eligible for blocking rather than deletion.
    public bool IsCrawled => !IsManual;

    // Auto-draft title — pre-fills Name and is the delta baseline for it.
    public string FallbackLabel { get; }

    // The crawler's drafted steps — pre-fills Steps and is its delta baseline.
    public string AutoSteps { get; }

    // The crawler's inferred award label — pre-fills Rewards and is its delta baseline.
    public string AutoRewards { get; }

    // Class-resolved permanent bonus summary; empty when the quest grants none.
    public string BonusText { get; }
    public bool HasBonus => BonusText.Length > 0;

    // Level-gate label; empty when ungated.
    public string LevelText { get; }
    public bool HasLevel => LevelText.Length > 0;

    // The crawler's inferred level gate — pre-fills RequiredLevelInput and is its delta baseline (0 when ungated / not found).
    public int AutoRequiredLevel { get; }

    // Class / race restriction the crawl found; empty when the quest is open to all.
    public string RequirementsText { get; }
    public bool HasRequirements => RequirementsText.Length > 0;

    // True when the current character can't complete this quest (crawl class/race guard,
    // the ClassRestrict override, or an unticked alignment gate). Fixed at editor-open
    // time. Flips "Show in quest journal" from the per-taste Visible hide to the
    // ShowIfIneligible opt-in, so a cannot-complete quest starts unchecked.
    public bool IsIneligible { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ListLabel))]
    private string _name;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ListLabel))]
    private bool _visible;

    // Backs ShowInJournal for a "Cannot complete" quest (the show-anyway opt-in). Kept
    // separate from Visible so the two never clobber each other. Persisted per character
    // on QuestProgress (not on the per-set QuestDefinition), so the editor VM seeds it in
    // and reads it back via ShowIfIneligibleOverride rather than through ToDefinition.
    private bool _showIfIneligible;

    // The live show-anyway value, read back by the journal VM to persist onto the
    // per-character QuestProgress. Only meaningful for an ineligible quest.
    public bool ShowIfIneligibleOverride => _showIfIneligible;

    // Live block flag bound to the editor's "Block" toggle (crawled quests only). When
    // set the quest is suppressed from the journal entirely as a false positive; the row
    // stays in the editor so it can be un-blocked.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ListLabel))]
    private bool _blocked;

    [ObservableProperty] private string _steps;

    [ObservableProperty] private string _rewards;

    // Live required-level override bound to the editor's spinner. null means
    // "no override" (the box is empty) and falls back to the crawled gate; any value
    // (including 0 to force ungated) persists as a user override.
    [ObservableProperty] private int? _requiredLevelInput;

    // The classes this quest is restricted to, one checkable row per class in the active
    // set. IsSelected persists as class Numbers on QuestDefinition.ClassRestrict — the
    // editor VM builds these (it owns the Classes table). The explicit override for the
    // genuinely class-locked quests the crawl can't detect (Magebane, Tarl).
    public ObservableCollection<ClassRestrictOption> ClassOptions { get; } = new();

    public QuestEditRowViewModel(int flag, int step, string fallbackLabel,
                                 string autoSteps, string autoRewards, string bonusText,
                                 string levelText, int autoRequiredLevel, string requirementsText,
                                 string name, bool visible, string steps, string rewards,
                                 int? requiredLevel, bool ineligible = false,
                                 bool showIfIneligible = false,
                                 IReadOnlyList<ClassRestrictOption>? classOptions = null)
    {
        Flag = flag;
        Step = step;
        FallbackLabel = fallbackLabel;
        AutoSteps = autoSteps;
        AutoRewards = autoRewards;
        BonusText = bonusText;
        LevelText = levelText;
        AutoRequiredLevel = autoRequiredLevel;
        RequirementsText = requirementsText;
        IsIneligible = ineligible;
        _showIfIneligible = showIfIneligible;
        // Prefill the editable boxes from the crawl baseline so the user starts from the
        // auto-draft rather than a blank field; a saved overlay value (if any) wins.
        _name = string.IsNullOrWhiteSpace(name) ? fallbackLabel : name;
        _visible = visible;
        _steps = string.IsNullOrEmpty(steps) ? autoSteps : steps;
        _rewards = string.IsNullOrEmpty(rewards) ? autoRewards : rewards;
        // Show the crawled level when there's one to correct, blank when the crawl found
        // none — so an empty box always reads as "no override".
        _requiredLevelInput = requiredLevel ?? (autoRequiredLevel > 0 ? autoRequiredLevel : null);

        if (classOptions is not null)
            foreach (ClassRestrictOption option in classOptions)
            {
                option.PropertyChanged += OnClassOptionChanged;
                ClassOptions.Add(option);
            }
    }

    private void OnClassOptionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ClassRestrictOption.IsSelected))
            OnPropertyChanged(nameof(ClassRestrictSummary));
    }

    // The "Show in quest journal" checkbox. For a quest this character CAN complete it
    // toggles the per-taste Visible hide (shown by default); for a "Cannot complete"
    // quest it toggles the show-anyway opt-in (hidden by default) — so a cannot-complete
    // quest opens with the box unchecked, per the journal's auto-hide.
    public bool ShowInJournal
    {
        get => IsIneligible ? _showIfIneligible : Visible;
        set
        {
            if (IsIneligible)
            {
                if (_showIfIneligible == value) return;
                _showIfIneligible = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ListLabel));
            }
            else Visible = value;
        }
    }

    // Dropdown-button caption summarizing the class restriction.
    public string ClassRestrictSummary
    {
        get
        {
            List<string> picked = ClassOptions.Where(o => o.IsSelected).Select(o => o.Name).ToList();
            if (picked.Count == 0) return "Any class";
            if (picked.Count <= 3) return string.Join(", ", picked);
            return $"{picked.Count} classes";
        }
    }

    // The ticked classes' Numbers for persistence, or null when none are ticked.
    public List<int>? SelectedClassNumbers()
    {
        List<int> ids = ClassOptions.Where(o => o.IsSelected).Select(o => o.Number).ToList();
        return ids.Count > 0 ? ids : null;
    }

    // Left-list label: the current name (or the auto-draft fallback), suffixed to show
    // why it's out of the journal — blocked, per-taste hidden, or an un-opted-in
    // cannot-complete quest.
    public string ListLabel
    {
        get
        {
            string baseName = string.IsNullOrWhiteSpace(Name) ? FallbackLabel : Name;
            if (Blocked) return $"{baseName}  (blocked)";
            if (IsIneligible && !_showIfIneligible) return $"{baseName}  (cannot complete — hidden)";
            return Visible ? baseName : $"{baseName}  (hidden)";
        }
    }

    // Materialize the current edits into a persistable definition, diffed against the
    // crawl baseline: a name still equal to the fallback, steps still equal to the
    // auto-draft, or rewards still equal to the inferred award, collapse to empty/null
    // so an untouched prefill isn't frozen into the overlay (QuestStore.Save then drops
    // the redundant row entirely).
    public QuestDefinition ToDefinition()
    {
        // A manual quest has no crawl baseline to diff against — it's self-contained, so its
        // fields persist verbatim (QuestStore keeps the row unless it's wholly blank).
        if (IsManual)
            return new QuestDefinition(
                Flag, Step, (Name ?? string.Empty).Trim(), Visible,
                string.IsNullOrWhiteSpace(Steps) ? null : Steps,
                string.IsNullOrWhiteSpace(Rewards) ? null : Rewards,
                RequiredLevelInput)
            { ClassRestrict = SelectedClassNumbers() };

        string name = (Name ?? string.Empty).Trim();
        if (string.Equals(name, FallbackLabel, StringComparison.Ordinal)) name = string.Empty;

        string? steps = string.IsNullOrWhiteSpace(Steps) ? null : Steps;
        if (steps is not null && string.Equals(steps.Trim(), AutoSteps.Trim(), StringComparison.Ordinal))
            steps = null;

        string? rewards = string.IsNullOrWhiteSpace(Rewards) ? null : Rewards;
        if (rewards is not null && string.Equals(rewards.Trim(), AutoRewards.Trim(), StringComparison.Ordinal))
            rewards = null;

        // An empty box (no override) or one still showing the crawled level isn't a user
        // delta, so it collapses to null rather than freezing into the overlay; a value
        // that differs (incl. 0 to force "ungated") persists as an override.
        int? requiredLevel = RequiredLevelInput;
        if (requiredLevel is null || requiredLevel == AutoRequiredLevel) requiredLevel = null;

        return new QuestDefinition(Flag, Step, name, Visible, steps, rewards, requiredLevel, Blocked)
            { ClassRestrict = SelectedClassNumbers() };
    }
}
