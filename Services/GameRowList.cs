using System.Collections;

namespace MudPlay.Services;

// The rows of a GameTable in file order. A struct so a foreach over a table
// allocates nothing.
public readonly struct GameRowList : IReadOnlyList<GameRow>
{
    private readonly GameTable _table;

    internal GameRowList(GameTable table, int count)
    {
        _table = table;
        Count = count;
    }

    public int Count { get; }

    public GameRow this[int index] =>
        (uint)index < (uint)Count ? new GameRow(_table, index) : throw new ArgumentOutOfRangeException(nameof(index));

    public Enumerator GetEnumerator() => new(_table, Count);
    IEnumerator<GameRow> IEnumerable<GameRow>.GetEnumerator() => GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public struct Enumerator : IEnumerator<GameRow>
    {
        private readonly GameTable _table;
        private readonly int _count;
        private int _row;

        internal Enumerator(GameTable table, int count)
        {
            _table = table;
            _count = count;
            _row = -1;
        }

        public readonly GameRow Current => new(_table, _row);
        readonly object IEnumerator.Current => Current;
        public bool MoveNext() => ++_row < _count;
        public void Reset() => _row = -1;
        public readonly void Dispose() { }
    }
}
