using Avalonia.Media;
using MudPlay.Game;

namespace MudPlay.ViewModels;

// One displayed row in the ConversationViewModel's filtered list. Wraps a
// ChatLogEntry with the channel-color brush + timestamp / channel-tag /
// speaker / message display strings the XAML binds to.
public sealed class ConversationRowViewModel
{
    public ChatLogEntry Entry { get; }

    public string TimestampText { get; }
    public string ChannelText { get; }
    public string SpeakerText { get; }
    public string MessageText { get; }
    // Accent brush for the channel tag + speaker; MessageBrush colours the body.
    public IBrush ChannelBrush { get; }
    public IBrush MessageBrush { get; }
    public bool IsDaySeparator => Entry.Channel == ChatChannel.DaySeparator;

    // Whether this row has a speaker prefix. Actions/emotes and server notices have
    // none, so the XAML collapses that column — otherwise its margin leaves an extra
    // gap between the chip and the message.
    public bool HasSpeaker => !string.IsNullOrEmpty(SpeakerText);

    // Plain-text form of the row for clipboard copy — the line as it reads on screen
    // (time + who + message), or just the date for a day separator.
    public string CopyText => IsDaySeparator
        ? MessageText
        : string.IsNullOrEmpty(SpeakerText)
            ? $"{TimestampText}  {MessageText}"
            : $"{TimestampText}  {SpeakerText} {MessageText}";

    public ConversationRowViewModel(ChatLogEntry entry, Func<ChatChannel, IBrush> brushLookup, Func<ChatChannel, IBrush> textBrushLookup)
    {
        Entry = entry;
        TimestampText = entry.Timestamp.ToLocalTime().ToString("HH:mm:ss");
        ChannelText   = ChannelAbbrev(entry.Channel);
        SpeakerText   = FormatSpeaker(entry);
        MessageText   = entry.Message;
        ChannelBrush  = brushLookup(entry.Channel);
        // Realm / server notices read as one coloured announcement — colour the whole
        // message the chip's accent (e.g. red) rather than the body brush, which would
        // leave the sentence white after a red "who". Other channels keep the body brush.
        MessageBrush  = entry.Channel is ChatChannel.Server or ChatChannel.RealmEvent
            ? ChannelBrush
            : textBrushLookup(entry.Channel);
    }

    private static string FormatSpeaker(ChatLogEntry entry)
    {
        if (entry.Channel == ChatChannel.DaySeparator) return string.Empty;

        // Server announcements have no speaker — the SERVER badge is the "who",
        // and the message is a full sentence. No prefix column at all.
        if (entry.Channel == ChatChannel.Server) return string.Empty;

        // An action/emote is a full self-contained sentence ("Fujin hugs you
        // close!" / "You wave to Suijin!") — the actor is in the text, so no
        // speaker prefix; the whole green line is the message.
        if (entry.Channel == ChatChannel.Social) return string.Empty;

        // Speaker is null for self-actions whose regex didn't capture a
        // name (e.g. "You yell ..." — the Megamind regex matches the verb
        // shape literally). Surface those as "You" so every chat row has
        // a consistent "<who>" prefix.
        string speaker = string.IsNullOrEmpty(entry.Speaker) ? "You" : entry.Speaker!;

        // A directed say names its target so the reader knows who it's aimed at, even
        // as a third party overhearing it: "Suijin (to Fujin): hi", or "You (to Fujin):
        // hi" for our own outgoing one.
        if (!string.IsNullOrEmpty(entry.DirectedTo))
            return $"{speaker} (to {entry.DirectedTo}):";

        // RealmEvent rows read as a sentence ("Raijin entered the Realm")
        // so the trailing colon would feel wrong. Every other channel
        // pairs "<who>" with the message body and gets the colon.
        return entry.Channel == ChatChannel.RealmEvent ? speaker : speaker + ":";
    }

    private static string ChannelAbbrev(ChatChannel c) => c switch
    {
        ChatChannel.Gossip            => "GOS",
        ChatChannel.Local             => "SAY",
        // Mirror the arrow so the two directions read as opposites at a
        // glance: an incoming telepath leads with the arrow (pointing in),
        // an outgoing one trails it (pointing away).
        ChatChannel.TelepathIncoming  => "←TELE",
        ChatChannel.TelepathOutgoing  => "TELE→",
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
}
