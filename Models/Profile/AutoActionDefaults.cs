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

    // Pure decision for settling the live engine modes back to the character's
    // base modes (the Settings → General "base modes" checkboxes) — run at every
    // profile load and each loop / auto-lair circuit start. Base is authoritative:
    // the live toolbar is what the user flips mid-route, the base is what the
    // engines return to.
    //   - `baseModes` null (a character from before the base/live split): adopt the
    //     current live modes AS the base so the checkboxes the user already sees
    //     become a concrete, persisted default that drives every future load. This
    //     is the one-time bootstrap that makes base-apply-on-load actually happen
    //     for legacy profiles — otherwise a null base reads back as "equal to live"
    //     and nothing ever applies. Nothing to apply this pass (base == live).
    //   - base set, live already equal: no-op.
    //   - base set, live differs: settle live back to the base.
    public static AutoModeReconcileResult ReconcileToBase(
        AutoActionDefaults? baseModes, AutoActionDefaults live)
    {
        if (baseModes is null)
            return new AutoModeReconcileResult(live.Clone(), live.Clone(),
                BaseSeeded: true, LiveChanged: false);

        if (live.SameAs(baseModes))
            return new AutoModeReconcileResult(baseModes.Clone(), live.Clone(),
                BaseSeeded: false, LiveChanged: false);

        return new AutoModeReconcileResult(baseModes.Clone(), baseModes.Clone(),
            BaseSeeded: false, LiveChanged: true);
    }
}

// Outcome of AutoActionDefaults.ReconcileToBase: the base + live to persist, and
// which of the two changed so the caller knows whether to reseed the toolbar badges
// (LiveChanged) and which log line to emit (BaseSeeded vs reset-to-base).
public readonly record struct AutoModeReconcileResult(
    AutoActionDefaults Base, AutoActionDefaults Live, bool BaseSeeded, bool LiveChanged);
