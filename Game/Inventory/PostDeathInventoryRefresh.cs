namespace MudPlay.Game.Inventory;

// Re-reads the inventory once after a death. A death takes what the character wore and
// carried (into a corpse, or onto the floor), but the record of it is only rebuilt by a
// full `i`, and nothing sent one: the record went on listing the old gear as worn. With
// it all handed back and sitting in the pack, a gear set found every piece "already
// worn" and sent nothing (report paradigm-20261009-105244).
//
// The record is marked stale at the death, so hand-overs before the re-read aren't
// patched onto it, and the `i` goes out when the character is next known to be standing
// in a room, by which time the game takes commands again.
public sealed class PostDeathInventoryRefresh
{
    private readonly Action _markStale;
    private readonly Action _requestInventory;
    private bool _due;

    public PostDeathInventoryRefresh(Action markStale, Action requestInventory)
    {
        ArgumentNullException.ThrowIfNull(markStale);
        ArgumentNullException.ThrowIfNull(requestInventory);
        _markStale = markStale;
        _requestInventory = requestInventory;
    }

    // True from a death until the re-read has been asked for.
    public bool Due => _due;

    public void OnDeath()
    {
        _markStale();
        _due = true;
    }

    // The master switch (true = off): off, the read stays owed and goes out at
    // the first known room after the switch is back on.
    public Func<bool>? MasterSwitchOff { get; set; }

    // Whether a command sent now would be dropped (the engine send gate is held).
    // The read stays owed then, and OnRoomKnown is called again when the hold
    // lifts: sent into a hold it was lost, and the record stayed stale with nothing
    // left to ask for it.
    public Func<bool>? SendHeld { get; set; }

    // The character's room is known again (the graveyard, on the way back in).
    public void OnRoomKnown()
    {
        if (!_due) return;
        if (MasterSwitchOff?.Invoke() == true || SendHeld?.Invoke() == true) return;
        _due = false;
        _requestInventory();
    }

    // A new character, or the connection dropped: the login's own read covers it.
    public void Reset() => _due = false;
}
