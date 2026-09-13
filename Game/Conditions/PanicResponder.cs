using MudPlay.Models.Profile;
using MudPlay.Services;

namespace MudPlay.Game.Conditions;

// Inbound @panic handler (MegaMUD parity). @panic is the party-wide bail-out
// signal: a leader whose HP hit its "hang if below" floor says a bare "@panic"
// on the say channel (AilmentSyncEngine's sibling send path lives on
// HealthManager) and escapes. This responder watches the say channel for that
// token from a party member and — unless the user set PartySettings.IgnorePanics
// — makes us escape the same way our own low-HP emergency would, via
// HealthManager.RespondToReceivedPanic (sys-goto-wimpy if configured, else hang
// up). @panic rides SAY, not a telepath @-command, so (like the ailment announce
// tokens) it's registered as a reserved token on the remote engine rather than a
// catalog command — the engine swallows it instead of bouncing "{command invalid}".
//
// Gates, all of which must pass before we bail:
//   - our own "You say" echo has a null speaker → ignored (we don't panic off our
//     own broadcast);
//   - we must be in a party AND the speaker must be an active party member — a
//     stranger saying "@panic" in the room can't drop us (grief guard);
//   - PartySettings.IgnorePanics must be false.
// The carrier-drop vs wimpy-jump decision and the DisableHangups master-switch
// interplay live in HealthManager.RespondToReceivedPanic.
public sealed class PanicResponder : IDisposable
{
    // LogService category — appears as [Panic] rows.
    public const string LogCategory = "Panic";

    // The exact say token. Bare "@panic" (the leading '.' is the say-shortcut and
    // is already stripped by the time ChatRouter classifies the Local entry).
    private const string Token = "@panic";

    private readonly ChatRouter _chat;
    private readonly PartyState _party;
    private readonly Func<PartySettings> _readPartySettings;
    private readonly Action<string> _respond;
    private readonly LogService? _log;
    private bool _disposed;

    public PanicResponder(
        ChatRouter chat,
        PartyState party,
        Func<PartySettings> readPartySettings,
        Action<string> respond,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(chat);
        ArgumentNullException.ThrowIfNull(party);
        ArgumentNullException.ThrowIfNull(readPartySettings);
        ArgumentNullException.ThrowIfNull(respond);
        _chat = chat;
        _party = party;
        _readPartySettings = readPartySettings;
        _respond = respond;
        _log = log;
        _chat.EntryClassified += OnChat;
    }

    private void OnChat(ChatLogEntry entry)
    {
        if (entry.Channel != ChatChannel.Local) return;
        // Null speaker = our own "You say" echo — never panic off our own broadcast.
        if (string.IsNullOrEmpty(entry.Speaker)) return;
        if (!entry.Message.Trim().Equals(Token, StringComparison.OrdinalIgnoreCase)) return;

        // Party + membership guard: only a partymate can make us bail.
        if (!_party.IsInParty || !IsActivePartyMember(entry.Speaker))
        {
            _log?.Info(LogCategory, $"@panic from non-party '{entry.Speaker}' ignored");
            return;
        }

        if (_readPartySettings().IgnorePanics)
        {
            _log?.Info(LogCategory, $"@panic from {entry.Speaker} ignored (ignore @panics set)");
            return;
        }

        _respond(entry.Speaker);
    }

    private bool IsActivePartyMember(string speaker)
    {
        string speakerGiven = GivenName(speaker);
        foreach (PartyMember m in _party.Members)
        {
            if (m.Name.Equals(speaker, StringComparison.OrdinalIgnoreCase)) return true;
            if (GivenName(m.Name).Equals(speakerGiven, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static string GivenName(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        int space = name.IndexOf(' ');
        return space >= 0 ? name[..space] : name;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _chat.EntryClassified -= OnChat;
    }
}
