using MudPlay.Game.Remote;
using Xunit;

namespace MudPlay.Tests;

// What `@help <command>` says has to match what the command does.
public sealed class RemoteCommandHelpTextTests
{
    private static RemoteCommandHelp Help(string command)
    {
        Assert.True(RemoteCommandCatalog.TryGetHelp(command, out RemoteCommandHelp help));
        return help;
    }

    [Fact]
    public void Quest_ListsItsUpdateForm()
    {
        Assert.Equal("@quest [name|flag|update]", Help("@quest").Syntax);
        Assert.Contains("'update'", Help("@quest").Description);
    }

    // A bare @token lists every token, so the argument is optional.
    [Fact]
    public void Token_ShowsItsArgumentAsOptional() =>
        Assert.Equal("@token [place]", Help("@token").Syntax);

    // Healing and resting are two toggles.
    [Fact]
    public void AutoHealAndAutoRest_AreDescribedAsSeparateToggles()
    {
        Assert.DoesNotContain("same flag", Help("@auto-heal").Description);
        Assert.Contains("@auto-rest", Help("@auto-heal").Description);
        Assert.Contains("@auto-heal", Help("@auto-rest").Description);
        Assert.DoesNotContain("/ heal", Help("@auto-rest").Description);
    }

    [Fact]
    public void Dupe_NamesTheQuestPermissionsItCopies() =>
        Assert.Contains("query, roomba and quest permissions", Help("@dupe").Description);

    [Theory]
    [InlineData("@loop")]
    [InlineData("@lair")]
    public void ARoomList_IsDescribedWithItsSeparators(string command) =>
        Assert.Contains("commas or semicolons", Help(command).Description);

    // Only option 4 takes a name.
    [Fact]
    public void AtkOrder_PutsTheNameOnOptionFourAlone() =>
        Assert.Equal("@atkorder [1|2|3|4 <name>|5]", Help("@atkorder").Syntax);

    [Fact]
    public void Train_SaysWhatAutoTrainAdds() =>
        Assert.Contains("every banked level if Auto-train is on", Help("@train").Description);

    [Fact]
    public void Kill_MentionsAPlayerTarget() =>
        Assert.Contains("player in your room", Help("@kill").Description);

    [Fact]
    public void Help_MentionsTheRelayBackForm()
    {
        Assert.Equal("@help [command|&@]", Help("@help").Syntax);
        Assert.Contains("@help &@", Help("@help").Description);
    }

    [Fact]
    public void DepositAll_SaysItAlsoWithdraws() =>
        Assert.Contains("withdraws", Help("@deposit-all").Description);

    // "Syntax - Description" goes out as one telepath; the game cuts a longer line.
    [Fact]
    public void EveryHelpLine_FitsOneTelepath()
    {
        foreach ((string command, RemoteCommandHelp help) in RemoteCommandCatalog.Help)
        {
            int length = help.Syntax.Length + 3 + help.Description.Length;
            Assert.True(length <= 225, $"{command}: {length} characters");
        }
    }
}
