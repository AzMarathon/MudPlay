using System.Collections.Generic;
using System.Collections.Specialized;
using System.Text;
using MudPlay.Services;

namespace MudPlay.Game.Remote;

// Gathers party members' intel by probing them when we start partying. On every
// join (a fresh row in PartyState.Members, or an invited row flipping to joined)
// this sends the member `@version` + `@level` — but only the FIRST time we party
// with them on a given local day, gated by PlayerObservation.LastPartiedUtc the
// same once-per-day way GreetManager rate-limits auto-greets. The @version reply
// is recorded onto the player record (PlayerDatabase.RecordVersion); the @level
// reply is recorded by PartyLevelProbe (the sole @level recorder), so nothing
// here parses it. `@health` is NOT sent from here — PartyPoller already telepaths
// it on every join for the live party-window vitals.
//
// Leadership-agnostic: unlike the old leader-only roster probe this fires whether
// we lead or follow — the point is populating the shared per-realm player record,
// which every alt benefits from. The route-gate level check (PartyLevelTracker)
// still handles the leader's navigation needs on demand.
//
// Correlating the @version reply: telepath replies aren't labelled with the
// command they answer, so we arm a short per-player expectation window when we
// send @version and match the next brace-wrapped reply from that member (the
// engine wraps @-command replies in { }), skipping the @level / @health shapes
// that share the window. A version string is a brace-wrapped, letter-led payload
// carrying a digit (a client name + version number) — which rejects denial /
// chat lines that happen to arrive while the window is open.
//
// Suspend + trainer-menu gating mirror PartyPoller: no telepath onto the wire
// while dropped at the BBS login menu or parked in the full-screen trainer menu.
public sealed class PartyProbeManager : IDisposable
{
    private readonly ChatRouter _chat;
    private readonly PartyState _state;
    private readonly PlayerDatabase _players;
    private readonly Func<DateTime> _clock;
    private readonly LogService? _log;
    private Action<byte[]>? _wireSender;
    private bool _suspended;
    private bool _disposed;

    // Per-player "expecting a @version reply" deadlines (given name → UTC
    // deadline), armed when we send @version and cleared on the reply or timeout.
    private readonly Dictionary<string, DateTime> _awaitingVersion =
        new(StringComparer.OrdinalIgnoreCase);

    // How long after sending @version to keep watching for the reply. Generous —
    // the member replies to @level and @version back to back over telepath.
    public TimeSpan VersionWindow { get; set; } = TimeSpan.FromSeconds(12);

    // Master enable, mirroring the new PartySettings toggle. Read at fire time so
    // toggling it off stops new probes immediately. Default on.
    public bool Enabled { get; set; } = true;

    // Live gate — true while parked in the full-screen trainer stats menu, where
    // a telepath would land as stray keystrokes. Null = never suppressed.
    public Func<bool>? IsInTrainerMenu { get; set; }

    private bool InTrainerMenu => IsInTrainerMenu is { } gate && gate();

    public PartyProbeManager(ChatRouter chat, PartyState state, PlayerDatabase players, LogService? log = null)
        : this(chat, state, players, clock: null, log: log) { }

    internal PartyProbeManager(
        ChatRouter chat, PartyState state, PlayerDatabase players,
        Func<DateTime>? clock, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(chat);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(players);
        _chat = chat;
        _state = state;
        _players = players;
        _clock = clock ?? (() => DateTime.UtcNow);
        _log = log;

        _state.Members.CollectionChanged += OnMembersChanged;
        _chat.EntryClassified += OnChatEntry;
        foreach (PartyMember m in _state.Members)
            m.PropertyChanged += OnMemberPropertyChanged;
    }

    // Bind the wire-sender (main-window VM supplies the engine send at connect).
    public void SetWireSender(Action<byte[]> sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        _wireSender = sender;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _state.Members.CollectionChanged -= OnMembersChanged;
        _chat.EntryClassified -= OnChatEntry;
        foreach (PartyMember m in _state.Members)
            m.PropertyChanged -= OnMemberPropertyChanged;
    }

    // ----- connection lifecycle (mirror PartyPoller) ---------------------

    public void NotifyDisconnected()
    {
        _suspended = true;
        _awaitingVersion.Clear();
    }

    public void NotifyEnteredRealm() => _suspended = false;

    // ----- join detection ------------------------------------------------

    private void OnMembersChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
            foreach (object? item in e.OldItems)
                if (item is PartyMember m) m.PropertyChanged -= OnMemberPropertyChanged;

        if (e.Action != NotifyCollectionChangedAction.Add || e.NewItems is null) return;
        foreach (object? item in e.NewItems)
        {
            if (item is not PartyMember m) continue;
            m.PropertyChanged += OnMemberPropertyChanged;
            TryProbeOnJoin(m);
        }
    }

    // An invited row becomes a real party member only when it flips
    // IsInvited true→false (acceptance), which arrives as a PropertyChanged
    // rather than a collection change — probe on that edge too.
    private void OnMemberPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PartyMember.IsInvited)) return;
        if (sender is not PartyMember m) return;
        if (m.IsInvited) return;
        TryProbeOnJoin(m);
    }

    private void TryProbeOnJoin(PartyMember m)
    {
        if (!Enabled || _suspended || InTrainerMenu || _wireSender is null) return;
        if (m.IsSelf || m.IsInvited || string.IsNullOrEmpty(m.Name)) return;

        string given = GivenName(m.Name);
        if (string.IsNullOrEmpty(given)) return;

        DateTime now = _clock();
        DateTime? prev = _players.GetLastPartiedUtc(given);
        if (prev is { } p && SameLocalDay(p, now)) return;   // already probed them today

        // Stamp the probe (not merely the join) so a leave-and-rejoin later the
        // same day reads today's stamp above and doesn't re-probe.
        _players.RecordPartied(m.Name, now);
        Send($"/{given} @level\r");
        Send($"/{given} @version\r");
        _awaitingVersion[given] = now + VersionWindow;
        _log?.Info("PartyProbe", $"First party today with {given} — sent @level + @version.");
    }

    // ----- @version reply capture ----------------------------------------

    private void OnChatEntry(ChatLogEntry entry)
    {
        if (entry.Channel != ChatChannel.TelepathIncoming) return;
        if (string.IsNullOrEmpty(entry.Speaker) || string.IsNullOrEmpty(entry.Message)) return;

        string given = GivenName(entry.Speaker);
        if (!_awaitingVersion.TryGetValue(given, out DateTime deadline)) return;

        DateTime now = _clock();
        if (now > deadline) { _awaitingVersion.Remove(given); return; }

        // @level / @health replies share the brace-wrap and this window (we sent
        // @level too) — leave the expectation armed and wait for the real
        // @version line rather than mis-recording those.
        if (!TryParseVersion(entry.Message, out string version)) return;

        _players.RecordVersion(entry.Speaker, version, now);
        _awaitingVersion.Remove(given);
        _log?.Info("PartyProbe", $"Recorded {given} version = {version}");
    }

    // A version reply is the brace-wrapped, letter-led payload the client returns
    // to @version (e.g. "{MudPlay 2.37.0}", "{MegaMud 1.03u}"). Requiring the
    // braces (every @-command reply is wrapped at SendReply) rejects ordinary
    // chat; requiring a leading letter + an embedded digit rejects @level /
    // @health / denial lines that also ride the wrap.
    internal static bool TryParseVersion(string message, out string version)
    {
        version = string.Empty;
        string p = message.Trim();
        if (p.Length < 3 || p[0] != '{' || p[^1] != '}') return false;
        string inner = p[1..^1].Trim();
        if (inner.Length is 0 or > 60) return false;
        if (!char.IsLetter(inner[0])) return false;
        if (inner.StartsWith("Level ", StringComparison.OrdinalIgnoreCase)
            || inner.StartsWith("level unknown", StringComparison.OrdinalIgnoreCase)
            || inner.StartsWith("HP=", StringComparison.OrdinalIgnoreCase)) return false;
        bool hasDigit = false;
        foreach (char c in inner) if (char.IsDigit(c)) { hasDigit = true; break; }
        if (!hasDigit) return false;
        version = inner;
        return true;
    }

    private void Send(string text)
    {
        if (_wireSender is null) return;
        _wireSender(Encoding.Latin1.GetBytes(text));
    }

    private static string GivenName(string name)
    {
        int space = name.IndexOf(' ');
        return space >= 0 ? name[..space] : name;
    }

    private static bool SameLocalDay(DateTime aUtc, DateTime bUtc)
        => aUtc.ToLocalTime().Date == bUtc.ToLocalTime().Date;
}
