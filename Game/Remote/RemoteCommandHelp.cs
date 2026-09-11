namespace MudPlay.Game.Remote;

// The user-facing help for one remote @-command: its argument syntax and a
// one-line description. Surfaced by `@help <command>`. Sourced from the
// remote-@-command section of the Help guide, kept terse enough that
// "Syntax — Description" fits a single telepath reply line.
public readonly record struct RemoteCommandHelp(string Syntax, string Description);
