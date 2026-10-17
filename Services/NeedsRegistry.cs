using System.Collections.Generic;
using System.Linq;

namespace MudPlay.Services;

// The kinds of acquisition need an engine can post to the NeedsRegistry.
// Deliberately minimal. New kinds are added when an engine actually needs
// one; no speculative bloat.
public enum NeedKind
{
    // The character needs an illumination source (torch / lantern / +illu
    // gear / light spell) to see a dark room. Posted by auto-light.
    LightSource,

    // The character needs a specific item to traverse a planned route — an
    // (Item: N) / (Ticket: N) exit gate the walker will hit but we don't
    // carry the required item. The Need.Descriptor is the item id (decimal).
    // Posted by Game.Map.PathItemDemandTracker at walk-start; resolved when
    // the item enters inventory. Active fulfillers (shop routing, monster-drop
    // reroute, give / summon routing, party obtain) react to the post.
    PathItem,
}

// One outstanding acquisition need. Immutable value identified by (Kind,
// Descriptor) — the registry treats two needs with the same kind + descriptor
// as the same request and dedupes them on NeedsRegistry.Post.
//   Kind       — what category of thing is needed.
//   Descriptor — a free-form specifier the fulfiller reads to know what to
//     get — e.g. the required illu gap ("illu>=42") for a LightSource. The
//     requester and fulfiller agree on the format; the registry treats it as
//     an opaque identity key.
//   Requester  — identifier of the engine that posted the need (e.g.
//     "AutoLightManager"). Diagnostic only.
//   PostedAt   — wall-clock time the need was first posted.
//   Quantity   — how many copies the local character should acquire to
//     satisfy this need — 1 solo, the party shortfall when the leader is
//     provisioning everyone across a gate. Fulfillers read it to acquire the
//     full count (e.g. buy N), and the requester resolves the need on
//     count-met rather than the first copy. Not part of registry identity
//     (dedup keys on kind + descriptor), so it is fixed at first post.
public readonly record struct Need(
    NeedKind Kind,
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

    // Post a need for quantity copies (clamped to at least one). Deduped on
    // (kind, descriptor): re-posting an already-outstanding identical need is
    // a no-op (no duplicate slot, no event) and returns the existing need — so
    // the quantity is fixed by the first post and a re-announce with a
    // different count doesn't disturb an acquisition already in flight.
    // Returns the (new or existing) need so the requester can hold the handle.
    public Need Post(NeedKind kind, string descriptor, string requester, int quantity = 1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(descriptor);
        ArgumentException.ThrowIfNullOrWhiteSpace(requester);

        Need need;
        bool isNew;
        lock (_gate)
        {
            int existing = IndexOf(kind, descriptor);
            if (existing >= 0)
            {
                isNew = false;
                need = _needs[existing];
            }
            else
            {
                isNew = true;
                need = new Need(kind, descriptor, requester, DateTimeOffset.Now, Math.Max(1, quantity));
                _needs.Add(need);
            }
        }

        if (isNew)
        {
            _log?.Info("Needs", $"posted — kind={kind} descriptor={descriptor} qty={need.Quantity} by={requester}");
            NeedPosted?.Invoke(need);
        }
        return need;
    }

    // Re-announce the still-outstanding needs of kind to the subscribers, as if
    // they had just been posted.
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
    public void Reoffer(NeedKind kind)
    {
        Need[] outstanding;
        lock (_gate)
            outstanding = _needs.Where(n => n.Kind == kind).ToArray();
        if (outstanding.Length == 0) return;

        _log?.Info("Needs", $"re-offering {outstanding.Length} outstanding {kind} need(s)");
        foreach (Need n in outstanding)
            NeedPosted?.Invoke(n);
    }

    // Remove a need from the registry — the fulfiller got the thing. Identity
    // is (Need.Kind, Need.Descriptor). Returns true if a matching need was
    // removed.
    public bool Resolve(Need need)
    {
        bool removed;
        lock (_gate)
        {
            int at = IndexOf(need.Kind, need.Descriptor);
            removed = at >= 0;
            if (removed) _needs.RemoveAt(at);
        }
        if (removed) _log?.Info("Needs", $"resolved — kind={need.Kind} descriptor={need.Descriptor}");
        return removed;
    }

    // Snapshot of every outstanding need of kind, oldest first. Lets a requester
    // check whether its need is still pending without re-posting.
    public IReadOnlyList<Need> Outstanding(NeedKind kind)
    {
        lock (_gate)
        {
            return _needs.Where(n => n.Kind == kind).ToArray();
        }
    }

    // Drop every outstanding need. Called on character swap so one character's
    // pending acquisitions don't leak into the next.
    public void Clear()
    {
        lock (_gate) { _needs.Clear(); }
    }

    // Drop every outstanding need of one kind and say how many
    // went. For the needs that name a game-data record (a route item's number)
    // when the game data becomes another set, under which the number is another
    // item; a need that names none (a light source) is still a need.
    public int Clear(NeedKind kind)
    {
        int dropped;
        lock (_gate) { dropped = _needs.RemoveAll(n => n.Kind == kind); }
        return dropped;
    }

    private int IndexOf(NeedKind kind, string descriptor)
        => _needs.FindIndex(
            n => n.Kind == kind &&
                 string.Equals(n.Descriptor, descriptor, StringComparison.Ordinal));
}
