using System.Globalization;

namespace MudPlay.Services;

// The numbered fields of a game-data row ("Abil-0", "Abil-1", …), spelled once. A
// per-set index reads them for every row of a table, and a name built for each read
// was most of what the smaller index builds allocated.
internal static class NumberedFields
{
    // prefix + 0 up to prefix + (count − 1), indexed by the number.
    public static string[] Of(string prefix, int count)
    {
        string[] names = new string[count];
        for (int i = 0; i < count; i++) names[i] = prefix + i.ToString(CultureInfo.InvariantCulture);
        return names;
    }
}
