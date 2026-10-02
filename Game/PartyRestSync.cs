using System.Text;
using MudPlay.Services;

namespace MudPlay.Game;

// Emit side of the @wait / @ok protocol. Engines call RequestWait / RequestOk
// when their own logic decides the party leader should pause / resume — e.g.
// the HealthManager auto-rest path, message-engine flag triggers (a paralyzed /
// held / confused ailment fires a "send @wait if following" message-flag
// handler), etc.
//
// Receive side lives in PartyEssentialHandlers (OnWait / OnOk handlers populate
// the WaitingMembers set the pause-gate consumer reads). The two halves talk
// past each other — leaders never emit (they have no one to wait for),
// followers never receive (only leaders care).
//
// We don't send @wait when solo or when we're the party leader. Leader has no
// one to ping; solo means there's no party context at all.
//
// We deliberately do NOT hook PlayerState.Position changes. A user typing a
// manual `rest` shouldn't trigger an automatic @wait — only engine decisions
// should. Engines call into this service when their own conditions fire.
public sealed class PartyRestSync : IDisposable
{
    private const string LogCategory = "Party";

    private readonly PartyState _party;
    private readonly HashSet<WaitReason> _waitReasons = new();
    private readonly LogService? _log;
    private Action<byte[]>? _wireSender;
    private bool _disposed;

    public PartyRestSync(PartyState party, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(party);
        _party  = party;
        _log    = log;
    }

    // The reasons holding the wait. An @ok goes out only when this empties, so one
    // listed here long after it should have cleared is what keeps a party stopped;
    // the bug report shows it.
    public IReadOnlyCollection<WaitReason> HeldReasons => _waitReasons;

    // Bind the wire-sender. Without it, RequestWait / RequestOk calls are silent
    // no-ops (no telepath). MainWindowViewModel supplies SendUserInput alongside
    // the other party hookups.
    public void SetWireSender(Action<byte[]> sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        _wireSender = sender;
    }

    // The reason a wait names after the token, in MegaMUD's wording, so a leader
    // sees why the party stopped ("@wait (HP's too low)"). A receiver keys on the
    // token alone (PartyEssentialHandlers.OnWait), so the note changes nothing it
    // does. Only strings MegaMUD has been seen to send are used (GAME_MECHANICS
    // "`@wait` / `@ok` party pause"). The rest go out as a bare @wait: MegaMUD sends
    // nothing for a mana wait, and its wording for poison and disease isn't recorded.
    public const string HpNote       = "(HP's too low)";
    public const string BlindNote    = "(blinded)";
    public const string ConfusedNote = "(confused)";
    public const string HeldNote     = "(can't move)";
    public const string TooHeavyNote = "(too heavy to move)";

    // The note a reason carries unless the caller passes one. Health has none here:
    // it covers both pools, and only HealthManager knows which one tripped.
    internal static string? DefaultNote(WaitReason reason) => reason switch
    {
        WaitReason.Blindness => BlindNote,
        WaitReason.Confusion => ConfusedNote,
        WaitReason.Held      => HeldNote,
        WaitReason.TooHeavy  => TooHeavyNote,
        _                    => null,
    };

    // Engine-callable entry point — register a wait reason and telepath @wait to
    // the party leader on the 0→non-empty transition. If another reason already
    // holds the wait, this only records the new reason and sends nothing (the
    // leader is already paused). No-ops on the wire when solo, when we're the
    // leader, when there's no leader yet, or when no wire-sender is bound — but
    // the reason is still tracked so a later RequestOk balances. Idempotent at
    // the protocol level — the receiving leader's PartyEssentialHandlers.OnWait
    // dedupes via a HashSet so repeat sends don't double-count.
    //
    // resend: telepath @wait even though a reason already holds the wait. The leader
    // gives up on a wait after its "If leading, wait only" window, so a wait we still
    // count as held may no longer be holding it; an engine that knows it needs the
    // leader to stop NOW (a fresh drop below a rest floor, or being dragged along
    // while still recovering) re-asks. A duplicate @wait is harmless — the leader
    // dedupes waiting members.
    //
    // note: the reason shown after the token, overriding the reason's DefaultNote.
    public void RequestWait(WaitReason reason, bool resend = false, string? note = null)
    {
        bool wasEmpty = _waitReasons.Count == 0;
        bool added = _waitReasons.Add(reason);
        if (!resend && (!added || !wasEmpty)) return;
        if (!CanSignal()) return;
        string? why = note ?? DefaultNote(reason);
        Telepath(_party.LeaderName!, why is null ? "@wait" : $"@wait {why}");
        _log?.Info(LogCategory, $"sent @wait for {reason}" + (why is null ? "" : $" {why}"));
    }

    // Engine-callable entry point — clear a wait reason and telepath @ok to the
    // party leader only on the non-empty→0 transition (the LAST reason
    // clearing). While other reasons still hold the wait, this records the
    // release and sends nothing. Same wire gates as RequestWait.
    //
    // A released reason that leaves others held is logged with what still holds: a
    // reason that never clears suppresses every later @ok, and without that line a
    // stopped party shows only an @wait and then nothing. Nothing auto-releases a
    // long-held reason — a timeout would send the leader on while a follower still
    // can't move. A release of a reason that wasn't held is routine (the ailment
    // engine releases unconditionally) and isn't logged.
    public void RequestOk(WaitReason reason)
    {
        if (!_waitReasons.Remove(reason)) return;
        if (_waitReasons.Count > 0)
        {
            _log?.Info(LogCategory,
                $"{reason} released, no @ok sent — still held by {string.Join(", ", _waitReasons)}");
            return;
        }
        if (!CanSignal()) return;
        Telepath(_party.LeaderName!, "@ok");
        _log?.Info(LogCategory, $"last wait reason ({reason}) cleared — sent @ok");
    }

    // True while any wait reason (a rest, a hold, a blinding…) still stands.
    public bool IsHoldingWait => _waitReasons.Count > 0;

    // Broadcast @heal to the whole party (gangpath) — the flee-side signal a
    // low-HP follower emits instead of running. A follower that ran off alone
    // would break party formation and strand itself, so it asks the party
    // healer(s) to top it up and stays put; the leader owns the party's run
    // decision. Broadcasts rather than telepathing the leader (as RequestWait /
    // RequestOk do) because the healer may be any party member, not just the
    // leader. No-ops solo, as leader, or without a wire-sender.
    public void RequestHeal()
    {
        if (!_party.IsInParty) return;
        if (_party.SelfIsLeader) return;
        if (_wireSender is null) return;
        _wireSender(Encoding.Latin1.GetBytes("bg @heal\r"));   // gang speak verb is `bg`
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
    }

    private bool CanSignal()
    {
        if (!_party.IsInParty) return false;
        if (_party.SelfIsLeader) return false;
        if (string.IsNullOrEmpty(_party.LeaderName)) return false;
        return true;
    }

    private void Telepath(string recipient, string body)
    {
        if (_wireSender is null) return;
        // MajorMUD telepath syntax: `/<given> <body>` (slash + given name, no
        // space). `t` / `tel` / `tell` are all interpreted as `say`; full
        // "Given Family" recipients are rejected.
        string given = GivenName(recipient);
        byte[] bytes = Encoding.Latin1.GetBytes($"/{given} {body}\r");
        _wireSender(bytes);
    }

    private static string GivenName(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        int space = name.IndexOf(' ');
        return space >= 0 ? name[..space] : name;
    }
}
