using MudPlay.Game.Calculators;
using Xunit;

namespace MudPlay.Tests;

// The confirmed alignment-value ladder + the "(Alignment: X to Y)" exit-gate
// parser (report paradigm-20260827-144553).
public sealed class AlignmentBandsTests
{
    [Theory]
    [InlineData("Saint", -201)]
    [InlineData("Good", -100)]
    [InlineData("Lawful", -100)]   // a Good-with-a-flag title → Good's value
    [InlineData("Neutral", 0)]
    [InlineData("Seedy", 40)]
    [InlineData("Outlaw", 80)]
    [InlineData("Criminal", 120)]
    [InlineData("Villain", 180)]
    [InlineData("Fiend", 300)]
    [InlineData("fiend", 300)]      // case-insensitive
    public void ValueOf_KnownBand_ReturnsValue(string band, int expected)
        => Assert.Equal(expected, AlignmentBands.ValueOf(band));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Bogus")]
    public void ValueOf_UnknownOrEmpty_ReturnsNull(string? band)
        => Assert.Null(AlignmentBands.ValueOf(band));

    [Fact]
    public void ParseGate_NeutralToFiend_Is0To300()
        => Assert.Equal((0, 300), AlignmentBands.ParseGate("Alignment: Neutral to Fiend"));

    [Fact]
    public void ParseGate_ReversedSpec_Normalised()
        // Tolerate a spec listed evil→good; Lo is always the smaller (more-good) value.
        => Assert.Equal((0, 300), AlignmentBands.ParseGate("Alignment: Fiend to Neutral"));

    [Fact]
    public void ParseGate_UnknownBand_ReturnsNull()
        => Assert.Null(AlignmentBands.ParseGate("Alignment: Neutral to Bogus"));

    [Fact]
    public void ParseGate_NotAnAlignment_ReturnsNull()
        => Assert.Null(AlignmentBands.ParseGate("Level: 10 to 20"));

    [Theory]
    [InlineData(-100, false)]   // Good — below the [0,300] window (the blocked evil entrance)
    [InlineData(-201, false)]   // Saint — below
    [InlineData(0, true)]       // Neutral — inside
    [InlineData(40, true)]      // Seedy — inside
    [InlineData(300, true)]     // Fiend — inside
    public void Admits_NeutralToFiend_InclusiveWindow(int value, bool admitted)
        => Assert.Equal(admitted, AlignmentBands.Admits((0, 300), value));
}
