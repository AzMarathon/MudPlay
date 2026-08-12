namespace MudPlay.Models.Profile;

// Initial state for every Action-menu auto-toggle when the character
// logs in. Lives on GeneralSettings twice — once per Manual-Mode column
// and once per Auto-Mode column — so the user can pick which engines come
// up engaged depending on the play mode. The engines read these flags as
// their boot-up state.
//
// Field set mirrors the Action menu's auto-toggle group exactly
// (Combat / Nuke / Heal-Rest / Bless / Light / Get-Items / Get-Cash /
// Sneak / Hide / Search).
//
// The on-by-default set mirrors the default toolbar's auto row: Combat / Nuke /
// Heal-Rest / Bless / Get-Items / Get-Cash / Sneak boot engaged. Light / Hide /
// Search default off.
public sealed class AutoActionDefaults
{
    public bool AutoCombat   { get; set; } = true;
    public bool AutoNuke     { get; set; } = true;
    public bool AutoHealRest { get; set; } = true;
    public bool AutoBless    { get; set; } = true;
    public bool AutoLight    { get; set; }
    public bool AutoGetItems { get; set; } = true;
    public bool AutoGetCash  { get; set; } = true;
    public bool AutoSneak    { get; set; } = true;
    public bool AutoHide     { get; set; }
    public bool AutoSearch   { get; set; }

    // Independent copy — the base-modes reconcile clones the base onto the live
    // AutoMode so the two never share a reference.
    public AutoActionDefaults Clone() => new()
    {
        AutoCombat   = AutoCombat,
        AutoNuke     = AutoNuke,
        AutoHealRest = AutoHealRest,
        AutoBless    = AutoBless,
        AutoLight    = AutoLight,
        AutoGetItems = AutoGetItems,
        AutoGetCash  = AutoGetCash,
        AutoSneak    = AutoSneak,
        AutoHide     = AutoHide,
        AutoSearch   = AutoSearch,
    };

    // Value equality over every engine flag — lets the reconcile skip a write
    // when the live state already matches the base (no needless Save / log line).
    public bool SameAs(AutoActionDefaults o) =>
        o is not null
        && AutoCombat   == o.AutoCombat
        && AutoNuke     == o.AutoNuke
        && AutoHealRest == o.AutoHealRest
        && AutoBless    == o.AutoBless
        && AutoLight    == o.AutoLight
        && AutoGetItems == o.AutoGetItems
        && AutoGetCash  == o.AutoGetCash
        && AutoSneak    == o.AutoSneak
        && AutoHide     == o.AutoHide
        && AutoSearch   == o.AutoSearch;
}
