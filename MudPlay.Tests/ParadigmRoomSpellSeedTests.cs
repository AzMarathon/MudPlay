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
    [InlineData(5665, "You can barely make out a figure lurching in the haze.")]
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

    // Paradigm's Smoldering Fields rooms carry room spell #5665 (farm 8), whose table
    // prints a line three casts in a hundred. One of its wordings has been captured
    // (report paradigm-20261010-145330); the record holds that one and no guess at
    // the rest.
    [Fact]
    public void FarmFieldRoomSpellRecord_HoldsTheOneCapturedWording_UnderItsOwnId()
    {
        MessageRecord r = Find(5665);

        Assert.Equal("farm 8", r.Name);
        Assert.Equal("You can barely make out a figure lurching in the haze.", r.WitnessMessage);
        Assert.Equal("63acecdce9a4fa4d", r.Id);
        Assert.Equal(
            MessageRecord.ComputeId(r.Name, r.CasterMessage, r.TargetMessage,
                r.WitnessMessage, r.AppliedMessage, r.AppliedEndsWith),
            r.Id);
        Assert.Single(_records, x => x.Id == r.Id);
        GameDataLink link = Assert.Single(r.Links!);
        Assert.Equal(new GameDataLink("Spells", 5665), link);
    }

    // The record is what recognises the line: with it the watcher stages nothing,
    // and without it the same line is staged as unrecognized, as the report shows.
    [Theory]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    public void FarmFieldHazeLine_IsStagedOnlyWithoutItsRecord(bool withRecord, int staged)
    {
        MessageStore messages = new();
        messages.Messages.ReplaceAll(withRecord ? _records : _records.Where(r => r.Id != "63acecdce9a4fa4d"));
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        MessageCandidateStore candidates = new();
        using MessageCandidateWatcher watcher = new(router, messages, candidates);
        watcher.NotifyInGame();

        // The room display the line follows in the report, then the line itself.
        foreach (string line in new[]
        {
            "Smoldering Fields",
            "Also here: brute zombie, brute zombie, brute zombie, brute zombie.",
            "Obvious exits: north, south, east, west",
            "You can barely make out a figure lurching in the haze.",
        })
        {
            var emitted = new LineExtractor.EmittedLine(
                line, Array.Empty<CellAttributes>(), DateTimeOffset.UtcNow, IsPromptLine: false);
            Invoke(watcher, "OnLine", emitted);
            Invoke(watcher, "CommitPending");
        }

        Assert.Equal(staged, candidates.Candidates.Count(c =>
            c.RawText == "You can barely make out a figure lurching in the haze."));
    }

    // A seed record whose text is fixed in place keeps the Id it shipped with: a
    // set's own message file stores the user's removals and overrides by that Id, so
    // a re-hashed one would bring the seed copy back beside theirs.
    [Theory]
    [InlineData(918, "9659da50fba38d23")]
    [InlineData(925, "fcb53fe4cc8969b9")]
    [InlineData(683, "209f6f20ceaeecf8")]
    [InlineData(684, "fa93443ae526ab2f")]
    [InlineData(427, "8ab76dc53b852768")]
    [InlineData(692, "ecf9cc2e00987fa6")]
    [InlineData(700, "12209f7fec678ced")]
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

    // The lines a 2026-10-03 unrecognized-lines export still carried: the eight card
    // readings a deck of cards deals (their records hold the opening of each quote),
    // and the cry of the dark cultists (MonsterFlavorLineSeedTests pins its record).
    [Theory]
    [InlineData("\"The Knight is both protector and aggressor. Strength and vigilance shall")]
    [InlineData("\"The Wheel of Fortune, when played in this instance, indicates that luck is")]
    [InlineData("\"The Wizard symbolizes mystical power and prodigious intelligence. You may")]
    [InlineData("\"When the Priest is played, wisdom and insight are paramount. Faith and")]
    [InlineData("\"The Grail! A most fortuitous card, the Grail symbolizes life itself. You")]
    [InlineData("\"The Sun is a boon to all those who search for things concealed. Keen")]
    [InlineData("\"The Angel indicates that you are being watched over. Protection is yours,")]
    [InlineData("\"The Chariot symbolizes mastery over movement. Speed and endurance are its")]
    [InlineData("The fanatic screams \"Death to those who oppose the Blood God!\"")]
    // The desert's room spells (683 / 684) and the hordeling's death, from the
    // 2026-10-08 export: wording from the game's own message table.
    [InlineData("Vultures circle high overhead.")]
    [InlineData("A shimmering image appears to the north!")]
    [InlineData("A shimmering image appears to the east!")]
    [InlineData("A shimmering image appears to the south!")]
    [InlineData("A shimmering image appears to the west!")]
    [InlineData("The howl of some awful beast can be heard far over the dunes.")]
    [InlineData("The hordeling screeches violently!")]
    // Two more room spells of the same kind in that export (692, 700).
    [InlineData("A heated wind howls through the passageway, kicking up sand and dust.")]
    [InlineData("Beast-like screams echo unnervingly throughout the passageways.")]
    [InlineData("The clicking sound of scrabbling claws can be heard from down the passage.")]
    [InlineData("Eerie lights dance about further down the passageway.")]
    [InlineData("Doors on this level creak and thump!")]
    // The farm fields' room spell (5665), report paradigm-20261010-145330.
    [InlineData("You can barely make out a figure lurching in the haze.")]
    public void AttributedLine_IsRecognized_NotStaged(string line)
    {
        MessageStore messages = new();
        messages.Messages.ReplaceAll(_records);
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        MessageCandidateStore candidates = new();
        using MessageCandidateWatcher watcher = new(router, messages, candidates);
        watcher.NotifyInGame();

        var emitted = new LineExtractor.EmittedLine(
            line, Array.Empty<CellAttributes>(), DateTimeOffset.UtcNow, IsPromptLine: false);
        Invoke(watcher, "OnLine", emitted);
        Invoke(watcher, "CommitPending");

        Assert.Empty(candidates.Candidates);
    }

    private static void Invoke(MessageCandidateWatcher watcher, string method, params object[] args) =>
        typeof(MessageCandidateWatcher)
            .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(watcher, args.Length == 0 ? null : args);
}
