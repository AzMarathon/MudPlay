using System.Globalization;
using MudPlay.ViewModels.GameData.Tables;
using Xunit;

namespace MudPlay.Tests;

// The range-filter text boxes bind int? through this converter; a negative or
// blank value must round-trip so the user can type "-50" to find a vulnerability.
public sealed class NullableIntConverterTests
{
    private static readonly NullableIntConverter C = NullableIntConverter.Instance;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    [Theory]
    [InlineData("-50", -50)]
    [InlineData("200", 200)]
    [InlineData(" 12 ", 12)]
    public void ConvertBack_ParsesInt(string text, int expected)
        => Assert.Equal(expected, C.ConvertBack(text, typeof(int?), null, Inv));

    [Theory]
    [InlineData("")]
    [InlineData("-")]      // mid-typed minus — no bound yet
    [InlineData("abc")]
    public void ConvertBack_BlankOrInvalid_IsNull(string text)
        => Assert.Null(C.ConvertBack(text, typeof(int?), null, Inv));

    [Fact]
    public void Convert_RendersValueOrEmpty()
    {
        Assert.Equal("-50", C.Convert(-50, typeof(string), null, Inv));
        Assert.Null(C.Convert(null, typeof(string), null, Inv));   // null -> placeholder
    }
}
