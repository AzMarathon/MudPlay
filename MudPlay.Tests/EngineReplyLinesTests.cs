using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using MudPlay.Game;
using MudPlay.Models.GameData;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

public sealed class EngineReplyLinesTests
{
    [Theory]
    [InlineData("Syntax: PICKLOCK {direction}")]
    [InlineData("Why would you want to rob yourself?")]
    [InlineData("You must close the door before you may lock it.")]
    [InlineData("The door to the north just opened.")]
    [InlineData("The gate is locked.")]
    [InlineData("You see Fujin pick the lock on the door to the east.")]
    [InlineData("You are not carrying torch.")]
    [InlineData("Gang member Tiny will be notified of their promotion next time they log on.")]
    [InlineData("You just joined channel 6969.")]
    [InlineData("--- Telepath Not Sent ---")]
    [InlineData("Someone yells from the north \"help\"")]
    [InlineData("You would get 40 gold crowns for your silver rapier.")]
    // A name that fits more than one thing in the room: the header, then one row each.
    [InlineData("Please be more specific.  You could have meant any of these:")]
    [InlineData("-- old gypsy woman")]
    [InlineData("-- old gypsy man")]
    public void EngineReplies_Match(string line) => Assert.True(EngineReplyLines.Matches(line));

    [Theory]
    [InlineData("You feel protected!")]
    [InlineData("You cast smite on Raijin!")]
    [InlineData("The door glows with a pale light.")]
    [InlineData("A shimmering rune flickers and fades.")]
    [InlineData("The is locked.")]                      // a value is never empty
    [InlineData("You must close the door")]
    [InlineData("-- ")]                                 // a row with nothing named
    [InlineData("")]
    public void OtherLines_DoNot(string line) => Assert.False(EngineReplyLines.Matches(line));

    // The list exists to keep engine chatter out of the review queue without ever
    // hiding a spell message, so no entry may match a message either seed carries.
    [Theory]
    [InlineData("Messages.stock.seed.json")]
    [InlineData("Messages.paradigm.seed.json")]
    public void NoEngineReply_MatchesACataloguedSpellMessage(string seed)
    {
        string dir = Path.Combine(Path.GetTempPath(), "mudplay-engine-lines-" + Path.GetRandomFileName());
        try
        {
            AppPaths.ExtractEmbeddedSeeds(dir);
            List<MessageRecord> records =
                JsonStore.Load<List<MessageRecord>>(Path.Combine(dir, seed)) ?? new List<MessageRecord>();
            Assert.NotEmpty(records);

            List<string> clashes = new();
            foreach (MessageRecord r in records)
            {
                foreach (string? slot in new[] { r.CasterMessage, r.TargetMessage, r.WitnessMessage, r.AppliedMessage })
                {
                    if (MessageRecord.IsBlankOrAbsent(slot)) continue;
                    foreach (string wording in slot!.Split('\n'))
                    {
                        // A templated slot is tried with a name in each placeholder.
                        string line = Regex.Replace(wording.Trim(), @"\{[^}]*\}", "Raijin");
                        if (EngineReplyLines.Matches(line)) clashes.Add($"{r.Name}: {line}");
                    }
                }
            }
            Assert.Empty(clashes);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { /* temp cleanup */ }
        }
    }
}
