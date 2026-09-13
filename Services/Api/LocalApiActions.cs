using MudPlay.Game.Remote;
using MudPlay.Models.GameData;

namespace MudPlay.Services.Api;

// Action side of the local API: run an @-command, type a raw line at the game,
// or write a settings tab. Every method here runs ON THE UI THREAD — the server
// marshals first, because these reach straight into the engines.
//
// Commands are not reimplemented. RemoteCommandManager already owns ~40 of them
// with their argument parsing and engine plumbing, so this dispatches through
// TryInvokeLocal and collects the replies the handler would have telepathed back.
//
// The destructive split is derived from RemoteCommandCatalog's permission
// categories rather than a hand-kept list here, so a command added to the
// catalog is classified automatically instead of silently defaulting to safe.
public static class LocalApiActions
{
    // Categories whose commands can end the session or do something irreversible.
    // These need Settings.Global.LocalApiAllowDestructive on top of the token.
    //
    // SysopCommands is the catalog's "elevated" bucket (@suicide). HangupDisconnect
    // drops the carrier or relogs — recoverable, but it ends the session out from
    // under whoever is watching, which is exactly the surprise this gate exists to
    // prevent.
    private const PlayerRemoteControls DestructiveCategories =
        PlayerRemoteControls.SysopCommands | PlayerRemoteControls.HangupDisconnect;

    // Whether a command needs the destructive opt-in.
    //
    // An UNKNOWN command counts as destructive. That's deliberate: the safe
    // default for something we can't classify is to refuse it, not to wave it
    // through. (TryInvokeLocal reports unknown commands separately, so this only
    // affects classification, never what actually runs.)
    public static bool IsDestructive(string command)
    {
        if (TryResolveCategory(command, out PlayerRemoteControls category))
            return (category & DestructiveCategories) != 0;
        return true;
    }

    // Whether the catalog knows this command at all. Used only to give a truthful
    // refusal message; IsDestructive remains the safety decision.
    public static bool IsKnown(string command) => TryResolveCategory(command, out _);

    // Resolve a command to its catalog permission category, matching the exact
    // normalisation RemoteCommandManager.TryInvokeLocal applies before dispatch —
    // if these two ever disagree about what a string means, the gate stops
    // guarding what actually runs.
    //
    // Suffix-form commands (@equip-<set>) aren't literal catalog keys, so they
    // resolve through their BASE command and inherit its category. Derived rather
    // than special-cased: the point of reading categories out of the catalog is
    // that a command added later is classified automatically, and a hardcoded
    // "@equip- is safe" is precisely the hand-kept exception that would let a
    // prefix handler registered later with a destructive category be classified
    // safe by omission. Literal keys are tried first, so a command whose own name
    // contains a dash still matches itself.
    private static bool TryResolveCategory(string command, out PlayerRemoteControls category)
    {
        category = default;
        if (string.IsNullOrWhiteSpace(command)) return false;

        string normalised = command.Trim().ToLowerInvariant();
        if (!normalised.StartsWith('@')) normalised = "@" + normalised;

        if (RemoteCommandCatalog.Map.TryGetValue(normalised, out category)) return true;

        int dash = normalised.IndexOf('-');
        return dash > 1 && RemoteCommandCatalog.Map.TryGetValue(normalised[..dash], out category);
    }

    public sealed record CommandOutcome(
        string Command,
        bool Ok,
        string Status,
        IReadOnlyList<string> Replies);

    // Dispatch one @-command. `allowDestructive` is the resolved setting, passed
    // in rather than read here so the gate is decided at one place in the server
    // and this stays a pure function of its inputs.
    public static CommandOutcome Command(
        AppServices svc, string command, IReadOnlyList<string>? args, bool allowDestructive)
    {
        ArgumentNullException.ThrowIfNull(svc);

        // Order matters for the MESSAGE, not for safety. IsDestructive treats an
        // unclassifiable command as destructive, so reporting the destructive
        // refusal first would tell someone who simply typo'd a command to go
        // enable destructive commands. Naming the real problem is safe here
        // because RemoteCommandHandlerCoverageTests IL-scans the assembly to prove
        // the catalog and the registered handlers stay in lockstep in BOTH
        // directions — so "absent from the catalog" really does mean "no handler",
        // and saying so can't wave an unclassified command through.
        if (!IsKnown(command))
            return new CommandOutcome(command, false, "unknown command", []);

        if (IsDestructive(command) && !allowDestructive)
        {
            return new CommandOutcome(command, false,
                "destructive commands are not enabled — turn on \"Allow destructive commands\" "
                + "under Settings → General → Local control API",
                []);
        }

        List<string> replies = [];
        RemoteCommandManager.LocalInvokeResult result =
            svc.RemoteCommands.TryInvokeLocal(command, args, r => replies.Add(r));

        string status = result switch
        {
            RemoteCommandManager.LocalInvokeResult.Ok => "ok",
            RemoteCommandManager.LocalInvokeResult.UnknownCommand => "unknown command",
            RemoteCommandManager.LocalInvokeResult.Disabled =>
                "remote commands are disabled (Settings → Talk)",
            RemoteCommandManager.LocalInvokeResult.HardBlocked =>
                "hard-blocked — denied by any route",
            _ => "failed",
        };
        return new CommandOutcome(command,
            result == RemoteCommandManager.LocalInvokeResult.Ok, status, replies);
    }

    // Type a line at the game exactly as if the user had typed it in the terminal.
    //
    // This is the most powerful endpoint in the API and intentionally the least
    // clever: it is a passthrough, so anything the game accepts, it accepts. It is
    // classified DESTRUCTIVE for that reason — a raw line can carry `suicide` just
    // as easily as `look`, and no amount of inspection here would reliably tell
    // them apart.
    public static CommandOutcome Send(AppServices svc, string line, bool allowDestructive)
    {
        ArgumentNullException.ThrowIfNull(svc);
        if (string.IsNullOrWhiteSpace(line))
            return new CommandOutcome("(send)", false, "empty line", []);

        if (!allowDestructive)
        {
            return new CommandOutcome("(send)", false,
                "raw send is treated as destructive because it passes any command straight "
                + "to the game — enable \"Allow destructive commands\" to use it", []);
        }

        // Single line only. A newline would let one request smuggle several
        // commands past the game's own rate limiting, and the caller should be
        // making that pacing decision explicitly.
        if (line.Contains('\n') || line.Contains('\r'))
            return new CommandOutcome("(send)", false, "one line per request", []);

        // SendTypedInput, NOT SendGameCommand. The typed path runs macro split,
        // alias expansion and the outbound observers, so a send is
        // indistinguishable from the user typing it. SendGameCommand rides the raw
        // wire with none of that — a `/send n` through it would move the character
        // without the room tracker ever observing the move, desyncing position and
        // handing the recovery ladder a problem it didn't need.
        svc.SendTypedInput(line);
        return new CommandOutcome("(send)", true, "sent", [line]);
    }
}
