using MudPlay.Game.Spells;
using MudPlay.Services;
using MudPlay.Terminal;

namespace MudPlay.Game.Combat;

// A monster in the room casting a summon spell mid-fight ("The fat half-orc sentry shouts
// for aid!") puts another monster in the room, but nothing on the wire announces it: no
// arrival line, no fresh "Also here:". The roster would stay at the summoner alone, so
// killing it emptied the roster, dropped combat and let the client rest or loot while the
// summoned monster swung at us (GAME_MECHANICS "Mid-fight summons"). On the summon line
// this asks for the room re-display the combat code already uses to resync its roster,
// so the summoned monster is on the roster before its summoner dies.
//
// Only a summon cast by a monster already on the roster counts: that's the case the
// roster can't otherwise see, and it keeps a line naming someone elsewhere, or no one,
// from sending anything.
public sealed class MonsterSummonWatcher : IDisposable
{
    private readonly MessageRouter _router;
    private readonly RoomEntityClassifier _classifier;
    private readonly Func<SummonLineSet> _build;
    private readonly Func<string, bool> _requestRoomRefresh;
    private readonly LogService? _log;
    private SummonLineSet? _lines;
    private bool _disposed;

    private const string LogCategory = "Summon";

    // requestRoomRefresh sends the debounced bare-CR room re-display and returns whether
    // one went out. build makes the summon wordings from the Spells table and the message
    // catalogue; Invalidate drops them after a set switch or a message edit.
    public MonsterSummonWatcher(MessageRouter router, RoomEntityClassifier classifier,
        Func<SummonLineSet> build, Func<string, bool> requestRoomRefresh, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(classifier);
        ArgumentNullException.ThrowIfNull(build);
        ArgumentNullException.ThrowIfNull(requestRoomRefresh);
        _router = router;
        _classifier = classifier;
        _build = build;
        _requestRoomRefresh = requestRoomRefresh;
        _log = log;
        _router.LineDispatched += OnLine;
    }

    public void Invalidate() => _lines = null;

    private void OnLine(LineExtractor.EmittedLine line)
    {
        if (line.IsPromptLine || line.IsChat) return;
        if (_classifier.Current is not { } room || room.Entities.Count == 0) return;
        _lines ??= _build();
        if (!_lines.TryMatch(line.Text, out string? source) || source is null) return;

        RoomEntity? summoner = FindMonster(room, source);
        if (summoner is null)
        {
            _log?.Debug(LogCategory, $"summon line names '{source}', not a monster on the roster — ignored");
            return;
        }

        bool sent = _requestRoomRefresh($"monster summon by '{summoner.Value.RawName}'");
        _log?.Log(LogSeverity.Info, LogCategory, sent
            ? $"'{summoner.Value.RawName}' cast a summon — re-displaying the room so the summoned monster joins the roster"
            : $"'{summoner.Value.RawName}' cast a summon — room re-display skipped (dark room or one just sent)");
    }

    // The roster names a monster in full ("fat half-orc sentry") and resolves it to its
    // record name ("half-orc sentry"); the summon line uses the name as shown.
    private static RoomEntity? FindMonster(RoomEntitiesObservation room, string source)
    {
        string name = source.Trim();
        foreach (RoomEntity e in room.Entities)
        {
            if (e.Kind != EntityKind.Monster) continue;
            if (e.RawName.Equals(name, StringComparison.OrdinalIgnoreCase)
                || e.ResolvedName.Equals(name, StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(" " + e.ResolvedName, StringComparison.OrdinalIgnoreCase))
                return e;
        }
        return null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _router.LineDispatched -= OnLine;
    }
}
