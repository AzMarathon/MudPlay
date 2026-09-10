namespace MudPlay.Models.Profile;

// Every keybindable built-in app action. One entry per command the menu or
// toolbar can invoke; KeybindingStore maps each to a KeyChord. New built-in
// actions get added here once + seeded with a default chord in
// KeybindingStore.DefaultBindings.
public enum BuiltInAction
{
    // ---- Window toggles (View menu + toolbar) ----
    OpenConversation,
    OpenParty,
    OpenBuffWatchdog,
    OpenProfileManager,
    OpenWorkshop,
    OpenNavigation,
    OpenSpellBook,
    OpenLogPane,
    OpenBackscroll,
    OpenSessionStats,
    OpenSettings,
    OpenGameDataBrowser,
    OpenWireInspector,
    OpenMonsterIntel,

    // ---- Connection ----
    ToggleConnection,
    ToggleCapture,
    ToggleDisableHangups,

    // ---- File menu ----
    // New / Open / Save-as profile were retired in favour of the Profile
    // Management window; Save profile (quick one-click save) + Quit remain.
    SaveProfile,
    Quit,

    // ---- Movement engine (toolbar) ----
    MovementStart,
    MovementPause,
    MovementStop,
    ToggleSprintMode,

    // ---- Bulk one-shot actions (toolbar / Action menu) ----
    ActionGetAll,
    ActionDropAll,
    ActionEquipAll,
    ActionDepositAll,

    // ---- Recovery (toolbar / Action menu) ----
    ResetStates,

    // ---- Auto-response toggles (toolbar / Action menu) ----
    ToggleAllAutoOff,
    ToggleAutoCombat,
    ToggleAutoNuke,
    ToggleAutoHealRest,
    ToggleAutoBless,
    ToggleAutoLight,
    ToggleAutoGetItems,
    ToggleAutoGetCash,
    ToggleAutoSneak,
    ToggleAutoHide,
    ToggleAutoSearch,
}
