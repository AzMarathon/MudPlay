using System;
using System.Collections.Generic;
using System.Linq;
using MudPlay.Game.Map;
using MudPlay.Services;

namespace MudPlay.Game.Remote;

// Requester side of @roomba sync: scrapes the `@roombadata <i>/<n> <blob>`
// reply lines off ChatRouter (ignored by RemoteCommandManager — see AppServices
// RegisterIgnored(RoombaQueryHandler.SyncResponseToken)), reassembles each
// sender's chunks in order, decodes, and merges straight into
// GhItemLocationStore. Unlike BossTimerSyncCollector there's no Begin/Stop
// gate tied to a merge-review window: an item sighting has no meaningful
// "conflict" the way a boss kill time does (newest wins, silently — see
// GhItemLocationStore.MergeSyncRecords), so this listens continuously, the
// same way the rest of @roomba works — opt in once via
// GhRoomLabelStore.ResponsesEnabled and everything else is automatic.
public sealed class RoombaSyncReceiver : IDisposable
{
    private readonly ChatRouter _chat;
    private readonly GhItemLocationStore _locations;
    private readonly GhRoomLabelStore _labels;
    private readonly LogService? _log;
    private bool _disposed;

    private readonly Dictionary<string, Partial> _partial = new(StringComparer.OrdinalIgnoreCase);

    public RoombaSyncReceiver(ChatRouter chat, GhItemLocationStore locations, GhRoomLabelStore labels, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(chat);
        ArgumentNullException.ThrowIfNull(locations);
        ArgumentNullException.ThrowIfNull(labels);
        _chat = chat;
        _locations = locations;
        _labels = labels;
        _log = log;
        _chat.EntryClassified += Ingest;
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
        // Gate on the same opt-in @roomba itself answers behind — a user who
        // hasn't turned the feature on never has sightings silently adopted
        // from someone else's client either.
        if (!_labels.ResponsesEnabled) return;
        if (e.Channel is not (ChatChannel.TelepathIncoming or ChatChannel.Gangpath or ChatChannel.Local))
            return;
        if (e.Speaker is not { Length: > 0 } sender) return;
        if (!TryParse(e.Message, out int index, out int count, out string blob)) return;

        if (!_partial.TryGetValue(sender, out Partial? p) || p.N != count)
        {
            p = new Partial { N = count, Chunks = new string?[count] };
            _partial[sender] = p;
        }
        if (p.Done) return;                 // already merged this sender's set
        p.Chunks[index - 1] = blob;
        _log?.Debug("RoombaSync", $"received line {index}/{count} from {sender} ({blob.Length} chars)");
        if (p.Chunks.Any(c => c is null)) return;   // still missing pieces

        p.Done = true;
        IReadOnlyList<GhItemSyncRecord> records;
        try
        {
            records = GhItemSyncCodec.Decode(string.Concat(p.Chunks));
        }
        catch (FormatException ex)
        {
            _log?.Warn("RoombaSync", $"discarding malformed roomba-sync payload from {sender}: {ex.Message}");
            return;
        }

        int applied = _locations.MergeSyncRecords(records);
        _log?.Info("RoombaSync",
            $"merged {applied}/{records.Count} item sighting(s) from {sender}'s @roomba sync");
    }

    // Parse "@roombadata <i>/<n> <blob>", tolerating the {} the remote reply path
    // wraps a reply in. The blob is a single spaceless base64url token, so a
    // 3-way split keeps it whole.
    private static bool TryParse(string message, out int index, out int count, out string blob)
    {
        index = count = 0; blob = string.Empty;
        string body = message.Trim();
        if (body.Length >= 2 && body[0] == '{' && body[^1] == '}') body = body[1..^1].Trim();

        string[] parts = body.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3) return false;
        if (!parts[0].Equals(RoombaQueryHandler.SyncResponseToken, StringComparison.OrdinalIgnoreCase)) return false;

        string[] frac = parts[1].Split('/');
        if (frac.Length != 2 || !int.TryParse(frac[0], out index) || !int.TryParse(frac[1], out count)) return false;
        if (index < 1 || count < 1 || index > count) return false;
        blob = parts[2];
        return true;
    }
}
