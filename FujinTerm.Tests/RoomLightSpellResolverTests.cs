using System;
using System.IO;
using FujinTerm.Game.Light;
using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

// Stage 1 — the room-light spell -> illu resolver. Two realm-dependent shapes:
// a buff spell (Illu 13 / RoomIllu 14 -> MinBase) and a light-ball spell
// (TextBlock 148 -> giveitem a light item -> that item's IlluTarget).
public sealed class RoomLightSpellResolverTests : IDisposable
{
    private readonly string _root;

    public RoomLightSpellResolverTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fujinterm-rlspell-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    private RoomLightSpellResolver Build(string spells, string? tbinfo = null, string? items = null)
    {
        const string set = "alpha";
        string dir = Path.Combine(_root, set);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Spells.json"), spells);
        File.WriteAllText(Path.Combine(dir, "TBInfo.json"), tbinfo ?? "[]");
        File.WriteAllText(Path.Combine(dir, "Items.json"), items ?? "[]");
        GameDataCache cache = new(_root);
        cache.SwitchSet(set);
        return new RoomLightSpellResolver(cache, new LightItemIndex(cache));
    }

    [Fact]
    public void BuffSpell_RoomIllu_UsesMinBase()
    {
        // starlight shape: RoomIllu (14) -> illu is the spell's MinBase.
        var r = Build("[{\"Number\":26,\"Name\":\"starlight\",\"MinBase\":175,\"Abil-0\":14}]");
        Assert.Equal(175, r.IlluForSpell("starlight"));
    }

    [Fact]
    public void BuffSpell_Illu_UsesMinBase()
    {
        // Paradigm illuminate shape: Illu (13) -> MinBase.
        var r = Build("[{\"Number\":2,\"Name\":\"illuminate\",\"MinBase\":95,\"Abil-0\":13}]");
        Assert.Equal(95, r.IlluForSpell("illuminate"));
    }

    [Fact]
    public void LightBallSpell_UsesGeneratedItemIlluTarget()
    {
        // Stock illuminate: TextBlock (148) whose action gives a light item; the
        // illu is that light ball's IlluTarget (100). AbilVal-0 is 0, so the
        // textblock ref lives in MinBase.
        var r = Build(
            spells: "[{\"Number\":2,\"Name\":\"illuminate\",\"MinBase\":4012,\"Abil-0\":148,\"AbilVal-0\":0}]",
            tbinfo: "[{\"Number\":4012,\"Action\":\"giveitem 1085\"}]",
            items:  "[{\"Number\":1085,\"Name\":\"light ball\",\"ItemType\":6,\"UseCount\":1000,\"Abil-0\":54,\"AbilVal-0\":100}]");
        Assert.Equal(100, r.IlluForSpell("illuminate"));
    }

    [Fact]
    public void LightBallSpell_TextblockRefInAbilVal()
    {
        // The other encoding: the textblock number lives in the ability slot's
        // AbilVal rather than MinBase.
        var r = Build(
            spells: "[{\"Number\":3,\"Name\":\"glow\",\"MinBase\":0,\"Abil-0\":148,\"AbilVal-0\":900}]",
            tbinfo: "[{\"Number\":900,\"Action\":\"message 5:giveitem 1085\"}]",
            items:  "[{\"Number\":1085,\"Name\":\"light ball\",\"ItemType\":6,\"UseCount\":1000,\"Abil-0\":54,\"AbilVal-0\":100}]");
        Assert.Equal(100, r.IlluForSpell("glow"));
    }

    [Fact]
    public void UnknownSpell_IsZero()
    {
        var r = Build("[{\"Number\":26,\"Name\":\"starlight\",\"MinBase\":175,\"Abil-0\":14}]");
        Assert.Equal(0, r.IlluForSpell("nonesuch"));
        Assert.Equal(0, r.IlluForSpell(null));
        Assert.Equal(0, r.IlluForSpell("  "));
    }

    [Fact]
    public void NonLightSpell_IsZero()
    {
        // A damage spell provides no illumination.
        var r = Build("[{\"Number\":1,\"Name\":\"magic missile\",\"MinBase\":10,\"Abil-0\":1}]");
        Assert.Equal(0, r.IlluForSpell("magic missile"));
    }

    [Fact]
    public void LightBallSpell_GivesNonLightItem_IsZero()
    {
        // A textblock spell that gives a non-light item contributes nothing.
        var r = Build(
            spells: "[{\"Number\":4,\"Name\":\"gift\",\"MinBase\":700,\"Abil-0\":148}]",
            tbinfo: "[{\"Number\":700,\"Action\":\"giveitem 500\"}]",
            items:  "[{\"Number\":500,\"Name\":\"apple\",\"ItemType\":11,\"UseCount\":0}]");
        Assert.Equal(0, r.IlluForSpell("gift"));
    }
}
