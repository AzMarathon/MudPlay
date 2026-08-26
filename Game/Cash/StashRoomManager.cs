using System.Text;
using MudPlay.Game.Inventory;
using MudPlay.Game.Map;
using MudPlay.Models.Profile;
using MudPlay.Services;

namespace MudPlay.Game.Cash;

// Stash dispatch for user-marked stash rooms. Offloads every coin denomination
// at or below CashSettings.StashCoinCutoff (or all of them when it's Everything)
// into one `hide N <coin>` command per denomination (lowest denomination first,
// so the coins left on hand are the fewest possible), then one `hide <item>` per
// carried item flagged ItemOverlay.AutoStash. The keep-on-hand floor is a banking
// rule (see CashSettings) — a stash keeps only the higher, filtered-out coins.
//
// Two triggers. A stash fires either as a step of an auto-deposit reroute (when
// the wealth / coin gate trips while a Loop or Auto-Lair is running and the
// configured destination is a stash room, AutoDepositManager walks the character
// there and calls ExecuteStash on arrival) OR when the character naturally passes
// through a stash room that sits on the active loop / lair route — a dedicated
// detour is only spent on a stash room that is off-route. A purely manual walk
// through a stash room while no engine is running never triggers a hide.
//
// Room set lives on CharacterProfile.StashRooms — the same list MovementFilter
// uses, populated by the right-click "Toggle: Stash room" on the Navigation map.
// The coin-type filter (StashCoinCutoff) lives on CashSettings so the rule applies
// uniformly across every stash room (no per-room rules).
//
// Stash rooms hold cash and items (banks are cash-only): every carried, unworn
// item whose game-data AutoStash flag is set is hidden by its canonical name. The
// per-item opt-in comes from the injected resolver, which reads the 4-tier
// ItemOverlay override.
//
// Master gate: AutoActionDefaults.AutoGetCash — same toggle as CashManager. Item
// stashing rides the same gate: a stash is one operation ("dump my excess cash
// and my auto-stash items"), not two independently toggled behaviours.
public sealed class StashRoomManager : IDisposable
{
    // LogService category — appears as [StashRoom] rows per entry + dispatch.
    public const string LogCategory = "StashRoom";

    // What a single stash dispatch put away: the room it happened in, the
    // currency amounts hidden, and the canonical names of the items hidden. Either
    // list may be empty (cash-only or items-only stash), but the event only fires
    // when at least one is non-empty.
    public sealed record StashDispatch(
        RoomKey Room,
        IReadOnlyList<(string Currency, long Amount)> Currencies,
        IReadOnlyList<string> Items);

    private readonly ProfileService _profile;
    private readonly Func<CashSettings> _readCash;
    private readonly Func<InventorySnapshot> _getSnapshot;
    private readonly Func<string, string?> _resolveAutoStashItem;
    private readonly Func<bool> _isEnabled;
    private readonly Func<bool> _isParadigm;
    private readonly LogService? _log;
    private readonly CurrencyNaming _naming;

    private Action<byte[]>? _wireSender;
    private bool _disposed;

    // Fires after a successful stash dispatch. Carries the room key, the
    // (currency, amount) pairs, and the item names that were sent.
    public event Action<StashDispatch>? StashExecuted;

    public StashRoomManager(
        ProfileService profile,
        Func<CashSettings> readCash,
        Func<InventorySnapshot> getSnapshot,
        Func<string, string?> resolveAutoStashItem,
        Func<bool> isEnabled,
        LogService? log = null,
        CurrencyNaming? naming = null,
        Func<bool>? isParadigm = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(readCash);
        ArgumentNullException.ThrowIfNull(getSnapshot);
        ArgumentNullException.ThrowIfNull(resolveAutoStashItem);
        ArgumentNullException.ThrowIfNull(isEnabled);
        _profile = profile;
        _readCash = readCash;
        _getSnapshot = getSnapshot;
        _resolveAutoStashItem = resolveAutoStashItem;
        _isEnabled = isEnabled;
        // Unbound (tests) → Stock behaviour: one `hide` per item, never batched.
        _isParadigm = isParadigm ?? (static () => false);
        _log = log;
        // Resolves the per-BBS runic word; unbound (tests) falls back to stock
        // "runic" so the stable-realm behaviour is unchanged.
        _naming = naming ?? new CurrencyNaming();
    }

    // Bind the wire sender — typically the gate-wrapped engine pipeline from
    // MainWindowViewModel.
    public void SetWireSender(Action<byte[]> sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        _wireSender = sender;
    }

    // Called by AutoDepositManager on arrival at a stash destination during an
    // auto-deposit reroute. Decomposes the coin held above the raw keep-on-hand
    // floor into one `hide N <coin>` per denomination (lowest denomination first,
    // leaving the fewest coins on hand). Guarded by the cash master toggle and a
    // defensive stash-room membership check (the caller only routes here for
    // stash destinations, but the guard keeps the contract local).
    public void ExecuteStash(RoomKey enteredRoom)
    {
        if (!_isEnabled()) return;
        if (_profile.Current is not { } profile) return;
        if (profile.StashRooms is not { Count: > 0 } stashes) return;

        bool isStash = false;
        foreach (RoomRef r in stashes)
        {
            if (r.Map == enteredRoom.Map && r.Room == enteredRoom.Room)
            {
                isStash = true;
                break;
            }
        }
        if (!isStash) return;

        CashSettings cash = _readCash();
        // Authoritative per-denomination holdings + carried items (the
        // `i`-seeded, delta-tracked snapshot) — NOT CashManager's
        // since-engine-start pickup tally, which never sees the starting
        // balance and would undercount the hide amounts.
        InventorySnapshot snapshot = _getSnapshot();
        CurrencyHoldings held = snapshot.Currency;
        _log?.Debug(LogCategory,
            $"entered stash room map={enteredRoom.Map} room={enteredRoom.Room}");

        // Stashing offloads all coin at or below the "stash coin up to" cutoff and
        // keeps everything above it — the keep-on-hand floor is a banking rule, so
        // the stash plan passes a zero floor. Everything ⇒ no cap.
        long maxUnit = CurrencyHoldings.MaxUnitFor(cash.StashCoinCutoff);

        List<(string Currency, long Amount)> dispatched = new();
        foreach ((string denom, long count) in held.PlanOffloadAboveKeep(0, maxUnit))
        {
            // Hide names the coins by their full two-word noun, same as the
            // get / drop paths — a bare denomination adjective binds
            // ambiguously (MajorMUD hides a "silver" ring instead of silver
            // nobles), so WireNoun forces a currency match (and maps runic to
            // the per-BBS word). The dispatched record keeps the canonical
            // denom key; the StashExecuted consumer canonicalizes for value math.
            Send($"hide {count} {_naming.WireNoun(denom)}");
            dispatched.Add((denom, count));
        }

        // Stash rooms hold items too (banks are cash-only). Hide every carried,
        // unworn item flagged AutoStash by its canonical name. Group repeated
        // copies so Paradigm can stash the pile in one `hide N <item>` (Stock
        // still sends one `hide <item>` per copy); the server echoes the count
        // back and InventoryManager decrements weight by N.
        Dictionary<string, int> toHide = new(StringComparer.OrdinalIgnoreCase);
        List<string> hideOrder = new();
        foreach (string entry in snapshot.CarriedItems)
        {
            if (_resolveAutoStashItem(entry) is not { } name) continue;
            if (!toHide.ContainsKey(name)) hideOrder.Add(name);
            toHide[name] = toHide.GetValueOrDefault(name) + 1;
        }
        List<string> hiddenItems = new();
        foreach (string name in hideOrder)
        {
            int count = toHide[name];
            CountedCommand.Emit(Send, "hide", count, name, _isParadigm());
            for (int i = 0; i < count; i++) hiddenItems.Add(name);
        }

        if (dispatched.Count > 0 || hiddenItems.Count > 0)
        {
            _log?.Info(LogCategory,
                $"stash dispatched room=({enteredRoom.Map},{enteredRoom.Room}) "
                + $"currencies={dispatched.Count} items={hiddenItems.Count}");
            StashExecuted?.Invoke(new StashDispatch(enteredRoom, dispatched, hiddenItems));
        }
    }

    private void Send(string text)
    {
        if (_wireSender is null) return;
        _wireSender(Encoding.Latin1.GetBytes(text + "\r"));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
    }
}
