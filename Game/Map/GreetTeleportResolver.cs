using System.Collections.Generic;
using MudPlay.Services;

namespace MudPlay.Game.Map;

// Decodes a placed monster's greet chain into an `ask <noun> <keyword>` command
// that TRANSPORTS the player out of the monster's room — the "ask the NPC to port
// you somewhere" mechanic (e.g. the Grey Lord in the Floating Citadel, who ports
// a character to Town Square when asked). This is the teleport analogue of
// GuardDoorCommandResolver, which decodes greet keywords that open a DOOR; the two
// share the greet-chain reading primitives (ResolveGreetBlock / CleanKeyword /
// LeadingInt / LastWord live on GuardDoorCommandResolver).
//
// A greet keyword points (through empty-Action LinkTo hops) at a directive block
// carrying a `teleport <room> <map>` directive. We surface ONLY ungated teleports:
// a block whose directives include an alignment / quest-ability gate (evilaligned,
// goodaligned, checkability, testability, giveability) is skipped, because the
// client can't verify those gates and a transport that silently fails would strand
// the walker mid-route. A `minlevel N` gate is kept and surfaced as MinLevel —
// BFS already honours a level floor on teleport edges, so a too-low character
// simply won't route through it. A `class N` gate is likewise kept and surfaced
// as RequiredClass, stamped onto the synthesized edge's ClassGate so the existing
// MovementFilter.IsClassGateBlocked drops it for every class but N — a class-only
// transport stays routable for its class but never for the wrong one (issue #455:
// a bard-only barmaid teleport was being routed through by non-bards). The player
// types `ask <noun> <keyword>`, noun = last word of the monster's name ("The Grey
// Lord" → "Lord").
public static class GreetTeleportResolver
{
    // One ask-transport exit: the verbatim `ask <noun> <keyword>` command, the
    // room it lands in, any level floor the game gates the transport behind (0 when
    // ungated by level), and any single class the transport is restricted to
    // (0 when ungated by class — a `class N` directive, N = Classes.Number).
    public readonly record struct GreetTeleport(string Command, RoomKey Destination, int MinLevel, int RequiredClass);

    // Matches the door decoder's depth cap — a malformed self-referential greet
    // chain can't spin the resolver; the per-walk visited set breaks true cycles.
    private const int MaxDepth = 40;

    // Yield every ungated ask-transport a monster's greet exposes. greetNumber is
    // Monsters.GreetTXT; monsterName is Monsters.Name (its last word becomes the
    // `ask` noun). Empty when the greet exposes no ungated teleport keyword.
    public static IEnumerable<GreetTeleport> Resolve(TBInfoStore store, int greetNumber, string? monsterName)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (greetNumber <= 0) yield break;

        string noun = GuardDoorCommandResolver.LastWord(monsterName);
        if (noun.Length == 0) yield break;

        TBInfoEntry? greet = GuardDoorCommandResolver.ResolveGreetBlock(store, greetNumber, new HashSet<int>());
        if (greet is null || string.IsNullOrEmpty(greet.Action)) yield break;

        foreach (string raw in greet.Action.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;
            int colon = line.IndexOf(':');
            if (colon < 0) continue; // not a keyword line

            string keyword = GuardDoorCommandResolver.CleanKeyword(line[..colon]);
            if (keyword.Length == 0) continue;

            int pointer = GuardDoorCommandResolver.LeadingInt(line[(colon + 1)..]);
            if (pointer <= 0) continue;

            if (TryResolveUngatedTeleport(store, pointer, new HashSet<int>(),
                    out RoomKey dest, out int minLevel, out int requiredClass))
            {
                yield return new GreetTeleport($"ask {noun} {keyword}", dest, minLevel, requiredClass);
            }
        }
    }

    // Follow the directive block a greet keyword points at (through empty-Action
    // LinkTo hops) and report its teleport destination + level floor + class gate.
    // Only the first non-empty block is scanned, mirroring
    // GuardDoorCommandResolver.TryResolveExit.
    //
    // A block is often a class-branch TABLE — one `class N:<effects>` line per
    // class, and usually only ONE of them teleports (the barmaid's `adventure`:
    // every non-bard line just prints text, the class-9 line teleports to the inn).
    // So each line is scanned in its OWN scope and the first line that actually
    // teleports decides the destination, level floor, and class gate — the class
    // that gates the transport is the one on the SAME line as the teleport, NOT the
    // last `class N` seen in the block (binding to the last one mis-gated the
    // barmaid transport to class 15 instead of class 9, report
    // stock-20260907-175035). A line carrying an alignment / quest-ability gate is
    // skipped (that branch is conditional on state the client can't read, so
    // routing it risks a silent no-op that strands the walker); a `testskill`
    // attribute roll is NOT such a gate — it gates the right class by a live skill
    // check the walker reacts to (verify-and-retry), so the transport stays
    // routable for its class.
    private static bool TryResolveUngatedTeleport(TBInfoStore store, int number,
        HashSet<int> visited, out RoomKey dest, out int minLevel, out int requiredClass)
    {
        dest = default;
        minLevel = 0;
        requiredClass = 0;
        int depth = 0;
        while (number > 0 && depth++ < MaxDepth && visited.Add(number))
        {
            TBInfoEntry? entry = store.GetEntry(number);
            if (entry is null) return false;
            if (string.IsNullOrEmpty(entry.Action))
            {
                number = entry.LinkTo;
                continue;
            }

            foreach (string rawLine in entry.Action.Split('\n'))
            {
                int lineMinLevel = 0;
                int lineClass = 0;
                RoomKey lineDest = default;
                bool lineHasDest = false;
                bool lineGated = false;

                foreach (string tokenRaw in rawLine.Split(':'))
                {
                    string token = tokenRaw.Trim();
                    if (token.Length == 0) continue;
                    if (IsGate(token)) lineGated = true;
                    else if (token.StartsWith("minlevel ", StringComparison.OrdinalIgnoreCase))
                    {
                        int lvl = GuardDoorCommandResolver.FirstIntAfter(token, "minlevel ");
                        if (lvl > 0) lineMinLevel = lvl;
                    }
                    // `class N` (N = Classes.Number) restricts this branch line to a
                    // single class; surfaced as the edge's ClassGate so a character
                    // of the wrong class is filtered out.
                    else if (token.StartsWith("class ", StringComparison.OrdinalIgnoreCase))
                    {
                        int cls = GuardDoorCommandResolver.FirstIntAfter(token, "class ");
                        if (cls > 0) lineClass = cls;
                    }
                    else if (!lineHasDest && TBInfoTeleportResolver.TryParseTeleport(token, out RoomKey d))
                    {
                        lineDest = d;
                        lineHasDest = true;
                    }
                }

                if (lineHasDest && !lineGated)
                {
                    dest = lineDest;
                    minLevel = lineMinLevel;
                    requiredClass = lineClass;
                    return true;
                }
            }
            return false; // first non-empty block decides
        }
        return false;
    }

    // Directives that make a greet effect conditional on alignment or a quest
    // ability the client can't track — their presence marks a quest-gated branch
    // we must not synthesise into an always-routable edge.
    private static bool IsGate(string token) =>
        token.StartsWith("evilaligned ", StringComparison.OrdinalIgnoreCase)
        || token.StartsWith("goodaligned ", StringComparison.OrdinalIgnoreCase)
        || token.StartsWith("checkability ", StringComparison.OrdinalIgnoreCase)
        || token.StartsWith("testability ", StringComparison.OrdinalIgnoreCase)
        || token.StartsWith("giveability ", StringComparison.OrdinalIgnoreCase);
}
