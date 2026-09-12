using MudPlay.Game.Remote;
using MudPlay.Models.GameData;
using MudPlay.Services.Api;
using Xunit;

namespace MudPlay.Tests;

// Which API-dispatchable commands need the destructive opt-in. The classification
// is derived from RemoteCommandCatalog's permission categories rather than a list
// kept here, so a command added to the catalog is classified automatically
// instead of quietly defaulting to allowed.
public sealed class LocalApiActionsTests
{
    [Theory]
    [InlineData("@suicide")]      // irreversible
    [InlineData("@hangup")]       // ends the session
    [InlineData("@relog")]        // ends the session
    public void SessionEndingAndIrreversibleCommands_AreDestructive(string command)
        => Assert.True(LocalApiActions.IsDestructive(command));

    [Theory]
    [InlineData("@health")]
    [InlineData("@where")]
    [InlineData("@goto")]
    [InlineData("@stop")]
    [InlineData("@inv")]
    public void OrdinaryQueryAndMovementCommands_AreNotDestructive(string command)
        => Assert.False(LocalApiActions.IsDestructive(command));

    [Fact]
    public void UnknownCommand_CountsAsDestructive()
    {
        // The safe default for something we can't classify is to refuse it. An
        // unclassified command sailing through would be the failure mode worth
        // avoiding here.
        Assert.True(LocalApiActions.IsDestructive("@no-such-command"));
        Assert.True(LocalApiActions.IsDestructive(""));
        Assert.True(LocalApiActions.IsDestructive("   "));
    }

    [Fact]
    public void MissingAtPrefix_IsNormalisedNotMisclassified()
    {
        // A caller posting {"command":"hangup"} must not be waved through as
        // "unknown, therefore... wait, destructive" by accident — it should
        // classify as the real command it names.
        Assert.True(LocalApiActions.IsDestructive("hangup"));
        Assert.False(LocalApiActions.IsDestructive("health"));
    }

    [Fact]
    public void Classification_IsCaseInsensitive()
    {
        Assert.True(LocalApiActions.IsDestructive("@SUICIDE"));
        Assert.False(LocalApiActions.IsDestructive("@HeAlTh"));
    }

    [Fact]
    public void EquipSuffixForm_IsNotDestructive()
    {
        // @equip-<set> is a prefix handler, so it isn't a literal catalog key and
        // would otherwise fall into the unknown-means-destructive bucket.
        Assert.False(LocalApiActions.IsDestructive("@equip-backstab"));
    }

    [Fact]
    public void EveryDestructiveCatalogEntry_IsInAnElevatedOrHangupCategory()
    {
        // Pins the derivation itself: nothing is classified destructive for a
        // reason other than its category, and nothing in those categories escapes.
        foreach ((string command, PlayerRemoteControls category) in RemoteCommandCatalog.Map)
        {
            bool expected = (category & (PlayerRemoteControls.SysopCommands
                | PlayerRemoteControls.HangupDisconnect)) != 0;
            Assert.Equal(expected, LocalApiActions.IsDestructive(command));
        }
    }

    [Fact]
    public void IsKnown_SeparatesTyposFromRealCommands()
    {
        // So a typo gets told it's a typo, instead of being told to go enable
        // destructive commands.
        Assert.True(LocalApiActions.IsKnown("@where"));
        Assert.True(LocalApiActions.IsKnown("where"));
        Assert.True(LocalApiActions.IsKnown("@equip-backstab"));
        Assert.False(LocalApiActions.IsKnown("@nonsense"));
        Assert.False(LocalApiActions.IsKnown(""));
    }

    [Fact]
    public void CatalogHasEntriesInBothBuckets()
    {
        // Guards the test above from passing vacuously if the catalog were ever
        // emptied or the categories renamed.
        var all = RemoteCommandCatalog.Map.Keys.ToList();
        Assert.Contains(all, c => LocalApiActions.IsDestructive(c));
        Assert.Contains(all, c => !LocalApiActions.IsDestructive(c));
    }
}
