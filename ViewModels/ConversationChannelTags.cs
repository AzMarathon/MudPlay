using MudPlay.Game;

namespace MudPlay.ViewModels;

// The short channel tags the Conversation window prints: the chip on each row and the
// label on the header's filter pill. One table, so a pill always reads exactly like the
// rows it shows or hides.
public static class ConversationChannelTags
{
    // The filter pill's label. Both telepath directions share one pill, so it carries the
    // bare tag; a row adds the direction arrow (see Row).
    public static string Pill(ChatChannel c) => c switch
    {
        ChatChannel.Gossip            => "GOS",
        ChatChannel.Local             => "SAY",
        ChatChannel.TelepathIncoming  => "TELE",
        ChatChannel.TelepathOutgoing  => "TELE",
        ChatChannel.Gangpath          => "GANG",
        ChatChannel.Broadcast         => "BCAST",
        ChatChannel.Yell              => "YELL",
        // Paradigm's realm events are server-authored, styled like the
        // Server PvP notices — same red "SERVER" chip.
        ChatChannel.RealmEvent        => "SERVER",
        ChatChannel.Server            => "SERVER",
        // Actions ride the say grouping — same chip, so they read as room-local.
        ChatChannel.Social            => "SAY",
        ChatChannel.DaySeparator      => string.Empty,
        _ => "?",
    };

    // A row's chip. Mirror the telepath arrow so the two directions read as opposites
    // at a glance: an incoming one leads with the arrow (pointing in), an outgoing one
    // trails it (pointing away).
    public static string Row(ChatChannel c) => c switch
    {
        ChatChannel.TelepathIncoming => "←" + Pill(c),
        ChatChannel.TelepathOutgoing => Pill(c) + "→",
        _ => Pill(c),
    };
}
