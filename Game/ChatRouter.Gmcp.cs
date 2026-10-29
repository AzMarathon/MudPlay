using System.Text.Json;

namespace MudPlay.Game;

// The server's chat events (Anomaly.Comm.Line), raised as the entries the text
// classification gives, with the raw line a trigger pattern written for the text
// would match.
public sealed partial class ChatRouter
{
    public Func<ChatChannel, bool>? GmcpOwnsChannel { get; set; }
    public Func<string?>? LocalName { get; set; }

    // Every entry the text classified, raised whether or not the server owns its
    // channel; EntryClassified then carries only the copy that is kept.
    public event Action<ChatLogEntry>? TextClassified;

    public static bool HasGmcpChannel(ChatChannel channel) =>
        channel is ChatChannel.Gossip or ChatChannel.Local or ChatChannel.TelepathIncoming or ChatChannel.Gangpath
            or ChatChannel.Broadcast or ChatChannel.Yell or ChatChannel.Party;

    public void ApplyGmcpChat(JsonElement value)
    {
        string kind = value.GetProperty("channel").GetString()!;
        ChatChannel channel = kind switch
        {
            "say" => ChatChannel.Local,
            "yell" => ChatChannel.Yell,
            "telepath" => ChatChannel.TelepathIncoming,
            "gangpath" => ChatChannel.Gangpath,
            "gossip" or "auction" => ChatChannel.Gossip,
            "broadcast" => ChatChannel.Broadcast,
            "party" => ChatChannel.Party,
            _ => ChatChannel.Server,
        };
        string? from = value.GetProperty("from").GetString();
        bool self = from is not null && from.Equals(LocalName?.Invoke()?.Split(' ')[0], StringComparison.OrdinalIgnoreCase);
        // Our own say and yell read "You say"; every other line names us as others see us.
        string? speaker = self && kind is "say" or "yell" ? null : from ?? "Someone";
        string message = value.GetProperty("text").GetString()!;
        string who = speaker ?? "You";
        string raw = kind switch
        {
            "say" => $"{who} {(self ? "say" : "says")} \"{message}\"",
            "yell" => $"{who} {(self ? "yell" : "yells")} \"{message}\"",
            "broadcast" => $"Broadcast from {from ?? "Someone"} \"{message}\"",
            _ => $"{who} {Verb(kind)}: {message}",
        };
        ReleaseHeldSocials();
        EntryClassified?.Invoke(new(_now(), channel, speaker, message, raw));
    }

    private static string Verb(string kind) => kind switch
    {
        "telepath" => "telepaths",
        "gangpath" => "gangpaths",
        "gossip" => "gossips",
        "auction" => "auctions",
        _ => kind,
    };
}
