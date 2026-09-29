namespace MudPlay.Models.Profile;

// The character's max HP / power pool with its Default gear set worn, read off a
// `stat` screen — the basis every rest / heal / flee percentage resolves against.
// Recorded only in Default gear, so a Pre-rest set or a stale screen can't shift it;
// it goes stale when the level or the Default set's own max-pool bonus changes (see
// Game.Health.DefaultPoolBaselineKeeper). Persisted on CharacterProfile.
public sealed class DefaultPoolBaseline
{
    public int MaxHp { get; set; }
    public int MaxMa { get; set; }
    public int Level { get; set; }
    // The Default set's summed +MaxHP / +MaxMana item bonus when it was recorded.
    public int DefaultGearHp { get; set; }
    public int DefaultGearMa { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
}
