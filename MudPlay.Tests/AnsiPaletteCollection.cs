using Xunit;

namespace MudPlay.Tests;

// AnsiPalette's base colours are process-wide. A test that changes them runs here,
// alone, so no test drawing or reading colours on another thread sees them moved.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AnsiPaletteCollection
{
    public const string Name = "AnsiPalette";
}
