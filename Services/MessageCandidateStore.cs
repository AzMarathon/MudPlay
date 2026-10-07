using System.IO;
using MudPlay.Models.GameData;

namespace MudPlay.Services;

// In-memory cache of staged, unrecognized-message candidates for the active
// game-data set. Parallels MessageStore/MonsterMessageStore's load/save shape,
// but candidates are pure runtime-observed state rather than curated data —
// there's nothing to ship as a starting point, so unlike those two stores
// there is no universal-seed fallback: a missing per-set file just means an
// empty catalogue.
//
// Wiring: AppServices subscribes the store to GameDataCache.ActiveSetChanged —
// on every set switch the per-set file at AppPaths.MessageCandidatesFile is
// reloaded (missing file ⇒ empty). Game.MessageCandidateWatcher is the sole
// writer via RecordSighting; the Game Data Browser's Unrecognized Lines tab and the
// LogPane double-click flow both read/dismiss/remove through this store.
public sealed class MessageCandidateStore
{
    private readonly LogService? _log;

    // Live mirror of the active set's staged candidates. BulkObservableCollection
    // so a full (re)load raises one Reset instead of Clear + N Add, matching
    // MessageStore/MonsterMessageStore's rationale.
    public BulkObservableCollection<MessageCandidateRecord> Candidates { get; } = new();

    // The realm whose lines are loaded, or null when none. Unrecognized lines are
    // what the characters on a realm saw there, so they are the realm's: they were
    // kept with the game-data set, which two realms can share.
    public string? ActiveRealmFolder { get; private set; }

    // Every client on the realm records into the one file. What this client changed
    // since it last read or wrote the file is kept here, so that a write is the
    // file as the last character left it plus these changes, not this client's whole
    // copy over theirs.
    private readonly SharedFileStamp _stamp = new();
    private readonly HashSet<string> _changedHere = new(StringComparer.Ordinal);
    private readonly HashSet<string> _removedHere = new(StringComparer.Ordinal);

    private bool _saveQueued;

    public MessageCandidateStore() { }

    public MessageCandidateStore(LogService log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
    }

    // Load realmFolder's lines. legacySet names the game-data set the realm runs
    // on: a realm with no file of its own yet takes a copy of the one that set
    // carried, from when the lines were kept per set.
    public void Load(string? realmFolder, string? legacySet = null)
    {
        ActiveRealmFolder = string.IsNullOrWhiteSpace(realmFolder) ? null : realmFolder;
        _changedHere.Clear();
        _removedHere.Clear();
        if (ActiveRealmFolder is null) { Candidates.ReplaceAll([]); return; }

        string path = AppPaths.RealmMessageCandidatesFile(ActiveRealmFolder);
        AdoptLegacyFile(path, legacySet);
        _stamp.Mark(path);
        Candidates.ReplaceAll(TryLoad(path) ?? []);
    }

    private void AdoptLegacyFile(string realmFile, string? legacySet)
    {
        if (string.IsNullOrWhiteSpace(legacySet) || File.Exists(realmFile)) return;
        string legacy = AppPaths.LegacySetMessageCandidatesFile(legacySet);
        if (!File.Exists(legacy)) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(realmFile)!);
            File.Copy(legacy, realmFile);
        }
        catch (IOException)
        {
            // Another client on the realm copied it first — the file is where it belongs.
        }
    }

    // Re-read the lines when another client on the realm has written them, keeping
    // what this client changed since. Called on the heartbeat. True when re-read.
    public bool TakeInOutsideChanges()
    {
        if (ActiveRealmFolder is null) return false;
        string path = AppPaths.RealmMessageCandidatesFile(ActiveRealmFolder);
        return _stamp.ChangedOutside(path) && Rebase(path);
    }

    // Make the list the file's, with this client's unsaved changes on top: a line
    // both saw keeps the later sighting, the higher count and a dismissal from
    // either. False when the file is there but can't be read (the list is kept).
    private bool Rebase(string path)
    {
        List<MessageCandidateRecord>? onFile = File.Exists(path) ? TryLoad(path) : [];
        if (onFile is null) return false;

        Dictionary<string, MessageCandidateRecord> mine = new(StringComparer.Ordinal);
        foreach (MessageCandidateRecord c in Candidates)
            if (_changedHere.Contains(c.Id)) mine[c.Id] = c;

        List<MessageCandidateRecord> merged = new(onFile.Count + mine.Count);
        foreach (MessageCandidateRecord theirs in onFile)
        {
            if (_removedHere.Contains(theirs.Id)) continue;
            if (!mine.Remove(theirs.Id, out MessageCandidateRecord? ours)) { merged.Add(theirs); continue; }
            MessageCandidateRecord latest = theirs.LastSeenAt > ours.LastSeenAt ? theirs : ours;
            merged.Add(latest with
            {
                Occurrences = Math.Max(theirs.Occurrences, ours.Occurrences),
                Dismissed = theirs.Dismissed || ours.Dismissed,
            });
        }
        merged.AddRange(mine.Values);   // first seen here

        _stamp.Mark(path);
        Candidates.ReplaceAll(merged);
        return true;
    }

    // Parsed list (possibly empty) iff the file existed AND parsed cleanly;
    // null for missing/corrupt so Load falls back to an empty catalogue.
    private List<MessageCandidateRecord>? TryLoad(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            return JsonStore.Load<List<MessageCandidateRecord>>(path);
        }
        catch (Exception ex)
        {
            _log?.Log(LogSeverity.Warn, "MessageCandidates",
                $"Failed to load '{path}': {ex.Message}");
            return null;
        }
    }

    // Write the realm's file: what is on file now, plus this client's changes.
    public void Save()
    {
        if (ActiveRealmFolder is null) return;
        string path = AppPaths.RealmMessageCandidatesFile(ActiveRealmFolder);
        if (_stamp.ChangedOutside(path)) Rebase(path);
        JsonStore.Save(path, Candidates);
        _stamp.Mark(path);
        _changedHere.Clear();
        _removedHere.Clear();
    }

    // Insert-or-bump keyed by ComputeId(rawText). A dismissed record still gets
    // bumped on a repeat sighting — dismissal only stops re-alerting
    // (MessageCandidateWatcher's first-sighting log), it doesn't stop dedup
    // tracking, so a recurring line that was already dismissed as boring
    // doesn't quietly resurface and re-alert. Returns IsNew so the caller knows
    // whether to log a first-sighting Warn.
    // map/room tag the FIRST sighting's location (a locator hint) — a bump keeps the
    // original location rather than overwriting, so the record shows where the line
    // was first noticed even if it later recurs elsewhere.
    public (MessageCandidateRecord Record, bool IsNew) RecordSighting(
        string rawText, DateTimeOffset when, int? map = null, int? room = null)
    {
        string id = MessageCandidateRecord.ComputeId(rawText);
        for (int i = 0; i < Candidates.Count; i++)
        {
            if (Candidates[i].Id != id) continue;
            // A dismissed candidate is frozen — a recurrence neither bumps its
            // count nor re-saves (the watcher already gates on IsDismissed; this
            // keeps a direct call honest too).
            if (Candidates[i].Dismissed) return (Candidates[i], false);
            MessageCandidateRecord bumped = Candidates[i] with
            {
                LastSeenAt = when,
                Occurrences = Candidates[i].Occurrences + 1,
            };
            Candidates[i] = bumped;
            _changedHere.Add(id);
            QueueSave();
            return (bumped, false);
        }

        MessageCandidateRecord created = new(
            id, rawText, when, when, Occurrences: 1, Dismissed: false, Map: map, Room: room);
        Candidates.Add(created);
        _changedHere.Add(id);
        _removedHere.Remove(id);
        QueueSave();
        return (created, true);
    }

    // Dismiss — marks the record Dismissed and freezes it: the row stays in the
    // table (occurrence count frozen where it was) but the watcher then ignores
    // every future recurrence of that text entirely (IsDismissed gate) — no
    // re-add, no bump, no re-alert. A final "decided, stop tracking" verdict, as
    // distinct from Remove (hard delete, which lets a later recurrence re-capture
    // the line as new). No-op if id isn't found.
    public void Dismiss(string id)
    {
        for (int i = 0; i < Candidates.Count; i++)
        {
            if (Candidates[i].Id != id) continue;
            if (Candidates[i].Dismissed) return;
            Candidates[i] = Candidates[i] with { Dismissed = true };
            _changedHere.Add(id);
            QueueSave();
            return;
        }
    }

    // Cheap existence check by raw text, without mutating — lets
    // MessageCandidateWatcher's burst-cap gate tell "this repeats an
    // already-staged candidate" (always let through, dedup is free) apart
    // from "this would create a new one" (subject to the cap) without a
    // wasted insert-then-undo.
    public bool Contains(string rawText)
    {
        string id = MessageCandidateRecord.ComputeId(rawText);
        foreach (MessageCandidateRecord c in Candidates)
            if (c.Id == id) return true;
        return false;
    }

    // True when this exact text is already staged AND marked dismissed — the
    // watcher uses this to drop a recurrence of a dismissed line entirely (no
    // re-add, no occurrence bump, no re-alert): dismissal is a final "I've
    // decided about this line, stop tracking it" verdict.
    public bool IsDismissed(string rawText)
    {
        string id = MessageCandidateRecord.ComputeId(rawText);
        foreach (MessageCandidateRecord c in Candidates)
            if (c.Id == id) return c.Dismissed;
        return false;
    }

    // Hard removal — used once a candidate is successfully converted into a
    // real MessageRecord (it's real data now, no longer a candidate).
    public void Remove(string id)
    {
        for (int i = 0; i < Candidates.Count; i++)
        {
            if (Candidates[i].Id != id) continue;
            Candidates.RemoveAt(i);
            _changedHere.Remove(id);
            _removedHere.Add(id);
            QueueSave();
            return;
        }
    }

    private void QueueSave()
    {
        if (_saveQueued) return;
        _saveQueued = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(
            () => { _saveQueued = false; Save(); },
            Avalonia.Threading.DispatcherPriority.Background);
    }
}
