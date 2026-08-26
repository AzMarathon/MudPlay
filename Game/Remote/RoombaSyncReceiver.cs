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
// no merge-review window and adopts a reply only from a sender we've granted the
// "Query Roomba" permission (isSenderGranted) — the same per-player gate that
// governs answering their @roomba, so there's no separate opt-in.
public sealed class RoombaSyncReceiver : IDisposable
{
    private readonly ChatRouter _chat;
    private readonly GhItemLocationStore _locations;
    private readonly GhRoomLabelStore _labels;
    private readonly Func<string, bool> _isSenderGranted;
    private readonly LogService? _log;
    private bool _disposed;

    // isSenderGranted: whether this client has granted the named sender the
    // per-player "Query Roomba" permission. A sync reply is adopted only from a
    // sender we'd answer ourselves — same grant, both directions — so a stranger
    // can't push sightings/labels into our log. Defaults to "deny everyone" when
    // unwired (tests that don't care about the gate pass their own).
    public RoombaSyncReceiver(ChatRouter chat, GhItemLocationStore locations, GhRoomLabelStore labels,
        Func<string, bool>? isSenderGranted = null, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(chat);
        ArgumentNullException.ThrowIfNull(locations);
        ArgumentNullException.ThrowIfNull(labels);
        _chat = chat;
        _locations = locations;
        _labels = labels;
        _isSenderGranted = isSenderGranted ?? (_ => false);
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
        if (e.Channel is not (ChatChannel.TelepathIncoming or ChatChannel.Gangpath or ChatChannel.Local))
            return;
        if (e.Speaker is not { Length: > 0 } sender) return;
        if (!TryParse(e.Message, out string blob)) return;   // cheap: an @roombadata line at all?
        // Adopt only from a sender we've granted "Query Roomba" — the same
        // per-player permission that gates answering their @roomba, so there's no
        // separate opt-in and a stranger can't inject data into our log.
        if (!_isSenderGranted(sender)) return;

        try
        {
            // The same @roombadata stream carries both room-label lines and item
            // sighting lines; route on the leading byte the codec exposes.
            if (GhItemSyncCodec.IsLabelLine(blob))
            {
                IReadOnlyList<Models.Profile.GhRoomLabel> labels = GhItemSyncCodec.DecodeLabelLine(blob);
                int adopted = _labels.MergeSyncLabels(labels);
                if (adopted > 0)
                    _log?.Info("RoombaSync",
                        $"adopted {adopted} gang-house room label(s) from {sender}'s @roomba sync");
            }
            else
            {
                IReadOnlyList<GhItemSyncRecord> records = GhItemSyncCodec.DecodeLine(blob);
                int applied = _locations.MergeSyncRecords(records);
                if (applied > 0)
                    _log?.Info("RoombaSync",
                        $"merged {applied}/{records.Count} item sighting(s) from {sender}'s @roomba sync");
            }
        }
        catch (FormatException ex)
        {
            _log?.Warn("RoombaSync", $"discarding malformed roomba-sync line from {sender}: {ex.Message}");
        }
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
