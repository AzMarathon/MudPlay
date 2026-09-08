using System.Collections.Generic;
using System.Linq;
using MudPlay.Game.Inventory;
using MudPlay.Models.Profile;
using MudPlay.Services;

namespace MudPlay.Game.Map;

// Pure sort-queue decision logic, split out of GhSweepManager so it's
// testable without the engine's LoopRunner/RoomTracker/MessageRouter
// wiring. Takes exactly what recon observed (never anything carried) and
// what's labeled, and decides what moves where.
public static class GhSortQueueBuilder
{
    // wouldAutoDiscard: true for an item the auto-discard engine is configured to
    // throw away. Those are skipped outright — collecting one means Roomba carries
    // it, auto-discard bins it mid-sweep, and next lap Roomba finds it on the floor
    // and collects it again. Worse, the binning leaves Roomba believing it still
    // holds the item, and the drop it later sends gets partial-matched by the game
    // onto a DIFFERENT held item. Not fighting over them at all is the fix.
    public static (IReadOnlyList<GhPendingMove> Moves, IReadOnlyList<GhSweepItemFound> LeftInPlace) Build(
        IReadOnlyDictionary<RoomKey, IReadOnlyList<string>> observedByRoom,
        IReadOnlyCollection<GhRoomLabel> labels,
        ItemNameStore itemNames,
        Func<string, bool>? wouldAutoDiscard = null)
    {
        List<GhPendingMove> moves = new();
        List<GhSweepItemFound> leftInPlace = new();

        Dictionary<RoomKey, GhRoomLabel> labelsByRoom = new();
        foreach (GhRoomLabel l in labels) labelsByRoom[new RoomKey(l.Map, l.Room)] = l;

        // Nothing is known full at plan time — the sweep hasn't tried a drop yet.
        // GhSweepManager re-resolves through the same function with its live
        // full-room set when a destination refuses.
        HashSet<RoomKey> nothingFull = new();

        foreach ((RoomKey room, IReadOnlyList<string> items) in observedByRoom)
        {
            labelsByRoom.TryGetValue(room, out GhRoomLabel? currentLabel);

            foreach (string entry in items)
            {
                GhItemClass? cls = GhItemClassifier.Classify(itemNames, entry);
                if (cls is null) continue;   // unresolvable — shouldn't occur, cash is pre-filtered

                if (GhItemClassifier.IsGuardEmblem(entry))
                {
                    leftInPlace.Add(new GhSweepItemFound(room, entry, GhLeftReason.NoMatchingRoom));
                    continue;
                }

                if (wouldAutoDiscard?.Invoke(entry) == true)
                {
                    leftInPlace.Add(new GhSweepItemFound(room, entry, GhLeftReason.AutoDiscarded));
                    continue;
                }

                if (currentLabel is not null
                    && GhItemClassifier.MatchesAny(currentLabel, cls.Value)) continue;   // already correct room

                // First matching label, else the catch-all, else nowhere. Already
                // sitting in the room it resolves to counts as correctly placed.
                if (GhDestinationResolver.Resolve(cls.Value, labels, nothingFull) is not { } dest)
                {
                    leftInPlace.Add(new GhSweepItemFound(room, entry, GhLeftReason.NoMatchingRoom));
                    continue;
                }
                if (dest.Equals(room)) continue;

                (int count, string singularName) = CountedCommand.SplitLeadingCount(entry);
                string canonicalName = itemNames.FindByName(entry) is int number
                    ? itemNames.GetName(number) ?? singularName
                    : singularName;

                moves.Add(new GhPendingMove(room, dest, canonicalName, count));
            }
        }

        return (moves, leftInPlace);
    }
}
