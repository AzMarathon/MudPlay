using System;
using System.Collections.Generic;
using MudPlay.Game.Map;
using MudPlay.Services;

namespace MudPlay.Game.Remote;

// Requester side of @roomba sync: scrapes the `@roombadata <blob>` reply lines
// off ChatRouter (ignored by RemoteCommandManager — see AppServices
// RegisterIgnored(RoombaQueryHandler.SyncResponseToken)), and merges each line's
// sightings straight into GhItemLocationStore as it arrives.
//
// Every line is SELF-CONTAINED (see GhItemSyncCodec): it decodes and merges on
// its own, with no cross-line reassembly. So a line the game's telepath
// flood-control dropped costs only the rooms it carried — the rest still land —
// instead of the old "one missing chunk discards the whole sync". There's also
// no meaningful "conflict" for a room-contents sighting (newest wins silently —
// see GhItemLocationStore.MergeSyncRecords), so this listens continuously with
// no merge-review window: opt in once via GhRoomLabelStore.ResponsesEnabled and
// everything else is automatic, the same way the rest of @roomba works.
public sealed class RoombaSyncReceiver : IDisposable
{
    private readonly ChatRouter _chat;
    private readonly GhItemLocationStore _locations;
    private readonly GhRoomLabelStore _labels;
    private readonly LogService? _log;
    private bool _disposed;

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

    internal void Ingest(ChatLogEntry e)
    {
        // Gate on the same opt-in @roomba itself answers behind — a user who
        // hasn't turned the feature on never has sightings silently adopted
        // from someone else's client either.
        if (!_labels.ResponsesEnabled) return;
        if (e.Channel is not (ChatChannel.TelepathIncoming or ChatChannel.Gangpath or ChatChannel.Local))
            return;
        if (e.Speaker is not { Length: > 0 } sender) return;
        if (!TryParse(e.Message, out string blob)) return;

        IReadOnlyList<GhItemSyncRecord> records;
        try
        {
            records = GhItemSyncCodec.DecodeLine(blob);
        }
        catch (FormatException ex)
        {
            _log?.Warn("RoombaSync", $"discarding malformed roomba-sync line from {sender}: {ex.Message}");
            return;
        }

        int applied = _locations.MergeSyncRecords(records);
        if (applied > 0)
            _log?.Info("RoombaSync",
                $"merged {applied}/{records.Count} item sighting(s) from {sender}'s @roomba sync");
    }

    // Parse "@roombadata <blob>", tolerating the {} the remote reply path wraps a
    // reply in. The blob is a single spaceless base64url token, so a 2-way split
    // keeps it whole. Each line is decoded on its own — there is no i/n fraction.
    private static bool TryParse(string message, out string blob)
    {
        blob = string.Empty;
        string body = message.Trim();
        if (body.Length >= 2 && body[0] == '{' && body[^1] == '}') body = body[1..^1].Trim();

        string[] parts = body.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return false;
        if (!parts[0].Equals(RoombaQueryHandler.SyncResponseToken, StringComparison.OrdinalIgnoreCase)) return false;
        blob = parts[1].Trim();
        return blob.Length > 0;
    }
}
