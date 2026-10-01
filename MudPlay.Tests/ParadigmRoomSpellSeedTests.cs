using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using MudPlay.Game;
using MudPlay.Models.GameData;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// The silvermere (#918) and darkwood forest (#915) room spells each fire several ambient
// flavor lines that do nothing but colour the room. They're seeded as ONE record apiece
// with the wordings newline-separated in WitnessMessage; the watcher splits that slot and
// recognizes every wording, so the set drops out of the unrecognized-lines report instead
// of flooding it. Pins the seed content, the Id integrity the loader trusts, and the
// end-to-end recognition.
public sealed class ParadigmRoomSpellSeedTests : IDisposable
{
    private readonly string _dir;
    private readonly List<MessageRecord> _records;

    public ParadigmRoomSpellSeedTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "mudplay-para-seed-" + Path.GetRandomFileName());
        AppPaths.ExtractEmbeddedSeeds(_dir);
        _records = JsonStore.Load<List<MessageRecord>>(
            Path.Combine(_dir, "Messages.paradigm.seed.json")) ?? new List<MessageRecord>();
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp cleanup */ }
    }

    private MessageRecord Find(int spellNumber) =>
        _records.Single(r => r.Links is { } links
            && links.Any(k => k.Table == "Spells" && k.Number == spellNumber));

    [Theory]
    [InlineData(918, "A dog barks off in the distance.")]
    [InlineData(918, "Children rush past you hopping around in youthful glee.")]
    [InlineData(918, "A voice shouts aloud \"Read the bulletin in the Adventurer's Guild!\"")]
    [InlineData(918, "A guardsman shouts out the time of day.")]
    [InlineData(918, "A cheer of many voices can be heard in the distance.")]
    [InlineData(925, "The large temple bell clangs loudly, echoing the time of day.")]
    [InlineData(925, "The smell of incense wafts faintly in the air.")]
    [InlineData(925, "The angelic sound of a choir floats down through the air.")]
    [InlineData(915, "A dry twig snaps loudly behind you.")]
    [InlineData(915, "The forest becomes strangely silent.")]
    [InlineData(915, "The leaves begin to rustle, as if some beast were about to spring forth!")]
    public void RoomSpellRecord_CarriesEachFlavorWording(int spell, string wording)
        => Assert.Contains(wording, Find(spell).WitnessMessage.Split('\n'));

    [Fact]
    public void UntouchedRoomSpellRecord_IdMatchesItsFields()
    {
        MessageRecord r = Find(915);
        Assert.Equal(
            MessageRecord.ComputeId(r.Name, r.CasterMessage, r.TargetMessage,
                r.WitnessMessage, r.AppliedMessage, r.AppliedEndsWith),
            r.Id);
    }

    // A seed record whose text is fixed in place keeps the Id it shipped with: a
    // set's own message file stores the user's removals and overrides by that Id, so
    // a re-hashed one would bring the seed copy back beside theirs.
    [Theory]
    [InlineData(918, "9659da50fba38d23")]
    [InlineData(925, "fcb53fe4cc8969b9")]
    public void EditedRoomSpellRecord_KeepsItsShippedId(int spell, string id)
        => Assert.Equal(id, Find(spell).Id);

    [Fact]
    public void SeededFlavorLines_AreRecognized_NotStaged()
    {
        MessageStore messages = new();
        messages.Messages.ReplaceAll(_records);
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        MessageCandidateStore candidates = new();
        using MessageCandidateWatcher watcher = new(router, messages, candidates);
        watcher.NotifyInGame();

        foreach (string line in new[]
        {
            "A dog barks off in the distance.",
            "The awful sound of a drunken chorus echoes through the streets.",
            "Children rush past you hopping around in youthful glee.",
            "A dry twig snaps loudly behind you.",
            "An ominous wind blows through the trees.",
            "A flock of birds fly overhead.",
            "The leaves begin to rustle, as if some beast were about to spring forth!",
        })
        {
            var emitted = new LineExtractor.EmittedLine(
                line, Array.Empty<CellAttributes>(), DateTimeOffset.UtcNow, IsPromptLine: false);
            Invoke(watcher, "OnLine", emitted);
            Invoke(watcher, "CommitPending");
        }

        Assert.Empty(candidates.Candidates);
    }

    private static void Invoke(MessageCandidateWatcher watcher, string method, params object[] args) =>
        typeof(MessageCandidateWatcher)
            .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(watcher, args.Length == 0 ? null : args);
}
