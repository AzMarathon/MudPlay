namespace MudPlay.Models.Profile;

// One line of a `stat` screen's active-effect list as kept in LastKnownStats: the
// text without its remaining-time tail, and whether the screen printed one.
public sealed class SavedEffectLine
{
    public string Text { get; set; } = string.Empty;
    public bool Timed { get; set; }
}
