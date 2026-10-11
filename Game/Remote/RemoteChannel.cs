namespace MudPlay.Game.Remote;

// Chat channels the RemoteCommandManager watches for inbound @-commands and
// routes replies back through. Subset of ChatChannel — exclude system-level
// (RealmEvent), realm-wide noise channels (Gossip — also carries auctions), and
// shout-style noise (Yell). The outbound echo (TelepathOutgoing) is excluded
// because it's our own send-back, not an inbound from another player. The
// synthetic DaySeparator has no sender.
public enum RemoteChannel
{
    // "X telepaths: @cmd" — the default channel for remote commands.
    Telepath,

    // "X gangpaths: @cmd" — gang/guild scope.
    Gangpath,

    // "X says ..." in the local room.
    Local,

    // "Broadcast from X "@cmd"" — the game's numbered channel (`join <n>`, spoken
    // with `br`). Public, so read only once the user opens it on the Talk tab;
    // then every remote command is, under the same gates as on the other three,
    // and what is said back is bounded (BroadcastAnswerBudget).
    Broadcast,
}
