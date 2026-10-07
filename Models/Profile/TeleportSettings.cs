namespace MudPlay.Models.Profile;

// Per-character "Teleports" settings. Stored as the "Teleports" entry in
// CharacterProfile.Settings.
public sealed class TeleportSettings
{
    // The teleports a walk the client starts on its own may use, each stored as
    // "map/room>map/room" (where it is, where it lands; TeleportCatalog.KeyOf). To
    // the route search a teleport is one step, so it is nearly always on the
    // shortest route, and it can land the character somewhere it can't survive: a
    // call the client can't make. A walk the user starts is asked about on the
    // route cards; a walk nobody is there to ask (a bank or sell trip, an auto-train
    // or spell-buying trip, Auto-Lair, an event, another player's remote command)
    // uses only what is listed here. Empty, the default, is none: such a walk fails
    // and says which teleport it needed. Char-tier; Settings → Teleports lists them
    // from the game data. Read as each automatic walk starts (AutoWalkManager).
    public List<string> AutomaticWalkTeleports { get; set; } = new();
}
