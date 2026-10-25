using System.Text.Json;
using MudPlay.Game.Combat;
using MudPlay.Game.Map;
using MudPlay.Net;
using MudPlay.Services;

namespace MudPlay.Game.Gmcp;

public sealed partial class GmcpIntegration
{
    private readonly PromptParser _player;
    private readonly StatParser _stats;
    private readonly RoomGraphManager _graph;
    private readonly RoomTracker _tracker;
    private readonly RoomEntityClassifier _classifier;
    private readonly CombatStateTracker _combat;
    private readonly EncumbranceParser _encumbrance;
    private readonly LogService _log;
    private bool _roomApplied;
    private bool _hadRoomAuthority;
    private int _generation;
    private Conditions.ConditionTracker? _conditions;
    private Stealth.StealthManager? _stealth;
    private MonsterHpTracker? _hp;
    private Func<string, object, Task>? _send;
    public void SetSender(Func<string, object, Task> send) => _send = send;

    public void BindConsumers(Conditions.ConditionTracker conditions, Stealth.StealthManager stealth, MonsterHpTracker hp)
    {
        _conditions = conditions;
        _stealth = stealth;
        _hp = hp;
        stealth.GmcpOwnsStatus = () => State.Owns("Anomaly.Char.Status");
        hp.GmcpRead = uid => State.Mode("Anomaly.Mob") == GmcpMode.Active && State.LiveMonsters.TryGetValue(uid, out JsonElement live)
            ? new MonsterHpRead(live.GetProperty("maxhp").GetInt32(), live.GetProperty("hp").GetInt32()) : null;
    }

    public (bool Known, DateTimeOffset? EligibleAt) LairEligibility(RoomKey room)
    {
        if (!State.Owns("Anomaly.Room.Lair") || State.Snapshot("Anomaly.Room.Lair") is not { } snapshot
            || GmcpProjection.Key(snapshot.GetProperty("room").GetString()!) != room) return (false, null);
        JsonElement lair = snapshot.GetProperty("lair");
        return (true, lair.ValueKind == JsonValueKind.Object
            && DateTimeOffset.TryParse(lair.GetProperty("eligibleAt").GetString(), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out DateTimeOffset deadline) ? deadline : null);
    }

    public async Task RefreshAsync()
    {
        if (_send is null || !State.Compatible || !State.InSession) return;
        foreach (string package in Packages.Where(p => p is not ("Anomaly.Mob" or "Anomaly.Comm.Line" or "Anomaly.Items.Detail" or "Anomaly.Items.Usable")))
            await _send("Anomaly.Request", new { package });
        if (State.Snapshot("Anomaly.Room.Occupants") is { } occupants)
            foreach (JsonElement monster in occupants.GetProperty("mobs").EnumerateArray())
                await _send("Anomaly.Request", new { package = "Anomaly.Mob.Info", uid = monster.GetProperty("uid").GetInt64() });
    }
    private bool _occupantsApplied;
    public GmcpState State { get; } = new();
    public static readonly string[] Packages = ["Anomaly.Char.Vitals", "Anomaly.Char.Status", "Anomaly.Char.Stats", "Anomaly.Room.Info", "Anomaly.Room.Occupants", "Anomaly.Room.Lair", "Anomaly.Mob", "Anomaly.Items.List", "Anomaly.Items.Detail", "Anomaly.Items.Usable", "Anomaly.Shop.List", "Anomaly.Spells.List", "Anomaly.Char.Effects", "Anomaly.Char.Abilities", "Anomaly.Group.Members", "Anomaly.Comm.Line"];

    public GmcpIntegration(PromptParser player, StatParser stats, RoomGraphManager graph, RoomTracker tracker,
        RoomEntityClassifier classifier, CombatStateTracker combat, EncumbranceParser encumbrance, LogService log)
    {
        (_player, _stats, _graph, _tracker, _classifier, _combat, _encumbrance, _log) = (player, stats, graph, tracker, classifier, combat, encumbrance, log);
        player.GmcpOwnsVitals = () => State.Owns("Anomaly.Char.Vitals");
        player.GmcpOwnsStatus = () => State.Owns("Anomaly.Char.Status");
        stats.GmcpOwnsVitals = player.GmcpOwnsVitals;
        stats.GmcpOwnsStats = () => State.Owns("Anomaly.Char.Stats");
        combat.GmcpOwnsStatus = player.GmcpOwnsStatus;
        encumbrance.GmcpOwnsStatus = player.GmcpOwnsStatus;
        classifier.GmcpOwnsOccupants = OwnsOccupants;
        classifier.TextObserved += ObserveOccupants;
        State.Diagnostic += message => log.Warn("GMCP", message);
        State.Changed += Apply;
        State.Communication += OnCommunication;
    }

    private bool OwnsOccupants() => State.Owns("Anomaly.Room.Occupants")
        && (State.Snapshot("Anomaly.Room.Occupants")!.Value.GetProperty("mobs").GetArrayLength() == 0 || State.Mode("Anomaly.Mob") == GmcpMode.Active);

    public void Configure(IReadOnlyDictionary<string, GmcpMode> modes)
    {
        foreach (string package in Packages) State.SetMode(package, modes.GetValueOrDefault(package, GmcpMode.Shadow));
    }

    public void Receive(GmcpMessage message)
    {
        State.Receive(message);
        if (message.Package is "Anomaly.Items.List" or "Anomaly.Shop.List" or "Anomaly.Session.Reset") RequestMissingCards();
    }
    public void Disconnected() => State.Disconnected();

    private void Apply(string package)
    {
        if (!State.Owns("Anomaly.Char.Status")) _conditions?.ApplyGmcpFlags(null);
        if (_generation != State.SessionGeneration && (_roomApplied || _hadRoomAuthority))
        {
            _tracker.ResetGmcpSession();
            _roomApplied = false;
            _hadRoomAuthority = false;
        }
        if (_generation != State.SessionGeneration || !State.Compatible || State.Mode("Anomaly.Room.Info") != GmcpMode.Active)
        {
            _generation = State.SessionGeneration;
            _graph.ClearGmcpRooms();
        }
        if (_roomApplied && !State.Owns("Anomaly.Room.Info"))
        {
            _roomApplied = false;
            _tracker.InvalidateGmcpRoom();
        }
        if (_occupantsApplied && !OwnsOccupants())
        {
            _occupantsApplied = false;
            _classifier.NoteGameLeft();
        }
        string applying = package;
        try
        {
            ApplyMilestone2(package, ref applying);
            if ((package == "*" || package == "Anomaly.Char.Vitals") && State.Owns("Anomaly.Char.Vitals"))
            {
                applying = "Anomaly.Char.Vitals";
                JsonElement value = State.Snapshot(applying)!.Value;
                _player.ApplyGmcpVitals(value.GetProperty("hp").GetInt32(), value.GetProperty("maxhp").GetInt32(),
                    value.GetProperty("mana").GetInt32(), value.GetProperty("maxmana").GetInt32(),
                    value.GetProperty("maxmana").GetInt32() == 0 ? ManaType.None : value.GetProperty("manaType").GetString() == "kai" ? ManaType.Kai : ManaType.Mana);
                _stats.ApplyGmcpVitals(value);
            }
            if ((package == "*" || package == "Anomaly.Char.Stats") && State.Owns("Anomaly.Char.Stats"))
            {
                applying = "Anomaly.Char.Stats";
                _stats.ApplyGmcpStats(State.Snapshot(applying)!.Value);
            }
            if ((package == "*" || package == "Anomaly.Char.Status") && State.Owns("Anomaly.Char.Status"))
            {
                applying = "Anomaly.Char.Status";
                JsonElement value = State.Snapshot(applying)!.Value;
                _player.ApplyGmcpPosition(value.GetProperty("resting").GetBoolean(), value.GetProperty("meditating").GetBoolean());
                _combat.ApplyGmcpCombat(value.GetProperty("combat").GetBoolean());
                _stealth?.ApplyGmcpStealth(value.GetProperty("sneaking").GetBoolean(), value.GetProperty("hidden").GetBoolean());
                Models.GameData.MessageFlags flags = Models.GameData.MessageFlags.None;
                foreach ((string field, Models.GameData.MessageFlags flag) in new[] {
                    ("blind", Models.GameData.MessageFlags.Blinded), ("confused", Models.GameData.MessageFlags.Confused),
                    ("poisoned", Models.GameData.MessageFlags.Poisoned), ("diseased", Models.GameData.MessageFlags.Diseased) })
                    if (value.GetProperty(field).GetBoolean()) flags |= flag;
                _conditions?.ApplyGmcpFlags(flags);
                _encumbrance.ApplyGmcp(Enum.Parse<EncumbranceLevel>(value.GetProperty("encumbrance").GetProperty("band").GetString()!, true));
            }
            if ((package == "*" || package == "Anomaly.Room.Info") && State.Owns("Anomaly.Room.Info"))
            {
                applying = "Anomaly.Room.Info";
                JsonElement value = State.Snapshot(applying)!.Value;
                Room room = GmcpProjection.Room(value, _graph.GetRoom(GmcpProjection.Key(value.GetProperty("room").GetString()!)));
                _graph.SetGmcpRoom(room);
                _roomApplied = true;
                _hadRoomAuthority = true;
                _tracker.ApplyGmcpRoom(room, GmcpProjection.Observation(value));
            }
            if ((package == "*" || package == "Anomaly.Room.Occupants" || package.StartsWith("Anomaly.Mob", StringComparison.Ordinal)) && OwnsOccupants())
            {
                applying = "Anomaly.Room.Occupants";
                _occupantsApplied = true;
                _classifier.ApplyGmcp(GmcpProjection.Entities(State));
                _hp?.NotifyGmcpChanged();
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or OverflowException or ArgumentException)
        {
            _log.Warn("GMCP", $"Cannot apply {applying}: {ex.Message}; returning affected state to text.");
            State.SetMode(applying, GmcpMode.Off);
        }
    }

    public void ObservePrompt(PromptObservation prompt)
    {
        if (State.Mode("Anomaly.Char.Vitals") != GmcpMode.Shadow || State.Snapshot("Anomaly.Char.Vitals") is not { } value) return;
        Compare("Char.Vitals.hp", value.GetProperty("hp").GetInt32(), prompt.Hp);
        Compare("Char.Vitals.mana", value.GetProperty("mana").GetInt32(), prompt.Mana);
    }

    public void ObserveRoom(RoomObservation observation)
    {
        if (_tracker.IsPeekSuppressed() || State.Mode("Anomaly.Room.Info") != GmcpMode.Shadow || State.Snapshot("Anomaly.Room.Info") is not { } value) return;
        RoomObservation expected = GmcpProjection.Observation(value);
        Compare("Room.Info.name", expected.Name, observation.Name);
        Compare("Room.Info.exits", string.Join(",", expected.Exits.Order()), string.Join(",", observation.Exits.Order()));
    }

    private void ObserveOccupants(RoomEntitiesObservation observation)
    {
        if (State.Mode("Anomaly.Room.Occupants") != GmcpMode.Shadow || !State.RosterComplete) return;
        string Names(IEnumerable<RoomEntity> entities) => string.Join("|", entities.Select(e => e.RawName.ToLowerInvariant()).Order(StringComparer.Ordinal));
        HashSet<string> visible = observation.Entities.Select(e => e.RawName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Compare("Room.Occupants", Names(GmcpProjection.Entities(State, visibleOnly: true, textVisible: visible)), Names(observation.Entities));
    }

    private void Compare<T>(string field, T expected, T observed)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, observed)) _log.Warn("GMCP shadow", $"{field}: GMCP={expected}; text={observed}");
    }
}
