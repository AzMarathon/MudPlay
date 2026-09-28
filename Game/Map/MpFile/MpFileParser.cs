using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace MudPlay.Game.Map.MpFile;

// Structural decoder for MegaMUD .mp path files, no graph resolution (that's
// MpFileImporter). Only a file with no recognisable header throws; anything else
// that's off — a goto path rather than a loop, a step count that disagrees, a
// broken step row — is recorded in MpLoopFile.Problems so the review window can
// still show what the file says.
public static partial class MpFileParser
{
    public static MpLoopFile ParseFile(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!File.Exists(path))
            throw new MpFileFormatException($"file not found: {path}");
        return Parse(File.ReadAllText(path));
    }

    public static MpLoopFile Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        // DOS-era editors end the file with a Ctrl-Z (0x1A) end-of-file byte, which
        // otherwise reads as a malformed last step.
        List<string> lines = new();
        foreach (string raw in text.Replace("\u001A", string.Empty).Split('\n'))
        {
            string line = raw.TrimEnd('\r').Trim();
            if (line.Length > 0) lines.Add(line);
        }
        if (lines.Count < 2)
            throw new MpFileFormatException("file too short — need at least a label line and a metadata line");

        List<string> problems = new();

        Match hdr1 = LabelLineRegex().Match(lines[0]);
        if (!hdr1.Success)
            throw new MpFileFormatException($"line 1 isn't a [label][author] pair: '{lines[0]}'");
        string label  = hdr1.Groups["label"].Value;
        string author = hdr1.Groups["author"].Value;
        int cursor = 1;

        // Some files repeat the [label][author] line, the second carrying the author.
        if (cursor < lines.Count && LabelLineRegex().Match(lines[cursor]) is { Success: true } again)
        {
            if (author.Length == 0) author = again.Groups["author"].Value;
            if (label.Length == 0) label = again.Groups["label"].Value;
            cursor++;
        }

        // Many goto-path files have no header room lines at all.
        MpHeaderRoom start = new(string.Empty, string.Empty, string.Empty);
        if (cursor < lines.Count && HeaderRoomRegex().Match(lines[cursor]) is { Success: true } startHdr)
        {
            start = Header(startHdr);
            cursor++;
        }
        else problems.Add("no [code:group:name] header room line");
        MpHeaderRoom end = start;
        if (cursor < lines.Count && HeaderRoomRegex().Match(lines[cursor]) is { Success: true } endHdr)
        {
            end = Header(endHdr);
            cursor++;
        }

        if (cursor >= lines.Count)
            throw new MpFileFormatException("missing metadata line");
        string metaLine = lines[cursor++];
        string[] meta = metaLine.Split(':');
        if (meta.Length < 3 || meta[0].Trim().Length != 8)
            throw new MpFileFormatException(
                $"metadata line malformed: '{metaLine}' — expected 'startHash:endHash:steps:…'");

        string startHash = meta[0].Trim().ToUpperInvariant();
        string endHash   = meta[1].Trim().ToUpperInvariant();
        if (!int.TryParse(meta[2].Trim(), out int declared) || declared < 0)
        {
            problems.Add($"step count '{meta[2].Trim()}' isn't a number");
            declared = -1;
        }
        string use       = Field(meta, 3);
        int gold         = int.TryParse(Field(meta, 4), out int g) ? g : 0;
        string item      = Field(meta, 5);
        string failPath  = Field(meta, 6);
        string donePath  = Field(meta, 7);

        if (!string.Equals(startHash, endHash, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(start.Code, end.Code, StringComparison.OrdinalIgnoreCase))
            problems.Add("this is a goto path (it ends in a different room), not a loop");

        List<MpStep> steps = new(Math.Max(declared, 0));
        for (int row = 1; cursor < lines.Count; cursor++, row++)
        {
            string line = lines[cursor];
            string[] parts = line.Split(':', 3);
            if (parts.Length < 3 || parts[0].Trim().Length != 8)
            {
                problems.Add($"step row {row} is malformed and was skipped: '{line}'");
                continue;
            }
            MpStepFlags flags = int.TryParse(parts[1].Trim(), NumberStyles.HexNumber, null, out int f)
                ? (MpStepFlags)f : MpStepFlags.None;
            steps.Add(ParseStep(parts[0].Trim().ToUpperInvariant(), flags, parts[2].Trim()));
        }

        if (declared >= 0 && steps.Count != declared)
            problems.Add($"the file says {declared} step(s) but has {steps.Count}");
        if (steps.Count < 2)
            problems.Add($"only {steps.Count} step(s) — a loop needs at least 2");
        else if (!string.Equals(steps[0].HashExits, startHash, StringComparison.OrdinalIgnoreCase))
            problems.Add($"the first step's room ({steps[0].HashExits}) isn't the start room ({startHash})");

        return new MpLoopFile(label, author, start, end, startHash, endHash, declared, use, gold,
            item, failPath, donePath, steps, problems);
    }

    // "n", "s[search s]", "e[use black star key e]", "E -- (Hidden/Needs 1 Actions",
    // or a plain command ("go path", "pull lever").
    internal static MpStep ParseStep(string hash, MpStepFlags flags, string raw)
    {
        Match m = CompassActionRegex().Match(raw);
        if (m.Success && TryParseDirection(m.Groups["dir"].Value, out Direction dir))
        {
            List<string> pre = new();
            if (m.Groups["pre"].Success)
                foreach (string p in m.Groups["pre"].Value.Split(','))
                    if (p.Trim().Length > 0) pre.Add(p.Trim());
            string? note = m.Groups["note"].Success ? m.Groups["note"].Value.Trim() : null;
            return new MpStep(hash, flags, raw, dir, pre, null, string.IsNullOrEmpty(note) ? null : note);
        }
        return new MpStep(hash, flags, raw, null, Array.Empty<string>(), raw.Length == 0 ? null : raw, null);
    }

    private static MpHeaderRoom Header(Match m) =>
        new(m.Groups["code"].Value, m.Groups["group"].Value, m.Groups["name"].Value);

    private static string Field(string[] parts, int i) => i < parts.Length ? parts[i].Trim() : string.Empty;

    internal static bool TryParseDirection(string raw, out Direction dir)
    {
        switch (raw.Trim().ToUpperInvariant())
        {
            case "N":  dir = Direction.N;  return true;
            case "S":  dir = Direction.S;  return true;
            case "E":  dir = Direction.E;  return true;
            case "W":  dir = Direction.W;  return true;
            case "NE": dir = Direction.NE; return true;
            case "NW": dir = Direction.NW; return true;
            case "SE": dir = Direction.SE; return true;
            case "SW": dir = Direction.SW; return true;
            case "U":  dir = Direction.U;  return true;
            case "D":  dir = Direction.D;  return true;
            default:   dir = Direction.N;  return false;
        }
    }

    [GeneratedRegex(@"^\[(?<label>[^\]]*)\]\[(?<author>[^\]]*)\]$")]
    private static partial Regex LabelLineRegex();

    [GeneratedRegex(@"^\[(?<code>[^:\]]+):(?<group>[^:\]]*):(?<name>[^\]]+)\]$")]
    private static partial Regex HeaderRoomRegex();

    // A compass token, then optional [extra,commands], then an optional "-- note".
    [GeneratedRegex(@"^(?<dir>[A-Za-z]{1,2})\s*(?:\[(?<pre>[^\]]*)\])?\s*(?:--(?<note>.*))?$")]
    private static partial Regex CompassActionRegex();
}

// A .mp file with no recognisable header — nothing to show.
public sealed class MpFileFormatException : Exception
{
    public MpFileFormatException(string message) : base(message) { }
}
