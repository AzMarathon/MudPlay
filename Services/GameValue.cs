using System.Text.Json;

namespace MudPlay.Services;

// One field's value in a GameTable row. Its reads follow System.Text.Json's
// JsonElement, wrong-kind reads included: asking a string for a number throws
// InvalidOperationException, and a number that is not whole fails TryGetInt32.
public readonly struct GameValue
{
    private readonly GameTable? _table;
    private readonly int _cell;

    internal GameValue(GameTable table, int cell)
    {
        _table = table;
        _cell = cell;
    }

    private GameTable Table =>
        _table ?? throw new InvalidOperationException("This value does not belong to a table.");

    // The JSON kind the file wrote; Undefined for a value no row produced.
    public JsonValueKind ValueKind => _table is null ? JsonValueKind.Undefined : _table.KindOf(_cell);

    // The text of a string, or null for a JSON null.
    public string? GetString() => Table.GetString(_cell);

    // Whether a string's text is exactly these UTF-8 bytes, without making a string
    // of it: for telling a common cell ("0") apart in a loop over a whole table.
    public bool ValueEquals(ReadOnlySpan<byte> utf8Text) => Table.ValueEquals(_cell, utf8Text);

    public bool TryGetInt32(out int value) => Table.TryGetInt32(_cell, out value);

    public int GetInt32() => TryGetInt32(out int value) ? value : throw new FormatException("The number is not a 32-bit whole number.");

    public bool TryGetDouble(out double value) => Table.TryGetDouble(_cell, out value);

    // The value as JSON text: a number or a nested value exactly as the file wrote
    // it, a string encoded afresh from its text (GameTable.GetRawText says where
    // that parts from the file).
    public string GetRawText() => Table.GetRawText(_cell);

    // A string's own text, a number's or nested value's JSON text, "True" / "False",
    // and nothing for null.
    public override string ToString() => _table is null ? string.Empty : _table.ToDisplayString(_cell);
}
