using System.IO;
using System.Text.Json;
using MudPlay.Game.Light;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.ViewModels.Settings;
using Xunit;

namespace MudPlay.Tests;

// The Auto-Light tab: what a fresh character starts with, and what the tab stores in
// the profile and reads back. The engine reads the stored AutoLight section, so the
// stored shape is the client's contract, not the JSON serializer's.
public sealed class AutoLightSettingsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mudplay-autolight-" + Path.GetRandomFileName());
    private readonly ProfileService _profile = TestProfiles.Loaded();
    private readonly LightItemIndex _lights;

    public AutoLightSettingsTests()
    {
        string dir = Path.Combine(_root, "alpha");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Items.json"),
            "[{\"Number\":40,\"Name\":\"lantern\",\"ItemType\":6,\"UseCount\":120,\"Abil-0\":54,\"AbilVal-0\":40}]");
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        _lights = new LightItemIndex(cache);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort temp cleanup */ }
    }

    private AutoLightSectionViewModel Section() => new(_profile, _lights, data: null);

    private JsonElement Stored() => _profile.Current!.Settings!["AutoLight"];

    [Fact]
    public void Defaults_AreSane()
    {
        AutoLightSettings dto = new();

        Assert.Equal(6, dto.CarryHours);
        Assert.Equal(60, dto.ReorderThresholdMinutes);
        Assert.Null(dto.PreferredLightName);   // null = engine auto-picks per route
        Assert.False(dto.UseRoomLightSpellOnly); // default: manage light items normally
    }

    [Fact]
    public void NamedLight_IsStoredByName_AndReadBack()
    {
        AutoLightSectionViewModel vm = Section();
        vm.CarryHours = 12;
        vm.ReorderThresholdMinutes = 90;
        vm.SelectedLight = "lantern";
        vm.Apply();

        Assert.Equal("lantern", Stored().GetProperty("PreferredLightName").GetString());
        Assert.False(Stored().GetProperty("UseRoomLightSpellOnly").GetBoolean());

        AutoLightSectionViewModel reopened = Section();
        Assert.Equal(12, reopened.CarryHours);
        Assert.Equal(90, reopened.ReorderThresholdMinutes);
        Assert.Equal("lantern", reopened.SelectedLight);
    }

    // Auto-pick is stored as no name, not an empty string or the label, which the
    // engine would try to resolve as a light.
    [Fact]
    public void AutoPick_IsStoredAsNoName()
    {
        AutoLightSectionViewModel vm = Section();
        vm.SelectedLight = "lantern";
        vm.Apply();
        vm.SelectedLight = AutoLightSectionViewModel.AutoPickLabel;
        vm.Apply();

        Assert.Equal(JsonValueKind.Null, Stored().GetProperty("PreferredLightName").ValueKind);
        Assert.Equal(AutoLightSectionViewModel.AutoPickLabel, Section().SelectedLight);
    }

    [Fact]
    public void SpellOnly_IsStoredAsTheFlag_WithNoLight()
    {
        AutoLightSectionViewModel vm = Section();
        vm.SelectedLight = AutoLightSectionViewModel.SpellOnlyLabel;
        vm.Apply();

        Assert.True(Stored().GetProperty("UseRoomLightSpellOnly").GetBoolean());
        Assert.Equal(JsonValueKind.Null, Stored().GetProperty("PreferredLightName").ValueKind);
        Assert.Equal(AutoLightSectionViewModel.SpellOnlyLabel, Section().SelectedLight);
    }

    [Fact]
    public void OutOfRangeHours_AreClampedWhenStored()
    {
        AutoLightSectionViewModel vm = Section();
        vm.CarryHours = 100;
        vm.ReorderThresholdMinutes = 1000;
        vm.Apply();

        Assert.Equal(48, Stored().GetProperty("CarryHours").GetInt32());
        Assert.Equal(600, Stored().GetProperty("ReorderThresholdMinutes").GetInt32());
    }
}
