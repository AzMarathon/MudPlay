using System.Collections;

namespace MudPlay.Services;

// One row of a GameTable: a view, not a copy. Holding one keeps its table alive, and
// it stays readable after the cache has dropped or replaced that table. The default
// value is a row with no fields, so "no row" needs no test of its own before a read.
public readonly struct GameRow
{
    private readonly GameTable? _table;
    private readonly int _row;

    internal GameRow(GameTable table, int row)
    {
        _table = table;
        _row = row;
    }

    // False for the default value alone: a row no table produced. For the reader whose
    // answer for "no row" is not its answer for a row with nothing set.
    public bool Exists => _table is not null;

    // The field of this name, matched exactly (case included). False when the row
    // has no such field; a field written as null is present.
    public bool TryGetProperty(string name, out GameValue value)
    {
        if (_table is not null && _table.TryGetCell(_row, name, out int cell))
        {
            value = new GameValue(_table, cell);
            return true;
        }
        value = default;
        return false;
    }

    // The row's fields in the order the file wrote them.
    public FieldList Fields => new(_table, _row);

    public readonly struct FieldList : IEnumerable<GameField>
    {
        private readonly GameTable? _table;
        private readonly int _row;

        internal FieldList(GameTable? table, int row)
        {
            _table = table;
            _row = row;
        }

        public Enumerator GetEnumerator() => new(_table, _row, _table?.FieldCount(_row) ?? 0);
        IEnumerator<GameField> IEnumerable<GameField>.GetEnumerator() => GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public struct Enumerator : IEnumerator<GameField>
        {
            private readonly GameTable? _table;
            private readonly int _row;
            private readonly int _count;
            private int _position;

            internal Enumerator(GameTable? table, int row, int count)
            {
                _table = table;
                _row = row;
                _count = count;
                _position = -1;
            }

            public readonly GameField Current => _table!.FieldAt(_row, _position);
            readonly object IEnumerator.Current => Current;
            public bool MoveNext() => ++_position < _count;
            public void Reset() => _position = -1;
            public readonly void Dispose() { }
        }
    }
}
