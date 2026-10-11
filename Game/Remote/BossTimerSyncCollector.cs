using System;
using System.Collections.Generic;
using System.Linq;
using MudPlay.Services;

namespace MudPlay.Game.Remote;

// One responder's fully-reassembled, decoded timer set, and the channel it came by.
// AskedOfSenderAlone: it came by telepath from a player we telepathed the request
// to, and nothing has been asked on a channel others listen on. Only such a set is
// one player's answer to one question; any other is one of several that may come.
public readonly record struct BossTimerSyncResponse(
    string Sender, IReadOnlyList<BossTimerSyncRecord> Records, RemoteChannel Channel, bool AskedOfSenderAlone);

// Requester side of @timer sync: scrapes the `@timerdata <i>/<n> <blob>` reply lines off
// ChatRouter (they ride the chat as ordinary text — the remote engine ignores the
// leading @timerdata, see AppServices RegisterIgnored), reassembles each sender's chunks
// in order, decodes, and raises ResponseReceived once a sender's set is complete.
// Correlation is by responder name (every reply line carries it), so a shared gang/local
// channel just yields one set per responder — no request token needed. Collection is
// transient: Begin on send, Stop / Dispose when the merge window closes.
//
// Data is taken only on a channel a request of ours went out on. Nothing here turns
// a responder away for who they are (answering at all is their grant to us), so a
// channel we did not ask on is a way in for anyone who can reach us there while the
// window is open: a request to the gang must not let a line on the public broadcast
// channel in. What a set is taken as is told with it: the answer of the one player a
// telepath asked, or one of several that may come (BossTimerSyncResponse).
public sealed class BossTimerSyncCollector : IDisposable
{
    private readonly ChatRouter _chat;
    private readonly LogService? _log;
    private readonly Func<bool> _broadcastOff;
    private readonly Func<string, bool> _isOurName;
    private bool _collecting;
    private bool _disposed;

    private readonly Dictionary<string, Partial> _partial = new(StringComparer.OrdinalIgnoreCase);

    // The channels our requests have gone out on while collecting, and the players
    // a request was telepathed to, by given name.
    private readonly HashSet<RemoteChannel> _askedOn = new();
    private readonly HashSet<string> _telepathed = new(StringComparer.OrdinalIgnoreCase);

    public event Action<BossTimerSyncResponse>? ResponseReceived;

    // broadcastOff: the Talk tab's "Disallow @commands from broadcast". Ticked, the
    // broadcast channel is not listened to at all, for answers no more than for
    // requests: it is public, and off until the user opens it.
    // isOurName: whether a speaker is our own character. A shared channel can echo
    // our own lines back under our name: a request we typed, or data we sent in
    // answer to someone else's request while this window is open.
    // telepathWasUsers: whether a telepathed request of ours, seen going out, was the
    // user's own and not one the client sent for another player (a relay-back, a
    // `@do`). Only the user's asks a player whose answer is then taken as theirs.
    public BossTimerSyncCollector(
        ChatRouter chat, LogService? log = null, Func<bool>? broadcastOff = null, Func<string, bool>? isOurName = null,
        Func<ChatLogEntry, bool>? telepathWasUsers = null)
    {
        ArgumentNullException.ThrowIfNull(chat);
        _chat = chat;
        _log = log;
        _broadcastOff = broadcastOff ?? (static () => false);
        _isOurName = isOurName ?? (static _ => false);
        _telepathWasUsers = telepathWasUsers ?? (static _ => true);
        _chat.EntryClassified += Ingest;
    }

    private readonly Func<ChatLogEntry, bool> _telepathWasUsers;

    // A request of ours went out on askedOn. Its answers are taken from here on,
    // with those of every request made before it while collecting: a second
    // request on another channel adds that channel, and late answers to the first
    // are still read. Whether the request came from the window's button or was
    // typed by hand makes no difference; Ingest tells of the typed ones.
    //
    // telepathReached: the player a telepath of the user's reached, as the game's
    // own `--- Telepath Sent to <Name> ---` names them; that player's answer is then
    // theirs alone. Never the name as typed: a typed name may be the first letters
    // of a player's, the game says whose it took them for, and a telepath whose
    // letters fit two players reaches neither (GAME_MECHANICS "Telepath throttle
    // and per-telepath acknowledgement"). Until the game has named who a telepath
    // reached, nobody was asked, and every answer is one of several.
    //
    // A responder whose set has been delivered may send again for the new request.
    // One whose set is half here keeps what has arrived.
    public void Begin(RemoteChannel askedOn, string? telepathReached = null)
    {
        _collecting = true;
        _askedOn.Add(askedOn);
        if (askedOn == RemoteChannel.Telepath && !string.IsNullOrWhiteSpace(telepathReached))
            _telepathed.Add(RemoteCommandManager.GivenName(telepathReached.Trim()));
        foreach (string delivered in _partial.Where(p => p.Value.Done).Select(p => p.Key).ToList())
            _partial.Remove(delivered);
    }

    public void Stop()
    {
        _collecting = false;
        _partial.Clear();
        _askedOn.Clear();
        _telepathed.Clear();
    }

    // True while every request made has been a telepath to a named player: the
    // case in which an asked player's answer is theirs alone.
    public bool OnlyTelepathsAsked => _askedOn.Count == 1 && _askedOn.Contains(RemoteChannel.Telepath);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _chat.EntryClassified -= Ingest;
    }

    private sealed class Partial
    {
        public required int N;
        public required string?[] Chunks;
        public bool Done;
        // A piece of the set came on the broadcast channel.
        public bool OnBroadcast;
    }

    internal void Ingest(ChatLogEntry e)
    {
        if (!_collecting) return;

        // A request of ours seen going out: one typed by hand while the window is
        // open, or the button's own. A telepath's echo names who it went to.
        if (TransferChannels.OwnRequest(e, _isOurName) is { Transfer: Transfer.TimerSync } own)
        {
            if (!TransferChannels.Allows(Transfer.TimerSync, own.Channel)) return;
            // A telepath the client sent for another player asks nobody on the
            // user's behalf: its channel is listened on, its answer is a pick.
            bool users = own.Channel != RemoteChannel.Telepath || _telepathWasUsers(e);
            Begin(own.Channel, users ? e.Speaker : null);
            return;
        }

        if (TransferChannels.Of(e.Channel) is not { } heardOn || !AnswersAreTakenOn(heardOn)) return;
        if (heardOn == RemoteChannel.Broadcast && _broadcastOff()) return;
        if (e.Speaker is not { Length: > 0 } sender) return;
        // Our own data, echoed back to us by the channel, is no one's answer.
        if (_isOurName(sender)) return;
        if (!TryParse(e.Message, out int index, out int count, out string blob)) return;

        if (!_partial.TryGetValue(sender, out Partial? p) || p.N != count)
        {
            p = new Partial { N = count, Chunks = new string?[count] };
            _partial[sender] = p;
        }
        if (p.Done) return;                 // already delivered this sender's set
        p.Chunks[index - 1] = blob;
        p.OnBroadcast |= heardOn == RemoteChannel.Broadcast;
        _log?.Debug("BossTimerSync", $"received line {index}/{count} from {sender} ({blob.Length} chars)");
        if (p.Chunks.Any(c => c is null)) return;   // still missing pieces

        p.Done = true;
        IReadOnlyList<BossTimerSyncRecord> records;
        try
        {
            records = BossTimerSyncCodec.Decode(string.Concat(p.Chunks));
        }
        catch (FormatException ex)
        {
            _log?.Warn("BossTimerSync", $"discarding malformed timer-sync payload from {sender}: {ex.Message}");
            return;
        }
        _log?.Info("BossTimerSync",
            $"received {records.Count} timer(s) from {sender} in {p.N} line(s): {Describe(records)}");
        // A set is as public as its most public piece: one line of it on the
        // broadcast channel, and it is a set heard there, wherever the last came.
        RemoteChannel cameBy = p.OnBroadcast ? RemoteChannel.Broadcast : heardOn;
        ResponseReceived?.Invoke(new BossTimerSyncResponse(sender, records, cameBy, AskedOfSenderAlone(cameBy, sender)));
    }

    // An answer comes back on the channel it was asked on (RemoteCommandManager
    // answers there), with one exception: a responder who is sneaking answers a said
    // request by telepath, since speaking aloud would end the sneak. So telepath is
    // taken after a said request too. Gangpath and broadcast end no sneak and are
    // never rerouted (GAME_MECHANICS "Channel broadcast").
    private bool AnswersAreTakenOn(RemoteChannel heardOn) =>
        _askedOn.Contains(heardOn)
        || (heardOn == RemoteChannel.Telepath && _askedOn.Contains(RemoteChannel.Local));

    // Whether a set is the answer of the one player a question was put to: it came
    // by telepath, from a player a request was telepathed to, and no request has
    // gone out on a channel others listen on. Once one has, whatever the order, a
    // telepath may be a sneaking client's answer to a say and every player there
    // may answer with a different timer. A telepath from a player nobody asked is
    // taken like any other, and is no one's answer to our question.
    private bool AskedOfSenderAlone(RemoteChannel heardOn, string sender) =>
        heardOn == RemoteChannel.Telepath
        && OnlyTelepathsAsked
        && _telepathed.Contains(RemoteCommandManager.GivenName(sender));

    // Parse "@timerdata <i>/<n> <blob>", tolerating the {} the remote reply path wraps a
    // reply in. The blob is a single spaceless base64url token, so a 3-way split keeps it
    // whole.
    private static bool TryParse(string message, out int index, out int count, out string blob)
    {
        index = count = 0; blob = string.Empty;
        string body = message.Trim();
        if (body.Length >= 2 && body[0] == '{' && body[^1] == '}') body = body[1..^1].Trim();

        string[] parts = body.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3) return false;
        if (!parts[0].Equals(BossTimerQueryHandler.SyncResponseToken, StringComparison.OrdinalIgnoreCase)) return false;

        string[] frac = parts[1].Split('/');
        if (frac.Length != 2 || !int.TryParse(frac[0], out index) || !int.TryParse(frac[1], out count)) return false;
        if (index < 1 || count < 1 || index > count) return false;
        blob = parts[2];
        return true;
    }

    // Compact one-line summary of a decoded set for the program log.
    private static string Describe(IReadOnlyList<BossTimerSyncRecord> records)
        => records.Count == 0 ? "(no active timers)" : string.Join(", ", records.Select(r => r.Describe()));
}
