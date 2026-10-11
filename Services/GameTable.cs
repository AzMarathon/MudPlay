using System.Buffers.Text;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace MudPlay.Services;

// One imported game-data table, held in the compact form GameTableReader fills
// straight from the table's JSON file. The parsed JSON document is never kept: it
// cost five to ten times the file's size, and Monsters and Items sit in memory for
// the whole session because combat and inventory code look rows up on every line.
//
// Nothing is lost against the file. A row keeps its fields in file order, each with
// the JSON kind it was written as, and a number keeps its exact text ("7.0" is not
// "7"). Field names are stored once per row shape (in practice one per table), a
// whole number is its four bytes, and every other value is an id into one pool of
// distinct UTF-8 texts, so the thousands of repeated exit and name strings are
// stored once.
//
// Immutable once built, so background builders read it from any thread. The number
// and name indexes are built on first use and die with the table.
public sealed class GameTable
{
    // What a cell holds. Number covers both of the first two.
    internal const byte Int32Cell = 0;      // value is the number itself
    internal const byte NumberCell = 1;     // value is the id of the number's exact text
    internal const byte StringCell = 2;     // value is the id of the unescaped text
    internal const byte NullCell = 3;
    internal const byte TrueCell = 4;
    internal const byte FalseCell = 5;
    internal const byte ObjectCell = 6;     // value is the id of the nested value's JSON text
    internal const byte ArrayCell = 7;

    // The field names of a row, in file order. Rows of one table nearly always share
    // one shape; a table whose rows differ holds one per distinct list.
    internal sealed class Shape
    {
        public readonly string[] Names;
        // A name written twice in one row resolves to its last field, as it did in
        // the parsed document.
        public readonly Dictionary<string, int> Positions;

        public Shape(string[] names)
        {
            Names = names;
            Positions = new Dictionary<string, int>(names.Length, StringComparer.Ordinal);
            for (int i = 0; i < names.Length; i++) Positions[names[i]] = i;
        }
    }

    private readonly Shape[] _shapes;
    // Null when every row has shape 0: row r then starts at r × width.
    private readonly int[]? _rowShape;
    private readonly int[]? _rowStart;
    private readonly int _rowCount;
    private readonly byte[] _kinds;
    private readonly int[] _values;
    private readonly byte[] _text;
    // Text id → offset into _text; one past the last id closes the final text.
    private readonly int[] _textStart;

    private Dictionary<int, int>? _byNumber;
    private Dictionary<string, int>? _byName;

    internal GameTable(
        Shape[] shapes, int[]? rowShape, int[]? rowStart, int rowCount,
        byte[] kinds, int[] values, byte[] text, int[] textStart)
    {
        _shapes = shapes;
        _rowShape = rowShape;
        _rowStart = rowStart;
        _rowCount = rowCount;
        _kinds = kinds;
        _values = values;
        _text = text;
        _textStart = textStart;
    }

    // The table's rows in file order.
    public GameRowList Rows => new(this, _rowCount);

    // Bytes this table holds, indexes excluded: what the set-load log line reports.
    public long ResidentBytes =>
        _kinds.LongLength + _values.LongLength * sizeof(int) + _text.LongLength
        + _textStart.LongLength * sizeof(int)
        + ((_rowShape?.LongLength ?? 0) + (_rowStart?.LongLength ?? 0)) * sizeof(int);

    // The first row whose Number field is this whole number.
    public GameRow? FindByNumber(int number) =>
        NumberIndex().TryGetValue(number, out int row) ? new GameRow(this, row) : null;

    // The first row whose Name field is this text, whatever its case.
    public GameRow? FindByName(string name) =>
        NameIndex().TryGetValue(name, out int row) ? new GameRow(this, row) : null;

    // Every Number the table holds.
    public IReadOnlyCollection<int> Numbers => NumberIndex().Keys;

    // Two threads may both build an index; the first one stored is the one kept.
    private Dictionary<int, int> NumberIndex()
    {
        if (Volatile.Read(ref _byNumber) is { } built) return built;
        Dictionary<int, int> index = new();
        for (int r = 0; r < _rowCount; r++)
        {
            if (!TryGetCell(r, "Number", out int cell)) continue;
            if (_kinds[cell] == Int32Cell) index.TryAdd(_values[cell], r);
            else if (_kinds[cell] == NumberCell && TryGetInt32(cell, out int n)) index.TryAdd(n, r);
        }
        return Interlocked.CompareExchange(ref _byNumber, index, null) ?? index;
    }

    private Dictionary<string, int> NameIndex()
    {
        if (Volatile.Read(ref _byName) is { } built) return built;
        Dictionary<string, int> index = new(StringComparer.OrdinalIgnoreCase);
        for (int r = 0; r < _rowCount; r++)
        {
            if (!TryGetCell(r, "Name", out int cell) || _kinds[cell] != StringCell) continue;
            index.TryAdd(Text(_values[cell]), r);
        }
        return Interlocked.CompareExchange(ref _byName, index, null) ?? index;
    }

    // ----- Row and value access (GameRow / GameValue forward here) --------------------

    private Shape ShapeOf(int row) => _shapes[_rowShape is null ? 0 : _rowShape[row]];

    private int StartOf(int row) => _rowStart is null ? row * _shapes[0].Names.Length : _rowStart[row];

    internal bool TryGetCell(int row, string name, out int cell)
    {
        if (_shapes.Length != 0 && ShapeOf(row).Positions.TryGetValue(name, out int position))
        {
            cell = StartOf(row) + position;
            return true;
        }
        cell = -1;
        return false;
    }

    internal int FieldCount(int row) => _shapes.Length == 0 ? 0 : ShapeOf(row).Names.Length;

    internal GameField FieldAt(int row, int position) =>
        new(ShapeOf(row).Names[position], new GameValue(this, StartOf(row) + position));

    internal JsonValueKind KindOf(int cell) => _kinds[cell] switch
    {
        Int32Cell or NumberCell => JsonValueKind.Number,
        StringCell => JsonValueKind.String,
        NullCell => JsonValueKind.Null,
        TrueCell => JsonValueKind.True,
        FalseCell => JsonValueKind.False,
        ObjectCell => JsonValueKind.Object,
        _ => JsonValueKind.Array,
    };

    private ReadOnlySpan<byte> Utf8(int textId) =>
        _text.AsSpan(_textStart[textId], _textStart[textId + 1] - _textStart[textId]);

    private string Text(int textId) => Encoding.UTF8.GetString(Utf8(textId));

    private InvalidOperationException WrongKind(int cell, string wanted) =>
        new($"The requested operation requires an element of type '{wanted}', but the target element has type '{KindOf(cell)}'.");

    internal string? GetString(int cell) => _kinds[cell] switch
    {
        StringCell => Text(_values[cell]),
        NullCell => null,
        _ => throw WrongKind(cell, "String"),
    };

    // Whether a string's text is exactly these UTF-8 bytes, compared in place: no
    // string is made. A null equals only the default span (not an empty text),
    // which is JsonElement.ValueEquals's rule too.
    internal bool ValueEquals(int cell, ReadOnlySpan<byte> utf8) => _kinds[cell] switch
    {
        StringCell => Utf8(_values[cell]).SequenceEqual(utf8),
        NullCell => Unsafe.IsNullRef(ref MemoryMarshal.GetReference(utf8)),
        _ => throw WrongKind(cell, "String"),
    };

    // A number is whole only when its text is: "7.0" and "7e0" are not.
    // A failed read leaves 0 in value, never the digits it got through.
    internal bool TryGetInt32(int cell, out int value)
    {
        switch (_kinds[cell])
        {
            case Int32Cell:
                value = _values[cell];
                return true;
            case NumberCell:
                ReadOnlySpan<byte> text = Utf8(_values[cell]);
                if (Utf8Parser.TryParse(text, out value, out int used) && used == text.Length) return true;
                value = 0;
                return false;
            default:
                throw WrongKind(cell, "Number");
        }
    }

    internal bool TryGetDouble(int cell, out double value)
    {
        switch (_kinds[cell])
        {
            case Int32Cell:
                value = _values[cell];
                return true;
            case NumberCell:
                ReadOnlySpan<byte> text = Utf8(_values[cell]);
                if (Utf8Parser.TryParse(text, out value, out int used) && used == text.Length) return true;
                value = 0;
                return false;
            default:
                throw WrongKind(cell, "Number");
        }
    }

    // Most cells are small whole numbers, and most of those are 0: one shared string
    // each instead of a new one per read (the Game Data browser reads every cell of
    // a table as text).
    private static readonly string[] SmallNumbers = Enumerable.Range(0, 1024)
        .Select(n => n.ToString(CultureInfo.InvariantCulture)).ToArray();

    // The value as JSON text. A number comes back exactly as the file wrote it; a
    // string is re-escaped the way the importer writes one.
    internal string GetRawText(int cell) => _kinds[cell] switch
    {
        Int32Cell => (uint)_values[cell] < (uint)SmallNumbers.Length
            ? SmallNumbers[_values[cell]]
            : _values[cell].ToString(CultureInfo.InvariantCulture),
        NumberCell or ObjectCell or ArrayCell => Text(_values[cell]),
        StringCell => "\"" + JavaScriptEncoder.UnsafeRelaxedJsonEscaping.Encode(Text(_values[cell])) + "\"",
        NullCell => "null",
        TrueCell => "true",
        _ => "false",
    };

    internal string ToDisplayString(int cell) => _kinds[cell] switch
    {
        StringCell => Text(_values[cell]),
        NullCell => string.Empty,
        TrueCell => bool.TrueString,
        FalseCell => bool.FalseString,
        _ => GetRawText(cell),
    };
}
