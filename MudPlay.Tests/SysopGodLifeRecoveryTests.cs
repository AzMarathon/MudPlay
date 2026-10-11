using System.Collections.Generic;
using MudPlay.Game;
using Xunit;

namespace MudPlay.Tests;

// "Sysop god lives": on the character's own death, auto-send `sys god <name> add
// life` — but only when the per-BBS power is enabled and the character name is known.
public sealed class SysopGodLifeRecoveryTests
{
    private static (SysopGodLifeRecovery rec, List<string> sent) Make(bool enabled, string? name)
    {
        var sent = new List<string>();
        var rec = new SysopGodLifeRecovery(() => enabled, () => name, sent.Add);
        return (rec, sent);
    }

    [Fact]
    public void Enabled_OnDeath_SendsAddLifeForOwnName()
    {
        var (rec, sent) = Make(enabled: true, name: "Grimlock");
        rec.OnDeath();
        Assert.Equal(new[] { "sys god Grimlock add life" }, sent);
    }

    // An arena death spends no life on either realm: asking one back would leave
    // the character a life up.
    [Fact]
    public void AnArenaDeath_AsksNoLifeBack()
    {
        var (rec, sent) = Make(enabled: true, name: "Grimlock");
        int asked = 0;
        rec.LifeRequested += () => asked++;

        rec.OnDeath(tookNothing: true);

        Assert.Empty(sent);
        Assert.Equal(0, asked);
    }

    [Fact]
    public void Disabled_OnDeath_SendsNothing()
    {
        var (rec, sent) = Make(enabled: false, name: "Grimlock");
        rec.OnDeath();
        Assert.Empty(sent);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EnabledButNoNameYet_SendsNothing(string? name)
    {
        var (rec, sent) = Make(enabled: true, name: name);
        rec.OnDeath();
        Assert.Empty(sent);
    }

    [Fact]
    public void Name_IsTrimmed()
    {
        var (rec, sent) = Make(enabled: true, name: "  Grimlock  ");
        rec.OnDeath();
        Assert.Equal(new[] { "sys god Grimlock add life" }, sent);
    }
}
