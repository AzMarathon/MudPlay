using System.Collections.Generic;
using System.Linq;
using MudPlay.Models.Profile;
using MudPlay.Services;

namespace MudPlay.Game.Map;

// Per-character gang-house (GH) room label set for Roomba Mode. Mirrors
// MovementFilter's stash-room surface (MarkStash/UnmarkStash/IsStash) but
// keyed to a richer value (a list of sort rules, not just membership) since a
// label carries the room's categories, not just a boolean flag. Lives on
// CharacterProfile.GhRoomLabels — a single active layout, same tier as
// StashRooms.
public sealed class GhRoomLabelStore
{
    // Default when CharacterProfile.GhSearchesPerRoom is unset.
    public const int DefaultSearchesPerRoom = 3;

    private readonly ProfileService _profile;
    private readonly LogService? _log;
    private readonly Dictionary<RoomKey, GhRoomLabel> _labels = new();

    // Fires after every mutation, including profile reload.
    public event Action? Changed;

    public GhRoomLabelStore(ProfileService profile, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _profile = profile;
        _log = log;

        _profile.ProfileLoaded += OnProfileLoaded;
        _profile.ProfileClosed += OnProfileClosed;

        if (_profile.Current is { } current) OnProfileLoaded(current);
    }

    // Read-only snapshot of the currently-labeled rooms.
    public IReadOnlyCollection<GhRoomLabel> Labels => _labels.Values;

    // How many times each room on the expanded circuit is searched per recon visit
    // (only used when hidden-item search is on). Backed by CharacterProfile so a
    // retune persists per-character; unset reads as the default. Recon always walks
    // the circuit exactly once — there is no lap-count setting.
    public int SearchesPerRoom => Math.Max(1, _profile.Current?.GhSearchesPerRoom ?? DefaultSearchesPerRoom);

    // Whether recon searches (`sea`) each room for hidden items. Off by default:
    // Roomba sorts only the visible floor unless the user opts in.
    public bool SearchForHidden => _profile.Current?.GhSearchForHidden ?? false;

    public void SetSearchesPerRoom(int count)
    {
        if (_profile.Current is not { } current) return;
        current.GhSearchesPerRoom = Math.Max(1, count);
        _profile.Save();
        _log?.Info("GhSweep", $"searches per room set to {current.GhSearchesPerRoom}");
        Changed?.Invoke();
    }

    public void SetSearchForHidden(bool on)
    {
        if (_profile.Current is not { } current) return;
        current.GhSearchForHidden = on;
        _profile.Save();
        _log?.Info("GhSweep", $"search for hidden items {(on ? "enabled" : "disabled")}");
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
        if (_profile.Current is not { } current) return;

        if (isCatchAll)
            foreach (GhRoomLabel other in _labels.Values)
                other.IsCatchAll = false;   // same instances current.GhRoomLabels holds — one mutation covers both

        GhRoomLabel label = new(key.Map, key.Room) { Rules = rules.ToList(), IsCatchAll = isCatchAll };
        _labels[key] = label;

        current.GhRoomLabels ??= new List<GhRoomLabel>();
        current.GhRoomLabels.RemoveAll(l => l.Map == key.Map && l.Room == key.Room);
        current.GhRoomLabels.Add(label);
        _profile.Save();
        _log?.Info("GhSweep",
            $"labeled {key}: {rules.Count} rule(s){(isCatchAll ? " [catch-all]" : "")}");
        Changed?.Invoke();
    }

    // Clear key's label. Persists immediately.
    public void ClearLabel(RoomKey key)
    {
        if (_profile.Current is not { } current) return;
        if (!_labels.Remove(key)) return;

        current.GhRoomLabels?.RemoveAll(l => l.Map == key.Map && l.Room == key.Room);
        _profile.Save();
        _log?.Info("GhSweep", $"cleared label {key}");
        Changed?.Invoke();
    }

    private void OnProfileLoaded(CharacterProfile profile)
    {
        _labels.Clear();
        if (profile.GhRoomLabels is { } list)
            foreach (GhRoomLabel l in list) _labels[new RoomKey(l.Map, l.Room)] = l;
        Changed?.Invoke();
    }

    private void OnProfileClosed()
    {
        bool had = _labels.Count > 0;
        _labels.Clear();
        if (had) Changed?.Invoke();
    }
}
