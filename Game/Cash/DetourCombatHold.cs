using MudPlay.Game.Map;
using MudPlay.Models.Profile;

namespace MudPlay.Game.Cash;

// Decides when an errand detour holds Auto-Combat off (Settings → Cash + Items, "No
// combat during an auto-sell detour / auto-deposit trip"). The hold applies only once
// the detour has walked out of the loop it will resume: inside the loop's rooms the
// grind carries on (user, 2026-09-29; report paradigm-20260929-224330, where a deposit
// trip starting mid-loop walked past three monsters). A detour from Auto-Lair or a
// walk-to has no fixed area, so its hold covers the whole trip.
//
// It only decides. The main window flips the real Auto-Combat toggle on HoldChanged,
// so everything Auto-Combat-off already does applies: a rest still fights to clear the
// room, and the toolbar shows the state.
public sealed class DetourCombatHold
{
    private readonly Func<bool> _selling;
    private readonly Func<DetourResume> _sellResume;
    private readonly Func<bool> _depositing;
    private readonly Func<DetourResume> _depositResume;
    private readonly Func<CashSettings> _readCash;
    private readonly Func<Loop, IReadOnlyCollection<RoomKey>> _loopRooms;
    private readonly Func<RoomKey?> _currentRoom;
    private readonly Services.LogService? _log;

    // The loop whose rooms are cached, and the cache — resolved once per detour.
    private Loop? _areaLoop;
    private HashSet<RoomKey> _area = new();

    // Why the hold is on ("auto-deposit trip"), or null when it's off.
    public string? HeldFor { get; private set; }

    // Raised when the hold turns on (true) or off (false).
    public event Action<bool>? HoldChanged;

    public DetourCombatHold(
        Func<bool> selling, Func<DetourResume> sellResume,
        Func<bool> depositing, Func<DetourResume> depositResume,
        Func<CashSettings> readCash,
        Func<Loop, IReadOnlyCollection<RoomKey>> loopRooms,
        Func<RoomKey?> currentRoom,
        Services.LogService? log = null)
    {
        _selling = selling;
        _sellResume = sellResume;
        _depositing = depositing;
        _depositResume = depositResume;
        _readCash = readCash;
        _loopRooms = loopRooms;
        _currentRoom = currentRoom;
        _log = log;
    }

    // Re-decide: on every confirmed room and on the heartbeat, which catches a
    // detour ending without a move (stopped by hand).
    public void Evaluate()
    {
        string? want = Decide();
        if (want == HeldFor) return;
        bool wasHeld = HeldFor is not null;
        HeldFor = want;
        if (want is not null)
            _log?.Info("Combat", $"Auto-Combat off for the {want} — outside the loop's rooms (Settings → Cash + Items)");
        else
            _log?.Info("Combat", "Auto-Combat back on — the detour's over or back in the loop's rooms");
        if (wasHeld != (want is not null)) HoldChanged?.Invoke(want is not null);
    }

    private string? Decide()
    {
        bool selling = _selling();
        bool depositing = _depositing();
        if (!selling && !depositing)
        {
            _areaLoop = null;
            return null;
        }
        CashSettings cash = _readCash();
        (string what, DetourResume resume) = selling && cash.NoCombatOnSellDetour
            ? ("auto-sell detour", _sellResume())
            : depositing && cash.NoCombatOnDepositTrip
                ? ("auto-deposit trip", _depositResume())
                : (string.Empty, default);
        if (what.Length == 0) return null;
        return InsideLoopArea(resume) ? null : what;
    }

    private bool InsideLoopArea(DetourResume resume)
    {
        if (resume.Kind != DetourResumeKind.Loop || resume.Loop is not { } loop) return false;
        if (!ReferenceEquals(loop, _areaLoop))
        {
            _areaLoop = loop;
            _area = new HashSet<RoomKey>(_loopRooms(loop));
        }
        return _currentRoom() is { } here && _area.Contains(here);
    }
}
