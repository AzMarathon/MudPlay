using MudPlay.Game.Map;

namespace MudPlay.Game.Pvp;

// A way out of the room a chase may take. Only exits that can simply be walked are
// offered: an open way, or a door that just opens (Door). A door that needs a key,
// picking or strength, and any exit that takes a command or a search, is left out.
public readonly record struct PvpChaseExit(Direction Way, bool Door);
