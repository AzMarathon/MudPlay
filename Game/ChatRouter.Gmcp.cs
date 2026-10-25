using System.Text.Json;

namespace MudPlay.Game;

public sealed partial class ChatRouter
{
    public Func<ChatChannel, bool>? GmcpOwnsChannel { get; set; }
    public Func<string?>? LocalName { get; set; }
    public event Action<ChatLogEntry>? TextClassified;

    public static bool HasGmcpChannel(ChatChannel channel) => channel is ChatChannel.Gossip or ChatChannel.Local
        or ChatChannel.TelepathIncoming or ChatChannel.Gangpath or ChatChannel.Broadcast or ChatChannel.Yell or ChatChannel.Party;

    public void ApplyGmcpChat(JsonElement value)
    {
        string kind = value.GetProperty("channel").GetString()!;
        ChatChannel channel = kind switch
        {
            "say" => ChatChannel.Local, "yell" => ChatChannel.Yell, "telepath" => ChatChannel.TelepathIncoming,
            "gangpath" => ChatChannel.Gangpath, "gossip" => ChatChannel.Gossip, "auction" => ChatChannel.Gossip,
            "broadcast" => ChatChannel.Broadcast, "party" => ChatChannel.Party, _ => ChatChannel.Server
        };
        string? from = value.GetProperty("from").GetString();
        bool self = from is not null && from.Equals(LocalName?.Invoke()?.Split(' ')[0], StringComparison.OrdinalIgnoreCase);
        string? speaker = self && kind is ("say" or "yell") ? null : from ?? "Someone";
        string message = value.GetProperty("text").GetString()!;
        string who = speaker ?? "You";
        string raw = kind switch
        {
            "say" => $"{who} {(self ? "say" : "says")} \"{message}\"",
            "yell" => $"{who} {(self ? "yell" : "yells")} \"{message}\"",
            "broadcast" => $"Broadcast from {from ?? "Someone"} \"{message}\"",
            _ => $"{who} {kind switch { "telepath" => "telepaths", "gangpath" => "gangpaths", "gossip" => "gossips", "auction" => "auctions", _ => kind }}: {message}"
        };
        ReleaseHeldSocials();
        EntryClassified?.Invoke(new(_now(), channel, speaker, message, raw));
    }
}
