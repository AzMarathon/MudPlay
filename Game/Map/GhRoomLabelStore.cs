using System.Collections.Generic;
using System.Linq;
using MudPlay.Models.Profile;
using MudPlay.Services;

namespace MudPlay.Game.Map;

// Per-BBS gang-house (GH) room label set for Roomba Mode, persisted to
// Data/BBS/{bbs}/roomba.json. A BBS ties to one game-data set and every
// character on it shares the same gang house, so labels + the sweep-tuning
// knobs are board-wide: label a room once on any character and every other
// character on that BBS sees it too. Mirrors RoomBlacklistStore's per-BBS
// load/persist shape (OnBbsPinApplied + a Changed event), not
// SettingsResolver's tiered-delta merge — this is a single BBS-scoped file,
// not a per-tab settings override.
public sealed class GhRoomLabelStore
{
    // Default when RoombaSettings.SearchesPerRoom is unset.
    public const int DefaultSearchesPerRoom = 3;

    private readonly ProfileService _profile;
    private readonly LogService? _log;
    private string? _activeBbs;
    private RoombaSettings _settings = new();
    private readonly Dictionary<RoomKey, GhRoomLabel> _labels = new();

    // Fires after every mutation, including a BBS-pin reload.
    public event Action? Changed;

    // profile is retained only to read (and, once, clear) the legacy
    // per-character GH fields during the one-time migration below — the store's
    // own state is otherwise entirely BBS-scoped.
    public GhRoomLabelStore(ProfileService profile, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _profile = profile;
        _log = log;
    }

    // Read-only snapshot of the currently-labeled rooms.
    public IReadOnlyCollection<GhRoomLabel> Labels => _labels.Values;

    // How many times each room on the expanded circuit is searched per recon visit
    // (only used when hidden-item search is on). Unset reads as the default. Recon
    // always walks the circuit exactly once — there is no lap-count setting.
    public int SearchesPerRoom => Math.Max(1, _settings.SearchesPerRoom ?? DefaultSearchesPerRoom);

    // Whether recon searches (`sea`) each room for hidden items. Off by default:
    // Roomba sorts only the visible floor unless the user opts in.
    public bool SearchForHidden => _settings.SearchForHidden ?? false;

    // Whether @roomba <item> replies with the item's last known room. Off by
    // default — opt-in per BBS.
    public bool ResponsesEnabled => _settings.ResponsesEnabled;

    public void SetSearchesPerRoom(int count)
    {
        if (_activeBbs is null) return;
        _settings.SearchesPerRoom = Math.Max(1, count);
        Persist();
        _log?.Info("GhSweep", $"searches per room set to {_settings.SearchesPerRoom}");
        Changed?.Invoke();
    }

    public void SetSearchForHidden(bool on)
    {
        if (_activeBbs is null) return;
        _settings.SearchForHidden = on;
        Persist();
        _log?.Info("GhSweep", $"search for hidden items {(on ? "enabled" : "disabled")}");
        Changed?.Invoke();
    }

    public void SetResponsesEnabled(bool on)
    {
        if (_activeBbs is null) return;
        _settings.ResponsesEnabled = on;
        Persist();
        _log?.Info("GhSweep", $"@roomba responses {(on ? "enabled" : "disabled")}");
        Changed?.Invoke();
    }

    public bool TryGetLabel(RoomKey key, out GhRoomLabel label)
        => _labels.TryGetValue(key, out label!);

    // Set (or replace) key's label. When isCatchAll is true, any other
    // labeled room's catch-all flag is cleared first — at most one room owns
    // the flag at a time (single-owner, radio-button semantics). Persists
    // immediately.
    public void SetLabel(RoomKey key, IReadOnlyList<GhCategoryRule> rules, bool isCatchAll)
    {
        if (_activeBbs is null) return;

        if (isCatchAll)
            foreach (GhRoomLabel other in _labels.Values)
                other.IsCatchAll = false;   // same instances _settings.RoomLabels holds — one mutation covers both

        GhRoomLabel label = new(key.Map, key.Room) { Rules = rules.ToList(), IsCatchAll = isCatchAll };
        _labels[key] = label;

        _settings.RoomLabels ??= new List<GhRoomLabel>();
        _settings.RoomLabels.RemoveAll(l => l.Map == key.Map && l.Room == key.Room);
        _settings.RoomLabels.Add(label);
        Persist();
        _log?.Info("GhSweep",
            $"labeled {key}: {rules.Count} rule(s){(isCatchAll ? " [catch-all]" : "")}");
        Changed?.Invoke();
    }

    // Clear key's label. Persists immediately.
    public void ClearLabel(RoomKey key)
    {
        if (_activeBbs is null) return;
        if (!_labels.Remove(key)) return;

        _settings.RoomLabels?.RemoveAll(l => l.Map == key.Map && l.Room == key.Room);
        Persist();
        _log?.Info("GhSweep", $"cleared label {key}");
        Changed?.Invoke();
    }

    // Load the Roomba settings for the active BBS. Called by AppServices on
    // ProfileService.ProfileLoaded / BbsPinApplied with the resolved active BBS
    // name; resets the in-memory store when the pin clears (bbs is null / blank).
    public void OnBbsPinApplied(string? bbs)
    {
        if (string.IsNullOrWhiteSpace(bbs))
        {
            if (_activeBbs is not null)
            {
                _activeBbs = null;
                _settings = new RoombaSettings();
                _labels.Clear();
                Changed?.Invoke();
            }
            return;
        }

        _activeBbs = bbs;
        _settings = JsonStore.Load<RoombaSettings>(AppPaths.BbsRoombaFile(bbs)) ?? new RoombaSettings();
        MigrateLegacyCharacterData();
        RebuildLabelIndex();
        Changed?.Invoke();
    }

    // One-time lift of a pre-upgrade character's GH data into this BBS's
    // roomba.json: only runs when the BBS file is still empty AND the currently
    // loaded character profile still carries the legacy fields. Clears the
    // character-tier copy afterward (and saves) so this never re-fires and the
    // old data doesn't linger duplicated in two places.
    private void MigrateLegacyCharacterData()
    {
        if (_settings.RoomLabels is { Count: > 0 }) return;   // BBS file already has real data
        if (_profile.Current is not { } current) return;
        if (current.GhRoomLabels is not { Count: > 0 } legacyLabels) return;

        _settings.RoomLabels = legacyLabels;
        _settings.SearchesPerRoom = current.GhSearchesPerRoom;
        _settings.SearchForHidden = current.GhSearchForHidden;
        Persist();

        current.GhRoomLabels = null;
        current.GhSearchesPerRoom = null;
        current.GhSearchForHidden = null;
        _profile.Save();

        _log?.Info("GhSweep",
            $"migrated {legacyLabels.Count} legacy per-character GH room label(s) to BBS '{_activeBbs}'");
    }

    private void RebuildLabelIndex()
    {
        _labels.Clear();
        if (_settings.RoomLabels is { } list)
            foreach (GhRoomLabel l in list) _labels[new RoomKey(l.Map, l.Room)] = l;
    }

    private void Persist()
    {
        if (_activeBbs is null) return;
        JsonStore.Save(AppPaths.BbsRoombaFile(_activeBbs), _settings);
    }
}
