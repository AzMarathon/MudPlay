using System;
using System.Collections.Generic;
using System.Linq;
using MudPlay.Services;

namespace MudPlay.Game.Remote;

// One responder's fully-reassembled, decoded timer set, the channel it came by, and
// the channel of the request of ours it answers. The two differ for a sneaking
// client's telepathed answer to a request we said.
public readonly record struct BossTimerSyncResponse(
    string Sender, IReadOnlyList<BossTimerSyncRecord> Records, RemoteChannel Channel, RemoteChannel AskedOn);

// Requester side of @timer sync: scrapes the `@timerdata <i>/<n> <blob>` reply lines off
// ChatRouter (they ride the chat as ordinary text — the remote engine ignores the
// leading @timerdata, see AppServices RegisterIgnored), reassembles each sender's chunks
// in order, decodes, and raises ResponseReceived once a sender's set is complete.
// Correlation is by responder name (every reply line carries it), so a shared gang/local
// channel just yields one set per responder — no request token needed. Collection is
// transient: Begin on send, Stop / Dispose when the merge window closes.
//
// Data is taken only on a channel a request of ours went out on. Nothing here checks
// who a responder is (answering at all is their grant to us), so a channel we did
// not ask on is a way in for anyone who can reach us there while the window is open:
// a request to the gang must not let a line on the public broadcast channel in.
public sealed class BossTimerSyncCollector : IDisposable
{
    private readonly ChatRouter _chat;
    private readonly LogService? _log;
    private readonly Func<bool> _broadcastOff;
    private readonly Func<string, bool> _isOurName;
    private bool _collecting;
    private bool _disposed;

    private readonly Dictionary<string, Partial> _partial = new(StringComparer.OrdinalIgnoreCase);

    // The channels our requests went out on since Begin.
    private readonly HashSet<RemoteChannel> _askedOn = new();

    public event Action<BossTimerSyncResponse>? ResponseReceived;

    // broadcastOff: the Talk tab's "Disallow @commands from broadcast". Ticked, the
    // broadcast channel is not listened to at all, for answers no more than for
    // requests: it is public, and off until the user opens it.
    // isOurName: whether a speaker is our own character. A shared channel can echo
    // our own lines back under our name: a request we typed, or data we sent in
    // answer to someone else's request while this window is open.
    public BossTimerSyncCollector(
        ChatRouter chat, LogService? log = null, Func<bool>? broadcastOff = null, Func<string, bool>? isOurName = null)
    {
        ArgumentNullException.ThrowIfNull(chat);
        _chat = chat;
        _log = log;
        _broadcastOff = broadcastOff ?? (static () => false);
        _isOurName = isOurName ?? (static _ => false);
        _chat.EntryClassified += Ingest;
    }

    // Start collecting the answers to a request that went out on askedOn, discarding
    // any prior run.
    public void Begin(RemoteChannel askedOn)
    {
        _collecting = true;
        _partial.Clear();
        _askedOn.Clear();
        _askedOn.Add(askedOn);
    }

    public void Stop()
    {
        _collecting = false;
        _partial.Clear();
        _askedOn.Clear();
    }

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
    }

    internal void Ingest(ChatLogEntry e)
    {
        if (!_collecting) return;

        // A further request of ours, typed by hand while the window is open: its
        // answers are taken on its channel as well.
        if (TransferChannels.OwnRequest(e, _isOurName) is { Transfer: Transfer.TimerSync } own)
        {
            if (TransferChannels.Allows(Transfer.TimerSync, own.Channel)) _askedOn.Add(own.Channel);
            return;
        }

        if (TransferChannels.Of(e.Channel) is not { } heardOn || RequestAnsweredOn(heardOn) is not { } askedOn) return;
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
        ResponseReceived?.Invoke(new BossTimerSyncResponse(sender, records, heardOn, askedOn));
    }

    // The channel of the request of ours that an answer heard on heardOn belongs to,
    // or null when we asked on none that it could answer. An answer comes back on
    // the channel it was asked on (RemoteCommandManager answers there), with one
    // exception: a responder who is sneaking answers a said request by telepath,
    // since speaking aloud would end the sneak. So telepath is taken after a said
    // request too. Gangpath and broadcast end no sneak and are never rerouted
    // (GAME_MECHANICS "Channel broadcast").
    //
    // With both a say and a telepath asked, a telepathed answer cannot be told
    // apart: it is counted as the say's, the one of the two that every listener in
    // the room could have answered.
    private RemoteChannel? RequestAnsweredOn(RemoteChannel heardOn)
    {
        if (heardOn == RemoteChannel.Telepath && _askedOn.Contains(RemoteChannel.Local)) return RemoteChannel.Local;
        return _askedOn.Contains(heardOn) ? heardOn : null;
    }

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
