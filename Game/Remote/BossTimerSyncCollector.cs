using System;
using System.Collections.Generic;
using System.Linq;
using MudPlay.Services;

namespace MudPlay.Game.Remote;

// One responder's fully-reassembled, decoded timer set.
public readonly record struct BossTimerSyncResponse(string Sender, IReadOnlyList<BossTimerSyncRecord> Records);

// Requester side of @timer sync: scrapes the `@timerdata <TOK> i/n <blob>` reply lines
// off ChatRouter (they ride the chat as ordinary text — the remote engine ignores the
// token, see AppServices RegisterIgnored), reassembles each sender's chunks in order,
// decodes, and raises ResponseReceived once a sender's set is complete. Scoped to the
// active token so a shared gang/local channel (many responders, and even two people
// syncing at once) doesn't cross-contaminate. Collection is transient: Begin on send,
// Stop / Dispose when the merge window closes.
public sealed class BossTimerSyncCollector : IDisposable
{
    private readonly ChatRouter _chat;
    private readonly LogService? _log;
    private string? _token;
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

    // Start collecting responses for a freshly-issued token, discarding any prior run.
    public void Begin(string token)
    {
        _token = token;
        _partial.Clear();
    }

    public void Stop()
    {
        _token = null;
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
        if (_token is not { } token) return;
        // Responses arrive on whichever channel the request went out on.
        if (e.Channel is not (ChatChannel.TelepathIncoming or ChatChannel.Gangpath or ChatChannel.Local))
            return;
        if (e.Speaker is not { Length: > 0 } sender) return;
        if (!TryParse(e.Message, out string tok, out int index, out int count, out string blob)) return;
        if (!string.Equals(tok, token, StringComparison.Ordinal)) return;   // someone else's sync

        if (!_partial.TryGetValue(sender, out Partial? p) || p.N != count)
        {
            p = new Partial { N = count, Chunks = new string?[count] };
            _partial[sender] = p;
        }
        if (p.Done) return;                 // already delivered this sender's set
        p.Chunks[index - 1] = blob;
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
        _log?.Info("BossTimerSync", $"timer-sync response from {sender}: {records.Count} timer(s)");
        ResponseReceived?.Invoke(new BossTimerSyncResponse(sender, records));
    }

    // Parse "@timerdata <TOK> <i>/<n> <blob>", tolerating the {} the remote reply path
    // wraps a reply in. The blob is a single spaceless base64url token, so a 4-way split
    // keeps it whole.
    private static bool TryParse(string message, out string token, out int index, out int count, out string blob)
    {
        token = string.Empty; index = count = 0; blob = string.Empty;
        string body = message.Trim();
        if (body.Length >= 2 && body[0] == '{' && body[^1] == '}') body = body[1..^1].Trim();

        string[] parts = body.Split(' ', 4, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 4) return false;
        if (!parts[0].Equals(BossTimerQueryHandler.SyncResponseToken, StringComparison.OrdinalIgnoreCase)) return false;

        token = parts[1];
        string[] frac = parts[2].Split('/');
        if (frac.Length != 2 || !int.TryParse(frac[0], out index) || !int.TryParse(frac[1], out count)) return false;
        if (index < 1 || count < 1 || index > count) return false;
        blob = parts[3];
        return true;
    }
}
