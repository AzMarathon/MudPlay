using System.Text.Json;
using MudPlay.Models.Profile;
using Xunit;

namespace MudPlay.Tests;

// The per-BBS sysop-power flags, plus the one-way migration of the old
// combined "HasSysopPowers" flag that releases through 3.50.x persisted.
public sealed class BbsCredentialsTests
{
    [Fact]
    public void LegacyHasSysopPowers_True_MigratesToSysopStatus()
    {
        BbsCredentials cred = JsonSerializer.Deserialize<BbsCredentials>(
            """{ "HasSysopPowers": true }""")!;
        Assert.True(cred.SysopStatus);   // the one power #461 wired the old flag to
        Assert.False(cred.SysopGodLives);
    }

    [Fact]
    public void LegacyHasSysopPowers_False_LeavesAllOff()
    {
        BbsCredentials cred = JsonSerializer.Deserialize<BbsCredentials>(
            """{ "HasSysopPowers": false }""")!;
        Assert.False(cred.SysopStatus);
        Assert.False(cred.SysopGodLives);
    }

    [Fact]
    public void ThreeFlags_RoundTrip_AndLegacyNeverWrittenBack()
    {
        var cred = new BbsCredentials { SysopStatus = true, SysopGodLives = false, SysopGoto = true };
        string json = JsonSerializer.Serialize(cred);

        BbsCredentials back = JsonSerializer.Deserialize<BbsCredentials>(json)!;
        Assert.True(back.SysopStatus);
        Assert.False(back.SysopGodLives);
        Assert.True(back.SysopGoto);

        // The legacy shim is set-only, so a new save never re-emits it.
        Assert.DoesNotContain("HasSysopPowers", json);
    }

    [Fact]
    public void SysopGoto_FlagAndTable_RoundTrip()
    {
        var cred = new BbsCredentials
        {
            SysopGoto = true,
            SysopGotos = new()
            {
                new() { Name = "twilight", Map = 9, Room = 42, MinLevel = 30 },
            },
        };
        string json = JsonSerializer.Serialize(cred);

        BbsCredentials back = JsonSerializer.Deserialize<BbsCredentials>(json)!;
        Assert.True(back.SysopGoto);
        // The starter set is replaced wholesale by whatever the user saved.
        var row = Assert.Single(back.SysopGotos);
        Assert.Equal("twilight", row.Name);
        Assert.Equal(9, row.Map);
        Assert.Equal(42, row.Room);
        Assert.Equal(30, row.MinLevel);
    }

    [Fact]
    public void SysopGotos_DefaultsToStarterSet_WhenFieldAbsent()
    {
        // An old profile with no SysopGotos field keeps the seeded starter towns.
        BbsCredentials cred = JsonSerializer.Deserialize<BbsCredentials>("""{ "SysopGoto": true }""")!;
        Assert.Contains(cred.SysopGotos, l => l.Name == "newhaven");
        Assert.Contains(cred.SysopGotos, l => l.Name == "lostcity" && l.MinLevel == 40);
        Assert.Equal(SysopGotoLocation.DefaultStarterSet().Count, cred.SysopGotos.Count);
    }

    [Fact]
    public void SysopGotos_ExplicitEmptyList_StaysEmpty()
    {
        // A user who clears every row saves []; the seed must not re-populate it.
        BbsCredentials cred = JsonSerializer.Deserialize<BbsCredentials>("""{ "SysopGotos": [] }""")!;
        Assert.Empty(cred.SysopGotos);
    }
}
