namespace MudPlay.Game.Combat;

// One emit of RoomEntityClassifier.EntitiesObserved — every time the classifier
// consumes a fresh "Also here:" line from the wire. Holds the parsed entity list
// plus the raw line so downstream consumers (CombatStateTracker, StealthManager,
// the unknown-entity fix dialog) don't have to re-parse.
//
// RawAlsoHereLine is the verbatim "Also here: ..." line from the wire, carried
// for the LogPane double-click-to-copy + Unknown-entity fix dialog flow
// (UnknownEntityFixDialogViewModel). Entities are the classified occupants, in
// the order they appeared in the line (left to right). At is the wall-clock
// timestamp of the observation. Source is which classifier path produced this
// emit — defaults to AlsoHere; the synthetic re-fire paths (arrival / death /
// room-change) stamp their own value so the empty-observation diagnosis can name
// the origin.
public readonly record struct RoomEntitiesObservation(
    string RawAlsoHereLine,
    IReadOnlyList<RoomEntity> Entities,
    DateTimeOffset At,
    RoomObservationSource Source = RoomObservationSource.AlsoHere)
{
    // The monster a game line names as it is shown. The roster names a monster in
    // full ("fat half-orc sentry") and resolves it to its record name ("half-orc
    // sentry"); a line may use either.
    public RoomEntity? FindMonster(string shownName)
    {
        string name = shownName.Trim();
        foreach (RoomEntity e in Entities)
        {
            if (e.Kind != EntityKind.Monster) continue;
            if (e.RawName.Equals(name, StringComparison.OrdinalIgnoreCase)
                || e.ResolvedName.Equals(name, StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(" " + e.ResolvedName, StringComparison.OrdinalIgnoreCase))
                return e;
        }
        return null;
    }

    // Whether the roster lists whoever a game line names: a monster as FindMonster
    // takes it, anyone else by the name or its first word, as a player is listed.
    public bool Lists(string shownName)
    {
        if (FindMonster(shownName) is not null) return true;
        string name = shownName.Trim();
        int space = name.IndexOf(' ');
        string given = space > 0 ? name[..space] : name;
        foreach (RoomEntity e in Entities)
            if (e.Kind != EntityKind.Monster
                && (e.RawName.Equals(name, StringComparison.OrdinalIgnoreCase)
                    || e.ResolvedName.Equals(given, StringComparison.OrdinalIgnoreCase)))
                return true;
        return false;
    }
}
