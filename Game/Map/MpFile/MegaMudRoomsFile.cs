using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace MudPlay.Game.Map.MpFile;

// MegaMUD's Rooms.md — its named rooms, one per line:
// "hashExits:options:minLevel:maxLevel:class:code:group:name". The .mp review
// window reads it from the .mp file's folder to name a loop's rooms and show their
// options. Only the rooms MegaMUD named are in it; most loop steps aren't.
public sealed class MegaMudRoomsFile
{
    private readonly Dictionary<string, MegaMudRoomEntry> _byCode;
    private readonly ILookup<string, MegaMudRoomEntry> _byHash;

    private MegaMudRoomsFile(IReadOnlyList<MegaMudRoomEntry> rooms)
    {
        Rooms = rooms;
        _byCode = new(StringComparer.OrdinalIgnoreCase);
        foreach (MegaMudRoomEntry r in rooms) _byCode.TryAdd(r.Code, r);
        _byHash = rooms.ToLookup(r => r.HashExits, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<MegaMudRoomEntry> Rooms { get; }

    public MegaMudRoomEntry? ByCode(string code) =>
        _byCode.TryGetValue(code, out MegaMudRoomEntry? r) ? r : null;

    public IEnumerable<MegaMudRoomEntry> ByHash(string hashExits) => _byHash[hashExits];

    // Rooms.md from the folder a .mp file sits in (any case — the Stock install
    // has Rooms.md, the Paradigm one ROOMS.md), or null when there isn't one.
    public static MegaMudRoomsFile? FindBeside(string mpPath)
    {
        string? dir = Path.GetDirectoryName(mpPath);
        if (dir is null || !Directory.Exists(dir)) return null;
        string? file = Directory.EnumerateFiles(dir)
            .FirstOrDefault(f => string.Equals(Path.GetFileName(f), "rooms.md", StringComparison.OrdinalIgnoreCase));
        return file is null ? null : Parse(File.ReadAllText(file));
    }

    public static MegaMudRoomsFile Parse(string text)
    {
        List<MegaMudRoomEntry> rooms = new();
        foreach (string raw in text.Replace("\u001A", string.Empty).Split('\n'))
        {
            string[] p = raw.Trim().Split(':', 8);
            if (p.Length < 8 || p[0].Length != 8) continue;
            rooms.Add(new MegaMudRoomEntry(
                HashExits: p[0].ToUpperInvariant(),
                Options:   int.TryParse(p[1], NumberStyles.HexNumber, null, out int o) ? (MegaMudRoomOptions)o : 0,
                MinLevel:  int.TryParse(p[2], out int min) ? min : 0,
                MaxLevel:  int.TryParse(p[3], out int max) ? max : 0,
                ClassId:   int.TryParse(p[4], out int cls) ? cls : 0,
                Code:      p[5],
                Group:     p[6],
                Name:      p[7]));
        }
        return new MegaMudRoomsFile(rooms);
    }
}

public sealed record MegaMudRoomEntry(
    string HashExits,
    MegaMudRoomOptions Options,
    int MinLevel,
    int MaxLevel,
    int ClassId,
    string Code,
    string Group,
    string Name);

// Rooms.md room options. Bits not listed here (0x4000, 0x8000 appear on some loop
// start rooms) are shown by value — their meaning isn't documented.
[Flags]
public enum MegaMudRoomOptions
{
    None         = 0,
    Shop         = 0x02,
    Bank         = 0x04,
    Trainer      = 0x08,
    StopBefore   = 0x10,
    Avoid        = 0x20,
    HideInGoto   = 0x40,
    CommonRooms  = 0x80,
}
