using System;
using System.IO;
using MudPlay.Game.Light;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.ViewModels;
using Xunit;

namespace MudPlay.Tests;

// The room-light spell resolver. Two shapes, told apart by ability code: a buff
// spell (Illu 13 / RoomIllu 14 -> MinBase, adds to a lit light) and a light-making
// spell (TextBlock 148 -> giveitem a light item -> that item's IlluTarget). Buff
// slots store the cast code, which the code-keyed lookups resolve to a name first.
public sealed class RoomLightSpellResolverTests : IDisposable
{
    private readonly string _root;

    public RoomLightSpellResolverTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-rlspell-" + Path.GetRandomFileName());
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

    private static int Illu(RoomLightSpellResolver r, string? code) => r.ForCastCode(code)?.Strength ?? 0;

    [Fact]
    public void BuffSpell_RoomIllu_UsesMinBase()
    {
        // starlight shape: RoomIllu (14) -> illu is the spell's MinBase.
        var r = Build("[{\"Number\":26,\"Name\":\"starlight\",\"Short\":\"star\",\"MinBase\":175,\"Abil-0\":14}]");
        Assert.Equal(175, Illu(r, "star"));
    }

    [Fact]
    public void BuffSpell_Illu_UsesMinBase()
    {
        // Paradigm illuminate shape: Illu (13) -> MinBase.
        var r = Build("[{\"Number\":2,\"Name\":\"illuminate\",\"Short\":\"illu\",\"MinBase\":95,\"Abil-0\":13}]");
        Assert.Equal(95, Illu(r, "illu"));
    }

    [Fact]
    public void LightBallSpell_UsesGeneratedItemIlluTarget()
    {
        // Stock illuminate: TextBlock (148) whose action gives a light item; the
        // illu is that light ball's IlluTarget (100). AbilVal-0 is 0, so the
        // textblock ref lives in MinBase.
        var r = Build(
            spells: "[{\"Number\":2,\"Name\":\"illuminate\",\"Short\":\"illu\",\"MinBase\":4012,\"Abil-0\":148,\"AbilVal-0\":0}]",
            tbinfo: "[{\"Number\":4012,\"Action\":\"giveitem 1085\"}]",
            items:  "[{\"Number\":1085,\"Name\":\"light ball\",\"ItemType\":6,\"UseCount\":1000,\"Abil-0\":54,\"AbilVal-0\":100}]");
        Assert.Equal(100, Illu(r, "illu"));
    }

    [Fact]
    public void LightBallSpell_TextblockRefInAbilVal()
    {
        // The other encoding: the textblock number lives in the ability slot's
        // AbilVal rather than MinBase.
        var r = Build(
            spells: "[{\"Number\":3,\"Name\":\"glow\",\"Short\":\"glow\",\"MinBase\":0,\"Abil-0\":148,\"AbilVal-0\":900}]",
            tbinfo: "[{\"Number\":900,\"Action\":\"message 5:giveitem 1085\"}]",
            items:  "[{\"Number\":1085,\"Name\":\"light ball\",\"ItemType\":6,\"UseCount\":1000,\"Abil-0\":54,\"AbilVal-0\":100}]");
        Assert.Equal(100, Illu(r, "glow"));
    }

    [Fact]
    public void UnknownSpell_IsZero()
    {
        var r = Build("[{\"Number\":26,\"Name\":\"starlight\",\"Short\":\"star\",\"MinBase\":175,\"Abil-0\":14}]");
        Assert.Equal(0, Illu(r, "none"));
        Assert.Equal(0, Illu(r, null));
        Assert.Equal(0, Illu(r, "  "));
    }

    [Fact]
    public void NonLightSpell_IsZero()
    {
        // A damage spell provides no illumination.
        var r = Build("[{\"Number\":1,\"Name\":\"magic missile\",\"Short\":\"mmis\",\"MinBase\":10,\"Abil-0\":1}]");
        Assert.Equal(0, Illu(r, "mmis"));
    }

    [Fact]
    public void LightBallSpell_GivesNonLightItem_IsZero()
    {
        // A textblock spell that gives a non-light item contributes nothing.
        var r = Build(
            spells: "[{\"Number\":4,\"Name\":\"gift\",\"Short\":\"gift\",\"MinBase\":700,\"Abil-0\":148}]",
            tbinfo: "[{\"Number\":700,\"Action\":\"giveitem 500\"}]",
            items:  "[{\"Number\":500,\"Name\":\"apple\",\"ItemType\":11,\"UseCount\":0}]");
        Assert.Equal(0, Illu(r, "gift"));
    }

    // Stock 1.11p's two light spells as the data has them: starlight a RoomIllu
    // buff, illuminate a TextBlock that gives the light ball.
    private const string StockSpells =
        "[{\"Number\":26,\"Name\":\"starlight\",\"Short\":\"star\",\"MinBase\":175,\"Abil-0\":14}," +
        " {\"Number\":2,\"Name\":\"illuminate\",\"Short\":\"illu\",\"MinBase\":4012,\"Abil-0\":148,\"AbilVal-0\":0}," +
        " {\"Number\":1,\"Name\":\"magic missile\",\"Short\":\"mmis\",\"MinBase\":10,\"Abil-0\":1}]";
    private const string StockTbinfo = "[{\"Number\":4012,\"Action\":\"giveitem 1085\\n\\n\"}]";
    private const string StockItems =
        "[{\"Number\":1085,\"Name\":\"light ball\",\"ItemType\":6,\"UseCount\":800,\"Abil-0\":54,\"AbilVal-0\":100}]";

    [Fact]
    public void ForCastCode_Stock_StarlightIsABuff_IlluminateMakesALightBall()
    {
        RoomLightSpellResolver r = Build(StockSpells, StockTbinfo, StockItems);

        RoomLightSpell star = r.ForCastCode("star")!.Value;
        Assert.Equal(RoomLightSpellKind.Buff, star.Kind);
        Assert.Equal(175, star.Strength);
        Assert.Null(star.MadeItem);

        RoomLightSpell illu = r.ForCastCode("illu")!.Value;
        Assert.Equal(RoomLightSpellKind.MakesLightItem, illu.Kind);
        Assert.Equal(100, illu.Strength);
        Assert.Equal("light ball", illu.MadeItem?.Name);
        Assert.Equal("illuminate", illu.Name);
    }

    [Fact]
    public void ForCastCode_Paradigm_IlluminateIsABuff()
    {
        // Paradigm 1.9.1's illuminate: an Illu (13) buff of 95, no item.
        RoomLightSpellResolver r = Build(
            "[{\"Number\":2,\"Name\":\"illuminate\",\"Short\":\"illu\",\"MinBase\":95,\"Abil-0\":13,\"Abil-1\":115,\"AbilVal-1\":10140}]");
        RoomLightSpell illu = r.ForCastCode("illu")!.Value;
        Assert.Equal(RoomLightSpellKind.Buff, illu.Kind);
        Assert.Equal(95, illu.Strength);
    }

    [Fact]
    public void ForCastCode_NotALightSpell_OrUnknown_IsNull()
    {
        RoomLightSpellResolver r = Build(StockSpells, StockTbinfo, StockItems);
        Assert.Null(r.ForCastCode("mmis"));
        Assert.Null(r.ForCastCode("zzzz"));
        Assert.Null(r.ForCastCode(" "));
        Assert.False(r.IsLightCastCode("mmis"));
        Assert.True(r.IsLightCastCode("star"));
    }

    [Fact]
    public void ForCastCode_SpellBookNameWinsOverTheShortColumn()
    {
        // Two rows share a code; the character's spell book says which one it casts.
        const string spells =
            "[{\"Number\":1,\"Name\":\"dim glow\",\"Short\":\"glow\",\"MinBase\":0,\"Abil-0\":1}," +
            " {\"Number\":2,\"Name\":\"bright glow\",\"Short\":\"glow\",\"MinBase\":120,\"Abil-0\":13}]";
        string dir = Path.Combine(_root, "beta");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Spells.json"), spells);
        GameDataCache cache = new(_root);
        cache.SwitchSet("beta");
        RoomLightSpellResolver r = new(cache, new LightItemIndex(cache),
            nameForCastCode: code => code == "glow" ? "bright glow" : null);

        Assert.Equal(120, r.ForCastCode("glow")?.Strength);
    }

    // Paradigm 1.9.1 has two rows named starlight: 26 `star` (RoomIllu, a light) and
    // 5820 `strl` (no light ability). Each code is its own row, even with the spell
    // book naming both "starlight".
    [Fact]
    public void ForCastCode_TwoRowsOfOneName_EachCodeIsItsOwnRow()
    {
        RoomLightSpellResolver r = BuildWithBook(
            "[{\"Number\":26,\"Name\":\"starlight\",\"Short\":\"star\",\"MinBase\":175,\"Abil-0\":14}," +
            " {\"Number\":5820,\"Name\":\"starlight\",\"Short\":\"strl\",\"MinBase\":1000,\"Abil-0\":1111}]",
            _ => "starlight");
        Assert.Equal(175, r.ForCastCode("star")?.Strength);
        Assert.Null(r.ForCastCode("strl"));
    }

    [Fact]
    public void IsDarkeningSpell_NegativeIlluOrIlluTarget()
    {
        RoomLightSpellResolver r = Build(
            "[{\"Number\":440,\"Name\":\"globe of darkness\",\"MinBase\":-999,\"Abil-0\":54,\"AbilVal-0\":-9999}," +
            " {\"Number\":443,\"Name\":\"darkness hits\",\"MinBase\":0,\"Abil-0\":13,\"AbilVal-0\":-999}," +
            " {\"Number\":26,\"Name\":\"starlight\",\"MinBase\":175,\"Abil-0\":14}]");
        Assert.True(r.IsDarkeningSpell(440));
        Assert.True(r.IsDarkeningSpell(443));
        Assert.False(r.IsDarkeningSpell(26));
    }

    private RoomLightSpellResolver BuildWithBook(string spells, Func<string, string?> book)
    {
        string dir = Path.Combine(_root, "gamma");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Spells.json"), spells);
        GameDataCache cache = new(_root);
        cache.SwitchSet("gamma");
        return new RoomLightSpellResolver(cache, new LightItemIndex(cache), nameForCastCode: book);
    }

    // The Add-buff dialog asks the resolver about the slot's cast code (what the
    // slot stores), so a light spell picked by its code offers "Only when the room
    // is dark", and the slot it saves keeps the tick.
    [Fact]
    public void AddBuffDialog_LightSpellPickedByCastCode_OffersAndKeepsOnlyWhenDark()
    {
        RoomLightSpellResolver r = Build(StockSpells, StockTbinfo, StockItems);
        BuffPickOption[] picks = { new("star", "starlight (Lvl 4)", true) };
        AddBuffDialogViewModel d = new(picks, isLightSpell: r.IsLightCastCode, isRollSpell: _ => false);

        d.SelectedPick = picks[0];
        Assert.True(d.IsLightSpell);
        d.OnlyWhenDark = true;
        AddBuffResult? result = null;
        d.CloseRequested += x => result = x;
        d.OkCommand.Execute(null);

        Assert.NotNull(result);
        Assert.True(result.OnlyWhenDark);
        BuffSlot slot = new();
        BuffPanelViewModel.ApplyResult(slot, result);
        Assert.Equal("star", slot.Spell);
        Assert.True(slot.OnlyWhenDark);
    }
}
