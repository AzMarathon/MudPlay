using System.Collections.Generic;

namespace FujinTerm.Game.Map;

// Canned solve scripts for the Great Pyramid puzzle climb (see GAME_MECHANICS.md
// "Great Pyramid puzzle climb"). The climb is NOT graph-routable — floors are
// disconnected clusters joined only by sphinx `remoteaction` teleports the graph
// builder never synthesises — so each floor is a fixed move+command script,
// validated move-for-move against the Paradigm 1.9.1 game data and a live
// follower capture. Every pyramid room is named "Great Pyramid", so the room
// NUMBER is the sole floor identity.
//
// Layout is static across BBSes (the puzzle is unmodified everywhere), so the
// scripts can be canned. The solver plays them from the current floor up to the
// target (12/2085); it stops there — the `e` sphinx into the Tomb, Pharaoh
// Rastep, and the Dao Lord are player-handled.

public enum PyramidFloor { None, Firepit, F1, F2, F3, F4, F5, Top }

public enum PyramidStepKind
{
    Move,        // plain cardinal move
    PushBlock,   // send "push block" (opens a remote F1 gate); no move follows
    Door,        // F3 door: walk when open, else bash (Bashable) or wait for the timer, then move
    KeyDoor,     // F3 golden-lion-key door: ensure the key, unlock/open, then move
    AskSphinx,   // send "ask sphinx <Word>", await the ceiling-opens broadcast, then move up
}

// One scripted step. Dir is the travel direction (ignored for PushBlock).
// Word is the sphinx keyword (AskSphinx only). Bashable distinguishes an F3
// door you can bash from one you must wait out (the 1000-picklock doors).
public readonly record struct PyramidStep(
    PyramidStepKind Kind,
    Direction Dir = Direction.N,
    string? Word = null,
    bool Bashable = false);

public static class PyramidScript
{
    public const int PyramidMap = 12;
    public const int TargetRoom = 2085;     // solver's terminal — the top room

    // Floor-1 pre-flight timer budget: ~126 moves + 6 actions must finish within
    // 5 min of the first firepit `up` or the party scatters. Used by the
    // pre-flight feasibility gate (see PyramidSolver).
    public const int Floor1MoveCount = 126;
    public const int Floor1ActionCount = 6;          // 5 push-blocks + ask sphinx fire
    public const int ActionMillis = 250;             // ~ per non-move action
    public static readonly System.TimeSpan Floor1Budget = System.TimeSpan.FromMinutes(5);

    // Firepit / Scorched Cavern landing range — a chance-cast scatter drops a
    // failed climber into a random room here (12/1239-1278). Plus the desert
    // secondary 12/335. Landing in either mid-climb means the climb failed.
    public const int FirepitLow = 1239;
    public const int FirepitHigh = 1278;
    public const int DesertScatterRoom = 335;

    public static bool IsScatterRoom(int map, int room)
        => map == PyramidMap && ((room >= FirepitLow && room <= FirepitHigh) || room == DesertScatterRoom);

    // Which floor a (map, room) sits on. None for anything outside the pyramid.
    public static PyramidFloor FloorOf(int map, int room)
    {
        if (map != PyramidMap) return PyramidFloor.None;
        return room switch
        {
            1239 => PyramidFloor.Firepit,
            >= 1800 and <= 1920 => PyramidFloor.F1,
            >= 1921 and <= 2001 => PyramidFloor.F2,
            >= 2002 and <= 2051 => PyramidFloor.F3,
            >= 2052 and <= 2076 => PyramidFloor.F4,
            >= 2077 and <= 2084 => PyramidFloor.F5,
            2085 => PyramidFloor.Top,
            _ => PyramidFloor.None,
        };
    }

    // Floors climbed blind/fast with no per-step confirmation: F1 is timed (must
    // sprint) and F2's room spells deal escalating damage the longer you dwell.
    public static bool IsBlindFast(PyramidFloor floor)
        => floor is PyramidFloor.F1 or PyramidFloor.F2;

    // The canned scripts, in the source form of the hand-drawn map. A bare cardinal
    // is a Move (or, on F3, a bashable Door); `PB` = push block; `W<dir>` = an F3
    // wait-for-timer door (1000 picklocks, unbashable); `K<dir>` = the F3
    // golden-lion-key door; `sphinx:<word>` = ask the sphinx then ascend.
    private const string F1Raw =
        "s,w,n,n,n,e,s,e,PB,w,n,w,s,s,s,e,n,e,s,e,n,n,n,n,e,n,PB,s,w,s,w,n,n,w,n,w,n,n,n,n,e,e,PB," +
        "w,w,s,s,e,s,e,s,e,n,n,w,n,e,n,e,e,e,e,e,s,e,n,e,s,s,w,w,w,n,w,w,s,w,s,s,e,n,e,e,e,PB," +
        "w,w,w,s,w,n,n,e,n,e,e,s,e,e,s,s,e,s,s,s,s,s,s,w,w,n,e,n,n,w,s,w,PB," +
        "e,n,e,n,w,n,n,w,w,s,w,n,sphinx:fire";

    private const string F2Raw =
        "s,e,s,e,e,n,w,n,n,n,e,n,e,n,w,w,w,w,s,w,n,w,w,w,s,s,s,e,e,s,s,w,n,sphinx:sun";

    private const string F3Raw =
        "e,e,Wn,w,w,n,n,e,e,e,We,e,e,Ws,w,s,s,w,w,e,Ws,Kw,w,s,e,sphinx:stars";

    private const string F4Raw =
        "w,w,n,n,n,n,e,e,s,w,s,s,e,s,e,e,n,n,n,w,s,u";

    private const string F5Raw =
        "n,w,w,s,e";

    private static readonly Dictionary<PyramidFloor, IReadOnlyList<PyramidStep>> _steps = new()
    {
        [PyramidFloor.F1] = Parse(PyramidFloor.F1, F1Raw),
        [PyramidFloor.F2] = Parse(PyramidFloor.F2, F2Raw),
        [PyramidFloor.F3] = Parse(PyramidFloor.F3, F3Raw),
        [PyramidFloor.F4] = Parse(PyramidFloor.F4, F4Raw),
        [PyramidFloor.F5] = Parse(PyramidFloor.F5, F5Raw),
    };

    // The step list to play on a given floor (empty for Firepit/Top/None — the
    // firepit's only move is the entry `up`, and Top is the terminal).
    public static IReadOnlyList<PyramidStep> Steps(PyramidFloor floor)
        => _steps.TryGetValue(floor, out IReadOnlyList<PyramidStep>? s) ? s : System.Array.Empty<PyramidStep>();

    private static IReadOnlyList<PyramidStep> Parse(PyramidFloor floor, string raw)
    {
        var steps = new List<PyramidStep>();
        foreach (string tokRaw in raw.Split(',', System.StringSplitOptions.RemoveEmptyEntries | System.StringSplitOptions.TrimEntries))
        {
            string tok = tokRaw;
            if (tok == "PB") { steps.Add(new PyramidStep(PyramidStepKind.PushBlock)); continue; }
            if (tok.StartsWith("sphinx:", System.StringComparison.Ordinal))
            {
                steps.Add(new PyramidStep(PyramidStepKind.AskSphinx, Direction.U, Word: tok["sphinx:".Length..]));
                continue;
            }
            if (tok.Length == 2 && tok[0] == 'W')   // wait-door: unbashable, wait for the timer
            {
                steps.Add(new PyramidStep(PyramidStepKind.Door, ParseDir(tok[1]), Bashable: false));
                continue;
            }
            if (tok.Length == 2 && tok[0] == 'K')   // golden-lion-key door
            {
                steps.Add(new PyramidStep(PyramidStepKind.KeyDoor, ParseDir(tok[1])));
                continue;
            }
            Direction dir = ParseDir(tok[0]);
            // On F3 every exit is a door; a bare token is a bashable one.
            steps.Add(floor == PyramidFloor.F3
                ? new PyramidStep(PyramidStepKind.Door, dir, Bashable: true)
                : new PyramidStep(PyramidStepKind.Move, dir));
        }
        return steps;
    }

    private static Direction ParseDir(char c) => c switch
    {
        'n' => Direction.N,
        's' => Direction.S,
        'e' => Direction.E,
        'w' => Direction.W,
        'u' => Direction.U,
        'd' => Direction.D,
        _ => throw new System.ArgumentException($"pyramid script: unknown direction '{c}'"),
    };
}
