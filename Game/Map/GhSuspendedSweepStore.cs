using System.Collections.Generic;
using System.Linq;
using MudPlay.Models.Profile;
using MudPlay.Services;

namespace MudPlay.Game.Map;

// The unfinished work of a Roomba sweep that stopped early, persisted per
// character so the next one can carry on rather than start from nothing.
//
// It covers the two things a dead sweep otherwise takes with it:
//
//   * what it was still CARRYING — those items are in the player's pack and only
//     this queue knew where each belonged, so without it the player is left
//     sorting a couple of dozen things by hand;
//   * what it had still PLANNED — the survey behind it costs a full lap of the
//     circuit to rebuild, which in a 120-room house is most of a sweep.
//
// Persisted rather than kept in memory because the point is to survive whatever
// ended the sweep, and that includes closing the client. Nothing here is trusted
// on restore: the player may have moved, sold or worn any of it in between, so
// carried entries are checked against a real inventory read and planned ones are
// re-verified the ordinary way (a get that fails drops its move).
public sealed class GhSuspendedSweepStore
{
    private const string LogCategory = "GhSweep";

    private readonly ProfileService _profile;
    private readonly LogService? _log;

    public GhSuspendedSweepStore(ProfileService profile, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _profile = profile;
        _log = log;
    }

    // Everything the last sweep left unfinished, in its original queue order.
    public IReadOnlyList<GhSuspendedMove> Load()
        => _profile.Current?.GhUnfinishedSweep is { Count: > 0 } moves
            ? moves.ToList()
            : Array.Empty<GhSuspendedMove>();

    public bool Any => _profile.Current?.GhUnfinishedSweep is { Count: > 0 };

    // Items still in the pack — the half a fresh Start also has to honour, since
    // they're the player's property sitting in their inventory either way.
    public IReadOnlyList<GhSuspendedMove> LoadCarried()
        => Load().Where(m => m.Carried).ToList();

    // Record what's left. Called on every sweep end, normal or not: a clean finish
    // saves an empty list, which is what clears a previous one.
    public void Save(IEnumerable<GhSuspendedMove> unfinished)
    {
        ArgumentNullException.ThrowIfNull(unfinished);
        if (_profile.Current is not { } current) return;

        List<GhSuspendedMove> moves = unfinished.ToList();
        bool had = current.GhUnfinishedSweep is { Count: > 0 };
        if (moves.Count == 0 && !had) return;   // nothing to write, nothing to clear

        current.GhUnfinishedSweep = moves.Count > 0 ? moves : null;
        _profile.Save();

        if (moves.Count > 0)
        {
            int carried = moves.Count(m => m.Carried);
            _log?.Info(LogCategory,
                $"remembered {moves.Count} unfinished move(s) ({carried} still in the pack) — "
                + "Resume picks them up without re-scanning");
        }
        else if (had)
        {
            _log?.Info(LogCategory, "unfinished-sweep record cleared — nothing left over");
        }
    }

    public void Clear() => Save(Array.Empty<GhSuspendedMove>());
}
