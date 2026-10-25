namespace MudPlay.Game.Spells;

public sealed partial class CastingDirector
{
    public Func<bool>? GmcpOwnsEffects { get; set; }
    public Func<string, (bool Present, DateTime? Until)>? GmcpSelfEffect { get; set; }
    public Func<IReadOnlyList<ActiveBuffTimer>>? GmcpTimers { get; set; }
}
