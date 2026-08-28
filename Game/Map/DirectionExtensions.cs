namespace MudPlay.Game.Map;

// Display helpers for Direction — the single home for the enum →
// human-readable name mapping that UI labels and @-command replies share,
// so the wording can't drift between surfaces.
public static class DirectionExtensions
{
    // Long-form lowercase name as MajorMUD prints it in the
    // "Obvious exits:" line — "north", "southeast", "up". Matches
    // RoomDisplayParser's inbound token vocabulary so a reply reads in the
    // same words the game itself uses.
    public static string ToLongName(this Direction d) => d switch
    {
        Direction.N  => "north",
        Direction.S  => "south",
        Direction.E  => "east",
        Direction.W  => "west",
        Direction.NE => "northeast",
        Direction.NW => "northwest",
        Direction.SE => "southeast",
        Direction.SW => "southwest",
        Direction.U  => "up",
        Direction.D  => "down",
        _            => d.ToString(),
    };

    // The abbreviation the client sends on the wire for a cardinal move — "n",
    // "ne", "u". The short forms `TryFromToken` accepts, going the other way; used
    // by the route step list to show the exact command each hop executes.
    public static string ToToken(this Direction d) => d switch
    {
        Direction.N  => "n",
        Direction.S  => "s",
        Direction.E  => "e",
        Direction.W  => "w",
        Direction.NE => "ne",
        Direction.NW => "nw",
        Direction.SE => "se",
        Direction.SW => "sw",
        Direction.U  => "u",
        Direction.D  => "d",
        _            => d.ToString().ToLowerInvariant(),
    };

    // Parse a movement TOKEN — either the abbreviation (`n`, `ne`, `u`) or the
    // long word (`north`, `northeast`, `up`) — to a Direction. This is the typed-
    // command vocabulary (both forms honoured on the wire), distinct from
    // TryFromLongName's prose-only forms. Returns false for a non-direction token
    // (a text exit like `hole` / `path`), which the caller must send verbatim
    // rather than as a bare direction. Case-insensitive.
    public static bool TryFromToken(string? token, out Direction direction)
    {
        switch (token?.Trim().ToLowerInvariant())
        {
            case "n":  case "north":     direction = Direction.N;  return true;
            case "s":  case "south":     direction = Direction.S;  return true;
            case "e":  case "east":      direction = Direction.E;  return true;
            case "w":  case "west":      direction = Direction.W;  return true;
            case "ne": case "northeast": direction = Direction.NE; return true;
            case "nw": case "northwest": direction = Direction.NW; return true;
            case "se": case "southeast": direction = Direction.SE; return true;
            case "sw": case "southwest": direction = Direction.SW; return true;
            case "u":  case "up":        direction = Direction.U;  return true;
            case "d":  case "down":      direction = Direction.D;  return true;
            default:   direction = default;                        return false;
        }
    }

    // Inverse of ToLongName — the long-form word the game prints back to a
    // Direction. Used where a movement direction arrives as a game word rather
    // than a typed command (the party-follow drag line). Case-insensitive; only
    // the long forms are accepted (the abbreviations never appear in prose).
    public static bool TryFromLongName(string? word, out Direction direction)
    {
        switch (word?.Trim().ToLowerInvariant())
        {
            case "north":     direction = Direction.N;  return true;
            case "south":     direction = Direction.S;  return true;
            case "east":      direction = Direction.E;  return true;
            case "west":      direction = Direction.W;  return true;
            case "northeast": direction = Direction.NE; return true;
            case "northwest": direction = Direction.NW; return true;
            case "southeast": direction = Direction.SE; return true;
            case "southwest": direction = Direction.SW; return true;
            case "up":        direction = Direction.U;  return true;
            case "down":      direction = Direction.D;  return true;
            default:          direction = default;      return false;
        }
    }
}
