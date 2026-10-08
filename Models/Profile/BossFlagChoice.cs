namespace MudPlay.Models.Profile;

// One character's Stop before / Grab All choice for a boss, kept in its profile
// (CharacterProfile.BossFlags, by boss Name). A null side is "not chosen": the
// boss's own default applies (BossDef.ResetStopBefore / ResetGrabAll). Only choices
// that differ from the default are stored.
public sealed class BossFlagChoice
{
    public bool? StopBefore { get; set; }
    public bool? GrabAll { get; set; }
}
