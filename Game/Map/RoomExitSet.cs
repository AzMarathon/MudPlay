using System.Collections;
using System.Collections.Generic;
using System.Numerics;

namespace MudPlay.Game.Map;

// A room's exits as the graph holds them. A realm has well over a hundred
// thousand exits and nineteen in twenty are a target and nothing else, so an exit
// like that is kept as its target alone and only the rest are kept whole. A
// dictionary of whole exits for every room was two thirds of the room map.
//
// The exits come back in Direction order: the order a Rooms row lists them in,
// with the Teleport slot a build pass may add coming last. A set never changes
// once made (a pass that alters an exit swaps in a new set made by With), so
// the route search and the map drawing read it from their own threads.
internal sealed class RoomExitSet : IReadOnlyDictionary<Direction, RoomExit>
{
    private const int Slots = (int)Direction.Teleport + 1;

    public static readonly RoomExitSet Empty = new(0, 0, [], null);

    // A bit for each direction that has an exit, and for each of those kept whole.
    private readonly ushort _present;
    private readonly ushort _whole;

    // Every exit's target, and the exits kept whole, each in Direction order.
    private readonly RoomKey[] _targets;
    private readonly RoomExit[]? _wholeExits;

    private RoomExitSet(ushort present, ushort whole, RoomKey[] targets, RoomExit[]? wholeExits)
    {
        _present = present;
        _whole = whole;
        _targets = targets;
        _wholeExits = wholeExits;
    }

    // The same exits as a set of this kind: exits itself when it already is one.
    public static RoomExitSet From(IReadOnlyDictionary<Direction, RoomExit> exits)
    {
        if (exits is RoomExitSet own) return own;
        Builder builder = new();
        foreach ((Direction direction, RoomExit exit) in exits) builder.Set(direction, exit);
        return builder.Build();
    }

    // These exits with the one that way replaced, or added when there is none.
    public RoomExitSet With(Direction direction, RoomExit exit)
    {
        Builder builder = new();
        foreach ((Direction present, RoomExit kept) in this) builder.Set(present, kept);
        builder.Set(direction, exit);
        return builder.Build();
    }

    public int Count => BitOperations.PopCount(_present);

    // Where each exit leads, in Direction order: all a reachability walk reads.
    public ReadOnlySpan<RoomKey> Targets => _targets;

    public bool ContainsKey(Direction key) => (_present & Bit(key)) != 0;

    public bool TryGetValue(Direction key, out RoomExit value)
    {
        uint bit = Bit(key);
        if ((_present & bit) == 0)
        {
            value = default;
            return false;
        }
        value = At(BitOperations.PopCount(_present & (bit - 1)), bit);
        return true;
    }

    public RoomExit this[Direction key] => TryGetValue(key, out RoomExit exit)
        ? exit
        : throw new KeyNotFoundException($"The given key '{key}' was not present in the dictionary.");

    // No bit for a value that isn't a direction: it is simply not found.
    private static uint Bit(Direction direction) => (uint)direction < Slots ? 1u << (int)direction : 0;

    private RoomExit At(int index, uint bit) => (_whole & bit) != 0
        ? _wholeExits![BitOperations.PopCount(_whole & (bit - 1))]
        : new RoomExit(_targets[index], RoomExitHint.None, null);

    // Asked of the exit's own equality, so a field added to RoomExit later counts
    // without this being told: an exit that sets it is kept whole.
    private static bool IsOnlyATarget(in RoomExit exit) =>
        exit == new RoomExit(exit.Target, RoomExitHint.None, null);

    public Enumerator GetEnumerator() => new(this);

    IEnumerator<KeyValuePair<Direction, RoomExit>> IEnumerable<KeyValuePair<Direction, RoomExit>>.GetEnumerator() =>
        GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    IEnumerable<Direction> IReadOnlyDictionary<Direction, RoomExit>.Keys =>
        new Part<Direction>(this, static at => at.Key);

    IEnumerable<RoomExit> IReadOnlyDictionary<Direction, RoomExit>.Values =>
        new Part<RoomExit>(this, static at => at.Value);

    // Walks the exits in place. A struct, so a build pass that holds the set by
    // its own type walks half the realm without an allocation for each room.
    public struct Enumerator : IEnumerator<KeyValuePair<Direction, RoomExit>>
    {
        private readonly RoomExitSet _exits;
        private uint _left;
        private uint _bit;
        private int _index;

        internal Enumerator(RoomExitSet exits)
        {
            _exits = exits;
            _left = exits._present;
            _bit = 0;
            _index = -1;
        }

        public bool MoveNext()
        {
            if (_left == 0)
            {
                _bit = 0;
                return false;
            }
            _bit = _left & (0u - _left);
            _left ^= _bit;
            _index++;
            return true;
        }

        public readonly Direction Key => _bit == 0 ? default : (Direction)BitOperations.TrailingZeroCount(_bit);

        public readonly RoomExit Value => _bit == 0 ? default : _exits.At(_index, _bit);

        public readonly KeyValuePair<Direction, RoomExit> Current => new(Key, Value);

        readonly object IEnumerator.Current => Current;

        public void Reset()
        {
            _left = _exits._present;
            _bit = 0;
            _index = -1;
        }

        public readonly void Dispose() { }
    }

    // The directions or the exits alone, as the interface's Keys and Values hand
    // them out: read from the set as they are asked for, nothing copied or kept.
    private sealed class Part<T>(RoomExitSet exits, Func<Enumerator, T> of) : IEnumerable<T>
    {
        public IEnumerator<T> GetEnumerator() => new Walk(exits, of);

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        private sealed class Walk(RoomExitSet exits, Func<Enumerator, T> of) : IEnumerator<T>
        {
            private Enumerator _at = new(exits);

            public T Current => of(_at);

            object? IEnumerator.Current => Current;

            public bool MoveNext() => _at.MoveNext();

            public void Reset() => _at.Reset();

            public void Dispose() { }
        }
    }

    // Takes a room's exits a direction at a time, in any order, and makes the set
    // of them; it is then empty again. The row read keeps one for each thread, so
    // a room costs no more than the arrays its set keeps.
    internal sealed class Builder
    {
        private readonly RoomExit[] _slots = new RoomExit[Slots];
        private uint _present;

        public void Set(Direction direction, RoomExit exit)
        {
            if ((uint)direction >= Slots)
                throw new ArgumentOutOfRangeException(nameof(direction), direction, "Not a direction a room has an exit in.");
            _slots[(int)direction] = exit;
            _present |= 1u << (int)direction;
        }

        public RoomExitSet Build()
        {
            if (_present == 0) return Empty;

            RoomKey[] targets = new RoomKey[BitOperations.PopCount(_present)];
            uint whole = 0;
            int at = 0;
            for (uint left = _present; left != 0; left &= left - 1)
            {
                int slot = BitOperations.TrailingZeroCount(left);
                targets[at++] = _slots[slot].Target;
                if (!IsOnlyATarget(_slots[slot])) whole |= 1u << slot;
            }

            RoomExit[]? wholeExits = null;
            if (whole != 0)
            {
                wholeExits = new RoomExit[BitOperations.PopCount(whole)];
                at = 0;
                for (uint left = whole; left != 0; left &= left - 1)
                    wholeExits[at++] = _slots[BitOperations.TrailingZeroCount(left)];
            }

            RoomExitSet built = new((ushort)_present, (ushort)whole, targets, wholeExits);
            _present = 0;
            return built;
        }
    }
}
