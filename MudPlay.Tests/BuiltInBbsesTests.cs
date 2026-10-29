using System.IO;
using MudPlay.Models.Profile;
using MudPlay.Models.Settings;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// The boards MudPlay comes with: added to the BBS list once per install, never on
// top of the same board under another name, and their logon steps (and the Stock
// steps every other board gets) carry no account or password of anyone's.
public sealed class BuiltInBbsesTests : IDisposable
{
    private readonly string _tag = Path.GetRandomFileName().Replace(".", "");
    private readonly BbsProfileStore _store = new();
    private readonly List<string> _made = [];

    public BuiltInBbsesTests() => ScratchRoot.Require();

    public void Dispose()
    {
        foreach (string name in _made) _store.Delete(name);
    }

    private BuiltInBbses.Preset Preset(string name)
    {
        _made.Add(name);
        return new BuiltInBbses.Preset(name, $"{name}.example.org", 2323, null,
            [new MenuStep { WaitForPattern = "login:", Send = "{username}" }]);
    }

    [Fact]
    public void APreset_IsAddedOnce_AndStaysRemovedOnceTheUserRemovesIt()
    {
        BuiltInBbses.Preset preset = Preset($"builtin-{_tag}");
        List<string> added = [];

        Assert.Equal([preset.Name], BuiltInBbses.AddMissing(_store, added, [preset]));

        BbsProfile bbs = _store.Get(preset.Name)!;
        Assert.Equal(preset.Host, bbs.Host);
        Assert.Equal(2323, bbs.Port);
        Assert.Equal(preset.Name, Assert.Single(bbs.Realms).Name);
        Assert.Contains(preset.Name, added);

        _store.Delete(preset.Name);
        Assert.Empty(BuiltInBbses.AddMissing(_store, added, [preset]));
        Assert.False(_store.Exists(preset.Name));
    }

    [Fact]
    public void ABoardAlreadyListedUnderAnotherName_IsNotAddedTwice()
    {
        BuiltInBbses.Preset preset = Preset($"builtin-{_tag}");
        string mine = $"mine-{_tag}";
        _made.Add(mine);
        _store.Save(new BbsProfile { Name = mine, Host = preset.Host.ToUpperInvariant() + " ", Port = preset.Port });
        List<string> added = [];

        Assert.Empty(BuiltInBbses.AddMissing(_store, added, [preset]));
        Assert.False(_store.Exists(preset.Name));
        Assert.Contains(preset.Name, added);
    }

    [Fact]
    public void ABuiltInBoard_GetsItsOwnCopyOfTheLogonSteps_MatchedOnHostAndPort()
    {
        BbsProfile paramud = new() { Name = "Paramud", Host = "Paramud.MudInfo.net", Port = 2323 };

        List<MenuStep> steps = BuiltInBbses.LogonStepsFor(paramud);
        Assert.Equal(["{username}", "{password}", "P", "1"], steps.Select(s => s.Send));
        steps[0].Send = "changed";
        Assert.Equal("{username}", BuiltInBbses.LogonStepsFor(paramud)[0].Send);

    }

    // Any other board, Paradigm's host on another port included, is taken for a Stock
    // one: its BBS login prompts, then the main menu's `m`.
    [Fact]
    public void AnyOtherBoard_GetsTheStockLogonSteps()
    {
        BbsProfile mine = new() { Name = "My Stock board", Host = "paramud.mudinfo.net", Port = 23 };

        List<MenuStep> steps = BuiltInBbses.LogonStepsFor(mine);

        Assert.Equal(["Otherwise type \"new\":", "Enter your password:", "Main System Menu (TOP)"],
            steps.Select(s => s.WaitForPattern));
        Assert.Equal(["{username}", "{password}", "m"], steps.Select(s => s.Send));
    }

    [Fact]
    public void TheShippedLogonSteps_SendOnlyCredentialTokensAndMenuKeys()
    {
        Assert.Equal(["Anomaly", "Paradigm"], BuiltInBbses.All.Select(p => p.Name));
        foreach (MenuStep step in BuiltInBbses.All.SelectMany(p => p.LogonSteps).Concat(BuiltInBbses.StockLogonSteps))
            Assert.True(step.Send is "{username}" or "{password}" || step.Send.Length == 1,
                $"'{step.Send}' is neither a credential token nor a menu key");
    }
}
