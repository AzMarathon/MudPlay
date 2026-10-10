using MudPlay.ViewModels.GameData.Edit;

namespace MudPlay.ViewModels.Navigation;

// A warning on a route step: the room is a protectable hazard (a river crossing,
// lava, an ice climb, the desert heat…) and/or an exit off it is item-gated (needs
// a rope & grapple, a raft, a ticket…). Rendered with a ⚠ flanking the room name
// plus a sub-line.
//
//   • A hazard carries the harmful cast-on-enter Spell (→ its spell record); the
//     Items are what make the room safe to cross.
//   • A pure item-gated exit has no Spell — the Items are simply what's required to
//     cross.
// Both surface the same way: one or more clickable item records, optionally led by
// the hazard spell. Label reads "cross with" when a hazard spell is named, else
// "needs".
public sealed class RouteStepWarning
{
    // The hazard's harmful cast-on-enter spell → its spell record. Null for a pure
    // item-gated exit (no spell involved).
    public RoomDetailLink? Spell { get; }

    // The item(s) that make the step passable — a hazard's counter items, an exit's
    // required item, or both — each opening its item record.
    public IReadOnlyList<RoomDetailLink> Items { get; }

    // True for a hazard no item makes safe (Crystal Lake's sea rooms): its counter
    // items would only mislead, so they are not listed and the line says so.
    public bool NoProtection { get; }

    public bool HasSpell => Spell is not null;
    public bool HasItems => Items.Count > 0;
    public bool HasLabel => HasItems || NoProtection;

    // "cross with a raft" for a named hazard; "needs rope & grapple" for a bare
    // item gate.
    public string Label => NoProtection
        ? (HasItems ? "— nothing protects from it; needs" : "— nothing protects from it")
        : HasSpell ? "— cross with" : "needs";

    public RouteStepWarning(RoomDetailLink? spell, IReadOnlyList<RoomDetailLink> items, bool noProtection = false)
    {
        Spell = spell;
        Items = items ?? Array.Empty<RoomDetailLink>();
        NoProtection = noProtection;
    }
}
