using System.Text.Json;
using MudPlay.Models.Profile;
using Xunit;

namespace MudPlay.Tests;

/// <summary>
/// Coverage for the persistable shape of <see cref="PartySettings"/>.
/// The Settings.Party section VM itself does I/O through
/// <see cref="Services.ProfileService"/> and the global
/// <see cref="Services.AppServices"/> singleton — exercising it cleanly
/// from a unit test would require swapping the singleton, which is out
/// of scope. The settings round-trip is the load-bearing piece worth
/// pinning here.
/// </summary>
public sealed class PartySectionViewModelTests
{
    [Fact]
    public void PartySettings_RoundTripsThroughJson()
    {
        PartySettings src = new()
        {
            ParPollFrequencySec        = 12,
            AutoInviteReconnecting     = false,
            ResetStatisticsOnLoopStart = false,
            Rank                       = PartyRank.Back,
        };

        string json = JsonSerializer.Serialize(src);
        PartySettings? back = JsonSerializer.Deserialize<PartySettings>(json);

        Assert.NotNull(back);
        Assert.Equal(12,           back!.ParPollFrequencySec);
        Assert.False(back.AutoInviteReconnecting);
        Assert.False(back.ResetStatisticsOnLoopStart);
        Assert.Equal(PartyRank.Back, back.Rank);
    }

    [Fact]
    public void PartySettings_Defaults_MatchPhase6Spec()
    {
        // Phase 6 spec: par poll 5 s, auto-invite on, auto-exp-reset on,
        // rank Mid. The defaults need to be stable because a freshly
        // loaded profile with no Party entry falls through to these
        // values for every consumer (Settings.Party UI + every Phase 6
        // service the VM pushes to).
        PartySettings dto = new();
        Assert.Equal(5,            dto.ParPollFrequencySec);
        Assert.True(dto.AutoInviteReconnecting);
        Assert.True(dto.ResetStatisticsOnLoopStart);
        Assert.Equal(PartyRank.Mid, dto.Rank);
        // "If leading, wait only" — drives the disconnect grace window
        // used by the Re-invite lost party members flow. Default 90 s.
        Assert.Equal(90,           dto.IfLeadingWaitTotalSec);
        // Vitals gate disabled by default — 0 means PartyVitalsWatcher
        // never holds the loop until the user sets a threshold.
        Assert.Equal(0,            dto.WaitIfMemberBelowPercent);
        // Party-scoped max-monsters default mirrors the Combat cap (no-op
        // until tightened).
        Assert.Equal(20,           dto.MaxMonstersWhenPartying);
        // Party-bless gating defaults OFF — both are opt-in overrides, matching
        // self-bless. The normal cadence buffs the party while moving / idle /
        // idly resting and holds only during combat and a triggered recovery
        // rest unless the user opts in.
        Assert.False(dto.BlessWhileResting);
        Assert.False(dto.BlessDuringCombat);
    }

    [Fact]
    public void BlessGates_RoundTripThroughJson()
    {
        PartySettings src = new()
        {
            BlessWhileResting = false,
            BlessDuringCombat = false,
        };
        string json = JsonSerializer.Serialize(src);
        PartySettings? back = JsonSerializer.Deserialize<PartySettings>(json);
        Assert.NotNull(back);
        Assert.False(back!.BlessWhileResting);
        Assert.False(back.BlessDuringCombat);
    }

    [Fact]
    public void IfLeadingWaitTotalSec_RoundTripsThroughJson()
    {
        PartySettings src = new() { IfLeadingWaitTotalSec = 305 };
        string json = JsonSerializer.Serialize(src);
        PartySettings? back = JsonSerializer.Deserialize<PartySettings>(json);
        Assert.NotNull(back);
        Assert.Equal(305, back!.IfLeadingWaitTotalSec);
    }

    [Fact]
    public void WaitIfMemberBelowPercent_RoundTripsThroughJson()
    {
        PartySettings src = new() { WaitIfMemberBelowPercent = 65 };
        string json = JsonSerializer.Serialize(src);
        PartySettings? back = JsonSerializer.Deserialize<PartySettings>(json);
        Assert.NotNull(back);
        Assert.Equal(65, back!.WaitIfMemberBelowPercent);
    }

    [Fact]
    public void NagToggles_DefaultOn()
    {
        // Both nag master-switches default ON so existing behaviour
        // (always send @join follow-ups and on-join @health round-trips)
        // is preserved for profiles with no explicit Party delta.
        PartySettings dto = new();
        Assert.True(dto.SendJoinToInvited);
        Assert.True(dto.SendHealthToMembers);
    }

    [Fact]
    public void SendHealthToMembers_RoundTripsThroughJson()
    {
        PartySettings src = new() { SendHealthToMembers = false };
        string json = JsonSerializer.Serialize(src);
        PartySettings? back = JsonSerializer.Deserialize<PartySettings>(json);
        Assert.NotNull(back);
        Assert.False(back!.SendHealthToMembers);
    }

    [Fact]
    public void BlessSlots_DefaultToTenEmpties()
    {
        PartySettings dto = new();
        Assert.Equal(PartySettings.PartyBlessSlotCount, dto.BlessSlots.Count);
        Assert.Equal(10, dto.BlessSlots.Count);
        Assert.All(dto.BlessSlots, s =>
        {
            Assert.Null(s.Spell);
            Assert.Empty(s.ClassNumbers);
            // A fresh slot carries the shared default recast lead.
            Assert.Equal(SpellsSettings.DefaultBlessRecastMarginSec, s.RecastMarginSec);
        });
    }

    [Fact]
    public void BlessSlots_RoundTripSpellAndClassSet()
    {
        PartySettings src = new();
        src.BlessSlots[0].Spell = "bles";
        src.BlessSlots[0].ClassNumbers = new() { 1, 4, 9 };
        src.BlessSlots[0].RecastMarginSec = 25;
        src.BlessSlots[3].Spell = "shie";
        src.BlessSlots[3].ClassNumbers = new() { 2 };
        src.BlessSlots[3].RecastMarginSec = 0;   // wait-for-expiry

        string json = JsonSerializer.Serialize(src);
        PartySettings? back = JsonSerializer.Deserialize<PartySettings>(json);

        Assert.NotNull(back);
        Assert.Equal(10, back!.BlessSlots.Count);
        Assert.Equal("bles", back.BlessSlots[0].Spell);
        Assert.Equal(new[] { 1, 4, 9 }, back.BlessSlots[0].ClassNumbers);
        Assert.Equal(25, back.BlessSlots[0].RecastMarginSec);
        Assert.Equal("shie", back.BlessSlots[3].Spell);
        Assert.Equal(new[] { 2 }, back.BlessSlots[3].ClassNumbers);
        Assert.Equal(0, back.BlessSlots[3].RecastMarginSec);
        // Untouched slots stay empty across the trip and keep the default lead.
        Assert.Null(back.BlessSlots[1].Spell);
        Assert.Empty(back.BlessSlots[1].ClassNumbers);
        Assert.Equal(SpellsSettings.DefaultBlessRecastMarginSec, back.BlessSlots[1].RecastMarginSec);
    }
}
