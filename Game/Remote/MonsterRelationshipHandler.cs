using MudPlay.Game.Combat;
using MudPlay.Models.GameData;
using MudPlay.Models.Profile;
using MudPlay.Services;

namespace MudPlay.Game.Remote;

// The @relation remote command (user, 2026-10-25): `@relation <monster>` reports
// how this client treats a monster, and `@relation <monster> <relationship>` sets
// it for this character (MonsterRelationshipRules), as the Monster editor's
// Relationship box does. The relationship is the last word, one of the values the
// editor offers, in any case; the rest is the monster's name, or `#<number>`.
//
// Gated at Alter settings with @auto-get <item>, which changes an item's record for
// this character the same way, and @atkprio / @atkorder, which change whom we fight.
public sealed class MonsterRelationshipHandler : IDisposable
{
    public const string Command = "@relation";
    private const string LogCategory = "RemoteCmd";

    private readonly RemoteCommandManager _engine;
    private readonly MonsterRelationshipRules _rules;
    private readonly ProfileService _profile;
    private readonly GameDataCache _gameData;
    private readonly LogService? _log;
    private readonly Func<DateTimeOffset> _now;
    private readonly Dictionary<int, RemoteChange> _changes = new();
    private bool _disposed;

    // The last change a remote command made to a monster, for the bug report: its
    // tier list says where an override lives, not who put it there. Kept for the
    // character and the game-data set it was made under; a profile load or a set
    // switch starts the list again, since a monster's number means nothing outside
    // its set and the change was saved for that character alone.
    public readonly record struct RemoteChange(
        DateTimeOffset At, string Sender, RemoteChannel Channel, MonsterRelationship From, MonsterRelationship To);

    public IReadOnlyDictionary<int, RemoteChange> Changes => _changes;

    public MonsterRelationshipHandler(
        RemoteCommandManager engine, MonsterRelationshipRules rules, ProfileService profile, GameDataCache gameData,
        LogService? log = null, Func<DateTimeOffset>? now = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(gameData);
        _engine = engine;
        _rules = rules;
        _profile = profile;
        _gameData = gameData;
        _log = log;
        _now = now ?? (() => DateTimeOffset.Now);
        if (RemoteCommandCatalog.TryGetCategory(Command, out PlayerRemoteControls category))
            _engine.RegisterHandler(Command, category, Handle);
        _profile.ProfileLoaded += OnProfileLoaded;
        _gameData.ActiveSetChanged += OnActiveSetChanged;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _engine.UnregisterHandler(Command);
        _profile.ProfileLoaded -= OnProfileLoaded;
        _gameData.ActiveSetChanged -= OnActiveSetChanged;
    }

    private void OnProfileLoaded(CharacterProfile _) => _changes.Clear();
    private void OnActiveSetChanged(string? _) => _changes.Clear();

    private void Handle(RemoteCommandContext ctx)
    {
        IReadOnlyList<string> args = ctx.Args;
        if (args.Count == 0)
        {
            ctx.Refuse(Usage);
            return;
        }

        // A single word is always the name: a monster may be called anything.
        MonsterRelationship wanted = default;
        bool setting = args.Count > 1 && TryParseRelationship(args[^1], out wanted);
        string typed = string.Join(' ', setting ? args.Take(args.Count - 1) : args);
        if (setting && !_rules.CanChange)
        {
            ctx.Refuse(NoCharacter);
            return;
        }

        IReadOnlyList<int> found = _rules.Find(typed);
        if (found.Count == 0)
        {
            // The name without its last word is a monster: that word was meant as the
            // relationship and isn't one.
            ctx.Refuse(!setting && args.Count > 1 && _rules.Find(string.Join(' ', args.Take(args.Count - 1))).Count > 0
                ? $"bad relationship '{WireText.Shortened(args[^1])}': use {Choices}"
                : $"no monster named '{WireText.Shortened(typed)}'");
            return;
        }
        if (found.Count > 1)
        {
            ctx.Refuse(Ambiguous(typed, found));
            return;
        }

        int number = found[0];
        string label = Label(number);
        MonsterRelationship before = _rules.Read(number);
        if (!setting)
        {
            _log?.Debug(LogCategory, $"{Command} from {ctx.Sender}: asked about {label}");
            ctx.Reply($"{label}: {before}");
            return;
        }
        // Nothing is written for a value the monster already has: saving it would pin
        // this character to a relationship it only had from a lower tier.
        if (wanted == before)
        {
            _log?.Debug(LogCategory, $"{Command} from {ctx.Sender}: {label} already {before}");
            ctx.Reply($"{label}: already {before}");
            return;
        }

        string was = _rules.RelationshipSourceOf(number);
        MonsterRelationship after = _rules.Change(number, wanted);
        _changes[number] = new RemoteChange(_now(), ctx.Sender, ctx.Channel, before, after);
        _log?.Log(LogSeverity.Info, LogCategory,
            $"{Command} from {ctx.Sender} on {TransferChannels.NameOf(ctx.Channel)}: {label} relationship "
            + $"{before} (from {was}) -> {after}, saved for this character");
        ctx.Reply($"{label}: {before} -> {after}");
    }

    // "dao lord (#597)", or "#597" for a record with no name.
    private string Label(int number) =>
        _rules.NameOf(number) is { Length: > 0 } name ? $"{WireText.Ascii(name)} (#{number})" : $"#{number}";

    // The values the Monster editor's Relationship box offers, read by their names.
    // A number is not taken: the editor never shows one.
    private static bool TryParseRelationship(string word, out MonsterRelationship relationship)
    {
        foreach (MonsterRelationship r in Enum.GetValues<MonsterRelationship>())
            if (string.Equals(r.ToString(), word, StringComparison.OrdinalIgnoreCase))
            {
                relationship = r;
                return true;
            }
        relationship = default;
        return false;
    }

    private static string[] Words =>
        Enum.GetValues<MonsterRelationship>().Select(r => r.ToString().ToLowerInvariant()).ToArray();

    // "neutral|enemy|friend|flee|hangup": the usage reply's and the @help entry's list.
    public static string WordChoices => string.Join('|', Words);

    private static readonly string Choices = string.Join(", ", Words[..^1]) + " or " + Words[^1];
    private static readonly string Usage = $"usage: {Command} <monster> [{WordChoices}]";
    private const string NoCharacter = "no character loaded";

    // The refusal for a name that fits several monsters, each with the number that
    // names it alone: same-named monsters can't be told apart any other way.
    private string Ambiguous(string typed, IReadOnlyList<int> found) =>
        WireText.Listed($"monster name '{WireText.Shortened(typed)}' matches {found.Count}",
            found.Select(number => _rules.NameOf(number) is { Length: > 0 } name
                ? $"'{WireText.Ascii(name)}' #{number}"
                : $"#{number}").ToList());
}
