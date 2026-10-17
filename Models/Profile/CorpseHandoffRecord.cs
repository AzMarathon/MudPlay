using System.Linq;
using System.Text.Json.Serialization;
using MudPlay.Game.Inventory;

namespace MudPlay.Models.Profile;

// Another player's corpse this character recovered with `recover corpse <name>
// <password>`: what came out of it, kept until it is handed back or cleared.
// Persisted on CharacterProfile.CorpseHandoffs. The password is never part of it.
public sealed class CorpseHandoffRecord
{
    // The corpse's owner as the game printed the name.
    public string Owner { get; set; } = string.Empty;

    public DateTimeOffset At { get; set; }
    public RoomRef? Room { get; set; }
    public string? RoomName { get; set; }

    // What is still to be handed back. Items and coins come off as the game
    // confirms each give.
    public List<CorpseHandoffItem> Items { get; set; } = new();
    public CurrencyHoldings Coins { get; set; } = CurrencyHoldings.Empty;

    // How the last handoff went, or a note on the recovery. null until there is one.
    public string? Note { get; set; }

    [JsonIgnore]
    public bool IsEmpty => Items.All(i => i.Count <= 0) && Coins.TotalCoinCount == 0;

    [JsonIgnore]
    public string RecoveredText => At == default ? "—" : At.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss");

    [JsonIgnore]
    public string RoomText => (RoomName ?? "Unknown room") + (Room is null ? string.Empty : $" ({Room.Map}/{Room.Room})");

    // One line for the list: the owner, then what is left to hand back.
    [JsonIgnore]
    public string Summary
    {
        get
        {
            List<string> parts = Items.Where(i => i.Count > 0).Select(i => i.ToString()).ToList();
            parts.AddRange(DeathRecord.DescribeCoins(Coins));
            return $"{Owner}: " + (parts.Count == 0 ? "nothing left" : string.Join(", ", parts));
        }
    }
}
