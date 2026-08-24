using System;
using System.Collections.Generic;
using System.Linq;
using MudPlay.Services;

namespace MudPlay.Game.Remote;

// One responder's fully-reassembled, decoded timer set.
public readonly record struct BossTimerSyncResponse(string Sender, IReadOnlyList<BossTimerSyncRecord> Records);

// Requester side of @timer sync: scrapes the `@timerdata <i>/<n> <blob>` reply lines off
// ChatRouter (they ride the chat as ordinary text — the remote engine ignores the
// leading @timerdata, see AppServices RegisterIgnored), reassembles each sender's chunks
// in order, decodes, and raises ResponseReceived once a sender's set is complete.
// Correlation is by responder name (every reply line carries it), so a shared gang/local
// channel just yields one set per responder — no request token needed. Collection is
// transient: Begin on send, Stop / Dispose when the merge window closes.
public sealed class BossTimerSyncCollector : IDisposable
{
    private readonly ChatRouter _chat;
    private readonly LogService? _log;
    private bool _collecting;
    private bool _disposed;

    private readonly Dictionary<string, Partial> _partial = new(StringComparer.OrdinalIgnoreCase);

    public event Action<BossTimerSyncResponse>? ResponseReceived;

    public BossTimerSyncCollector(ChatRouter chat, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(chat);
        _chat = chat;
        _log = log;
        _chat.EntryClassified += Ingest;
    }

    // Start collecting responses, discarding any prior run.
    public void Begin()
    {
        _collecting = true;
        _partial.Clear();
    }

    public void Stop()
    {
        _collecting = false;
        _partial.Clear();
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
        // Responses arrive on whichever channel the request went out on.
        if (e.Channel is not (ChatChannel.TelepathIncoming or ChatChannel.Gangpath or ChatChannel.Local))
            return;
        if (e.Speaker is not { Length: > 0 } sender) return;
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
        ResponseReceived?.Invoke(new BossTimerSyncResponse(sender, records));
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
