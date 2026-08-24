using System.Text;
using System.Text.RegularExpressions;
using MudPlay.Game;

namespace MudPlay.Services;

// Streaming scanner that watches the post-IAC byte stream from the live Telnet
// connection for MajorMUD status-line prompts and fires one PromptObserved event
// per match. Bypasses Terminal.LineExtractor for prompt parsing because the
// server rewrites the statline in place (CR + erase-line + new content on the
// same row) — by the time the cell grid finally emits a "line", only the last
// statline survives and any intermediate HP / MA / position changes are lost.
// Scanning the wire stream catches every update as it lands.
//
// Stateful: CSI escapes (ESC '[' params final-byte) are stripped inline as bytes
// arrive, so a sequence like [HP=27\x1b[0;37m/MA=31\x1b[0;37m]: still matches the
// regex. A small carryover buffer (~1 KB cap) preserves partial matches across
// chunk boundaries.
//
// The regex remains unanchored because the server chains multiple statlines
// back-to-back on the same row
// ([HP=27/MA=31]:[HP=28/MA=34]:[HP=29/MA=34]:). Each candidate is nevertheless
// boundary-validated: it must start at a real wire row/control boundary or
// directly after a previously accepted statline. This rejects another player's
// prompt quoted inside chat ("Bob gossips: [HP=671/KAI=40]:w") without losing
// the chained rewrites this scanner exists to preserve.
public sealed class WirePromptScanner
{
    private const int BufferCap = 1024;

    // Stripped-text carryover. Bytes flow through the inline ANSI state machine
    // into here; the regex runs against this string.
    private readonly StringBuilder _buffer = new(BufferCap);

    // Offsets in _buffer immediately after a wire row/control boundary. Kept
    // separately instead of inserting sentinel characters into _buffer because
    // custom statlines may use %n: their regex intentionally spans CR/LF after the
    // scanner strips those controls. Offset 0 is a valid boundary for a fresh or
    // fully-consumed buffer.
    private readonly List<int> _promptBoundaries = new() { 0 };

    private StripState _state;

    // The active status-line pattern. Defaults to the permissive class-default
    // shape; InstallRegex swaps in a regex built from the user's custom statline
    // so the parser matches whatever the editor authored. Reference assignment is
    // atomic, so a swap from the UI thread while Append reads it off the Telnet
    // pump is safe — at worst one append still uses the previous pattern.
    private Regex _statusLine = StatlinePromptRegexBuilder.Default;

    // Fired once per matched status line, in the order observed on the wire.
    public event Action<PromptObservation>? PromptObserved;

    // Fired (at most once per Append) when a default-shaped statline appears that
    // the active pattern did NOT match — i.e. the live prompt isn't the statline
    // the editor authored. Drives the logon reconciler to resend `set statline`.
    // Structurally unreachable while the active pattern IS the default, so
    // default-statline users never see it (and never trigger a resend).
    public event Action? PromptShapeUnmatched;

    // Swap in the status-line pattern for the active profile's statline — built
    // by StatlinePromptRegexBuilder from the editor command string. Installed on
    // profile load / mutation so the scanner reads exactly the shape the BBS was
    // told to print.
    public void InstallRegex(Regex statusLine)
    {
        ArgumentNullException.ThrowIfNull(statusLine);
        _statusLine = statusLine;
    }

    // Restore the permissive class-default pattern (on profile close).
    public void ResetRegexToDefault() => _statusLine = StatlinePromptRegexBuilder.Default;

    // Append data from the live Telnet stream. Strips CSI escapes inline, runs
    // the status-line regex, and fires PromptObserved for each match.
    public void Append(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return;

        foreach (byte b in data)
        {
            switch (_state)
            {
                case StripState.Normal:
                    if (b == 0x1B) { _state = StripState.EscSeen; }
                    else if (b is (byte)'\r' or (byte)'\n')
                    {
                        MarkPromptBoundary();
                    }
                    else if (b >= 0x20 && b < 0x7F)
                    {
                        // Printable ASCII. The statline regex only cares about
                        // these. CR / LF and row-rewrite CSI controls are recorded
                        // as boundary offsets above/below; other controls are dropped.
                        _buffer.Append((char)b);
                    }
                    break;

                case StripState.EscSeen:
                    _state = b == (byte)'[' ? StripState.Csi : StripState.Normal;
                    break;

                case StripState.Csi:
                    // CSI final bytes are 0x40-0x7E. Parameter / intermediate
                    // bytes (0x20-0x3F) keep us in Csi.
                    if (b >= 0x40 && b <= 0x7E)
                    {
                        // MajorMUD commonly starts each rewritten row with
                        // ESC[79D ESC[K rather than a literal CR. Record cursor-left /
                        // cursor-position / erase-line finals as row boundaries, but
                        // never SGR 'm' — colour changes occur inside a statline.
                        if (b is (byte)'D' or (byte)'G' or (byte)'H' or (byte)'f' or (byte)'K')
                            MarkPromptBoundary();
                        _state = StripState.Normal;
                    }
                    break;
            }
        }

        // Run the regex over the carryover. Successive matches on the same
        // buffer are cheap because the StringBuilder→string conversion happens
        // once and the regex is compiled.
        string text = _buffer.ToString();
        int lastEnd = 0;
        int previousAcceptedEnd = -1;
        bool activeMatched = false;
        foreach (Match m in _statusLine.Matches(text))
        {
            if (!IsPromptBoundary(text, m.Index, previousAcceptedEnd)) continue;
            if (!int.TryParse(m.Groups["hp"].Value, out int hp)) continue;
            activeMatched = true;

            string typeRaw = m.Groups["type"].Value;
            ManaType manaType = typeRaw switch
            {
                "MA"  => ManaType.Mana,
                "KAI" => ManaType.Kai,
                _      => ManaType.None,
            };

            int mana = 0;
            if (manaType != ManaType.None && int.TryParse(m.Groups["mana"].Value, out int parsedMana))
            {
                mana = parsedMana;
            }

            string posRaw = m.Groups["statea"].Success ? m.Groups["statea"].Value
                          : m.Groups["stateb"].Success ? m.Groups["stateb"].Value
                          : string.Empty;
            PlayerPosition position = posRaw switch
            {
                "Resting"    => PlayerPosition.Resting,
                "Meditating" => PlayerPosition.Meditating,
                _            => PlayerPosition.Standing,
            };

            PromptObserved?.Invoke(new PromptObservation(hp, manaType, mana, position));
            lastEnd = m.Index + m.Length;
            previousAcceptedEnd = lastEnd;
        }

        // Mismatch detection for the logon reconciler: if the active pattern
        // matched nothing here but a default-shaped statline IS present, the
        // server is printing a statline our editor-built pattern doesn't
        // recognise (typically: editor holds a custom statline but the game is
        // still on the class default). Signal it once so the reconciler can
        // resend `set statline`. Skipped when the active pattern already IS the
        // default — default-statline users can't drift, so they never resend.
        if (!activeMatched && !ReferenceEquals(_statusLine, StatlinePromptRegexBuilder.Default))
        {
            bool defaultMatched = false;
            int previousDefaultEnd = -1;
            foreach (Match d in StatlinePromptRegexBuilder.Default.Matches(text))
            {
                if (!IsPromptBoundary(text, d.Index, previousDefaultEnd)) continue;
                defaultMatched = true;
                int end = d.Index + d.Length;
                if (end > lastEnd) lastEnd = end;
                previousDefaultEnd = end;
            }
            if (defaultMatched) PromptShapeUnmatched?.Invoke();
        }

        // Drop everything up to the last match — the tail (anything after the
        // last match's end) might be the start of a partial statline that
        // completes in the next Append, so keep it.
        if (lastEnd > 0)
        {
            RemovePrefix(lastEnd, establishStartBoundary: true);
        }

        // Hard cap so a long quiet stretch of non-statline text doesn't pin
        // memory. Drops oldest first; statlines are short so we never lose a
        // legitimate in-flight partial match here.
        if (_buffer.Length > BufferCap)
        {
            RemovePrefix(_buffer.Length - BufferCap, establishStartBoundary: false);
        }
    }

    private void MarkPromptBoundary()
    {
        int offset = _buffer.Length;
        if (_promptBoundaries.Count == 0 || _promptBoundaries[^1] != offset)
            _promptBoundaries.Add(offset);
    }

    // A candidate is valid when only spaces separate it from the nearest real
    // wire boundary, or from the end of the previously accepted prompt (the
    // chained-statline case). Any printable prefix — especially "X gossips: " —
    // makes it ordinary text rather than our status line.
    private bool IsPromptBoundary(string text, int candidateStart, int previousAcceptedEnd)
    {
        if (previousAcceptedEnd >= 0
            && OnlySpaces(text, previousAcceptedEnd, candidateStart))
            return true;

        for (int i = _promptBoundaries.Count - 1; i >= 0; i--)
        {
            int boundary = _promptBoundaries[i];
            if (boundary > candidateStart) continue;
            return OnlySpaces(text, boundary, candidateStart);
        }
        return false;
    }

    private static bool OnlySpaces(string text, int start, int end)
    {
        if (start < 0 || end < start) return false;
        for (int i = start; i < end; i++)
            if (text[i] != ' ') return false;
        return true;
    }

    private void RemovePrefix(int count, bool establishStartBoundary)
    {
        if (count <= 0) return;
        _buffer.Remove(0, count);

        int write = 0;
        for (int read = 0; read < _promptBoundaries.Count; read++)
        {
            int shifted = _promptBoundaries[read] - count;
            if (shifted < 0) continue;
            if (write > 0 && _promptBoundaries[write - 1] == shifted) continue;
            _promptBoundaries[write++] = shifted;
        }
        if (write < _promptBoundaries.Count)
            _promptBoundaries.RemoveRange(write, _promptBoundaries.Count - write);

        if (establishStartBoundary
            && (_promptBoundaries.Count == 0 || _promptBoundaries[0] != 0))
            _promptBoundaries.Insert(0, 0);
    }

    // Reset the scanner — drops carryover and any in-flight CSI escape.
    public void Reset()
    {
        _buffer.Clear();
        _promptBoundaries.Clear();
        _promptBoundaries.Add(0);
        _state = StripState.Normal;
    }

    private enum StripState : byte { Normal, EscSeen, Csi }
}

// One observed prompt — payload of WirePromptScanner.PromptObserved.
public readonly record struct PromptObservation(
    int Hp,
    ManaType ManaType,
    int Mana,
    PlayerPosition Position);
