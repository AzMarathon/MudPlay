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
    public static (IReadOnlyList<GhPendingMove> Moves, IReadOnlyList<GhSweepItemFound> LeftInPlace) Build(
        IReadOnlyDictionary<RoomKey, IReadOnlyList<string>> observedByRoom,
        IReadOnlyCollection<GhRoomLabel> labels,
        ItemNameStore itemNames)
    {
        List<GhPendingMove> moves = new();
        List<GhSweepItemFound> leftInPlace = new();

        Dictionary<RoomKey, GhRoomLabel> labelsByRoom = new();
        foreach (GhRoomLabel l in labels) labelsByRoom[new RoomKey(l.Map, l.Room)] = l;

        GhRoomLabel? catchAll = labels.FirstOrDefault(l => l.IsCatchAll);
        RoomKey? catchAllRoom = catchAll is { } ca ? new RoomKey(ca.Map, ca.Room) : null;

        foreach ((RoomKey room, IReadOnlyList<string> items) in observedByRoom)
        {
            labelsByRoom.TryGetValue(room, out GhRoomLabel? currentLabel);

            foreach (string entry in items)
            {
                GhItemClass? cls = GhItemClassifier.Classify(itemNames, entry);
                if (cls is null) continue;   // unresolvable — shouldn't occur, cash is pre-filtered

                if (GhItemClassifier.IsGuardEmblem(entry))
                {
                    leftInPlace.Add(new GhSweepItemFound(room, entry));
                    continue;
                }

                if (currentLabel is not null
                    && GhItemClassifier.MatchesAny(currentLabel, cls.Value)) continue;   // already correct room

                RoomKey? dest = null;
                foreach (GhRoomLabel candidate in labels)
                {
                    if (!GhItemClassifier.MatchesAny(candidate, cls.Value)) continue;
                    dest = new RoomKey(candidate.Map, candidate.Room);
                    break;
                }

                if (dest is null)
                {
                    // No explicit rule matched anywhere — fall back to the catch-all
                    // room, if one is designated. Already sitting in it counts as
                    // correctly placed (nothing to queue); no catch-all designated
                    // at all is the original "leave unmatched in place" behavior.
                    if (catchAllRoom is { } fallback)
                    {
                        if (fallback.Equals(room)) continue;
                        dest = fallback;
                    }
                    else
                    {
                        leftInPlace.Add(new GhSweepItemFound(room, entry));
                        continue;
                    }
                }

                (int count, string singularName) = CountedCommand.SplitLeadingCount(entry);
                string canonicalName = itemNames.FindByName(entry) is int number
                    ? itemNames.GetName(number) ?? singularName
                    : singularName;

                moves.Add(new GhPendingMove(room, dest.Value, canonicalName, count));
            }
        }

        return (moves, leftInPlace);
    }
}
