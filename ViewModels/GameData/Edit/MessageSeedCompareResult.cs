using System.Collections.Generic;
using MudPlay.Models.GameData;
using MudPlay.Services;

namespace MudPlay.ViewModels.GameData.Edit;

// What Apply in the compare-with-seed dialog commits: the messages to put back to the
// seed, and how many the user kept as theirs.
public sealed record MessageSeedCompareResult(
    IReadOnlyList<SeedDelta<MessageRecord>.Difference> UseSeed,
    int Kept);
