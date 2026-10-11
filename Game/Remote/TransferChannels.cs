using System.Collections.Generic;
using System.Linq;

namespace MudPlay.Game.Remote;

// The three exchanges in which one client hands another a body of data over chat.
public enum Transfer
{
    // `@loop send`: a saved loop.
    LoopSend,
    // `@roomba sync`: the gang house's room labels and item sightings.
    RoombaSync,
    // `@timer sync`: the boss timers being tracked.
    TimerSync,
}

// Which chat channels each transfer may go by (user, 2026-10-11: "@loop send should
// only be sent via telepath, roomba sync as well, timers sync can be say, telepath,
// gangpath, broadcast", then "roomba sync should also be allowed via gangpath").
//
// One table for both ends of every transfer. The client asked refuses a request
// that comes by a channel its transfer does not use, and the client asking takes no
// data that comes by one.
//
// A loop goes to the one player who asked: on a shared channel every client there
// answered the ask, and a yes named nobody. The gang house belongs to a gang, so its
// log may go to the gang. Boss timers are no one's secret and may go anywhere
// players talk.
public static class TransferChannels
{
    private static readonly Dictionary<Transfer, RemoteChannel[]> Allowed = new()
    {
        [Transfer.LoopSend] = new[] { RemoteChannel.Telepath },
        [Transfer.RoombaSync] = new[] { RemoteChannel.Telepath, RemoteChannel.Gangpath },
        [Transfer.TimerSync] = new[]
        {
            RemoteChannel.Local, RemoteChannel.Telepath, RemoteChannel.Gangpath, RemoteChannel.Broadcast,
        },
    };

    public static bool Allows(Transfer transfer, RemoteChannel channel) => Allowed[transfer].Contains(channel);

    // The same for a line another player sent, as the chat log has it: a transfer's
    // data, or its answer. A channel no remote command travels by (gossip, yell) is
    // never allowed, nor is the echo of a telepath of our own.
    public static bool Allows(Transfer transfer, ChatChannel heardOn) =>
        Of(heardOn) is { } remote && Allows(transfer, remote);

    // The remote channel a line from another player arrived by, or null for a
    // channel that carries no remote command.
    public static RemoteChannel? Of(ChatChannel heardOn) => heardOn switch
    {
        ChatChannel.TelepathIncoming => RemoteChannel.Telepath,
        ChatChannel.Gangpath => RemoteChannel.Gangpath,
        ChatChannel.Local => RemoteChannel.Local,
        ChatChannel.Broadcast => RemoteChannel.Broadcast,
        _ => null,
    };

    // The remote channel a line of our own went out by, as the chat log shows it
    // back to us: the "telepath sent" echo, or our own words on a shared channel.
    public static RemoteChannel? OfOurOwn(ChatChannel echoedOn) =>
        echoedOn == ChatChannel.TelepathOutgoing ? RemoteChannel.Telepath
        : echoedOn == ChatChannel.TelepathIncoming ? null
        : Of(echoedOn);

    // The command as a player types it.
    public static string CommandOf(Transfer transfer) => transfer switch
    {
        Transfer.LoopSend => "@loop send",
        Transfer.RoombaSync => "@roomba sync",
        _ => "@timer sync",
    };

    // "telepath", "telepath or gangpath", "say, telepath, gangpath or broadcast".
    public static string Describe(Transfer transfer)
    {
        string[] names = Allowed[transfer].Select(NameOf).ToArray();
        return names.Length == 1 ? names[0] : string.Join(", ", names[..^1]) + " or " + names[^1];
    }

    // What the client asked answers a request that came by the wrong channel, and
    // what the client asking shows its own user: it says what to do instead.
    public static string Refusal(Transfer transfer) =>
        $"{CommandOf(transfer)} works by {Describe(transfer)} only";

    // Which transfer a command and its arguments ask for, if any: `@loop send …`,
    // `@roomba sync`, `@timer sync`. The other forms of those three commands
    // (`@loop <name>`, `@roomba <item>`, `@timer`) are ordinary remote commands.
    public static Transfer? Identify(string command, IReadOnlyList<string> args)
    {
        if (args.Count == 0) return null;
        (string Command, string Verb, Transfer Transfer)[] forms =
        {
            ("@loop", LoopShareHandler.SendVerb, Transfer.LoopSend),
            ("@roomba", "sync", Transfer.RoombaSync),
            ("@timer", "sync", Transfer.TimerSync),
        };
        foreach ((string name, string verb, Transfer transfer) in forms)
            if (command.Equals(name, StringComparison.OrdinalIgnoreCase)
                && args[0].Equals(verb, StringComparison.OrdinalIgnoreCase))
                return transfer;
        return null;
    }

    // The asking side sees its own request go out in the chat log: which transfer
    // that line asks for and the channel it went by, or null when the line is
    // another player's or asks for none. A telepath's echo is always ours; on a
    // shared channel ours is the line with no speaker ("You say") or with our own
    // name, which isOurName tells.
    public static (Transfer Transfer, RemoteChannel Channel)? OwnRequest(ChatLogEntry e, Func<string, bool> isOurName)
    {
        ArgumentNullException.ThrowIfNull(isOurName);
        if (OfOurOwn(e.Channel) is not { } channel) return null;
        if (e.Channel != ChatChannel.TelepathOutgoing && e.Speaker is { } speaker && !isOurName(speaker)) return null;
        string[] words = (e.Message ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 2 || Identify(words[0], words[1..]) is not { } transfer) return null;
        return (transfer, channel);
    }

    // Whether a request of ours seen going out can be taken as the user's own, as
    // against one the client sent for another player (their `@do`, their
    // relay-back). For a telepath that is known: telepathWasUsers is the chat
    // log's word for the confirmation in hand (ChatRouter.OutgoingTelepathWasUsers).
    // A line on a shared channel comes back with no word of whose send it was, and
    // is taken as the user's.
    public static bool IsUsersOwn(RemoteChannel channel, bool telepathWasUsers) =>
        channel != RemoteChannel.Telepath || telepathWasUsers;

    // A channel as the log and a refusal name it.
    public static string NameOf(RemoteChannel channel) => channel switch
    {
        RemoteChannel.Telepath => "telepath",
        RemoteChannel.Gangpath => "gangpath",
        RemoteChannel.Local => "say",
        _ => "broadcast",
    };
}
