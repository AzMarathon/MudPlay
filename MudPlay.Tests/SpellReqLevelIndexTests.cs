using System.IO;
using MudPlay.Game.Combat;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// Pins SpellReqLevelIndex — the cast-code → ReqLevel lookup used to decide whether a
// configured attack spell clears a monster's SpellImmu. Keyed by Short, case-insensitive.
public sealed class SpellReqLevelIndexTests : IDisposable
{
    private readonly string _root;

    public SpellReqLevelIndexTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-spellreqlevel-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    private const string SpellsJson = """
        [
          { "Number": 1,  "Name": "disrupt",   "Short": "disr", "ReqLevel": 16, "Learnable": 1 },
          { "Number": 2,  "Name": "bless",     "Short": "bles", "ReqLevel": 2,  "Learnable": 1 },
          { "Number": 3,  "Name": "no level",  "Short": "noen", "Learnable": 1 }
        ]
        """;

    private SpellReqLevelIndex NewIndex(string set = "alpha", string json = SpellsJson)
    {
        Directory.CreateDirectory(Path.Combine(_root, set));
        File.WriteAllText(Path.Combine(_root, set, "Spells.json"), json);
        GameDataCache cache = new(_root);
        cache.SwitchSet(set);
        return new SpellReqLevelIndex(cache);
    }

    [Fact]
    public void KnownCastCode_ReturnsReqLevel()
    {
        SpellReqLevelIndex s = NewIndex();
        Assert.Equal(16, s.ReqLevel("disr"));
        Assert.Equal(2, s.ReqLevel("bles"));
        Assert.Equal(16, s.ReqLevel("DISR"));   // case-insensitive
    }

    [Fact]
    public void MissingReqLevelColumn_DefaultsToZero()
    {
        SpellReqLevelIndex s = NewIndex();
        Assert.Equal(0, s.ReqLevel("noen"));
    }

    [Fact]
    public void UnknownOrMissing_FailsOpen()
    {
        SpellReqLevelIndex s = NewIndex();
        Assert.Equal(-1, s.ReqLevel("zzz"));
        Assert.Equal(-1, s.ReqLevel(""));
        Assert.Equal(-1, s.ReqLevel(null));
    }

    [Fact]
    public void DuplicateCastCode_LearnableRowWinsOverNonLearnableDuplicate()
    {
        // "disr" = the player's Disrupt Undead (ReqLevel 16, Learnable) plus an
        // item-triggered "disr" variant (ReqLevel 0, not learnable) that sorts AFTER
        // it in the table. A last-writer-wins map lets the 0 clobber the 16, so every
        // SpellImmu >= 1 monster reads as immune to a spell that actually clears it
        // (report paradigm-20260819-195419 — blood skeleton wrongly deemed Unkillable).
        const string json = """
            [
              { "Number": 151,  "Name": "disrupt", "Short": "disr", "ReqLevel": 16, "Learnable": 1 },
              { "Number": 5532, "Name": "disrupt", "Short": "disr", "ReqLevel": 0,  "Learnable": 0 }
            ]
            """;
        SpellReqLevelIndex s = NewIndex(json: json);
        Assert.Equal(16, s.ReqLevel("disr"));
    }

    [Fact]
    public void DuplicateCastCode_LearnableRowWinsRegardlessOfOrder()
    {
        // Same collision with the non-learnable duplicate sorted BEFORE the real spell.
        const string json = """
            [
              { "Number": 5532, "Name": "disrupt", "Short": "disr", "ReqLevel": 0,  "Learnable": 0 },
              { "Number": 151,  "Name": "disrupt", "Short": "disr", "ReqLevel": 16, "Learnable": 1 }
            ]
            """;
        SpellReqLevelIndex s = NewIndex(json: json);
        Assert.Equal(16, s.ReqLevel("disr"));
    }

    [Fact]
    public void DuplicateCastCode_AllNonLearnable_FallsBackToLastWriter()
    {
        // No learnable row exists at all for this code — nothing to prefer, so the
        // plain last-writer-wins behavior stands (unchanged from before the fix).
        const string json = """
            [
              { "Number": 100, "Name": "mob cast a",  "Short": "xyz", "ReqLevel": 5, "Learnable": 0 },
              { "Number": 101, "Name": "mob cast b",  "Short": "xyz", "ReqLevel": 9, "Learnable": 0 }
            ]
            """;
        SpellReqLevelIndex s = NewIndex(json: json);
        Assert.Equal(9, s.ReqLevel("xyz"));
    }
}
