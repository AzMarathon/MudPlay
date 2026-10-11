using System.Globalization;

namespace MudPlay.Services;

// The numbered fields of a game-data row ("Abil-0", "Abil-1", …), spelled once. A
// per-set index reads every slot of every row of a table, and a row is looked up by
// field name: the names are held in an array per reader so a read costs a lookup
// and no string.
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
