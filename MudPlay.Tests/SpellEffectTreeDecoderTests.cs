using System;
using System.IO;
using System.Linq;
using MudPlay.Services;
using MudPlay.ViewModels.GameData.Edit;
using Xunit;

namespace MudPlay.Tests;

// Pins the room-spell effect tree decode (Spells → Abil 148 → TBInfo): conditional
// branches with carry/level gates and their tone, cumulative-threshold weighted
// outcomes, nested random, summon/cast/teleport effect runs with record links, and
// the teleport-sweep collapse. A synthetic sea-crossing set mirrors the real spell
// shape (no-boat danger branch vs boat-carried accent branch):
//
//   Spell 1000 → TBInfo 5000 (conditional root)
//     5000: failitem 690:failitem 691:random 5001   (no boat  → danger)
//           maxlevel 49:checkitem 690:random 5002    (has boat → accent)
//     5001: 30 → addexp 0        (30% nothing)
//          100 → random 5003     (70% → teleport sweep, collapses)
//     5002: 100 → nomonsters:summon 880 x2:summon 904
//     5003: 5 teleports, all "Deep Sea"  → "Deep Sea (5 rooms)"
public sealed class SpellEffectTreeDecoderTests : IDisposable
{
    private readonly string _root;

    public SpellEffectTreeDecoderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-spelltree-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    private const string SpellsJson = """
        [
          { "Number": 1000, "Name": "Sea Crossing", "Abil-0": 148, "AbilVal-0": 5000 },
          { "Number": 1001, "Name": "Ordered Summon", "Abil-0": 148, "AbilVal-0": 6000 }
        ]
        """;

    private const string TBInfoJson = """
        [
          { "Number": 5000, "Action": "failitem 690:failitem 691:random 5001\nmaxlevel 49:checkitem 690:random 5002\n" },
          { "Number": 5001, "Action": "30:addexp 0\n100:random 5003\n" },
          { "Number": 5002, "Action": "100:nomonsters:summon 880:summon 880:summon 904\n" },
          { "Number": 5003, "Action": "20:teleport 501 17\n40:teleport 502 17\n60:teleport 503 17\n80:teleport 504 17\n100:teleport 505 17\n" },
          { "Number": 6000, "Action": "100:summon 904:nomonsters:summon 880:summon 880:summon 880\n" }
        ]
        """;

    private const string MonstersJson = """
        [ { "Number": 880, "Name": "sea hag" }, { "Number": 904, "Name": "crimson mist" } ]
        """;

    private const string ItemsJson = """
        [ { "Number": 690, "Name": "log raft" }, { "Number": 691, "Name": "wooden skiff" } ]
        """;

    private const string RoomsJson = """
        [
          { "Number": 1, "Map Number": 17, "Room Number": 501, "Name": "Deep Sea" },
          { "Number": 2, "Map Number": 17, "Room Number": 502, "Name": "Deep Sea" },
          { "Number": 3, "Map Number": 17, "Room Number": 503, "Name": "Deep Sea" },
          { "Number": 4, "Map Number": 17, "Room Number": 504, "Name": "Deep Sea" },
          { "Number": 5, "Map Number": 17, "Room Number": 505, "Name": "Deep Sea" }
        ]
        """;

    private GameDataCache NewCache()
    {
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Spells.json"), SpellsJson);
        File.WriteAllText(Path.Combine(_root, "alpha", "TBInfo.json"), TBInfoJson);
        File.WriteAllText(Path.Combine(_root, "alpha", "Monsters.json"), MonstersJson);
        File.WriteAllText(Path.Combine(_root, "alpha", "Items.json"), ItemsJson);
        File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), RoomsJson);
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        return cache;
    }

    private static string Flatten(SpellEffectNode node) => string.Concat(node.Runs.Select(r => r.Text));

    [Fact]
    public void Decode_SplitsIntoTwoConditionBranches()
    {
        var tree = new SpellInfoRowsBuilder(NewCache()).BuildEffectTree(1000);
        Assert.Equal(2, tree.Count);
        Assert.All(tree, n => Assert.True(n.IsBranch));
    }

    [Fact]
    public void Decode_NoBoatBranch_IsDangerAndNamesBothItems()
    {
        var branch = new SpellInfoRowsBuilder(NewCache()).BuildEffectTree(1000)[0];
        Assert.True(branch.IsDanger);
        string text = Flatten(branch);
        Assert.Contains("not carrying log raft", text);
        Assert.Contains("not carrying wooden skiff", text);
    }

    [Fact]
    public void Decode_NoBoatBranch_HasWeightedOutcomes()
    {
        var branch = new SpellInfoRowsBuilder(NewCache()).BuildEffectTree(1000)[0];
        Assert.Equal(2, branch.Children.Count);
        Assert.Equal(30, branch.Children[0].Percent);
        Assert.Equal("nothing", Flatten(branch.Children[0]));
        Assert.Equal(70, branch.Children[1].Percent);
    }

    [Fact]
    public void Decode_CollapsesTeleportSweepToNamedSummary()
    {
        var sweepOutcome = new SpellInfoRowsBuilder(NewCache()).BuildEffectTree(1000)[0].Children[1];
        // A 5-teleport table with one common destination name collapses to a single line,
        // not five children.
        Assert.Empty(sweepOutcome.Children);
        string text = Flatten(sweepOutcome);
        Assert.Contains("Deep Sea", text);
        Assert.Contains("(5 rooms)", text);
        Assert.Contains(sweepOutcome.Runs, r => r.IsLink);   // the destination is a map link
    }

    [Fact]
    public void Decode_BoatBranch_IsAccentWithLinkedSummons()
    {
        var branch = new SpellInfoRowsBuilder(NewCache()).BuildEffectTree(1000)[1];
        Assert.True(branch.IsAccent);
        Assert.Contains("level ≤ 49", Flatten(branch));
        Assert.Contains("carrying log raft", Flatten(branch));

        var outcome = Assert.Single(branch.Children);
        Assert.Equal(100, outcome.Percent);
        string text = Flatten(outcome);
        Assert.Contains("no NPCs in the room", text);   // nomonsters is a gate, not "clears the room"
        Assert.Contains("sea hag", text);
        Assert.Contains("×2", text);            // two identical summons collapse
        Assert.Contains("crimson mist", text);
        Assert.Contains(outcome.Runs, r => r.IsLink && r.Text == "sea hag");
    }

    [Fact]
    public void Decode_MidSequenceGate_NestsOnlyTrailingCommands()
    {
        // "summon 904 : nomonsters : summon 880 ×3" runs top-down: crimson mist is
        // unconditional; the sea hags fire only when the room is empty. The gate must
        // scope to the trailing summons, NOT hoist onto the whole outcome.
        var outcome = Assert.Single(new SpellInfoRowsBuilder(NewCache()).BuildEffectTree(1001));
        Assert.Equal(100, outcome.Percent);
        Assert.Equal("crimson mist", Flatten(outcome));   // own line, no gate prefix

        var gated = Assert.Single(outcome.Children);
        string text = Flatten(gated);
        Assert.StartsWith("if no NPCs in the room —", text);
        Assert.Contains("sea hag", text);
        Assert.Contains("×3", text);
    }

    [Fact]
    public void Decode_SpellWithNoTextblock_IsEmpty()
        => Assert.Empty(new SpellInfoRowsBuilder(NewCache()).BuildEffectTree(999999));
}
