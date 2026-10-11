namespace MudPlay.Models.Profile;

// One character's two "Entire Loop Settings" for one saved loop, kept in its profile
// (CharacterProfile.LoopSettings, by loop name). Written the first time the character
// sets either for that loop; a loop with no entry runs with what its file holds.
public sealed class LoopSettingsChoice
{
    public bool OnlyAttackInLairRooms { get; set; }

    // A Game.Map.LairEntryDebuffMode by name. A plain string so a profile that names
    // a way of waiting this client doesn't know still loads; it reads as off here.
    public string? LairEntryDebuff { get; set; }
}
