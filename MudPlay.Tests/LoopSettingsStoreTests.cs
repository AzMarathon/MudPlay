using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MudPlay.Game;
using MudPlay.Game.Map;
using MudPlay.Game.Remote;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.ViewModels.Navigation;
using Xunit;

namespace MudPlay.Tests;

// A loop's two "Entire Loop Settings" (only attack in lair rooms, the wait to enter
// lairs) belong to the character; the loop itself stays with the game data, shared.
// Before this both rode in the loop file, so one character ticking either changed it
// for every character on the set. Each "client" here is a profile, a loop catalogue
// and the store between them, as the app wires them; two of them on one set stand in
// for two characters, a client each.
public sealed class LoopSettingsStoreTests : IDisposable
{
    private readonly string _setName;
    private readonly string _awaySet;
    private readonly string _bbs;

    public LoopSettingsStoreTests()
    {
        string id = Guid.NewGuid().ToString("N").Substring(0, 12);
        _setName = "test-loopset-" + id;
        _awaySet = "test-loopset-away-" + id;
        _bbs = "test-loopsetbbs-" + id;
        foreach (string set in new[] { _setName, _awaySet })
        {
            Directory.CreateDirectory(AppPaths.GameDataSetLoopsFolder(set));
            File.WriteAllText(Path.Combine(AppPaths.GameDataRoot, set, "Rooms.json"), GraphJson);
        }
    }

    public void Dispose()
    {
        foreach (string folder in new[]
                 {
                     Path.Combine(AppPaths.GameDataRoot, _setName), Path.Combine(AppPaths.GameDataRoot, _awaySet),
                     AppPaths.BbsFolder(_bbs),
                 })
        {
            try { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
            catch { /* best-effort */ }
        }
    }

    // 1/1 - 1/2 - 1/3 in a line, so the builder has rooms to click.
    private const string GraphJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "A",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/2", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "B",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/3", "S": "1/1", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 3, "Name": "C",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "1/2", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    private static readonly RoomKey[] TwoRooms = { new(1, 1), new(1, 2) };

    private static readonly LoopOwnSettings Neither = new(false, LairEntryDebuffMode.Off);
    private static readonly LoopOwnSettings LairOnlyAndBlock = new(true, LairEntryDebuffMode.BlockSpells);

    private string LoopPath(string name, string? set = null) =>
        Path.Combine(AppPaths.GameDataSetLoopsFolder(set ?? _setName), name + LoopManager.LoopFileSuffix);

    // A loop file as a version from before the move wrote it, both settings in it.
    private void WriteSharedLoop(string name, bool onlyLair, string wait, string? set = null, int secondRoom = 2) =>
        File.WriteAllText(LoopPath(name, set),
            $$"""{ "SchemaVersion": 3, "Name": "{{name}}", "OnlyAttackInLairRooms": {{(onlyLair ? "true" : "false")}}, "LairEntryDebuff": "{{wait}}", "Waypoints": [ { "Room": "1/1" }, { "Room": "1/{{secondRoom}}" } ] }""");

    private void WriteOlderCharacter(string name, string extra = "")
    {
        Directory.CreateDirectory(AppPaths.ProfileFolder(_bbs, name));
        File.WriteAllText(AppPaths.CharacterProfileFile(_bbs, name), $$"""{ "Name": "{{name}}", "SchemaVersion": 8{{extra}} }""");
    }

    private sealed record Client(ProfileService Profile, LoopManager Loops, LoopSettingsStore Store, LogService Log,
        RoomGraphManager Graph)
    {
        public IEnumerable<string> Messages => Log.Snapshot().Select(e => e.Message);
    }

    // The catalogue as the app has it when a character loads: its set's files read.
    private Client Open(string character, string? set = null)
    {
        LogService log = new();
        GameDataCache cache = new();
        cache.SwitchSet(set ?? _setName);
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged(set ?? _setName);
        LoopManager loops = new(new BfsMapper(graph), graph, log);
        loops.LoadAll(set ?? _setName);
        ProfileService profile = new();
        LoopSettingsStore store = new(profile, loops, log);
        profile.Load(_bbs, character);
        return new Client(profile, loops, store, log, graph);
    }

    private static (bool OnlyLair, string Wait, string Notes) FileHolds(string path)
    {
        using JsonDocument file = JsonDocument.Parse(File.ReadAllText(path));
        return (file.RootElement.GetProperty("OnlyAttackInLairRooms").GetBoolean(),
            file.RootElement.GetProperty("LairEntryDebuff").GetString()!,
            file.RootElement.GetProperty("Notes").GetString() ?? "");
    }

    // ----- first values ----------------------------------------------

    [Fact]
    public void CharacterWithNoneOfItsOwn_RunsTheLoopWithWhatTheFileHolds()
    {
        WriteSharedLoop("Dragons", onlyLair: true, wait: "BlockSpells");
        WriteSharedLoop("Orcs", onlyLair: false, wait: "Off");
        WriteOlderCharacter("Alpha");

        Client alpha = Open("Alpha");

        Assert.Null(alpha.Profile.Current!.LoopSettings);   // a profile from before the field loads
        Assert.Equal(LairOnlyAndBlock, alpha.Loops.Get("Dragons")!.OwnSettings);
        Assert.Equal(Neither, alpha.Loops.Get("Orcs")!.OwnSettings);
        Assert.False(alpha.Store.HasOwn("Dragons"));
    }

    [Fact]
    public void FirstChange_TakesTheOtherSettingFromTheFile_AndSaysSo()
    {
        WriteSharedLoop("Dragons", onlyLair: true, wait: "BlockSpells");
        WriteOlderCharacter("Alpha");
        Client alpha = Open("Alpha");

        alpha.Loops.SetSettings("dragons", new LoopOwnSettings(true, LairEntryDebuffMode.Off));

        LoopSettingsChoice kept = alpha.Profile.Current!.LoopSettings!.Single().Value;
        Assert.True(kept.OnlyAttackInLairRooms);
        Assert.Equal("Off", kept.LairEntryDebuff);
        Assert.True(alpha.Store.HasOwn("Dragons"));
        // Named as the catalogue has it, however the caller spelled it.
        Assert.Contains(alpha.Messages, m => m.Contains("loop 'Dragons'") && m.Contains("start from the loop file's")
            && m.Contains("only attack in lair rooms on") && m.Contains("Block spells"));
        Assert.Contains(alpha.Messages, m => m.Contains("wait to enter lairs off for this character (was Block spells)"));
        Assert.DoesNotContain(alpha.Messages, m => m.Contains("only attack in lair rooms on for this character"));

        // A second change is the character's own from the start: no file read named.
        alpha.Loops.SetSettings("Dragons", Neither);
        Assert.Single(alpha.Messages, m => m.Contains("start from the loop file's"));
        Assert.Contains(alpha.Messages, m => m.Contains("only attack in lair rooms off for this character (was on)"));
    }

    // ----- one character's change never reaches another ---------------

    [Fact]
    public void OneCharactersChange_LeavesTheOtherAsItWas_BothWays()
    {
        WriteSharedLoop("Dragons", onlyLair: true, wait: "BlockSpells");
        WriteSharedLoop("Orcs", onlyLair: false, wait: "Off");
        WriteOlderCharacter("Alpha");
        WriteOlderCharacter("Beta");

        Client alpha = Open("Alpha");
        alpha.Loops.SetSettings("Dragons", Neither);
        alpha.Loops.SetSettings("Orcs", new LoopOwnSettings(true, LairEntryDebuffMode.WaitForSpells));

        // Beta loads after Alpha's changes and still finds what the files hold.
        Client beta = Open("Beta");
        Assert.Equal(LairOnlyAndBlock, beta.Loops.Get("Dragons")!.OwnSettings);
        Assert.Equal(Neither, beta.Loops.Get("Orcs")!.OwnSettings);

        beta.Loops.SetSettings("Dragons", new LoopOwnSettings(true, LairEntryDebuffMode.WaitForPriorityBuffs));
        beta.Loops.SetSettings("Orcs", new LoopOwnSettings(false, LairEntryDebuffMode.BlockSpells));

        // And Alpha, reloaded from its own file, keeps its own.
        Client alphaAgain = Open("Alpha");
        Assert.Equal(Neither, alphaAgain.Loops.Get("Dragons")!.OwnSettings);
        Assert.Equal(new LoopOwnSettings(true, LairEntryDebuffMode.WaitForSpells), alphaAgain.Loops.Get("Orcs")!.OwnSettings);
        Client betaAgain = Open("Beta");
        Assert.Equal(new LoopOwnSettings(true, LairEntryDebuffMode.WaitForPriorityBuffs), betaAgain.Loops.Get("Dragons")!.OwnSettings);
        Assert.Equal(new LoopOwnSettings(false, LairEntryDebuffMode.BlockSpells), betaAgain.Loops.Get("Orcs")!.OwnSettings);
    }

    [Fact]
    public void LiveChange_DoesNotRewriteTheLoopFile()
    {
        WriteSharedLoop("Dragons", onlyLair: false, wait: "Off");
        WriteOlderCharacter("Alpha");
        byte[] before = File.ReadAllBytes(LoopPath("Dragons"));
        DateTime stamped = File.GetLastWriteTimeUtc(LoopPath("Dragons"));
        Client alpha = Open("Alpha");
        int catalogueEvents = 0;
        alpha.Loops.LoopsChanged += () => catalogueEvents++;

        alpha.Loops.SetSettings("Dragons", LairOnlyAndBlock);
        alpha.Loops.SetSettings("Dragons", new LoopOwnSettings(false, LairEntryDebuffMode.WaitForSpells));

        Assert.Equal(before, File.ReadAllBytes(LoopPath("Dragons")));
        Assert.Equal(stamped, File.GetLastWriteTimeUtc(LoopPath("Dragons")));
        Assert.Equal(0, catalogueEvents);   // nothing of the catalogue changed for the other views
        Assert.Equal(new LoopOwnSettings(false, LairEntryDebuffMode.WaitForSpells), alpha.Loops.Get("Dragons")!.OwnSettings);
    }

    // A save that hands in a fresh object (a running loop's snapshot) never read the
    // file; the file's own two settings must come through it as they were.
    [Fact]
    public void SavingTheLoop_WritesTheFilesTwoSettingsBackAsFound()
    {
        WriteSharedLoop("Dragons", onlyLair: true, wait: "BlockSpells");
        WriteOlderCharacter("Alpha");
        WriteOlderCharacter("Beta");
        Client alpha = Open("Alpha");
        alpha.Loops.SetSettings("Dragons", Neither);

        Loop held = alpha.Loops.Get("Dragons")!;
        held.Notes = "edited";
        alpha.Loops.Save(held);
        alpha.Loops.Save(new Loop("Dragons", TwoRooms) { Notes = "snapshot", OwnSettings = Neither });

        Assert.Equal((true, "BlockSpells", "snapshot"), FileHolds(LoopPath("Dragons")));
        Assert.Equal(Neither, alpha.Loops.Get("Dragons")!.OwnSettings);
        Assert.Equal(LairOnlyAndBlock, Open("Beta").Loops.Get("Dragons")!.OwnSettings);
    }

    // ----- a loop re-read from disk ------------------------------------

    [Fact]
    public void LoopReReadAfterAnotherClientEditedIt_KeepsTheCharactersSettings()
    {
        WriteSharedLoop("Dragons", onlyLair: false, wait: "Off");
        WriteSharedLoop("Orcs", onlyLair: true, wait: "WaitForSpells");
        WriteOlderCharacter("Alpha");
        Client alpha = Open("Alpha");
        alpha.Loops.SetSettings("Dragons", LairOnlyAndBlock);
        Loop dragonsBefore = alpha.Loops.Get("Dragons")!, orcsBefore = alpha.Loops.Get("Orcs")!;

        // Another client changes both routes; a re-read makes new objects of them.
        WriteSharedLoop("Dragons", onlyLair: false, wait: "Off", secondRoom: 3);
        WriteSharedLoop("Orcs", onlyLair: true, wait: "WaitForSpells", secondRoom: 3);
        Assert.True(alpha.Loops.TakeInOutsideChanges());

        Loop dragons = alpha.Loops.Get("Dragons")!, orcs = alpha.Loops.Get("Orcs")!;
        Assert.NotSame(dragonsBefore, dragons);
        Assert.NotSame(orcsBefore, orcs);
        Assert.Equal(3, dragons.Waypoints[1].Key.Room);
        Assert.Equal(LairOnlyAndBlock, dragons.OwnSettings);
        // One it has set neither for is still the file's.
        Assert.Equal(new LoopOwnSettings(true, LairEntryDebuffMode.WaitForSpells), orcs.OwnSettings);
    }

    // ----- what reads them ---------------------------------------------

    [Fact]
    public void CombatSuppression_GoesByTheCharactersSetting()
    {
        WriteSharedLoop("Dragons", onlyLair: false, wait: "Off");
        WriteOlderCharacter("Alpha");
        WriteOlderCharacter("Beta");
        Client alpha = Open("Alpha");
        alpha.Loops.SetSettings("Dragons", new LoopOwnSettings(true, LairEntryDebuffMode.Off));
        Client beta = Open("Beta");
        RoomKey notALair = new(1, 2);

        Assert.True(LoopCombatSuppression.IsSuppressed(alpha.Loops.Get("Dragons")!, notALair, currentIsLair: false));
        Assert.False(LoopCombatSuppression.IsSuppressed(beta.Loops.Get("Dragons")!, notALair, currentIsLair: false));
    }

    // A run holds its loop apart from the catalogue (the object it started from, its
    // snapshot of the last run). A change reaches those by name; a loop that was
    // never saved has no settings kept and is left alone.
    [Fact]
    public void ChangedSettings_ReachTheCopiesARunHolds_ByName()
    {
        WriteSharedLoop("Dragons", onlyLair: false, wait: "Off");
        WriteSharedLoop("Orcs", onlyLair: false, wait: "Off");
        WriteOlderCharacter("Alpha");
        Client alpha = Open("Alpha");
        Loop running = new("Dragons", TwoRooms);
        Loop otherLoop = new("Orcs", TwoRooms);
        Loop neverSaved = new("Scratch", TwoRooms) { OwnSettings = LairOnlyAndBlock };
        Loop[] held = { running, otherLoop, neverSaved };
        alpha.Loops.SettingsChanged += name => alpha.Loops.CopySettingsTo(held, name);

        alpha.Loops.SetSettings("Dragons", LairOnlyAndBlock);

        Assert.Equal(LairOnlyAndBlock, running.OwnSettings);
        Assert.Equal(Neither, otherLoop.OwnSettings);
        Assert.Equal(LairOnlyAndBlock, neverSaved.OwnSettings);
    }

    [Fact]
    public void AnotherCharacterLoadingInTheSameClient_SwapsEveryLoopsSettings()
    {
        WriteSharedLoop("Dragons", onlyLair: false, wait: "Off");
        WriteOlderCharacter("Alpha");
        WriteOlderCharacter("Beta");
        Client client = Open("Alpha");
        client.Loops.SetSettings("Dragons", LairOnlyAndBlock);
        Loop lastRun = new("Dragons", TwoRooms) { OwnSettings = LairOnlyAndBlock };
        client.Loops.SettingsChanged += name => client.Loops.CopySettingsTo(new[] { lastRun }, name);

        client.Profile.Load(_bbs, "Beta");

        Assert.Equal(Neither, client.Loops.Get("Dragons")!.OwnSettings);
        Assert.Equal(Neither, lastRun.OwnSettings);
        Assert.False(client.Store.HasOwn("Dragons"));

        client.Profile.Load(_bbs, "Alpha");
        Assert.Equal(LairOnlyAndBlock, client.Loops.Get("Dragons")!.OwnSettings);
        Assert.Equal(LairOnlyAndBlock, lastRun.OwnSettings);
    }

    // ----- the editor and the builder ----------------------------------

    [Fact]
    public async Task EditorSave_KeepsTheTwoSettingsForTheCharacter_AndTheRestInTheFile()
    {
        WriteSharedLoop("Dragons", onlyLair: false, wait: "Off");
        WriteOlderCharacter("Alpha");
        WriteOlderCharacter("Beta");
        Client alpha = Open("Alpha");
        LoopEditorDialogViewModel vm = new(alpha.Loops.Get("Dragons")!, alpha.Loops, alpha.Graph)
        {
            Notes = "edited",
            OnlyAttackInLairRooms = true,
            WaitToDebuffBeforeLairs = true,
            LairDebuffWaitChoice = LairEntryDebuffModes.BlockSpellsChoice,
        };

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(string.Empty, vm.SaveError);
        Assert.Equal((false, "Off", "edited"), FileHolds(LoopPath("Dragons")));
        LoopSettingsChoice kept = Open("Alpha").Profile.Current!.LoopSettings!["Dragons"];
        Assert.True(kept.OnlyAttackInLairRooms);
        Assert.Equal("BlockSpells", kept.LairEntryDebuff);

        Loop betas = Open("Beta").Loops.Get("Dragons")!;
        Assert.Equal("edited", betas.Notes);
        Assert.Equal(Neither, betas.OwnSettings);
    }

    // An editor Save that changes neither setting leaves the character reading the file.
    [Fact]
    public async Task EditorSave_WithBothSettingsAsTheyWere_KeepsNothingForTheCharacter()
    {
        WriteSharedLoop("Dragons", onlyLair: true, wait: "BlockSpells");
        WriteOlderCharacter("Alpha");
        Client alpha = Open("Alpha");
        LoopEditorDialogViewModel vm = new(alpha.Loops.Get("Dragons")!, alpha.Loops, alpha.Graph) { Notes = "edited" };

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal((true, "BlockSpells", "edited"), FileHolds(LoopPath("Dragons")));
        Assert.False(alpha.Store.HasOwn("Dragons"));
        Assert.Null(alpha.Profile.Current!.LoopSettings);
    }

    [Fact]
    public async Task EditorRename_KeepsTheCharactersSettingsUnderTheNewName()
    {
        WriteSharedLoop("Dragons", onlyLair: false, wait: "Off");
        WriteOlderCharacter("Alpha");
        Client alpha = Open("Alpha");
        alpha.Loops.SetSettings("Dragons", LairOnlyAndBlock);
        LoopEditorDialogViewModel vm = new(alpha.Loops.Get("Dragons")!, alpha.Loops, alpha.Graph) { Name = "Wyrms" };

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.False(File.Exists(LoopPath("Dragons")));
        Assert.Equal((false, "Off", ""), FileHolds(LoopPath("Wyrms")));
        Client again = Open("Alpha");
        Assert.Equal(LairOnlyAndBlock, again.Loops.Get("Wyrms")!.OwnSettings);
        Assert.Equal(new[] { "Wyrms" }, again.Profile.Current!.LoopSettings!.Keys);
    }

    [Fact]
    public void DeletingALoop_DropsTheCharactersSettingsForIt()
    {
        WriteSharedLoop("Dragons", onlyLair: false, wait: "Off");
        WriteOlderCharacter("Alpha");
        Client alpha = Open("Alpha");
        alpha.Loops.SetSettings("Dragons", LairOnlyAndBlock);

        alpha.Loops.Delete("Dragons");
        alpha.Loops.Save(new Loop("Dragons", TwoRooms));

        Assert.Equal(Neither, alpha.Loops.Get("Dragons")!.OwnSettings);
        Assert.False(alpha.Store.HasOwn("Dragons"));
        Assert.Empty(Open("Alpha").Profile.Current!.LoopSettings!);
    }

    [Fact]
    public void BuilderSave_ANewLoopsSettingsAreTheBuildingCharacters()
    {
        WriteOlderCharacter("Alpha");
        WriteOlderCharacter("Beta");
        Client alpha = Open("Alpha");
        LoopBuilderSessionViewModel builder = new(alpha.Loops, alpha.Graph) { ProposedName = "Built" };
        builder.AddClick(new RoomKey(1, 1));
        builder.AddClick(new RoomKey(1, 3));
        builder.OnlyAttackInLairRooms = true;
        builder.WaitToDebuffBeforeLairs = true;
        builder.LairDebuffWaitChoice = LairEntryDebuffModes.WaitForPriorityBuffsChoice;

        Assert.NotNull(builder.Save());

        LoopOwnSettings built = new(true, LairEntryDebuffMode.WaitForPriorityBuffs);
        Assert.Equal((false, "Off", ""), FileHolds(LoopPath("Built")));
        Assert.Equal(built, alpha.Loops.Get("Built")!.OwnSettings);
        Assert.Equal(built, Open("Alpha").Loops.Get("Built")!.OwnSettings);
        Assert.Equal(Neither, Open("Beta").Loops.Get("Built")!.OwnSettings);
    }

    // ----- @loop send ----------------------------------------------------

    private static List<string> DataLines(Loop loop)
    {
        IReadOnlyList<string> chunks = LoopShareCodec.Encode(loop);
        return chunks.Select((c, i) => $"{{{LoopShareHandler.DataToken} a1b2 {i + 1}/{chunks.Count} {c}}}").ToList();
    }

    private static List<string> Receive(Client receiver, Loop sent)
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        List<string> notices = new();
        using LoopShareReceiver inbox = new(new ChatRouter(router), receiver.Loops, notices.Add);
        inbox.NoteSendConfirmed("Sender");
        foreach (string line in DataLines(sent))
            inbox.Ingest(new ChatLogEntry(DateTimeOffset.UnixEpoch, ChatChannel.TelepathIncoming, "Sender", line,
                $"Sender telepaths: {line}"));
        return notices;
    }

    [Fact]
    public void LoopSend_CarriesTheSendersOwnSettings_AndTheReceiverTakesThemAsItsOwn()
    {
        // The sender's file says neither; the character sending has both its own way.
        WriteSharedLoop("Dragons", onlyLair: false, wait: "Off");
        WriteOlderCharacter("Alpha");
        WriteOlderCharacter("Beta");
        Client sender = Open("Alpha");
        LoopOwnSettings sendersOwn = new(true, LairEntryDebuffMode.WaitForPriorityBuffs);
        sender.Loops.SetSettings("Dragons", sendersOwn);
        Client receiver = Open("Beta", _awaySet);

        List<string> notices = Receive(receiver, sender.Loops.Get("Dragons")!);

        Assert.Equal("[Received loop 'Dragons' 2 rooms from Sender]", notices.Single());
        Assert.Equal(sendersOwn, receiver.Loops.Get("Dragons")!.OwnSettings);
        Assert.True(receiver.Store.HasOwn("Dragons"));
        // The file it saved is every character's on its set, and holds neither.
        Assert.Equal((false, "Off", ""), FileHolds(LoopPath("Dragons", _awaySet)));
        Assert.Equal(sendersOwn, Open("Beta", _awaySet).Loops.Get("Dragons")!.OwnSettings);
    }

    [Fact]
    public void LoopSend_RouteAlreadyHere_SavesNoFile_AndTakesTheSettings()
    {
        WriteSharedLoop("Dragons", onlyLair: false, wait: "Off");
        WriteSharedLoop("Dragons", onlyLair: false, wait: "Off", set: _awaySet);
        WriteOlderCharacter("Alpha");
        WriteOlderCharacter("Beta");
        Client sender = Open("Alpha");
        sender.Loops.SetSettings("Dragons", LairOnlyAndBlock);
        Client receiver = Open("Beta", _awaySet);
        byte[] before = File.ReadAllBytes(LoopPath("Dragons", _awaySet));

        List<string> notices = Receive(receiver, sender.Loops.Get("Dragons")!);

        Assert.Equal("[Received loop 'Dragons' 2 rooms from Sender, already have it; its loop settings are now yours]",
            notices.Single());
        Assert.Single(receiver.Loops.Loops);
        Assert.Equal(before, File.ReadAllBytes(LoopPath("Dragons", _awaySet)));
        Assert.Equal(LairOnlyAndBlock, receiver.Loops.Get("Dragons")!.OwnSettings);

        // Sent again, there is nothing left to take.
        Assert.Equal("[Received loop 'Dragons' 2 rooms from Sender, already have it]",
            Receive(receiver, sender.Loops.Get("Dragons")!).Single());
    }

    // ----- the profile ---------------------------------------------------

    // A way of waiting a later version added must not keep the profile from loading.
    [Fact]
    public void ProfileNamingAWaitThisClientDoesNotKnow_Loads_AndReadsItAsOff()
    {
        WriteSharedLoop("Dragons", onlyLair: false, wait: "BlockSpells");
        WriteOlderCharacter("Alpha",
            """, "LoopSettings": { "dragons": { "OnlyAttackInLairRooms": true, "LairEntryDebuff": "SomeLaterWait" } }""");

        Client alpha = Open("Alpha");

        Assert.Equal(new LoopOwnSettings(true, LairEntryDebuffMode.Off), alpha.Loops.Get("Dragons")!.OwnSettings);

        // A change replaces the entry under the name however the file spelled it.
        alpha.Loops.SetSettings("Dragons", LairOnlyAndBlock);
        Assert.Equal(new[] { "Dragons" }, alpha.Profile.Current!.LoopSettings!.Keys);
    }
}
