namespace MudPlay.Models.Profile;

// The Party-tab subset a combat profile carries: party healing (the minor / major
// single-target and AOE heals, their member thresholds, the AOE member count). Rank
// and the rest of Settings["Party"] stay per-character. The two bless switches are
// legacy: each buff slot carries its own now, and these are only stored and copied
// so an older profile round-trips.
public sealed class CombatProfileParty
{
    public string? MinorPartyHealSpell { get; set; }
    public string? MinorPartyHealAoeSpell { get; set; }
    public string? MajorPartyHealSpell { get; set; }
    public string? MajorPartyHealAoeSpell { get; set; }
    public int MinorHealMemberThresholdPercent { get; set; } = 70;
    public int MajorHealMemberThresholdPercent { get; set; } = 40;
    public int AoeMinMembers { get; set; } = 2;

    public bool BlessWhileResting { get; set; }
    public bool BlessDuringCombat { get; set; }

    public void CaptureFrom(PartySettings src)
    {
        ArgumentNullException.ThrowIfNull(src);
        MinorPartyHealSpell = src.MinorPartyHealSpell;
        MinorPartyHealAoeSpell = src.MinorPartyHealAoeSpell;
        MajorPartyHealSpell = src.MajorPartyHealSpell;
        MajorPartyHealAoeSpell = src.MajorPartyHealAoeSpell;
        MinorHealMemberThresholdPercent = src.MinorHealMemberThresholdPercent;
        MajorHealMemberThresholdPercent = src.MajorHealMemberThresholdPercent;
        AoeMinMembers = src.AoeMinMembers;
        BlessWhileResting = src.BlessWhileResting;
        BlessDuringCombat = src.BlessDuringCombat;
    }

    // Overlay onto a live PartySettings, leaving Rank and every other party field.
    public void WriteInto(PartySettings dst)
    {
        ArgumentNullException.ThrowIfNull(dst);
        dst.MinorPartyHealSpell = MinorPartyHealSpell;
        dst.MinorPartyHealAoeSpell = MinorPartyHealAoeSpell;
        dst.MajorPartyHealSpell = MajorPartyHealSpell;
        dst.MajorPartyHealAoeSpell = MajorPartyHealAoeSpell;
        dst.MinorHealMemberThresholdPercent = MinorHealMemberThresholdPercent;
        dst.MajorHealMemberThresholdPercent = MajorHealMemberThresholdPercent;
        dst.AoeMinMembers = AoeMinMembers;
        dst.BlessWhileResting = BlessWhileResting;
        dst.BlessDuringCombat = BlessDuringCombat;
    }

    public CombatProfileParty Clone() => (CombatProfileParty)MemberwiseClone();
}
