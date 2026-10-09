using MudPlay.Game.Calculators;
using MudPlay.Services;

namespace MudPlay.Game.Spells;

// ListedEffectReader against the live message catalogue and Spells table, keeping
// the answer for the effect list it was last asked about. The plan baseline is
// resolved again on every change to PlayerStats, and matching the list walks the
// whole catalogue; one `stat` screen publishes one list, so until the next screen,
// a catalogue edit or a set switch the answer is the same.
public sealed class ListedEffectCatalog
{
    private readonly MessageStore _messages;
    private readonly GameDataCache _gameData;
    private IReadOnlyList<StatusEffectLine>? _lines;
    private IReadOnlyList<ListedEffect> _effects = Array.Empty<ListedEffect>();

    public ListedEffectCatalog(MessageStore messages, GameDataCache gameData)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(gameData);
        _messages = messages;
        _gameData = gameData;
        _messages.Messages.CollectionChanged += (_, _) => _lines = null;
        _gameData.ActiveSetChanged += _ => _lines = null;
    }

    public IReadOnlyList<ListedEffect> Read(IReadOnlyList<StatusEffectLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (lines.Count == 0) return Array.Empty<ListedEffect>();
        if (ReferenceEquals(lines, _lines)) return _effects;
        _effects = ListedEffectReader.Read(lines, _gameData.ActiveRealm, _messages.Messages,
                                           number => _gameData.FindRowByNumber("Spells", number));
        _lines = lines;
        return _effects;
    }
}
