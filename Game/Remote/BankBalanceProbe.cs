using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia.Threading;
using MudPlay.Services;
using MudPlay.Terminal;

namespace MudPlay.Game.Remote;

// Self-only bank-balance probe + passive parser. The `bank` command lists every
// bank the character has ever deposited at, one block per bank, from ANY room —
// it's a global account query, not a room action (a bank never used stays hidden;
// one used then emptied shows 0). Blocks look like:
//   Your balance at Bank of Godfrey is:            (Paradigm)
//   On deposit: 19578816 copper farthings [195,788.16 gold crowns]
//   Your balance at Bank of Godfrey (#8) is:       (Stock — adds the shop number)
//   On deposit: 4512 copper farthings [45.12 gold crowns]
// We can't see party members' banks (bank is self-only), so this tracks only our
// own deposits, keyed by bank name — which IS the bank's shop name, so a balance
// maps back to its room(s) via BankCatalog. Amounts are copper farthings (the
// denomination the game prices in). Balances are parsed whenever they appear on
// the wire; QueryAsync sends `bank` and awaits the reply block(s) so the route
// picker can weigh a buy the purse can't cover against money on deposit — the
// withdraw itself still happens at the bank room (PathItemShopRouter).
public sealed partial class BankBalanceProbe : IDisposable
{
    private readonly Action<string> _send;
    private readonly Action<Action> _armWindow;
    private readonly LogService? _log;
    private LineExtractor? _lines;
    private readonly Dictionary<string, long> _balances = new(StringComparer.OrdinalIgnoreCase);
    private string? _pendingName;   // header seen, awaiting its On-deposit line
    private readonly List<TaskCompletionSource<IReadOnlyDictionary<string, long>>> _pending = new();
    private bool _disposed;

    // How long to wait for the `bank` reply block(s) before completing with
    // whatever arrived. The whole listing comes back in one burst, so a short
    // window covers a laggy BBS without stalling the picker.
    public TimeSpan QueryWindow { get; set; } = TimeSpan.FromSeconds(3);

    // "Your balance at <name> is:" / "Your balance at <name> (#N) is:". The
    // Stock (#N) suffix is the shop number — dropped so the captured name matches
    // the bank's shop name (what BankCatalog keys rooms on). Non-greedy name up to
    // the optional suffix + " is:".
    [GeneratedRegex(@"^Your balance at (?<name>.+?)(?: \(#\d+\))? is:\s*$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex BalanceHeader();

    // "On deposit: N copper farthings [G gold crowns]" — take the authoritative
    // copper figure (thousands-commas allowed), ignore the gold-crown gloss.
    [GeneratedRegex(@"^On deposit:\s+(?<copper>[\d,]+) copper farthings",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex OnDeposit();

    public BankBalanceProbe(Action<string> send, LogService? log = null)
        : this(send, armWindow: null, log) { }

    internal BankBalanceProbe(Action<string> send, Action<Action>? armWindow, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(send);
        _send = send;
        _armWindow = armWindow ?? DefaultArmWindow;
        _log = log;
    }

    // Last-known deposit per bank name (case-insensitive) — a copy so callers
    // can't mutate the live map.
    public IReadOnlyDictionary<string, long> LastKnown =>
        new Dictionary<string, long>(_balances, StringComparer.OrdinalIgnoreCase);

    // Last-known deposit at one named bank, or null when that bank has never been
    // seen in a `bank` reply (never used → hidden).
    public long? Balance(string bankName) =>
        _balances.TryGetValue(bankName, out long v) ? v : null;

    public void AttachLineExtractor(LineExtractor lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (_lines is not null) _lines.LineEmitted -= OnLine;
        _lines = lines;
        _lines.LineEmitted += OnLine;
    }

    // Send `bank` and complete once the reply block(s) have had QueryWindow to
    // arrive. Returns the freshly-merged balances (also folded into LastKnown).
    public Task<IReadOnlyDictionary<string, long>> QueryAsync()
    {
        if (_disposed) return Task.FromResult(LastKnown);
        TaskCompletionSource<IReadOnlyDictionary<string, long>> tcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending.Add(tcs);
        _send("bank");
        _armWindow(() => Complete(tcs));
        return tcs.Task;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_lines is not null) _lines.LineEmitted -= OnLine;
        foreach (TaskCompletionSource<IReadOnlyDictionary<string, long>> t in _pending.ToArray())
            t.TrySetResult(LastKnown);
        _pending.Clear();
    }

    private void OnLine(LineExtractor.EmittedLine emitted) => HandleLine(emitted.Text);

    // The balance lines are distinctive enough to parse unconditionally — they
    // only appear in a `bank` reply, so no outbound-arm gate is needed.
    internal void HandleLine(string line)
    {
        if (BalanceHeader().Match(line) is { Success: true } h)
        {
            _pendingName = h.Groups["name"].Value.Trim();
            return;
        }
        if (_pendingName is { } name && OnDeposit().Match(line) is { Success: true } d
            && long.TryParse(d.Groups["copper"].Value.Replace(",", ""),
                NumberStyles.Integer, CultureInfo.InvariantCulture, out long copper))
        {
            _balances[name] = copper;
            _log?.Info("BankBalance", $"{name}: {copper:N0} copper on deposit");
            _pendingName = null;
        }
    }

    private void Complete(TaskCompletionSource<IReadOnlyDictionary<string, long>> tcs)
    {
        if (!_pending.Remove(tcs)) return;
        tcs.TrySetResult(LastKnown);
    }

    private void DefaultArmWindow(Action onElapsed)
    {
        DispatcherTimer timer = new(DispatcherPriority.Background) { Interval = QueryWindow };
        timer.Tick += (_, _) => { timer.Stop(); onElapsed(); };
        timer.Start();
    }
}
