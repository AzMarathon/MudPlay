using System.Collections.Generic;
using System.Text;
using MudPlay.Game.Remote;
using MudPlay.Services;

namespace MudPlay.Game.Map;

// Party-first stage of the path-item demand pipeline. Sits between the walker's
// walk-start item announcer and PathItemDemandTracker: when a planned route
// crosses an (Item: N) / (Ticket: N) gate whose per-member item the party might
// lack, this consults the party (via PartyInventoryProbe) before spending a
// search / shop detour on it. Backs the item record's "Auto-obtain for path"
// flag (ItemOverlay.AutoObtainForPath) — a per-item gate, so the announced list
// is partitioned: flagged items go through the party pool, the rest pass straight
// through to the per-item demand pipeline unchanged.
//
// The behaviour splits on who's driving:
//
// The per-person quota q comes from the item's carry policy (MaxToGet target,
// MinToKeep floor) — 1 for a rope/grapple, 2–3 for a buff item like waterskin,
// and 1 for any item with no policy set (the historical one-per-member default).
// Times the head-count (self + everyone who replied) it gives the total the pool
// must hold.
//
// Leader — provision the whole party. The leader is the one walking a party
// route (followers are held by the movement gate), so it owns getting everyone
// across the gate. For each gated item it probes the party for a count, then
// treats the party (self + everyone who replied) as one pool that needs q copies
// each. Members keep whatever they're holding — nothing is redistributed early.
// If the pool already holds enough (totalHeld >= q * partySize), the leader
// immediately coordinates the hand-off so every member reaches q: it gives its
// own surplus directly (give <item> to <R>) and directs other holders' surplus
// with a targeted /<holder> @do give <item> to <R>. If the pool is short, the
// shortfall count — how many copies the leader's own bag must gain to reach
// q-per-member — is forwarded to the demand pipeline (search / shop, per the
// user's checked options), so a shop detour buys that many rather than a single
// copy; when "search rooms if item needed" is on, the slot is retained so
// SearchDemandActive keeps auto-search armed past the first copy and the
// redistribution fires (via OnInventoryChanged) the moment the pool becomes
// whole.
//
// Non-leader — borrow spares for self. A grouped follower doing its own walk-to:
// if it holds fewer than q and members carry copies above their own quota, it
// asks for enough hand-offs to reach q, forwarding any remaining shortfall to
// the search / shop pipeline.
//
// Both paths degrade to forwarding an item unchanged when it isn't flagged for
// party provisioning or we're solo. The probe round-trip is async (a telepath
// each way), so the decision is deferred off the walker's WalkTo call stack
// through _post.
//
// Substitutes. A hazard counter is an any-of group (any boat crosses the river),
// but the route announces one representative item. Every count here is COVERAGE
// — copies of the item or any of its route substitutes (PathItemSubstitutes) —
// so a member carrying a canoe counts as covered for a raft need, the probe asks
// the party about every substitute, and a hand-off names the item the holder
// actually carries. An item with no substitutes (an Item/Ticket gate) behaves
// exactly as before.
//
// Scope: multiple copies are acquired by the forwarded shortfall count — a shop
// detour buys that many, and auto-search stays armed (until the pool is whole)
// to reveal the rest off the floor. Monster-drop reroute remains single-copy
// best-effort. Non-responders are treated as outside the pool — the leader
// provisions only itself and the members that answered, leaving a silent member
// to its own per-member pipeline.
public sealed class PartyPathItemGate
{
    private const string LogCategory = "AutoSearch";

    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<int, int>> NoHoldings =
        new Dictionary<string, IReadOnlyDictionary<int, int>>();

    // A route item the leader is provisioning: its name and what each member
    // that replied holds of it and its substitutes (given name → item id → copies).
    private sealed record Pending(
        int Id, string Name, IReadOnlyDictionary<string, IReadOnlyDictionary<int, int>> Others);

    // One copy a member can hand over, and which item it is.
    private readonly record struct Spare(string? Giver, int ItemId);

    private readonly Func<int, bool> _isCarried;
    private readonly Func<int, int> _selfCount;
    private readonly Func<int, string, Task<PartyInventoryProbe.PartyItemResult>> _query;
    private readonly Func<int, string?> _itemName;
    private readonly Func<int, bool> _isEnabled;
    private readonly Func<int, int> _perPerson;
    private readonly Func<bool> _searchEnabled;
    private readonly Func<bool> _inParty;
    private readonly Func<bool> _selfIsLeader;
    private readonly Func<string?> _selfGivenName;
    private readonly Action<IReadOnlyList<int>, int> _forward;
    private readonly Action<Action> _post;
    private readonly Func<int, IReadOnlyList<int>> _substitutes;
    private readonly LogService? _log;
    private readonly object _gate = new();
    private readonly Dictionary<int, Pending> _pending = new();
    private Action<byte[]>? _wireSender;

    public PartyPathItemGate(
        Func<int, bool> isCarried,
        Func<int, int> selfCount,
        Func<int, string, Task<PartyInventoryProbe.PartyItemResult>> query,
        Func<int, string?> itemName,
        Func<int, bool> isEnabled,
        Func<int, int> perPersonQuantity,
        Func<bool> searchEnabled,
        Func<bool> inParty,
        Func<bool> selfIsLeader,
        Func<string?> selfGivenName,
        Action<IReadOnlyList<int>, int> forward,
        Action<Action> post,
        LogService? log = null,
        Func<int, IReadOnlyList<int>>? substitutes = null)
    {
        ArgumentNullException.ThrowIfNull(isCarried);
        ArgumentNullException.ThrowIfNull(selfCount);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(itemName);
        ArgumentNullException.ThrowIfNull(isEnabled);
        ArgumentNullException.ThrowIfNull(perPersonQuantity);
        ArgumentNullException.ThrowIfNull(searchEnabled);
        ArgumentNullException.ThrowIfNull(inParty);
        ArgumentNullException.ThrowIfNull(selfIsLeader);
        ArgumentNullException.ThrowIfNull(selfGivenName);
        ArgumentNullException.ThrowIfNull(forward);
        ArgumentNullException.ThrowIfNull(post);
        _isCarried = isCarried;
        _selfCount = selfCount;
        _query = query;
        _itemName = itemName;
        _isEnabled = isEnabled;
        _perPerson = perPersonQuantity;
        _searchEnabled = searchEnabled;
        _inParty = inParty;
        _selfIsLeader = selfIsLeader;
        _selfGivenName = selfGivenName;
        _forward = forward;
        _post = post;
        _substitutes = substitutes ?? (static id => new[] { id });
        _log = log;
    }

    // Per-person copies to provision for a path item, resolved from the item's
    // carry policy (MaxToGet target, MinToKeep floor). Rope/grapple resolve to 1;
    // a buff item like waterskin to its 2–3 MaxToGet. Never below 1, so an item
    // with no carry policy set keeps the historical one-per-member behaviour.
    private int PerPersonFor(int id) => Math.Max(1, _perPerson(id));

    // The item and everything that stands in for it on this route, item first.
    private IReadOnlyList<int> SubstitutesFor(int id)
    {
        IReadOnlyList<int> subs = _substitutes(id);
        return subs.Count > 0 ? subs : new[] { id };
    }

    // What self carries of the item and its substitutes, by item id.
    private Dictionary<int, int> SelfHoldings(int id)
    {
        var held = new Dictionary<int, int>();
        foreach (int s in SubstitutesFor(id))
        {
            int n = _selfCount(s);
            if (n > 0) held[s] = n;
        }
        return held;
    }

    private static int Total(IReadOnlyDictionary<int, int> holdings)
    {
        int total = 0;
        foreach (int c in holdings.Values) total += c;
        return total;
    }

    // Bind the wire-sender for the give hand-off. MainWindowVM supplies the
    // engine-gated SendUserInput.
    public void SetWireSender(Action<byte[]> sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        _wireSender = sender;
    }

    // True while the leader is still acquiring copies to make the party whole
    // AND search is a chosen acquisition option — keeps AutoSearchManager armed
    // past the first copy (the shared demand tracker resolves its need at copy
    // one). OR'd into auto-search's demand gate alongside
    // PathItemDemandTracker.SearchDemandActive.
    public bool SearchDemandActive
    {
        get
        {
            if (!_searchEnabled()) return false;
            lock (_gate) return _pending.Count > 0;
        }
    }

    // Walk-start callback (re-points AutoWalkManager.SetPathItemAnnouncer ahead
    // of the demand tracker): every item id gating an Item / Ticket exit along
    // the planned route. Solo forwards each item at its per-person quota (1 for
    // an unflagged item). In a party the list is partitioned by the per-item
    // auto-obtain flag: flagged items go through the pool (the leader provisions
    // the whole party to quota, a follower borrows spares for any shortfall of
    // its own); unflagged items pass straight through to the per-item demand
    // pipeline as a self-only copy.
    public void OnPathItemsRequired(IReadOnlyList<int> itemIds)
    {
        ArgumentNullException.ThrowIfNull(itemIds);
        if (!_inParty())
        {
            // Solo: one pool of one, so the per-person quantity is the whole
            // demand. A flagged item wants its resolved count; anything unflagged
            // stays a single self-copy so a stray carry cap doesn't inflate a
            // non-provisioning item's search demand.
            var seen = new HashSet<int>();
            foreach (int id in itemIds)
            {
                if (id <= 0 || !seen.Add(id)) continue;
                _forward(new[] { id }, _isEnabled(id) ? PerPersonFor(id) : 1);
            }
            return;
        }

        bool leader = _selfIsLeader();
        var considered = new HashSet<int>();
        List<int>? passthrough = null;   // items not flagged for party provisioning
        foreach (int id in itemIds)
        {
            if (id <= 0 || !considered.Add(id)) continue;
            if (!_isEnabled(id))
            {
                (passthrough ??= new()).Add(id);   // self-only demand, per-item routers handle it
                continue;
            }
            if (leader)
            {
                // The leader provisions even for an item it already carries —
                // a follower may still lack it, and only the leader coordinates
                // the hand-off. Skip only when one is already in flight.
                bool inFlight;
                lock (_gate) inFlight = _pending.ContainsKey(id);
                if (inFlight) continue;
                _post(() => _ = ProvisionAsync(id));
            }
            else
            {
                if (Total(SelfHoldings(id)) >= PerPersonFor(id)) continue;   // already hold our quota
                _post(() => _ = TryBorrowSpareAsync(id));
            }
        }
        if (passthrough is not null) _forward(passthrough, 1);
    }

    // Inventory-change callback (wired to InventoryManager.Changed): re-checks
    // each in-flight provisioning and coordinates the hand-off the moment the
    // pool becomes whole. A no-op when the leader isn't provisioning anything.
    public void OnInventoryChanged()
    {
        int[] ids;
        lock (_gate)
        {
            if (_pending.Count == 0) return;
            ids = new int[_pending.Count];
            _pending.Keys.CopyTo(ids, 0);
        }
        foreach (int id in ids) TryComplete(id);
    }

    // Ask the party about the item and every substitute at once — the probe keeps
    // concurrent queries apart by item name — and fold the replies into what each
    // member holds. A member who answered any of the queries is in the pool; one
    // who answered none is a non-responder, as before.
    private async Task<Dictionary<string, IReadOnlyDictionary<int, int>>> QueryHoldingsAsync(int id)
    {
        var asks = new List<Task<PartyInventoryProbe.PartyItemResult>>();
        var askedNames = new List<string>();
        foreach (int s in SubstitutesFor(id))
            if (_itemName(s) is { } name && !string.IsNullOrWhiteSpace(name))
            {
                asks.Add(_query(s, name));
                askedNames.Add(name);
            }
        if (askedNames.Count > 1)
            _log?.Info(LogCategory, $"party probe for path item {id}: asking about any of {string.Join(", ", askedNames)}");

        PartyInventoryProbe.PartyItemResult[] results = await Task.WhenAll(asks).ConfigureAwait(true);
        var byMember = new Dictionary<string, Dictionary<int, int>>(StringComparer.OrdinalIgnoreCase);
        foreach (PartyInventoryProbe.PartyItemResult r in results)
            foreach (KeyValuePair<string, int> kv in r.CountsByMember)
            {
                if (!byMember.TryGetValue(kv.Key, out Dictionary<int, int>? held))
                    byMember[kv.Key] = held = new Dictionary<int, int>();
                if (kv.Value > 0) held[r.ItemId] = kv.Value;
            }

        var holdings = new Dictionary<string, IReadOnlyDictionary<int, int>>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, Dictionary<int, int>> kv in byMember) holdings[kv.Key] = kv.Value;
        return holdings;
    }

    private async Task ProvisionAsync(int id)
    {
        string? name = _itemName(id);
        if (string.IsNullOrWhiteSpace(name)) { _forward(new[] { id }, 1); return; }

        // Reserve the slot up-front so a re-announce mid-probe doesn't kick off
        // a second probe / double-give for the same item.
        lock (_gate)
        {
            if (!_pending.TryAdd(id, new Pending(id, name, NoHoldings))) return;
        }

        Dictionary<string, IReadOnlyDictionary<int, int>> others = await QueryHoldingsAsync(id).ConfigureAwait(true);
        lock (_gate)
        {
            if (!_pending.ContainsKey(id)) return;   // cleared while probing
            _pending[id] = new Pending(id, name, others);
        }

        if (TryComplete(id)) return;

        // Genuine shortfall: feed the demand pipeline (search / shop per the
        // user's checked options) with the count the leader must acquire so the
        // whole party clears the gate — the leader's own bag must hold at least
        // (q * partySize - othersTotal) so the pool reaches q-per-member, where q
        // is the per-person quota. This is the same wholeness check TryComplete
        // uses, expressed as a target. With search on, keep the slot so
        // SearchDemandActive stays armed for copies beyond the first and the
        // redistribution fires once the pool is whole; otherwise it's a one-shot
        // best-effort and the slot is dropped.
        int othersTotal = 0;
        foreach (IReadOnlyDictionary<int, int> held in others.Values) othersTotal += Total(held);
        int q = PerPersonFor(id);
        int target = Math.Max(1, q * (1 + others.Count) - othersTotal);
        if (!_searchEnabled())
            lock (_gate) _pending.Remove(id);
        _forward(new[] { id }, target);
    }

    // Redistributes if the pool (self + responders) now holds at least the
    // per-person quota for every member; returns true when the slot is settled
    // (either handed out or nothing to do), false while still short.
    private bool TryComplete(int id)
    {
        Pending? p;
        lock (_gate) p = _pending.TryGetValue(id, out Pending? found) ? found : null;
        if (p is null) return false;

        Dictionary<int, int> self = SelfHoldings(id);
        int othersTotal = 0;
        foreach (IReadOnlyDictionary<int, int> held in p.Others.Values) othersTotal += Total(held);
        int q = PerPersonFor(id);
        int partySize = 1 + p.Others.Count;      // self + everyone who replied
        int totalHeld = Total(self) + othersTotal;
        if (totalHeld < q * partySize) return false; // not enough yet — keep acquiring

        lock (_gate)
        {
            if (!_pending.Remove(id)) return true;   // another pass already handled it
        }
        Redistribute(p.Name, p.Others, self, id, q);
        return true;
    }

    // The copies a member can give away: everything above the quota q they keep
    // for their own crossing. The representative item is handed over first, so a
    // member holding a raft and a canoe keeps the canoe and gives the raft.
    private List<Spare> SparesOf(string? giver, IReadOnlyDictionary<int, int> held, int id, int q)
    {
        var copies = new List<int>();
        foreach (int s in SubstitutesFor(id))
            if (held.TryGetValue(s, out int n))
                for (int i = 0; i < n; i++) copies.Add(s);
        var spares = new List<Spare>();
        for (int i = 0; i < copies.Count - q; i++) spares.Add(new Spare(giver, copies[i]));
        return spares;
    }

    private string NameOf(int itemId, string fallback) =>
        _itemName(itemId) is { Length: > 0 } n ? n : fallback;

    private void Redistribute(
        string name, IReadOnlyDictionary<string, IReadOnlyDictionary<int, int>> others,
        IReadOnlyDictionary<int, int> self, int id, int q)
    {
        if (_wireSender is null) { _forward(new[] { id }, 1); return; }

        // null giver / sink == self. Each member's copies above the quota q are
        // giveable; its deficit below q is that many sink slots (a member two
        // short shows up as two sinks → two gives). Feasibility
        // (totalHeld >= q * partySize) was already checked, so surplus covers
        // every sink.
        var sources = new List<Spare>();   // one entry per giveable copy
        var sinks = new List<string?>();
        int selfNow = Total(self);
        for (int i = 0; i < q - selfNow; i++) sinks.Add(null);
        sources.AddRange(SparesOf(null, self, id, q));
        foreach (KeyValuePair<string, IReadOnlyDictionary<int, int>> kv in others)
        {
            int held = Total(kv.Value);
            for (int i = 0; i < q - held; i++) sinks.Add(kv.Key);
            sources.AddRange(SparesOf(kv.Key, kv.Value, id, q));
        }
        if (sinks.Count == 0) return;   // everyone already holds the quota

        string? selfName = _selfGivenName();
        bool selfInvolved = sinks.Contains(null) || sources.Any(static s => s.Giver is null);
        if (selfInvolved && string.IsNullOrEmpty(selfName)) { _forward(new[] { id }, 1); return; }

        int sent = 0;
        int si = 0;
        foreach (string? sink in sinks)
        {
            if (si >= sources.Count) break;   // guarded by feasibility, but stay safe
            Spare copy = sources[si++];
            string recipient = sink ?? selfName!;
            string item = NameOf(copy.ItemId, name);
            if (copy.Giver is null)
                SendRaw($"give {item} to {recipient}");                        // hand over our own
            else
                SendRaw($"/{copy.Giver} @do give {item} to {recipient}");     // direct the holder
            sent++;
        }
        _log?.Info(LogCategory,
            $"party provisioning {name}: issued {sent} give(s) so each member holds {q}.");
    }

    private async Task TryBorrowSpareAsync(int id)
    {
        string? name = _itemName(id);
        if (string.IsNullOrWhiteSpace(name)) { _forward(new[] { id }, 1); return; }

        int q = PerPersonFor(id);
        Dictionary<string, IReadOnlyDictionary<int, int>> holdings = await QueryHoldingsAsync(id).ConfigureAwait(true);

        // Might have arrived between the announce and the reply window (an
        // earlier give, a floor pickup) — nothing left to do once we hold quota.
        int need = q - Total(SelfHoldings(id));
        if (need <= 0) return;

        // A holder can spare whatever it carries above its own quota q (it keeps
        // q for its own crossing). Rank largest spare first so the copies come
        // from the fewest members.
        var holders = new List<(string Name, List<Spare> Spares)>();
        int membersWithAny = 0;
        foreach (KeyValuePair<string, IReadOnlyDictionary<int, int>> kv in holdings)
        {
            if (Total(kv.Value) > 0) membersWithAny++;
            List<Spare> spares = SparesOf(kv.Key, kv.Value, id, q);
            if (spares.Count > 0) holders.Add((kv.Key, spares));
        }

        // No member has a spare to give — post the need (quota copies) so
        // demand-driven search / shop take over.
        if (holders.Count == 0) { _forward(new[] { id }, q); return; }

        string self = _selfGivenName() ?? string.Empty;
        if (self.Length == 0 || _wireSender is null) { _forward(new[] { id }, q); return; }

        holders.Sort((a, b) => b.Spares.Count.CompareTo(a.Spares.Count));
        int borrowed = 0;
        if (membersWithAny == 1)
        {
            // Only one member holds any copies: @party give is permission-free
            // and only they can act on it, so there's no risk of over-giving.
            foreach (Spare copy in holders[0].Spares.Take(need))
            {
                SendRaw($"@party give {NameOf(copy.ItemId, name)} to {self}");
                borrowed++;
            }
        }
        else
        {
            // Several members hold copies: target each holder explicitly so we
            // don't collect a duplicate from every one of them.
            foreach ((string Name, List<Spare> Spares) h in holders)
            {
                foreach (Spare copy in h.Spares)
                {
                    if (borrowed >= need) break;
                    SendRaw($"/{h.Name} @do give {NameOf(copy.ItemId, name)} to {self}");
                    borrowed++;
                }
                if (borrowed >= need) break;
            }
        }

        // Spares couldn't cover the whole quota — let the demand pipeline top up.
        if (borrowed < need) _forward(new[] { id }, q);

        _log?.Info(LogCategory,
            $"party spares cover {borrowed}/{need} of {name}; requested give(s)" +
            (borrowed < need ? " — remaining left to path-item search." : "."));
    }

    private void SendRaw(string command)
    {
        if (_wireSender is null) return;
        _wireSender(Encoding.Latin1.GetBytes(command + "\r"));
    }
}
