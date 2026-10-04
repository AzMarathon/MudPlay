using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MudPlay.Models.GameData;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// A monster can carry a textblock the engine reads a line from on its slow tick. The
// Blood God cultists' is one line, held by fifteen Stock monster records; the
// imported game data has no column for it, so the seeds carry the line, linked to
// those monsters, in both realms.
public sealed class MonsterFlavorLineSeedTests : IDisposable
{
    private const string Cry = "The fanatic screams \"Death to those who oppose the Blood God!\"";

    private static readonly int[] Cultists =
        { 29, 142, 143, 144, 145, 146, 147, 149, 150, 151, 1099, 1100, 1101, 1102, 1104 };

    private readonly string _dir;

    public MonsterFlavorLineSeedTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "mudplay-flavor-seed-" + Path.GetRandomFileName());
        AppPaths.ExtractEmbeddedSeeds(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp cleanup */ }
    }

    [Theory]
    [InlineData("Messages.paradigm.seed.json")]
    [InlineData("Messages.stock.seed.json")]
    public void CultistsCry_IsSeeded_LinkedToItsMonsters_WithAnIdThatMatchesItsFields(string seed)
    {
        List<MessageRecord> records =
            JsonStore.Load<List<MessageRecord>>(Path.Combine(_dir, seed)) ?? new List<MessageRecord>();

        MessageRecord r = records.Single(m => m.WitnessMessage == Cry);

        Assert.Equal(Cultists, r.Links!.Where(k => k.Table == "Monsters").Select(k => k.Number));
        Assert.Equal(
            MessageRecord.ComputeId(r.Name, r.CasterMessage, r.TargetMessage,
                r.WitnessMessage, r.AppliedMessage, r.AppliedEndsWith),
            r.Id);
    }
}
