using System.Text.Json;
using MudPlay.Game.Combat;
using MudPlay.Game.Map;

namespace MudPlay.Game.Gmcp;

// Shadow as a fallback (user, 2026-10-29): the text stays the source, and the
// server's data answers only where the text hasn't, or can't.
//   - Until the text's first reading of a kind this session (a prompt, the stat
//     screen, `sp`, `i`, `par`), a Shadow package's snapshot is written through its
//     owner as Active writes it; from that reading on, Shadow only compares.
//   - The server's room places us where the map would otherwise ask the game (`rm`,
//     `sys st`) or give up as Lost (GmcpPositionLocator, RoomTracker.ServerRoom).
//   - A monster the game data gives no max HP takes the server's reading.
// Active is untouched: there the server's data is the source and the text secondary.
public sealed partial class GmcpIntegration
{
    // The text readings that end a package's fallback.
    private enum TextRead { Prompt, StatScreen, Spells, Inventory, Party }

    // The text readings had this server session; a new session starts over.
    private readonly HashSet<TextRead> _textRead = new();
    // What the fallback has written this session, and the reading that ends it.
    private readonly Dictionary<string, TextRead> _fallbackUsed = new(StringComparer.Ordinal);
    private int _fallbackGeneration = -1;
    // The book holds the server's list while this is set (SpellbookState.ApplyGmcpSpells).
    private bool _spellsFallback;
    // The server's party being written raises ParReplyRead as a `par` reply does.
    private bool _applyingServerParty;

    // The server's room for a locate: Room.Info not Off, its snapshot in hand for the
    // room the server has us in, and on the map (a room only the server knows can't
    // be walked from). shownName is the display being read, when there is one: a
    // server room of another name is not the room shown.
    public RoomKey? ServerRoom(string? shownName = null)
    {
        if (!State.Compatible || !State.InSession || State.Mode("Anomaly.Room.Info") == GmcpMode.Off
            || State.Snapshot("Anomaly.Room.Info") is not { } room)
            return null;
        RoomKey key;
        try { key = GmcpProjection.Key(room.GetProperty("room").GetString()!); }
        catch (FormatException) { return null; } // a room the contract can't name: no locate, the text goes on
        if (_graph.GetRoom(key) is null) return null;
        if (shownName is not null && !string.Equals(room.GetProperty("name").GetString()?.Trim(), shownName.Trim(),
                StringComparison.OrdinalIgnoreCase))
            return null;
        return key;
    }

    private void ApplyFallback(string package, ref string applying)
    {
        if (_fallbackGeneration != State.SessionGeneration)
        {
            _fallbackGeneration = State.SessionGeneration;
            _textRead.Clear();
            _fallbackUsed.Clear();
        }
        applying = "Anomaly.Spells.List";
        HandBackFallbackSpells();
        if (!State.Compatible || !State.InSession) return;

        if (Falls(package, "Anomaly.Char.Vitals", TextRead.Prompt))
        {
            applying = "Anomaly.Char.Vitals";
            ApplyPromptVitals(State.Snapshot(applying)!.Value);
            Filled("HP and mana", TextRead.Prompt);
        }
        if (Falls(package, "Anomaly.Char.Vitals", TextRead.StatScreen))
        {
            applying = "Anomaly.Char.Vitals";
            // A gain is the text's to count while it reads the vitals.
            _stats.ApplyGmcpVitals(State.Snapshot(applying)!.Value);
            Filled("level and experience", TextRead.StatScreen);
        }
        if (Falls(package, "Anomaly.Char.Stats", TextRead.StatScreen))
        {
            applying = "Anomaly.Char.Stats";
            _stats.ApplyGmcpStats(State.Snapshot(applying)!.Value);
            Filled("stats", TextRead.StatScreen);
        }
        if (Falls(package, "Anomaly.Spells.List", TextRead.Spells))
        {
            applying = "Anomaly.Spells.List";
            _book?.ApplyGmcpSpells(State.Snapshot(applying));
            _spellsFallback = true;
            Filled("spell list", TextRead.Spells);
        }
        if (Falls(package, "Anomaly.Items.List", TextRead.Inventory))
        {
            applying = "Anomaly.Items.List";
            _inventory?.ApplyGmcpItems(State.Snapshot(applying)!.Value);
            Filled("inventory", TextRead.Inventory);
        }
        if (Falls(package, "Anomaly.Group.Members", TextRead.Party))
        {
            applying = "Anomaly.Group.Members";
            ApplyServerParty(State.Snapshot(applying)!.Value);
            Filled("party", TextRead.Party);
        }
    }

    private bool Falls(string changed, string package, TextRead until) =>
        (changed == "*" || changed == package) && State.Mode(package) == GmcpMode.Shadow
        && State.Snapshot(package) is not null && !_textRead.Contains(until);

    private void Filled(string what, TextRead until)
    {
        if (_fallbackUsed.TryAdd(what, until))
            _log.Info("GMCP", $"Shadow fallback: the server's {what} in use until the text's first {Describe(until)}.");
    }

    private void NoteTextRead(TextRead read)
    {
        if (!_textRead.Add(read)) return;
        foreach ((string what, TextRead until) in _fallbackUsed)
            if (until == read) _log.Info("GMCP", $"Shadow fallback: the text's {Describe(read)} is in; {what} comes from the text again.");
    }

    private static string Describe(TextRead read) => read switch
    {
        TextRead.Prompt => "prompt",
        TextRead.StatScreen => "stat screen",
        TextRead.Spells => "spell list",
        TextRead.Inventory => "inventory",
        _ => "party list",
    };

    // The book goes back to the text once the text has read the spell list, or the
    // server's list is no longer one Shadow falls back on. A list now Active is the
    // server's to keep.
    private void HandBackFallbackSpells()
    {
        if (!_spellsFallback) return;
        if (State.Owns("Anomaly.Spells.List"))
        {
            _spellsFallback = false;
            return;
        }
        if (State.Compatible && State.InSession && State.Mode("Anomaly.Spells.List") == GmcpMode.Shadow
            && State.Snapshot("Anomaly.Spells.List") is not null && !_textRead.Contains(TextRead.Spells))
            return;
        _spellsFallback = false;
        _book?.ApplyGmcpSpells(null);
    }

    private void ApplyServerParty(JsonElement value)
    {
        _applyingServerParty = true;
        try { _party?.ApplyGmcpGroup(value); }
        finally { _applyingServerParty = false; }
    }

    private void OnPartyRead()
    {
        if (!_applyingServerParty) NoteTextRead(TextRead.Party);
    }

    // The server's one monster of a name in the room, for a monster the game data
    // gives no max HP (MonsterHpTracker.ServerIdFor). With two of a name, the text's
    // order can't be matched to the server's, so neither is taken.
    private long? ServerMonsterNamed(string name)
    {
        if (!State.Compatible || !State.InSession || State.Mode("Anomaly.Room.Occupants") == GmcpMode.Off
            || State.Mode("Anomaly.Mob") == GmcpMode.Off)
            return null;
        long? found = null;
        foreach (RoomEntity entity in GmcpProjection.Entities(State))
        {
            if (entity.Kind != EntityKind.Monster || !string.Equals(entity.RawName, name, StringComparison.OrdinalIgnoreCase)) continue;
            if (found is not null) return null;
            found = entity.RuntimeId;
        }
        return found;
    }

    private string DescribeFallback() =>
        $"Shadow fallback: text read {(_textRead.Count == 0 ? "nothing yet" : string.Join(", ", _textRead.Order().Select(Describe)))}; "
        + $"server data written {(_fallbackUsed.Count == 0 ? "none" : string.Join(", ", _fallbackUsed.Keys))}; "
        + $"spell list held: {_spellsFallback}";
}
