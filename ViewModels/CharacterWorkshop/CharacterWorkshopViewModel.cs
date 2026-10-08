using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MudPlay.Game;
using MudPlay.Game.Inventory;
using MudPlay.Game.Recovery;
using MudPlay.Models.Profile;
using MudPlay.Services;

namespace MudPlay.ViewModels.CharacterWorkshop;

// Shell view-model for the Player Workshop window: a tab strip of sections, three
// of them groups of sub-tabs — Character Info / Death Recovery / Auto-Train (CP
// Allocation, Level Projection) / Quest Status / My Equipment (Equipment Manager, Item
// Finder) / Calculators / Record Keeping (Bosses, Chest Offload, Roomba, Realm Rankings).
public sealed partial class CharacterWorkshopViewModel : ObservableObject, IDisposable
{
    private readonly ProfileService _profile;
    private readonly GameDataCache _gameData;

    public const string AutoTrainGroupId = "autotrain";
    public const string EquipmentGroupId = "myequipment";
    public const string RecordsGroupId = "recordkeeping";

    public ObservableCollection<WorkshopSectionViewModel> Sections { get; } = new();

    [ObservableProperty] private WorkshopSectionViewModel? _selectedSection;

    // The section on screen: the selected tab, or its selected sub-tab.
    public WorkshopSectionViewModel? ActiveSection => SelectedSection?.Leaf;

    // The tab on screen changed what it shows (a sub-tab switch) or the window
    // size it asks for. A switch of the main tab isn't reported here; the window
    // sees that from the tab strip itself.
    public event Action? ActiveLayoutChanged;

    // Show the section with this id: a main tab, or a sub-tab along with the tab
    // that holds it. False when nothing has that id.
    public bool Select(string sectionId)
    {
        foreach (WorkshopSectionViewModel section in Sections)
        {
            if (Matches(section, sectionId)) { SelectedSection = section; return true; }
            if (section is not WorkshopGroupSectionViewModel group) continue;
            if (group.Children.FirstOrDefault(c => Matches(c, sectionId)) is not { } child) continue;
            group.SelectedChild = child;
            SelectedSection = group;
            return true;
        }
        return false;
    }

    // Whether that section is the one on screen (a group's id counts while any of
    // its sub-tabs is showing).
    public bool IsShowing(string sectionId) =>
        SelectedSection is { } tab && (Matches(tab, sectionId) || Matches(tab.Leaf, sectionId));

    private static bool Matches(WorkshopSectionViewModel section, string id) =>
        string.Equals(section.Id, id, StringComparison.OrdinalIgnoreCase);

    private void OnSectionLayoutChanged(WorkshopSectionViewModel section)
    {
        if (ReferenceEquals(section, SelectedSection)) ActiveLayoutChanged?.Invoke();
    }

    // Window title — "Player Workshop - {character} - {bbs} - {realm}". Recomputed
    // live as the profile / pinned BBS / active game-data set (realm) change while
    // the window is open.
    [ObservableProperty] private string _windowTitle = "Player Workshop";

    public CharacterWorkshopViewModel(
        DeathRecoveryManager recovery,
        ProfileService profile,
        PlayerStats playerStats,
        GameDataCache gameData,
        InventoryManager inventory,
        PlayerDatabase players,
        AlignmentTracker alignment,
        TrainerWalkManager trainerWalk,
        QuestStore quests,
        EquipmentManager equipment,
        LeaderboardSnapshotStore leaderboards,
        string? initialSectionId = null)
    {
        ArgumentNullException.ThrowIfNull(recovery);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(playerStats);
        ArgumentNullException.ThrowIfNull(gameData);
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(players);
        ArgumentNullException.ThrowIfNull(alignment);
        ArgumentNullException.ThrowIfNull(trainerWalk);
        ArgumentNullException.ThrowIfNull(quests);
        ArgumentNullException.ThrowIfNull(equipment);
        ArgumentNullException.ThrowIfNull(leaderboards);
        _profile = profile;
        _gameData = gameData;

        // The Quest Status tab (writer) publishes completed-quest bonuses into this
        // shared state; the Character Info tab (reader) folds them into derived combat.
        var questBonuses = new QuestBonusState();

        Sections.Add(new CharacterInfoSectionViewModel(playerStats, gameData, inventory, players, alignment, questBonuses, AppServices.Current.Currency, AppServices.Current.CarriedCharges,
            openChestOffload: () => Select(ChestOffloadViewModel.SectionId)));

        Sections.Add(new DeathSectionViewModel(recovery, profile));

        // The CP Allocation tab (writer) and Level Projection tab (reader) share
        // one plan state so the projection's HP / regen reflect planned training.
        var planState = new CpPlanState();
        Sections.Add(new WorkshopGroupSectionViewModel(AutoTrainGroupId, "Auto-Train",
            new CpAllocationSectionViewModel(playerStats, gameData, inventory, profile, planState, trainerWalk, AppServices.Current.AutoTrain),
            new LazyWorkshopSection(LevelProjectionSectionViewModel.SectionId, LevelProjectionSectionViewModel.SectionTitle,
                () => new LevelProjectionSectionViewModel(playerStats, gameData, planState, inventory, questBonuses))));

        Sections.Add(new QuestSectionViewModel(playerStats, gameData, profile, quests, questBonuses));

        Sections.Add(new WorkshopGroupSectionViewModel(EquipmentGroupId, "My Equipment",
            new LazyWorkshopSection(EquipmentSectionViewModel.SectionId, EquipmentSectionViewModel.SectionTitle,
                () => new EquipmentSectionViewModel(profile, inventory, gameData, equipment, playerStats, players, questBonuses)),
            // Built on first visit: its catalog is every equippable item in the set.
            new LazyWorkshopSection(ItemFinderViewModel.SectionId, ItemFinderViewModel.SectionTitle,
                () => new ItemFinderViewModel(
                    gameData, playerStats, inventory,
                    ItemEquipFilter.GearBucketForWord(alignment.SelfAlignment, gameData.ActiveRealm),
                    alignment.SelfEvilPoints(gameData.ActiveRealm),
                    () => questBonuses.Bonuses, AppServices.Current.MonsterCatalog))));

        Sections.Add(new LazyWorkshopSection(CalculatorsSectionViewModel.SectionId, CalculatorsSectionViewModel.SectionTitle,
            () => new CalculatorsSectionViewModel(playerStats, gameData, inventory, questBonuses, profile)));

        Sections.Add(new WorkshopGroupSectionViewModel(RecordsGroupId, "Record Keeping",
            new LazyWorkshopSection(BossesSectionViewModel.SectionId, BossesSectionViewModel.SectionTitle,
                () => new BossesSectionViewModel(gameData, AppServices.Current.Bosses, AppServices.Current.BossTimers, AppServices.Current.Tick, AppServices.Current.Profile,
                    AppServices.Current.BossReach)),
            // One Workshop, so one of these: two would both read the inventory and
            // diff the same chest opens.
            new LazyWorkshopSection(ChestOffloadViewModel.SectionId, ChestOffloadViewModel.SectionTitle,
                () => new ChestOffloadViewModel()),
            new GhManagementSectionViewModel(AppServices.Current.GhRoomLabels, AppServices.Current.GhSweep, AppServices.Current.RoomGraph, AppServices.Current.GhItemLocations, AppServices.Current.GhManagedRooms),
            new LazyWorkshopSection(RealmRankingsSectionViewModel.SectionId, RealmRankingsSectionViewModel.SectionTitle,
                () => new RealmRankingsSectionViewModel(gameData, leaderboards))));

        foreach (WorkshopSectionViewModel section in Sections)
            section.LayoutChanged += OnSectionLayoutChanged;

        if (initialSectionId is null || !Select(initialSectionId))
            SelectedSection = Sections.FirstOrDefault();

        UpdateTitle();
        _profile.ProfileLoaded += OnProfileTitleChanged;
        _profile.BbsPinApplied += OnProfileTitleChanged;
        _gameData.ActiveSetChanged += OnSetTitleChanged;
    }

    private void OnProfileTitleChanged(CharacterProfile _) => UpdateTitle();
    private void OnSetTitleChanged(string? _) => UpdateTitle();

    private void UpdateTitle()
    {
        string character = _profile.CurrentProfileName ?? "{default}";
        string bbs = _profile.CurrentBbsName ?? "{No BBS}";
        string realm = _gameData.ActiveRealm == RealmType.ParaMud ? "ParaMUD" : "Stock";
        WindowTitle = $"Player Workshop - {character} - {bbs} - {realm}";
    }

    // Dispose every section so they detach from long-lived service events, and
    // unsubscribe the title's own hooks. Called from the Workshop window's Closed
    // handler — the window (and these view-models) are rebuilt on each open.
    public void Dispose()
    {
        _profile.ProfileLoaded -= OnProfileTitleChanged;
        _profile.BbsPinApplied -= OnProfileTitleChanged;
        _gameData.ActiveSetChanged -= OnSetTitleChanged;
        foreach (WorkshopSectionViewModel section in Sections)
        {
            section.LayoutChanged -= OnSectionLayoutChanged;
            section.Dispose();
        }
    }
}
