namespace MudPlay.Game.Remote;

// Chat channels the RemoteCommandManager watches for inbound @-commands and
// routes replies back through. Subset of ChatChannel — exclude the realm-wide
// noise channel (Gossip — also carries auctions), shout-style noise (Yell),
// and RealmEvent (player entrance/exit notices, no sender to gate on). The
// outbound echo (TelepathOutgoing) is excluded because it's our own
// send-back, not an inbound from another player. The synthetic DaySeparator
// has no sender.
public enum RemoteChannel
{
    // "X telepaths: @cmd" — the default channel for remote commands.
    Telepath,

    // "X gangpaths: @cmd" — gang/guild scope.
    Gangpath,

    // "X says ..." in the local room.
    Local,

    // "Broadcast from X "@cmd"" — realm-wide. On Paradigm/GreaterMUD an
    // ordinary player broadcasts by sending `-<text>` (confirmed 2026-09-19,
    // user demonstration: `-teastasdtasdflaskdjfalsdkj` echoed as `Broadcast
    // from Ermias "teastasdtasdflaskdjfalsdkj"`) — it is NOT operator-only
    // there, despite ChatChannel.Broadcast's doc comment describing the stock
    // MajorMUD convention.
    Broadcast,
}
