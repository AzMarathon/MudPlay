using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MudPlay.Game.GameData;
using MudPlay.Models.GameData;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// Weakness and frail cut carrying capacity (a negative Encum%, ability 96); they are
// not holds. The index picks them out of the Spells table, and the seeds no longer
// flag weakness as movement-prevented (report paradigm-20261003-201253: every frail
// from a vengeful spirit held the loop for its whole duration and spent mana on
// cure paralysis).
public sealed class EncumbranceDebuffIndexTests
{
    [Fact]
    public void Build_PicksSpellsWithANegativeEncumPercent()
    {
        using JsonDocument spells = JsonDocument.Parse("""
            [
              { "Number": 424, "Name": "weakness", "Abil-0": 4, "Abil-1": 96, "AbilVal-1": -25, "Abil-2": 22, "AbilVal-2": -10 },
              { "Number": 949, "Name": "frail", "Abil-0": 99, "AbilVal-0": -15, "Abil-2": 96, "AbilVal-2": -5 },
              { "Number": 12,  "Name": "giant strength", "Abil-0": 96, "AbilVal-0": 25 },
              { "Number": 66,  "Name": "hold person", "Abil-0": 74, "AbilVal-0": 1 }
            ]
            """);

        HashSet<int> lowering = EncumbranceDebuffIndex.Build(spells);

        Assert.Equal(new[] { 424, 949 }, lowering.OrderBy(n => n));
    }

    [Theory]
    [InlineData("Messages.paradigm.seed.json")]
    [InlineData("Messages.stock.seed.json")]
    public void Seed_NoWeakAndPowerlessRecordIsAHold(string seed)
    {
        string dir = Path.Combine(Path.GetTempPath(), "mudplay-weak-seed-" + Path.GetRandomFileName());
        try
        {
            AppPaths.ExtractEmbeddedSeeds(dir);
            List<MessageRecord> records =
                JsonStore.Load<List<MessageRecord>>(Path.Combine(dir, seed)) ?? new List<MessageRecord>();

            List<MessageRecord> weak = records
                .Where(r => r.AppliedMessage == "You feel weak and powerless").ToList();

            Assert.NotEmpty(weak);
            Assert.All(weak, r => Assert.False(r.Flags.HasFlag(MessageFlags.MovementPrevented), r.Name));

            // Globe of darkness darkens; it carries no hold code either.
            Assert.All(records.Where(r => r.Name == "globe of darkness"),
                r => Assert.False(r.Flags.HasFlag(MessageFlags.MovementPrevented), r.Name));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* temp cleanup */ }
        }
    }
}
