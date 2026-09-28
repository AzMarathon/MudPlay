using System;
using System.Collections.Generic;
using MudPlay.Game.Map;

namespace MudPlay.Services;

// Two routes the Navigation map draws on top of everything for a side-by-side
// comparison — today the MegaMUD .mp import review: the recording's moves followed
// literally on our map, and the loop as we've converted it. The review window fills
// it; the Navigation window binds to it. Everything runs on the UI thread.
public sealed class MapComparisonOverlay
{
    public IReadOnlyList<RoomKey>? RecordedPath { get; private set; }
    public IReadOnlyList<RoomKey>? ConvertedPath { get; private set; }
    // Rooms where a recorded step couldn't be followed on our map.
    public IReadOnlyList<RoomKey>? StuckRooms { get; private set; }

    public bool IsShowing => RecordedPath is not null || ConvertedPath is not null;

    public event Action? Changed;

    public void Show(IReadOnlyList<RoomKey> recorded, IReadOnlyList<RoomKey> converted, IReadOnlyList<RoomKey> stuck)
    {
        RecordedPath = recorded;
        ConvertedPath = converted;
        StuckRooms = stuck;
        Changed?.Invoke();
    }

    public void Clear()
    {
        if (!IsShowing) return;
        RecordedPath = null;
        ConvertedPath = null;
        StuckRooms = null;
        Changed?.Invoke();
    }
}
