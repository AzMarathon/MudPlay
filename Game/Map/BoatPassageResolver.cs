using System.Collections.Generic;
using System.Globalization;
using MudPlay.Game.Spells;
using MudPlay.Services;

namespace MudPlay.Game.Map;

// Discovers the sailings a sea-captain dock offers by decoding its CMD TBInfo
// `secure passage to <place>` lines into BoatPassage records. Sibling to
// TBInfoCastTeleportResolver — both walk a room's CMD chain and resolve a
// spell-driven teleport into a destination room — but a boat is a two-stage,
// indirected teleport the cast resolver can't follow:
//
//   secure passage to albion:minlevel 50 <msg>:price 2000000 <msg>:random 4987:text <msg>
//
// The line carries the per-crosser gates (minlevel, price) inline, but the
// teleport is reached through `random <tbId>` → a weighted-random TBInfo table
// whose lines read `<roll>:cast <boardSpell>:cast <tripSpell>`. The board spell
// random-teleports the caster onto a ship room; the trip spell begins a
// buff-locked voyage that chains — via each spell's Abil 151 (EndCast → follow-on
// spell) — through the transit legs to a terminal `disembark to <port>` spell
// whose fixed Abil 140 (TeleportRoom) + Abil 141 (TeleportMap) is the arrival
// room. This resolver follows that whole chain to land the arrival RoomKey, so
// the router can plan a land-leg → boat → land-leg route without hardcoding any
// realm's dock or port numbers.
public static class BoatPassageResolver
{
    // The keyword prefix that marks a dock line. The full keyword the player
    // types is "secure passage to <place>"; <place> is the tail.
    private const string PassagePrefix = "secure passage to ";

    // Ability codes (shared with TBInfoCastTeleportResolver): the room and map a
    // teleport spell moves the caster to, and the EndCast chain link. A
    // disembark spell carries a fixed TeleportRoom (Abil 140 value > 0); the
    // board spell's is 0 (random ship room) so it never resolves as an arrival.
    private const int TeleportRoomCode = 140;
    private const int TeleportMapCode  = 141;
    private const int EndCastCode      = 151;

    // Bounds on the transit spell chain — a real voyage is board + 3 trip legs +
    // disembark, so a handful of hops. The visited set breaks a malformed
    // self-referential chain; this bounds pathological depth too.
    private const int MaxChainDepth = 32;

    // Decode every `secure passage to <place>` line off a dock room's CMD chain
    // into a BoatPassage. dockRoom is the room the command is typed in (its map
    // is the arrival-map fallback when a disembark spell carries no explicit
    // TeleportMap). catalog resolves the transit spell numbers to their ability
    // lists. Lines whose arrival room can't be resolved (a broken random table /
    // transit chain, or a cast that never reaches a fixed teleport) are skipped
    // rather than offered — a passage we can't land is not routable.
    public static IEnumerable<BoatPassage> EnumerateBoatPassages(
        TBInfoStore store, RoomKey dockRoom, int dockCmd, KnownSpellCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(catalog);
        if (dockCmd <= 0) yield break;

        TBInfoEntry? entry = store.GetEntry(dockCmd);
        // A dialogue-only block continues at its LinkTo (mirrors the other
        // TBInfo resolvers / the greet decoder).
        while (entry is not null && string.IsNullOrWhiteSpace(entry.Action) && entry.LinkTo > 0)
            entry = store.GetEntry(entry.LinkTo);
        if (entry is null || string.IsNullOrWhiteSpace(entry.Action)) yield break;

        foreach (string raw in entry.Action.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;

            string[] parts = line.Split(':', StringSplitOptions.TrimEntries);
            if (parts.Length < 2) continue;

            string keyword = parts[0];
            if (!keyword.StartsWith(PassagePrefix, StringComparison.OrdinalIgnoreCase)) continue;

            string place = keyword[PassagePrefix.Length..].Trim();
            if (place.Length == 0) continue;

            int minLevel = 0;
            long fareCopper = 0;
            int randomTbId = 0;
            bool requiresCheckability = false;

            for (int i = 1; i < parts.Length; i++)
            {
                string tok = parts[i];
                if (tok.StartsWith("minlevel ", StringComparison.OrdinalIgnoreCase))
                    minLevel = FirstInt(tok["minlevel ".Length..]);
                else if (tok.StartsWith("price ", StringComparison.OrdinalIgnoreCase))
                    fareCopper = FirstLong(tok["price ".Length..]);
                else if (tok.StartsWith("random ", StringComparison.OrdinalIgnoreCase))
                    randomTbId = FirstInt(tok["random ".Length..]);
                else if (tok.StartsWith("checkability ", StringComparison.OrdinalIgnoreCase))
                    requiresCheckability = true;
            }

            if (randomTbId <= 0) continue;
            if (!TryResolveArrivalRoom(store, catalog, randomTbId, dockRoom.Map,
                    out RoomKey arrival, out int voyageRounds))
                continue;

            yield return new BoatPassage(
                dockRoom, place, keyword, arrival, minLevel, fareCopper,
                requiresCheckability, voyageRounds);
        }
    }

    // Follow `random <tbId>` → weighted-random table `<roll>:cast <board>:cast
    // <trip>` → the transit spell chain to the disembark room. Returns the first
    // arrival room any cast on any table line resolves to; false when none does.
    // voyageRounds carries the summed transit-spell duration of the winning chain
    // (spell rounds — see TryResolveDisembark) so the walker can time the sail.
    private static bool TryResolveArrivalRoom(
        TBInfoStore store, KnownSpellCatalog catalog, int randomTbId, int sourceMap,
        out RoomKey arrival, out int voyageRounds)
    {
        arrival = default;
        voyageRounds = 0;
        TBInfoEntry? table = store.GetEntry(randomTbId);
        while (table is not null && string.IsNullOrWhiteSpace(table.Action) && table.LinkTo > 0)
            table = store.GetEntry(table.LinkTo);
        if (table is null || string.IsNullOrWhiteSpace(table.Action)) return false;

        foreach (string raw in table.Action.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;

            // `<roll>:cast <board>:cast <trip>` — try each cast's chain. The board
            // spell is a random teleport (never reaches a fixed disembark), so the
            // first cast that DOES land a fixed teleport is the trip spell's
            // arrival, regardless of cast order.
            foreach (string tok in line.Split(':', StringSplitOptions.TrimEntries))
            {
                if (!tok.StartsWith("cast ", StringComparison.OrdinalIgnoreCase)) continue;
                int spellNumber = FirstInt(tok["cast ".Length..]);
                if (spellNumber <= 0) continue;
                if (TryResolveDisembark(catalog, spellNumber, sourceMap, out arrival, out voyageRounds))
                    return true;
            }
        }
        return false;
    }

    // Walk a spell's Abil 151 (EndCast) chain until a spell carries a fixed
    // TeleportRoom (Abil 140 value > 0). That spell is the `disembark to <port>`
    // — its room + TeleportMap (Abil 141, or sourceMap when unset) is the arrival.
    // A random teleport (Abil 140 value 0, the board spell) is not a fixed
    // landing, so the walk continues past / falls off it. Cycle-guarded + depth-
    // capped against a malformed chain. voyageRounds accumulates each visited
    // spell's Dur (in spell rounds — the trip legs' buff durations); the disembark
    // itself is instantaneous (Dur 0), so summing through it is harmless and the
    // total is the boarding-to-landing wait the walker times.
    private static bool TryResolveDisembark(
        KnownSpellCatalog catalog, int spellNumber, int sourceMap,
        out RoomKey arrival, out int voyageRounds)
    {
        arrival = default;
        voyageRounds = 0;
        HashSet<int> visited = new();
        int cur = spellNumber;
        for (int depth = 0; cur > 0 && depth < MaxChainDepth; depth++)
        {
            if (!visited.Add(cur)) return false;
            if (catalog.GetFormulaByNumber(cur) is not { } spell) return false;

            voyageRounds += Math.Max(0, spell.Dur);

            int? teleRoom = null;
            int? teleMap = null;
            int endCast = 0;
            foreach (SpellAbility ab in spell.Abilities)
            {
                switch (ab.Code)
                {
                    case TeleportRoomCode: teleRoom = ab.Value; break;
                    case TeleportMapCode:  teleMap = ab.Value; break;
                    case EndCastCode:      endCast = ab.Value; break;
                }
            }

            if (teleRoom is { } tr && tr > 0)
            {
                int map = teleMap is { } tm && tm > 0 ? tm : sourceMap;
                arrival = new RoomKey(map, tr);
                return true;
            }

            cur = endCast;   // 0 ends the chain
        }
        return false;
    }

    // Leading-integer / long parse (VB val semantics): skip leading whitespace,
    // read the digit run, ignore the rest ("50 3887" → 50; "2000000 3890" →
    // 2000000). 0 when no digits.
    private static int FirstInt(string s) => (int)FirstLong(s);

    private static long FirstLong(string s)
    {
        int i = 0;
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        int start = i;
        while (i < s.Length && s[i] is >= '0' and <= '9') i++;
        return i > start && long.TryParse(
            s.AsSpan(start, i - start), NumberStyles.Integer, CultureInfo.InvariantCulture, out long n)
            ? n : 0;
    }
}
