using System;
using System.Collections.Generic;
using MudPlay.Game.Tokens;
using MudPlay.Services;
using MudPlay.Terminal;

namespace MudPlay.Game.Inventory;

// Tracks the "Uses remaining: N" charge count of limited-use items the player LOOKS
// at — general, not just tokens. This line is a Paradigm feature (stock realms don't
// print it), so this populates only there; on stock it stays empty and the UI shows
// no charge readout (stock charge-tracking by counting uses is a separate concern).
//
// When a `look <item>` for a carried item goes out, the first "Uses remaining: N" in
// the reply is recorded against that item, keyed by its carried name. Tokens populate
// automatically because TokenTracker looks each held token on login and those looks
// flow through the same outbound tap; other limited-use items populate when the player
// looks at them. Session-only, cleared on profile swap. The parsing is TokenCatalog's
// (the look-reply format is shared with tokens).
public sealed class ItemChargeTracker : IDisposable
{
    // A `look <item>` that prints no "Uses remaining" (a non-charged item) must let the
    // pending capture lapse so a later, unrelated charge line isn't misattributed.
    private const int LookReplyWindowMs = 2500;
    // Directions are 1-2 chars; require a longer arg so "look ne" can't match an item.
    private const int MinLookArgLength = 3;

    private static readonly string[] LookVerbs = { "look ", "examine ", "exam ", "exa ", "l " };

    private readonly Func<IReadOnlyList<string>> _carried;
    private readonly Action<int, Action> _schedule;
    private readonly LogService? _log;
    private LineExtractor? _lines;

    private readonly Dictionary<string, int> _charges = new();   // key = lowercased carried name
    private string? _pendingItem;                                // carried name whose look reply we await
    private int _pendingGen;
    private bool _disposed;

    // Fired after a charge count is recorded, so the Character Info panel refreshes.
    public event Action? Changed;

    public ItemChargeTracker(
        Func<IReadOnlyList<string>> carried,
        Action<int, Action> schedule,
        LogService? log = null)
    {
        _carried = carried ?? throw new ArgumentNullException(nameof(carried));
        _schedule = schedule ?? throw new ArgumentNullException(nameof(schedule));
        _log = log;
    }

    public void AttachLineExtractor(LineExtractor lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (_lines is not null) _lines.LineEmitted -= OnLine;
        _lines = lines;
        _lines.LineEmitted += OnLine;
    }

    // Charges last read for a carried item (by its inventory name), or null when we've
    // never captured a "Uses remaining" for it this session.
    public int? ChargesFor(string carriedName)
        => !string.IsNullOrWhiteSpace(carriedName)
           && _charges.TryGetValue(carriedName.Trim().ToLowerInvariant(), out int n)
            ? n : null;

    public void Clear()
    {
        _pendingItem = null;
        _pendingGen++;
        if (_charges.Count == 0) return;
        _charges.Clear();
        Changed?.Invoke();
    }

    // Outbound `look <item>` (or l / examine / exa) for a carried item arms the capture;
    // the reply's first "Uses remaining: N" records against it.
    public void ObserveOutbound(byte[] data)
    {
        if (_disposed || data is null || data.Length == 0) return;
        string text = System.Text.Encoding.Latin1.GetString(data);
        foreach (string raw in text.Split('\r', '\n'))
        {
            if (LookArg(raw.Trim()) is not { } arg) continue;
            if (ResolveCarried(arg) is not { } name) continue;
            _pendingItem = name;
            int gen = ++_pendingGen;
            _schedule(LookReplyWindowMs, () => { if (gen == _pendingGen) _pendingItem = null; });
        }
    }

    private void OnLine(LineExtractor.EmittedLine emitted) => HandleLine(emitted.Text);

    // Split out so tests can drive a line without standing up a LineExtractor.
    internal void HandleLine(string text)
    {
        if (_disposed || _pendingItem is not { } name) return;
        int uses = TokenCatalog.ParseUsesRemaining(text);
        if (uses < 0) return;

        _pendingItem = null;
        _pendingGen++;
        string key = name.ToLowerInvariant();
        if (_charges.TryGetValue(key, out int prev) && prev == uses) return;
        _charges[key] = uses;
        _log?.Info("Items", $"charges: {name} → {uses} uses remaining");
        Changed?.Invoke();
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

    // The carried item a look arg refers to — case-insensitive substring, the loose
    // resolution the game does for a partial `use`/`look`. First match wins.
    private string? ResolveCarried(string arg)
    {
        string a = arg.ToLowerInvariant();
        foreach (string carried in _carried())
            if (!string.IsNullOrWhiteSpace(carried) && carried.ToLowerInvariant().Contains(a))
                return carried;
        return null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_lines is not null) _lines.LineEmitted -= OnLine;
    }
}
