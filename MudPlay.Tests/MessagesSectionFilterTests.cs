using System.Collections.Generic;
using MudPlay.Models.GameData;
using MudPlay.ViewModels.GameData.Tables;
using Xunit;

namespace MudPlay.Tests;

// The Messages tab hides a record claimed by a spell PRESENT in the set (it's edited
// from the Spells section), but keeps orphan-linked and standalone records visible so
// they aren't stranded with no reachable editor.
public sealed class MessagesSectionFilterTests
{
    private static MessageRecord WithLinks(string name, params GameDataLink[] links) =>
        new(Id: name, Name: name, Action: MessageAction.Ignore, Flags: MessageFlags.None,
            RawFlagsHex: 0, Response: "", CasterMessage: "", TargetMessage: "",
            WitnessMessage: "", AppliedMessage: "", AppliedEndsWith: "",
            Links: links.Length == 0 ? null : links);

    [Fact]
    public void ClaimedByExistingSpell_IsHidden()
    {
        HashSet<int> spells = new() { 107 };
        MessageRecord bless = WithLinks("bless", new GameDataLink("Spells", 107));
        Assert.True(MessagesSectionViewModel.IsClaimedByExistingSpell(bless, spells));
    }

    [Fact]
    public void OrphanSpellLink_StaysVisible()
    {
        // Links a spell that isn't in this set ⇒ unreachable from the Spells section, so
        // it must remain listed here.
        HashSet<int> spells = new() { 107 };
        MessageRecord orphan = WithLinks("gone", new GameDataLink("Spells", 999));
        Assert.False(MessagesSectionViewModel.IsClaimedByExistingSpell(orphan, spells));
    }

    [Fact]
    public void StandaloneOrNonSpellLinked_StaysVisible()
    {
        HashSet<int> spells = new() { 107 };
        Assert.False(MessagesSectionViewModel.IsClaimedByExistingSpell(WithLinks("plain"), spells));
        Assert.False(MessagesSectionViewModel.IsClaimedByExistingSpell(
            WithLinks("itemproc", new GameDataLink("Items", 107)), spells));
    }
}
