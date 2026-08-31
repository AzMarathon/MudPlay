using System.Collections.Frozen;
using System.Linq;

namespace MudPlay.Services;

// The pool of entries the user can place in the customizable terminal
// right-click menu (Settings → Toolbar + Shortcuts → Terminal right-click menu).
// Separate from ToolbarItemCatalogue on purpose: the context menu offers far
// more than the toolbar's ~40 icon actions — every main-menu command, whole
// main menus as nested submenus, direct links to each Player Workshop tab, and
// direct links to each calculator. Command / toggle resolution reuses the same
// reflection bridge the toolbar and keybinds use: a CommandName is looked up as
// an ICommand property on MainWindowViewModel; a ToggleProperty as a two-way
// bool. Every CommandName / ToggleProperty / GestureProperty here is taken
// verbatim from the live menu bar + terminal context menu bindings in
// MainWindow.axaml, so nothing resolves to a dead item.
public static class MenuActionCatalogue
{
    public enum Kind
    {
        Command,      // CommandName → ICommand on MainWindowViewModel
        Toggle,       // ToggleProperty → two-way bool on MainWindowViewModel
        Submenu,      // Members → nested MenuItem built from other entries
        WorkshopTab,  // Parameter → CharacterWorkshop section id (OpenWorkshopTab)
        Calculator,   // Parameter → calculator id (OpenWorkshopCalculator)
    }

    public sealed record Entry(
        string Id,                 // stable id persisted in ContextMenuSettings
        string Label,              // display text in the menu + editor
        Kind EntryKind,
        string Group,              // editor grouping header (also the submenu label source)
        string? CommandName = null,
        string? ToggleProperty = null,
        string? GestureProperty = null,
        string? Parameter = null,
        IReadOnlyList<string>? Members = null,
        string? Tooltip = null);

    // ----- Individual File-menu commands -----
    private static readonly Entry[] _file =
    {
        new("file.connect", "Connect / Disconnect", Kind.Command, "File", CommandName: "ToggleConnectionCommand", GestureProperty: "ToggleConnectionGesture"),
        new("file.quickconnect", "Quick Connect…", Kind.Command, "File", CommandName: "OpenQuickConnectCommand"),
        new("file.bbslist", "BBS list", Kind.Command, "File", CommandName: "OpenBbsSettingsCommand"),
        new("file.disablehangups", "Disable hangups", Kind.Toggle, "File", ToggleProperty: "IsDisableHangupsActive"),
        new("file.newprofile", "New profile…", Kind.Command, "File", CommandName: "NewProfileCommand", GestureProperty: "NewProfileGesture"),
        new("file.openprofile", "Open profile…", Kind.Command, "File", CommandName: "OpenProfileCommand", GestureProperty: "OpenProfileGesture"),
        new("file.saveprofile", "Save profile", Kind.Command, "File", CommandName: "SaveProfileCommand", GestureProperty: "SaveProfileGesture"),
        new("file.saveprofileas", "Save profile as…", Kind.Command, "File", CommandName: "SaveProfileAsCommand", GestureProperty: "SaveProfileAsGesture"),
        new("file.quit", "Quit", Kind.Command, "File", CommandName: "QuitCommand", GestureProperty: "QuitGesture"),
    };

    // ----- Individual View-menu commands -----
    private static readonly Entry[] _view =
    {
        new("view.settings", "Settings…", Kind.Command, "View", CommandName: "OpenSettingsCommand", GestureProperty: "SettingsGesture"),
        new("view.backscroll", "Backscroll", Kind.Command, "View", CommandName: "OpenBackscrollCommand", GestureProperty: "BackscrollGesture"),
        new("view.conversation", "Conversation", Kind.Command, "View", CommandName: "OpenConversationCommand", GestureProperty: "ConversationGesture"),
        new("view.party", "Party", Kind.Command, "View", CommandName: "OpenPartyCommand", GestureProperty: "PartyGesture"),
        new("view.buffwatchdog", "Buff Watchdog", Kind.Command, "View", CommandName: "OpenBuffWatchdogCommand", GestureProperty: "BuffWatchdogGesture"),
        new("view.workshop", "Player Workshop", Kind.Command, "View", CommandName: "OpenWorkshopCommand", GestureProperty: "WorkshopGesture"),
        new("view.navigation", "Navigation", Kind.Command, "View", CommandName: "OpenNavigationCommand", GestureProperty: "NavigationGesture"),
        new("view.spellbook", "Spell Book", Kind.Command, "View", CommandName: "OpenSpellBookCommand", GestureProperty: "SpellBookGesture"),
        new("view.monsterintel", "Monster Intel", Kind.Command, "View", CommandName: "OpenMonsterIntelCommand", GestureProperty: "MonsterIntelGesture"),
        new("view.sessionstats", "Session Stats", Kind.Command, "View", CommandName: "OpenSessionStatsCommand", GestureProperty: "SessionStatsGesture"),
        new("view.gdplayers", "Players (Game Data)", Kind.Command, "View", CommandName: "OpenGameDataPlayersCommand"),
        new("view.gdmacros", "Macros (Game Data)", Kind.Command, "View", CommandName: "OpenGameDataMacrosCommand"),
        new("view.gdtriggers", "Triggers (Game Data)", Kind.Command, "View", CommandName: "OpenGameDataTriggersCommand"),
        new("view.gdaliases", "Aliases (Game Data)", Kind.Command, "View", CommandName: "OpenGameDataAliasesCommand"),
        new("view.events", "Events", Kind.Command, "View", CommandName: "OpenEventsCommand"),
        new("view.resetlayout", "Reset layout", Kind.Command, "View", CommandName: "ResetLayoutCommand"),
    };

    // ----- Individual Action-menu commands -----
    private static readonly Entry[] _action =
    {
        new("action.resetstates", "Reset States", Kind.Command, "Action", CommandName: "ResetStatesCommand", Tooltip: "Clear my own stuck ailments, waits, and movement holds — return to idle"),
        new("action.getall", "Get All", Kind.Command, "Action", CommandName: "GetAllCommand", Tooltip: "Pick up every item on the room floor"),
        new("action.dropall", "Drop All", Kind.Command, "Action", CommandName: "DropAllCommand", Tooltip: "Drop every carried (unworn) item"),
        new("action.equipall", "Equip All", Kind.Command, "Action", CommandName: "EquipAllCommand", Tooltip: "Wear the Default gear set"),
        new("action.depositall", "Deposit All", Kind.Command, "Action", CommandName: "DepositAllCommand", Tooltip: "Bank wealth down to the keep-on-hand floor"),
        new("action.sprint", "Sprint Mode", Kind.Toggle, "Action", ToggleProperty: "IsSprintModeActive"),
        new("action.autocombat", "Auto Combat", Kind.Toggle, "Action", ToggleProperty: "IsAutoCombatActive"),
        new("action.autonuke", "Auto Nuke", Kind.Toggle, "Action", ToggleProperty: "IsAutoNukeActive"),
        new("action.autohealrest", "Auto Rest / Heal", Kind.Toggle, "Action", ToggleProperty: "IsAutoHealRestActive"),
        new("action.autobless", "Auto Bless", Kind.Toggle, "Action", ToggleProperty: "IsAutoBlessActive"),
        new("action.autolight", "Auto Light", Kind.Toggle, "Action", ToggleProperty: "IsAutoLightActive"),
        new("action.autogetitems", "Auto Get Items", Kind.Toggle, "Action", ToggleProperty: "IsAutoGetItemsActive"),
        new("action.autogetcash", "Auto Get Cash", Kind.Toggle, "Action", ToggleProperty: "IsAutoGetCashActive"),
        new("action.autosneak", "Auto Sneak", Kind.Toggle, "Action", ToggleProperty: "IsAutoSneakActive"),
        new("action.autohide", "Auto Hide", Kind.Toggle, "Action", ToggleProperty: "IsAutoHideActive"),
        new("action.autosearch", "Auto Search", Kind.Toggle, "Action", ToggleProperty: "IsAutoSearchActive"),
    };

    // ----- Individual Tools-menu commands -----
    private static readonly Entry[] _tools =
    {
        new("tools.capture", "Toggle Capture", Kind.Command, "Tools", CommandName: "ToggleDumpCommand"),
        new("tools.log", "Program Log…", Kind.Command, "Tools", CommandName: "OpenLogPaneCommand", GestureProperty: "LogPaneGesture"),
        new("tools.wireinspector", "Wire Inspector…", Kind.Command, "Tools", CommandName: "OpenWireInspectorCommand"),
        new("tools.clearchatlog", "Clear chatlog", Kind.Command, "Tools", CommandName: "ClearChatlogCommand"),
        new("tools.logsfolder", "Open Logs folder…", Kind.Command, "Tools", CommandName: "OpenLogsFolderCommand"),
        new("tools.bugreport", "Bug report…", Kind.Command, "Tools", CommandName: "ReportBugCommand", Tooltip: "Capture current client state and recent output to a report file on your Desktop"),
    };

    // ----- Player Workshop tab deep-links -----
    // Parameter = the WorkshopSectionViewModel.Id (CharacterWorkshopViewModel).
    private static readonly Entry[] _tabs =
    {
        new("tab.characterinfo", "Workshop: Character Info", Kind.WorkshopTab, "Workshop tabs", Parameter: "characterinfo"),
        new("tab.death", "Workshop: Death Recovery", Kind.WorkshopTab, "Workshop tabs", Parameter: "death"),
        new("tab.levelprojection", "Workshop: Level Projection", Kind.WorkshopTab, "Workshop tabs", Parameter: "levelprojection"),
        new("tab.cpallocation", "Workshop: CP Allocation", Kind.WorkshopTab, "Workshop tabs", Parameter: "cpallocation"),
        new("tab.queststatus", "Workshop: Quest Status", Kind.WorkshopTab, "Workshop tabs", Parameter: "queststatus"),
        new("tab.equipment", "Workshop: Equipment Manager", Kind.WorkshopTab, "Workshop tabs", Parameter: "equipment"),
        new("tab.calculators", "Workshop: Calculators", Kind.WorkshopTab, "Workshop tabs", Parameter: "calculators"),
        new("tab.bosses", "Workshop: Bosses", Kind.WorkshopTab, "Workshop tabs", Parameter: "bosses"),
        new("tab.ghmanagement", "Workshop: Roomba", Kind.WorkshopTab, "Workshop tabs", Parameter: "ghmanagement"),
    };

    // ----- Calculator deep-links (Workshop → Calculators tab → expanded + centered) -----
    // Parameter = the CalculatorId enum name (see CalculatorsSectionViewModel).
    private static readonly Entry[] _calcs =
    {
        new("calc.hit", "Calculator: Hit Calculator", Kind.Calculator, "Calculators", Parameter: "Hit"),
        new("calc.movement", "Calculator: Movement Speed", Kind.Calculator, "Calculators", Parameter: "Movement"),
        new("calc.swing", "Calculator: Swing Calculator", Kind.Calculator, "Calculators", Parameter: "Swing"),
        new("calc.backstab", "Calculator: Backstab Calculator", Kind.Calculator, "Calculators", Parameter: "Backstab"),
        new("calc.manaregen", "Calculator: Mana Regen", Kind.Calculator, "Calculators", Parameter: "ManaRegen"),
        new("calc.realmrankings", "Calculator: Realm Rankings", Kind.Calculator, "Calculators", Parameter: "RealmRankings"),
    };

    // ----- Whole-menu submenus (Members are the individual-command ids above) -----
    private static readonly Entry[] _submenus =
    {
        new("menu.file", "File", Kind.Submenu, "Whole menus", Members: _file.Select(e => e.Id).ToArray()),
        new("menu.view", "View", Kind.Submenu, "Whole menus", Members: _view.Select(e => e.Id).ToArray()),
        new("menu.action", "Action", Kind.Submenu, "Whole menus", Members: _action.Select(e => e.Id).ToArray()),
        new("menu.tools", "Tools", Kind.Submenu, "Whole menus", Members: _tools.Select(e => e.Id).ToArray()),
    };

    private static readonly Entry[] _all =
        _submenus
        .Concat(_file).Concat(_view).Concat(_action).Concat(_tools)
        .Concat(_tabs).Concat(_calcs)
        .ToArray();

    private static readonly FrozenDictionary<string, Entry> _byId =
        _all.ToFrozenDictionary(e => e.Id, System.StringComparer.OrdinalIgnoreCase);

    // Every addable entry, in editor-picker order (submenus first, then each
    // menu's items, then workshop tabs, then calculators).
    public static IReadOnlyList<Entry> AllEntries => _all;

    public static Entry? Find(string? id)
        => id is not null && _byId.TryGetValue(id, out Entry? e) ? e : null;
}
