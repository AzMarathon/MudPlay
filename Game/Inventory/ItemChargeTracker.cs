using System;
using System.Collections.Generic;
using MudPlay.Game.Map;          // BossTimerMath
using MudPlay.Game.Tokens;       // TokenCatalog.ParseUsesRemaining
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.Terminal;

namespace MudPlay.Game.Inventory;

// Paradigm charge tracking for limited-use items. Paradigm prints "Uses remaining: N"
// when you look at an item, so the count here is AUTHORITATIVE — read from the game,
// not derived (that's the stock model, ItemUseCountTracker). This tracker:
//   • records the "Uses remaining: N" from any `look <item>` reply against that item,
//     keyed by item number, PERSISTED on the character profile (CharacterProfile.
//     ItemCharges) so it survives between sessions;
//   • auto-looks a held charged item (carried or worn) whose count we don't know yet,
//     so the readout fills itself in without the user thinking to look;
//   • re-looks an item after a use OR a stack removal (drop / sell / give / put), so a
//     manual or remote use reconciles to the game's true count and a dropped top-of-stack
//     copy hands off to the next copy's charges — a blocked / failed use never mis-
//     decrements, because the look reply is the source of truth (tokens work this way too);
//   • treats a RECHARGEABLE item (Retain After Uses) as restocked to its game-data max
//     once the BBS cleanup boundary passes, and a FINITE item's count as permanent.
// Gated to Paradigm (Func onParadigm): stock realms print no such line and use the
// counted-uses tracker instead. Tokens flow through here too — TokenTracker's login
// looks ride the same outbound tap — so token charges persist without special-casing;
// @token still reads TokenTracker for the routing/party features.
public sealed class ItemChargeTracker : IDisposable
{
    // A `look <item>` that prints no "Uses remaining" (a non-charged item) must let the
    // pending capture lapse so a later, unrelated charge line isn't misattributed.
    private const int LookReplyWindowMs = 2500;
    // Space auto-looks so each item's reply lands before the next look arms the capture.
    private const int AutoLookPaceMs = 1200;
    // Let a use + its result resolve before the reconciling re-look; coalesces a rapid
    // burst of uses (a combat wand fired several rounds running) into one look.
    private const int RelookDelayMs = 1500;
    // Directions are 1-2 chars; require a longer arg so "look ne" can't match an item.
    private const int MinLookArgLength = 3;

    private static readonly string[] LookVerbs = { "look ", "examine ", "exam ", "exa ", "l " };

    private readonly GameDataCache _gameData;
    private readonly ProfileService _profile;
    private readonly Func<IReadOnlyList<string>> _held;   // carried pack + worn/wielded gear + key-ring
    private readonly Func<string, int> _itemNumberOf;
    private readonly Func<bool> _onParadigm;
    private readonly Func<BossCleanupConfig?> _cleanupConfig;
    private readonly Action<string> _sendLook;
    private readonly Action<int, Action> _schedule;
    private readonly Func<DateTimeOffset> _now;
    private readonly LogService? _log;
    private LineExtractor? _lines;

    private string? _pendingItem;        // held-item name whose look reply we await
    private int _pendingNumber;
    private int _pendingGen;

    private readonly Queue<string> _autoLookQueue = new();  // held-item names to auto-look
    private readonly HashSet<int> _autoAttempted = new();   // numbers auto-looked this session — never retried
    private bool _autoDispatching;

    private readonly Dictionary<int, int> _relookGen = new();   // per-item re-look debounce
    private bool _disposed;

    // Fired after a charge count changes, so the Character Info panel refreshes.
    public event Action? Changed;

    public ItemChargeTracker(
        GameDataCache gameData,
        ProfileService profile,
        Func<IReadOnlyList<string>> heldItems,
        Func<string, int> itemNumberOf,
        Func<bool> onParadigm,
        Func<BossCleanupConfig?> cleanupConfig,
        Action<string> sendLook,
        Action<int, Action> schedule,
        Func<DateTimeOffset>? now = null,
        LogService? log = null)
    {
        _gameData = gameData ?? throw new ArgumentNullException(nameof(gameData));
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _held = heldItems ?? throw new ArgumentNullException(nameof(heldItems));
        _itemNumberOf = itemNumberOf ?? throw new ArgumentNullException(nameof(itemNumberOf));
        _onParadigm = onParadigm ?? throw new ArgumentNullException(nameof(onParadigm));
        _cleanupConfig = cleanupConfig ?? throw new ArgumentNullException(nameof(cleanupConfig));
        _sendLook = sendLook ?? throw new ArgumentNullException(nameof(sendLook));
        _schedule = schedule ?? throw new ArgumentNullException(nameof(schedule));
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _log = log;
    }

    public void AttachLineExtractor(LineExtractor lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (_lines is not null) _lines.LineEmitted -= OnLine;
        _lines = lines;
        _lines.LineEmitted += OnLine;
    }

    // Remaining charges for a limited-use item by number, or null when unknown (never
    // looked) or the item isn't limited-use. Recharge-adjusted: a rechargeable item
    // whose stored read predates the last cleanup reads as its game-data max (assumed
    // restocked); a finite item's stored count stands.
    public int? RemainingFor(int number)
    {
        if (number <= 0) return null;
        if (_profile.Current?.ItemCharges is not { } map || !map.TryGetValue(number, out ItemChargeRecord? rec))
            return null;
        if (ItemChargeMeta.Read(_gameData, number) is { Recharges: true, IsLimitedUse: true } meta
            && _cleanupConfig() is { } cfg
            && _now() >= BossTimerMath.NextCleanup(rec.UpdatedUtc, cfg.TimeOfDay, cfg.Tz))
            return meta.MaxUses;
        return rec.Remaining;
    }

    public int? RemainingForName(string name)
        => string.IsNullOrWhiteSpace(name) ? null : RemainingFor(_itemNumberOf(Singular(name)));

    // A stacked carry entry reads as "2 gnarled wand" — Paradigm keeps the name singular
    // under a leading count. Strip the count so the name resolves to its item number and
    // a re-look sends the bare name. (A stack's charges are the TOP copy's; `look` only
    // ever reports the top, so tracking one number per item — reconciled on each use /
    // drop — follows the top as copies deplete and the next surfaces.)
    private static string Singular(string name) => CountedCommand.SplitLeadingCount(name).Name;

    // Look up (and dispatch) the charges of any held charged item we don't yet know.
    // Called on inventory-settle. No-op off Paradigm. Each unknown item gets one paced
    // `look`; a persisted count (or a rechargeable that cleanup-resolves to max) needs
    // no look, so this stays quiet after the first encounter with each item.
    public void EnsureChargesKnown()
    {
        if (_disposed || !_onParadigm()) return;
        foreach (string entry in _held())
        {
            if (string.IsNullOrWhiteSpace(entry)) continue;
            string name = Singular(entry);                                  // strip a stack's leading count
            int number = _itemNumberOf(name);
            if (number <= 0 || _autoAttempted.Contains(number)) continue;   // one look per item per session
            if (RemainingFor(number) is not null) continue;                 // already known
            if (ItemChargeMeta.Read(_gameData, number) is not { IsLimitedUse: true }) continue;
            _autoLookQueue.Enqueue(name);
            _autoAttempted.Add(number);   // mark now so a look that prints no charge line isn't retried forever
        }
        if (!_autoDispatching) DispatchNextAutoLook();
    }

    private void DispatchNextAutoLook()
    {
        if (_disposed || _autoLookQueue.Count == 0) { _autoDispatching = false; return; }
        _autoDispatching = true;
        string name = _autoLookQueue.Dequeue();
        _log?.Info("Items", $"reading charges for '{name}' (unknown — looking)");
        _sendLook($"look {name}");                                          // arms capture via ObserveOutbound
        _schedule(AutoLookPaceMs, DispatchNextAutoLook);
    }

    // Verbs that change which copy of a stacked item is "on top": using it spends the
    // top copy's charge, and removing a copy (drop / sell / give / put) surfaces the next
    // one — which carries its OWN charge count. Each triggers a reconciling re-look so
    // the stored count follows the current top, matching how `look` only ever reports the
    // top-of-stack (last-obtained) copy on Paradigm.
    private static readonly string[] RelookVerbs = { "use ", "drop ", "sell ", "give ", "put " };

    // Outbound `look <item>` (arm capture) or a stack-changing verb (schedule a
    // reconciling re-look). Tokens' login looks ride this path too, so their charges
    // persist here.
    public void ObserveOutbound(byte[] data)
    {
        if (_disposed || !_onParadigm() || data is null || data.Length == 0) return;
        string text = System.Text.Encoding.Latin1.GetString(data);
        foreach (string raw in text.Split('\r', '\n'))
        {
            string line = raw.Trim();
            if (LookArg(line) is { } lookArg && ResolveHeld(lookArg) is { } lookName)
            {
                _pendingItem = lookName;
                _pendingNumber = _itemNumberOf(lookName);
                int gen = ++_pendingGen;
                _schedule(LookReplyWindowMs, () => { if (gen == _pendingGen) _pendingItem = null; });
                continue;
            }
            foreach (string verb in RelookVerbs)
                if (line.StartsWith(verb, StringComparison.OrdinalIgnoreCase)
                    && ResolveHeld(line[verb.Length..].Trim()) is { } name)
                {
                    ScheduleRelook(name);
                    break;
                }
        }
    }

    // A use or a stack removal is reconciled by re-looking (the reply is the truth), so a
    // blocked use never mis-decrements and a dropped top copy hands off to the next one's
    // count. Debounced per item.
    private void ScheduleRelook(string name)
    {
        int number = _itemNumberOf(name);
        if (number <= 0) return;
        if (RemainingFor(number) is null
            && ItemChargeMeta.Read(_gameData, number) is not { IsLimitedUse: true }) return;   // not a charged item
        int gen = _relookGen.TryGetValue(number, out int g) ? g + 1 : 1;
        _relookGen[number] = gen;
        _schedule(RelookDelayMs, () =>
        {
            if (_disposed || !_onParadigm()) return;
            if (_relookGen.TryGetValue(number, out int cur) && cur == gen)
                _sendLook($"look {name}");
        });
    }

    private void OnLine(LineExtractor.EmittedLine emitted) => HandleLine(emitted.Text);

    // Split out so tests can drive a line without standing up a LineExtractor.
    internal void HandleLine(string text)
    {
        if (_disposed || _pendingItem is null) return;
        int uses = TokenCatalog.ParseUsesRemaining(text);
        if (uses < 0) return;

        int number = _pendingNumber;
        string name = _pendingItem;
        _pendingItem = null;
        _pendingGen++;
        if (number <= 0) return;                    // no clickable number — nothing to persist against
        Record(number, name, uses);
    }

    private void Record(int number, string name, int uses)
    {
        if (_profile.Current is not { } prof) return;
        if (prof.ItemCharges is { } existing && existing.TryGetValue(number, out ItemChargeRecord? prev)
            && prev.Remaining == uses)
        {
            // No numeric change, but refresh the timestamp so the cleanup-recharge
            // window is measured from the latest read.
            existing[number] = prev with { UpdatedUtc = _now() };
            _profile.Save();
            return;
        }
        prof.ItemCharges ??= new Dictionary<int, ItemChargeRecord>();
        prof.ItemCharges[number] = new ItemChargeRecord(uses, _now());
        _profile.Save();
        _log?.Info("Items", $"charges: {name} → {uses} use(s) remaining");
        Changed?.Invoke();
    }

    // Drop in-session capture / auto-look plumbing on a profile swap. The persisted
    // counts live on the profile itself, so the new character reads its own from disk.
    public void ResetSession()
    {
        _pendingItem = null;
        _pendingGen++;
        _autoLookQueue.Clear();
        _autoAttempted.Clear();
        _autoDispatching = false;
        _relookGen.Clear();
    }

    // The item argument of a look/examine verb, or null when the line isn't one (or the
    // arg is too short to be an item — a bare room look or a direction).
    private static string? LookArg(string line)
    {
        foreach (string verb in LookVerbs)
            if (line.StartsWith(verb, StringComparison.OrdinalIgnoreCase))
            {
                string arg = line[verb.Length..].Trim();
                return arg.Length >= MinLookArgLength ? arg : null;
            }
        return null;
    }

    // The held item (carried or worn) a look/use arg refers to — case-insensitive
    // substring, the loose resolution the game does for a partial. First match wins.
    private string? ResolveHeld(string arg)
    {
        if (arg.Length < 2) return null;
        string a = arg.ToLowerInvariant();
        foreach (string held in _held())
            if (!string.IsNullOrWhiteSpace(held) && held.ToLowerInvariant().Contains(a))
                return Singular(held);
        // `use <item> <target>` — fall back to the first word matching a held item.
        string first = a.Split(' ')[0];
        if (first.Length >= MinLookArgLength && first != a)
            foreach (string held in _held())
                if (!string.IsNullOrWhiteSpace(held) && held.ToLowerInvariant().Contains(first))
                    return Singular(held);
        return null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_lines is not null) _lines.LineEmitted -= OnLine;
    }
}
