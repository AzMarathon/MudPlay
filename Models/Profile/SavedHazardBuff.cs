using MudPlay.Services;

namespace MudPlay.Models.Profile;

// One buff kept up against a room hazard, as SavedBuffTimers keeps it.
public sealed class SavedHazardBuff
{
    // The buff's spell number.
    public int BuffSpell { get; set; }

    // What it had left when the timers stopped.
    [SettingRange(-1_000_000, 1_000_000)]
    public int RemainingSec { get; set; }

    // Its own line was seen, as against only the `use` having gone out.
    public bool Confirmed { get; set; }
}
