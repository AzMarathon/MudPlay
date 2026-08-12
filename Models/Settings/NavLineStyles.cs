namespace MudPlay.Models.Settings;

// Per-line navigation-line appearance overrides (Global tier — one look for every
// character). Delta only: a null line, or a null field within a line, resolves to
// NavLineDefaults. Explicit fields (not a dictionary) keep the JSON readable and
// the schema self-documenting.
public sealed class NavLineStyles
{
    public NavLineStyle? Goto { get; set; }
    public NavLineStyle? Loop { get; set; }
    public NavLineStyle? Preview { get; set; }
    public NavLineStyle? LoopBuilder { get; set; }
    public NavLineStyle? AutoLair { get; set; }

    public NavLineStyle? Get(NavLineKind kind) => kind switch
    {
        NavLineKind.Goto        => Goto,
        NavLineKind.Loop        => Loop,
        NavLineKind.Preview     => Preview,
        NavLineKind.LoopBuilder => LoopBuilder,
        NavLineKind.AutoLair    => AutoLair,
        _                       => null,
    };

    public void Set(NavLineKind kind, NavLineStyle? style)
    {
        switch (kind)
        {
            case NavLineKind.Goto:        Goto = style;        break;
            case NavLineKind.Loop:        Loop = style;        break;
            case NavLineKind.Preview:     Preview = style;     break;
            case NavLineKind.LoopBuilder: LoopBuilder = style; break;
            case NavLineKind.AutoLair:    AutoLair = style;    break;
        }
    }

    // Resolve a line to its effective (colour hex, thickness), falling back to the
    // factory default for any unset field. This is what the map renders from.
    public (string Hex, double Thickness) Resolve(NavLineKind kind)
    {
        (string defHex, double defThick, _) = NavLineDefaults.For(kind);
        NavLineStyle? o = Get(kind);
        return (o?.Color ?? defHex, o?.Thickness ?? defThick);
    }

    // True when nothing is customised — every line resolves to its default. Lets
    // the writer drop the whole object from Global settings rather than persist an
    // all-null husk.
    public bool IsEmpty =>
        Goto is null && Loop is null && Preview is null && LoopBuilder is null && AutoLair is null;
}
