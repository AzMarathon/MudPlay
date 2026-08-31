using System.Collections.Generic;
using System.Text.Json;
using MudPlay.Game.Map;
using MudPlay.Models.Profile;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

/// <summary>
/// <see cref="ProfileService.NormalizeForLoad"/> rebuilds the BBS credential
/// lookup case-insensitively, so a profile that keyed credentials under
/// "Playpen" resolves for a "playpen" BBS (BBS names are case-insensitive
/// folder names).
/// </summary>
public sealed class ProfileServiceTests
{
    [Theory]
    // report stock-20260828-104653: a copied profile keeps the old name; heal it.
    [InlineData("Fujin", "Raijin WuzHere", "Raijin WuzHere")]  // copied profile → heal to full live name
    [InlineData("Raijin", "Raijin WuzHere", "Raijin WuzHere")] // given matches, family missing → still heal (store family)
    [InlineData("Raijin WuzHere", "Raijin WuzHere", null)]     // identical → no heal, no Save churn
    [InlineData("raijin wuzhere", "Raijin WuzHere", null)]     // case-only difference → no heal
    [InlineData("Fujin", "", null)]                            // blank stat name (pre-stat) → nothing to heal from
    [InlineData("Fujin", null, null)]
    [InlineData(null, "Raijin WuzHere", "Raijin WuzHere")]     // no stored name yet → adopt the live name
    public void HealedCharacterName_HealsOnlyOnRealChange(string? current, string? stat, string? expected)
        => Assert.Equal(expected, ProfileService.HealedCharacterName(current, stat));

    [Fact]
    public void NormalizeForLoad_BbsCredentials_ResolveCaseInsensitively()
    {
        var profile = new CharacterProfile
        {
            // Default (case-sensitive) dictionary keyed with capital P — the
            // shape a deserialized profile arrives in.
            BbsCredentials = new Dictionary<string, BbsCredentials>
            {
                ["Playpen"] = new BbsCredentials { EncryptedUsername = "enc" },
            },
        };

        // Pre-condition: the mismatched-case lookup misses before normalization.
        Assert.False(profile.BbsCredentials.TryGetValue("playpen", out _));

        ProfileService.NormalizeForLoad(profile);

        Assert.True(profile.BbsCredentials!.TryGetValue("playpen", out BbsCredentials? cred));
        Assert.Equal("enc", cred!.EncryptedUsername);
    }

    [Fact]
    public void NormalizeForLoad_NullCredentials_IsNoOp()
    {
        var profile = new CharacterProfile();
        ProfileService.NormalizeForLoad(profile);
        Assert.Null(profile.BbsCredentials);
    }

    [Fact]
    public void NavLairMode_DefaultsUniform_AndRoundTripsByName()
    {
        Assert.Equal(LairDisplayMode.Uniform, new CharacterProfile().NavLairMode);

        var profile = new CharacterProfile { NavLairMode = LairDisplayMode.HeatCount };
        string json = JsonSerializer.Serialize(profile, JsonStore.Options);

        // Persisted by member name (JsonStringEnumConverter), so reordering the
        // enum can never remap a saved profile's mode to the wrong value.
        Assert.Contains("\"HeatCount\"", json);

        CharacterProfile back = JsonSerializer.Deserialize<CharacterProfile>(json, JsonStore.Options)!;
        Assert.Equal(LairDisplayMode.HeatCount, back.NavLairMode);
    }
}
