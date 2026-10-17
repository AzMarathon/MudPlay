namespace MudPlay.Game.Cash;

// What a TransactionEntry records — a bank deposit, a stash-room hide, or a
// shop sale or purchase.
public enum TransactionKind
{
    // A bank `dep`osit, the client's or one typed by hand.
    Bank,

    // A stash room hid excess coin and/or auto-stash items.
    Stash,

    // Items sold to a shop on one visit, with what they fetched.
    Sold,

    // Items bought from a shop on one visit, with what they cost.
    Bought,
}

// One recorded cash/item offload for the Session Stats → Transaction history
// window: the time it happened, its kind, a human-readable description of what was put away (e.g. "Deposited
// 12,300 wealth" or "Hid a torch, 400 gold"), and where it happened — the bank
// / stash room rendered as "Name (map/room)". Location is null when the current
// room isn't known at echo time.
public readonly record struct TransactionEntry(
    DateTimeOffset Time,
    TransactionKind Kind,
    string Detail,
    string? Location = null,
    // The user's "keep" mark: the row survives the window's Clear. Saved with the row.
    bool Keep = false);
