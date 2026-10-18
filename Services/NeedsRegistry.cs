using System.Collections.Generic;
using System.Linq;

namespace MudPlay.Services;

// One outstanding acquisition need: the character needs a specific item to
// traverse a planned route — an (Item: N) / (Ticket: N) exit gate (or a hazard
// counter, a fetchable door key) the walker will hit but we don't carry. Posted
// by Game.Map.PathItemDemandTracker at walk-start; resolved when the item enters
// inventory. Active fulfillers (shop routing, monster-drop reroute, give /
// summon routing, party obtain) react to the post. Immutable value identified by
// Descriptor — the registry dedupes two posts of one descriptor on
// NeedsRegistry.Post.
//   Descriptor — the item id, in decimal ("175"). The registry treats it as an
//     opaque identity key.
//   Requester  — identifier of the engine that posted the need (e.g.
//     "PathItemDemandTracker"). Diagnostic only.
//   PostedAt   — wall-clock time the need was first posted.
//   Quantity   — how many copies the local character should acquire to
//     satisfy this need — 1 solo, the party shortfall when the leader is
//     provisioning everyone across a gate. Fulfillers read it to acquire the
//     full count (e.g. buy N), and the requester resolves the need on
//     count-met rather than the first copy. Not part of registry identity
//     (dedup keys on the descriptor), so it is fixed at first post.
public readonly record struct Need(
    string Descriptor,
    string Requester,
    DateTimeOffset PostedAt,
    int Quantity = 1);

// The fulfillment half of the auto-engine coordination model (the gating half
// is Game.Map.MovementCoordinator). No engine references another by type: a
// requester Posts a Need, fulfilling engines react to NeedPosted, and the
// requester Resolves it once satisfied. The requester never knows who fulfils.
//
// Thread affinity: posts come from MessageRouter handlers and resolves from
// engine ticks — both on the UI thread. The internal list is
// nonetheless guarded by a lock (cheap insurance, matching LogService /
// Game.Map.MovementCoordinator). The NeedPosted event fires on the posting
// thread after the lock is released.
public sealed class NeedsRegistry
{
    private readonly object _gate = new();
    private readonly List<Need> _needs = new();
    private readonly LogService? _log;

    // Fires after a genuinely new need is posted (not on a deduped re-post).
    // Lets a fulfilling engine act on it at once instead of polling. Fired on
    // the posting thread after the internal lock is released.
    public event Action<Need>? NeedPosted;

    public NeedsRegistry(LogService? log = null)
    {
        _log = log;
    }

    // Post a need for quantity copies (clamped to at least one). Deduped on the
    // descriptor: re-posting an already-outstanding identical need is
    // a no-op (no duplicate slot, no event) and returns the existing need — so
    // the quantity is fixed by the first post and a re-announce with a
    // different count doesn't disturb an acquisition already in flight.
    // Returns the (new or existing) need so the requester can hold the handle.
    public Need Post(string descriptor, string requester, int quantity = 1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(descriptor);
        ArgumentException.ThrowIfNullOrWhiteSpace(requester);

        Need need;
        bool isNew;
        lock (_gate)
        {
            int existing = IndexOf(descriptor);
            if (existing >= 0)
            {
                isNew = false;
                need = _needs[existing];
            }
            else
            {
                isNew = true;
                need = new Need(descriptor, requester, DateTimeOffset.Now, Math.Max(1, quantity));
                _needs.Add(need);
            }
        }

        if (isNew)
        {
            _log?.Info("Needs", $"posted — descriptor={descriptor} qty={need.Quantity} by={requester}");
            NeedPosted?.Invoke(need);
        }
        return need;
    }

    // Re-announce the still-outstanding needs to the subscribers, as if they had
    // just been posted.
    //
    // Post fires NeedPosted only for a NEW need, which makes a fulfiller's first
    // offer its one and only chance. That loses a need whenever a
    // fulfiller had to stand down the first time it was offered: a route needing
    // two items posts both back-to-back, the first router takes item one and every
    // other router defers so they don't fight over the walk — and when that detour
    // finishes, nothing ever re-offers item two. The route then fails at the gate
    // it never fetched for (report paradigm-20260911-103025: the orb was collected,
    // then the walk died on the gate-key door nobody had been sent after).
    //
    // Called at walk-start, once the previous detour has necessarily finished (its
    // resume is what started this walk), so the deferring router is now free.
    // Re-offering a need already being fetched is harmless: every fulfiller no-ops
    // when its own detour is already running.
    public void Reoffer()
    {
        Need[] outstanding;
        lock (_gate)
            outstanding = _needs.ToArray();
        if (outstanding.Length == 0) return;

        _log?.Info("Needs", $"re-offering {outstanding.Length} outstanding need(s)");
        foreach (Need n in outstanding)
            NeedPosted?.Invoke(n);
    }

    // Remove a need from the registry — the fulfiller got the thing. Identity
    // is Need.Descriptor. Returns true if a matching need was
    // removed.
    public bool Resolve(Need need)
    {
        bool removed;
        lock (_gate)
        {
            int at = IndexOf(need.Descriptor);
            removed = at >= 0;
            if (removed) _needs.RemoveAt(at);
        }
        if (removed) _log?.Info("Needs", $"resolved — descriptor={need.Descriptor}");
        return removed;
    }

    // Snapshot of every outstanding need, oldest first. Lets a requester check
    // whether its need is still pending without re-posting.
    public IReadOnlyList<Need> Outstanding()
    {
        lock (_gate)
        {
            return _needs.ToArray();
        }
    }

    // Drop every outstanding need and say how many went. Called on character swap
    // so one character's pending acquisitions don't leak into the next, and when
    // the game data becomes another set, under which an item number is another
    // item.
    public int Clear()
    {
        int dropped;
        lock (_gate)
        {
            dropped = _needs.Count;
            _needs.Clear();
        }
        return dropped;
    }

    private int IndexOf(string descriptor)
        => _needs.FindIndex(n => string.Equals(n.Descriptor, descriptor, StringComparison.Ordinal));
}
