using System;
using System.IO;
using System.Linq;
using MudPlay.Models.GameData;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// Every trigger is the character's own. A profile from when some were kept with the
// game-data set takes a copy of that set's list the first time it loads.
public sealed class TriggerOwnershipTests : IDisposable
{
    private readonly string _setName;
    private readonly string _bbs;

    public TriggerOwnershipTests()
    {
        string id = Guid.NewGuid().ToString("N").Substring(0, 12);
        _setName = "test-trigset-" + id;
        _bbs = "test-trigbbs-" + id;
        Directory.CreateDirectory(AppPaths.GameDataSetDir(_setName));
    }

    public void Dispose()
    {
        foreach (string folder in new[] { AppPaths.GameDataSetDir(_setName), AppPaths.BbsFolder(_bbs) })
        {
            try { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
            catch { /* best-effort */ }
        }
    }

    private static Trigger Named(string name, string pattern) =>
        new(name, true, TriggerScope.GameMessages, TriggerMatchType.Literal, pattern, "say hi");

    private void WriteSetTriggers(params Trigger[] triggers) =>
        JsonStore.Save(AppPaths.LegacySetTriggersFile(_setName), triggers.ToList());

    private void WriteOlderCharacter(string name, params Trigger[] own)
    {
        Directory.CreateDirectory(AppPaths.ProfileFolder(_bbs, name));
        JsonStore.Save(AppPaths.CharacterProfileFile(_bbs, name),
            new { Name = name, SchemaVersion = 7, Triggers = own.ToList() });
    }

    private (ProfileService Profile, TriggerEngine Engine) LoadCharacter(string name)
    {
        ProfileService profile = new();
        TriggerEngine engine = new(profile, () => _setName);
        profile.Load(_bbs, name);
        return (profile, engine);
    }

    [Fact]
    public void OlderCharacter_TakesTheSetsTriggers_BesideItsOwn_Once()
    {
        WriteSetTriggers(Named("chase", "* runs away"), Named("shared", "You hear a bell"));
        WriteOlderCharacter("Alpha", Named("mine", "Alpha, wake up"), Named("shared", "You hear a bell"));

        (ProfileService profile, TriggerEngine engine) = LoadCharacter("Alpha");

        Assert.Equal(new[] { "mine", "shared", "chase" }, engine.Triggers.Select(t => t.Name));
        Assert.True(profile.Current!.OwnsTriggers);

        // Removing one sticks: the set's list is not read again.
        engine.Remove(engine.Triggers.Single(t => t.Name == "chase"));
        (_, TriggerEngine again) = LoadCharacter("Alpha");
        Assert.Equal(new[] { "mine", "shared" }, again.Triggers.Select(t => t.Name));
    }

    [Fact]
    public void NewCharacter_DoesNotTakeTheSetsTriggers()
    {
        WriteSetTriggers(Named("someone else's", "* runs away"));
        new ProfileService().CreateProfile(_bbs, "Fresh");

        (ProfileService profile, TriggerEngine engine) = LoadCharacter("Fresh");

        Assert.DoesNotContain(engine.Triggers, t => t.Name == "someone else's");
        Assert.True(profile.Current!.OwnsTriggers);
    }

    [Fact]
    public void AnEdit_IsSavedOnTheCharacter()
    {
        WriteOlderCharacter("Alpha");
        (_, TriggerEngine engine) = LoadCharacter("Alpha");

        engine.Add(Named("new one", "ding"));

        (_, TriggerEngine again) = LoadCharacter("Alpha");
        Assert.Contains(again.Triggers, t => t.Name == "new one");
    }
}
