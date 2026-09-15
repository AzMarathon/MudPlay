namespace MudPlay.ViewModels.GameData.Batch;

// A tri-state batch control for a boolean-ish field (an item/monster flag, a player
// behaviour toggle, or one remote-control permission). "Leave" means the batch
// doesn't touch that field on any selected record; "On"/"Off" set it the same way
// across every selected record. For permissions, On = grant and Off = revoke.
public enum BatchToggle
{
    Leave,
    On,
    Off,
}

public static class BatchToggleExtensions
{
    // Apply the tri-state onto a current bool: Leave keeps it, On/Off force it.
    public static bool Apply(this BatchToggle toggle, bool current) => toggle switch
    {
        BatchToggle.On => true,
        BatchToggle.Off => false,
        _ => current,
    };

    // Nullable variant for overlay fields that distinguish "no override" (null)
    // from an explicit false: On → true, Off → false, Leave → unchanged.
    public static bool? Apply(this BatchToggle toggle, bool? current) => toggle switch
    {
        BatchToggle.On => true,
        BatchToggle.Off => false,
        _ => current,
    };
}
