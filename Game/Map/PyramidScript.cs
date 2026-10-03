using System.Collections.Generic;

namespace MudPlay.Game.Map;

// Canned solve scripts for the Great Pyramid puzzle climb (see GAME_MECHANICS.md
// "Great Pyramid puzzle climb"). The climb is NOT graph-routable — floors are
// disconnected clusters joined only by sphinx `remoteaction` teleports the graph
// builder never synthesises — so each floor is a fixed move+command script,
// validated move-for-move against the stock and Paradigm game data. Every pyramid room is named "Great Pyramid", so the room
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
// door you can bash from one you must wait out (the 1000-picklock doors). Gate
// marks an F1 move through a gate that only the push block before it opens.
public readonly record struct PyramidStep(
    PyramidStepKind Kind,
    Direction Dir = Direction.N,
    string? Word = null,
    bool Bashable = false,
    bool Gate = false);

public static class PyramidScript
{
    public const int PyramidMap = 12;
    public const int TargetRoom = 2085;     // solver's terminal — the top room

    // Floor-1 pre-flight timer budget: 126 moves + 6 actions must finish within
    // 5 min of the first firepit `up` or the party scatters. The fire sphinx's answer
    // ends the timer, so the ascent after it isn't on the clock. Used by the
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

    // Floors climbed without stopping for fights or rests: F1 is timed (must
    // sprint) and F2's room spells deal escalating damage the longer you dwell.
    public static bool IsBlindFast(PyramidFloor floor)
        => floor is PyramidFloor.F1 or PyramidFloor.F2;

    // The canned scripts, in the source form of the hand-drawn map. A bare cardinal
    // is a Move (or, on F3, a bashable Door); `PB` = push block; `G<dir>` = the move
    // through the gate the last push block opened; `W<dir>` = an F3 wait-for-timer
    // door (1000 picklocks, unbashable); `K<dir>` = the F3 golden-lion-key door;
    // `sphinx:<word>` = ask the sphinx then ascend.
    // Floor 1 ends `s,s,w,n`: the map's own line drops one `s` there, and following it
    // stops a room short of the fire sphinx.
    private const string F1Raw =
        "s,w,n,n,n,e,s,e,PB," +
        "w,n,w,s,s,s,e,n,e,s,e,Gn,n,n,n,e,n,PB," +
        "s,w,s,w,n,n,Gw,n,w,n,n,n,n,e,e,PB," +
        "w,w,s,s,e,s,e,s,e,n,n,w,Gn,e,n,e,e,e,e,e,s,e,n,e,s,s,w,w,w,n,w,w,s,w,s,s,e,n,e,e,e,PB," +
        "w,w,w,s,w,n,n,e,n,e,e,s,e,e,Gs,s,e,s,s,s,s,s,s,w,w,n,e,n,n,w,s,w,PB," +
        "e,n,e,n,Gw,n,n,w,w,s,s,w,n,sphinx:fire";

    private const string F2Raw =
        "s,e,s,e,e,n,w,n,n,n,e,n,e,n,w,w,w,w,s,w,n,w,w,w,s,s,s,e,e,s,s,w,n,sphinx:sun";

    private const string F3Raw =
        "e,e,Wn,w,w,n,n,e,e,e,We,e,e,Ws,w,s,s,w,w,e,Ws,Kw,w,s,e,sphinx:stars";

    private const string F4Raw =
        "w,w,n,n,n,n,e,e,s,w,s,s,e,s,e,e,n,n,n,w,s,u";

    private const string F5Raw =
        "n,w,w,s,e";

    // The room each step is taken FROM, per floor and in script order — traced
    // move-for-move through the game data, identical on stock and Paradigm. A push
    // block sits at its block's room and a sphinx step at its sphinx's. Every pyramid
    // room renders as "Great Pyramid", so this is the only way to set the tracker's
    // room number against where the script thinks it is: a move that didn't land
    // otherwise leaves the solver a step out, walking into walls or bashing a
    // 1000-picklock door that isn't on its route. Rooms recur: F1 walks out to each
    // block and back, F3 steps into 2005 for the floating key and back to 2032, and
    // F4's footpath crosses its entry room 2052 a second time.
    private static readonly int[] F1FromRooms =
    {
        1800, 1801, 1802, 1803, 1804, 1805, 1806, 1807, 1808, 1808, 1807, 1806, 1805, 1804, 1803, 1802,
        1801, 1800, 1809, 1810, 1811, 1819, 1827, 1828, 1829, 1834, 1835, 1835, 1834, 1829, 1828, 1831,
        1832, 1833, 1836, 1837, 1838, 1842, 1843, 1853, 1854, 1855, 1856, 1856, 1855, 1854, 1853, 1843,
        1844, 1845, 1846, 1847, 1848, 1849, 1850, 1851, 1852, 1858, 1859, 1860, 1862, 1863, 1864, 1865,
        1866, 1867, 1868, 1869, 1870, 1871, 1872, 1874, 1875, 1877, 1878, 1879, 1880, 1881, 1882, 1883,
        1884, 1885, 1886, 1887, 1888, 1888, 1887, 1886, 1885, 1884, 1883, 1882, 1881, 1880, 1879, 1878,
        1877, 1875, 1874, 1872, 1873, 1889, 1890, 1892, 1893, 1894, 1895, 1896, 1897, 1898, 1899, 1900,
        1901, 1902, 1903, 1906, 1907, 1908, 1908, 1907, 1906, 1903, 1904, 1909, 1910, 1911, 1912, 1913,
        1914, 1918, 1919, 1920,
    };

    private static readonly int[] F2FromRooms =
    {
        1921, 1922, 1923, 1924, 1925, 1959, 1960, 1961, 1962, 1963, 1964, 1965, 1966, 1967, 1975, 1976,
        1977, 1978, 1979, 1980, 1988, 1989, 1990, 1991, 1992, 1993, 1994, 1995, 1996, 1997, 1998, 1999,
        2000, 2001,
    };

    private static readonly int[] F3FromRooms =
    {
        2002, 2003, 2004, 2006, 2013, 2012, 2011, 2014, 2015, 2016, 2017, 2018, 2023,
        2022, 2021, 2020, 2024, 2031, 2032, 2005, 2032, 2034, 2048, 2049, 2050, 2051,
    };

    private static readonly int[] F4FromRooms =
    {
        2052, 2054, 2067, 2066, 2065, 2064, 2063, 2062, 2061, 2068, 2069, 2070, 2071, 2072, 2052, 2053,
        2055, 2056, 2057, 2058, 2073, 2074,
    };

    private static readonly int[] F5FromRooms = { 2077, 2084, 2083, 2082, 2081 };

    // Per-step source rooms for a floor, index-aligned with Steps(floor); null off the
    // five climbed floors.
    public static IReadOnlyList<int>? FromRooms(PyramidFloor floor) => floor switch
    {
        PyramidFloor.F1 => F1FromRooms,
        PyramidFloor.F2 => F2FromRooms,
        PyramidFloor.F3 => F3FromRooms,
        PyramidFloor.F4 => F4FromRooms,
        PyramidFloor.F5 => F5FromRooms,
        _ => null,
    };

    // The room a floor's last step lands in: the next floor's entry, or the top.
    public static int EndRoom(PyramidFloor floor) => floor switch
    {
        PyramidFloor.F1 => 1921,
        PyramidFloor.F2 => 2002,
        PyramidFloor.F3 => 2052,
        PyramidFloor.F4 => 2077,
        PyramidFloor.F5 => TargetRoom,
        _ => 0,
    };

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
            if (tok.Length == 2 && tok[0] == 'G')   // through a pushed-block gate
            {
                steps.Add(new PyramidStep(PyramidStepKind.Move, ParseDir(tok[1]), Gate: true));
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
