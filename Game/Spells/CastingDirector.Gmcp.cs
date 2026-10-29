namespace MudPlay.Game.Spells;

public sealed partial class CastingDirector
{
    public Func<bool>? GmcpOwnsEffects { get; set; }
    // A null presence means this cast token cannot be mapped to a server spell id.
    public Func<string, (bool? Present, DateTime? Until)>? GmcpSelfEffect { get; set; }
    public Func<IReadOnlyList<ActiveBuffTimer>>? GmcpTimers { get; set; }
}
