using System;
using System.Collections.Generic;
using MudPlay.Game.Map;

namespace MudPlay.Game.Cash;

// What we believe each stash room is holding, in copper.
//
// The transaction-history ledger can't answer this: it formats amounts into a
// display string ("Hid a torch, 400 gold") and keeps no structured figure, it
// records deposits only with nothing ever decrementing, and it rolls at 500
// entries. Those are all correct choices for a history window and all fatal for
// arithmetic, so funding keeps its own running tally.
//
// This is a BELIEF, never a fact. Of the three places money sits, two are
// certain — the purse and a bank balance both move only in ways the game echoes
// to us. A stash has a third mutation path we can never see: any player who
// searches the room can walk off with the pile. So a caller may plan against
// these figures, but must confirm by searching before it spends them, and must
// re-plan in place when the room comes up short.
public sealed class StashLedger
{
    private readonly Dictionary<string, long> _copperByRoom = new(StringComparer.OrdinalIgnoreCase);

    // Raised whenever a believed balance changes, so the owner can persist it.
    public event Action? Changed;

    // Believed copper in one room. Zero for a room we've never hidden coin in —
    // indistinguishable from one that's been emptied, which is the honest answer
    // either way.
    public long Believed(RoomKey room)
        => _copperByRoom.TryGetValue(Key(room), out long v) ? v : 0;

    // Every room we believe holds something, as (room, copper). Rooms that have
    // dropped to zero are omitted rather than listed as empty.
    public IReadOnlyList<(RoomKey Room, long Copper)> NonEmpty()
    {
        List<(RoomKey, long)> result = new();
        foreach ((string wire, long copper) in _copperByRoom)
            if (copper > 0 && RoomKey.TryParseWire(wire, out RoomKey key))
                result.Add((key, copper));
        return result;
    }

    // Record coin hidden here. Additive: a stash is built up over many laps, one
    // `You hid N <coin>.` echo per denomination.
    public void NoteHidden(RoomKey room, long copper)
    {
        if (copper <= 0) return;
        string key = Key(room);
        _copperByRoom[key] = (_copperByRoom.TryGetValue(key, out long v) ? v : 0) + copper;
        Changed?.Invoke();
    }

    // Record coin taken back out. Clamped at zero — picking up more than we
    // believed was there just means someone else's coin was on the floor too, or
    // our tally had drifted; either way the room is empty afterwards, not negative.
    public void NoteRecovered(RoomKey room, long copper)
    {
        if (copper <= 0) return;
        string key = Key(room);
        if (!_copperByRoom.TryGetValue(key, out long known)) return;
        long left = Math.Max(0, known - copper);
        if (left == 0) _copperByRoom.Remove(key); else _copperByRoom[key] = left;
        Changed?.Invoke();
    }

    // Correct the tally to what a search actually revealed. The one path that can
    // move a balance DOWN without us having taken anything — it's how a robbed
    // stash gets written off instead of being re-planned against forever.
    public void Reconcile(RoomKey room, long observedCopper)
    {
        string key = Key(room);
        long known = _copperByRoom.TryGetValue(key, out long v) ? v : 0;
        if (known == observedCopper) return;
        if (observedCopper <= 0) _copperByRoom.Remove(key); else _copperByRoom[key] = observedCopper;
        Changed?.Invoke();
    }

    // Replace the whole tally from persisted profile state on load.
    public void Hydrate(IReadOnlyDictionary<string, long>? saved)
    {
        _copperByRoom.Clear();
        if (saved is not null)
            foreach ((string wire, long copper) in saved)
                if (copper > 0) _copperByRoom[wire] = copper;
        Changed?.Invoke();
    }

    // Point-in-time copy for persisting into the character profile.
    public Dictionary<string, long> Snapshot() => new(_copperByRoom, StringComparer.OrdinalIgnoreCase);

    private static string Key(RoomKey room) => $"{room.Map}/{room.Room}";
}
