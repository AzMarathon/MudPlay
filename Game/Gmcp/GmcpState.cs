using System.Text.Json;
using MudPlay.Net;

namespace MudPlay.Game.Gmcp;

// Accessed on the session's dispatcher, in the same order as terminal bytes.
public sealed class GmcpState
{
    public const string ContractVersion = GmcpProtocol.ContractVersion;
    private readonly Dictionary<string, JsonElement> _snapshots = new(StringComparer.Ordinal);
    private readonly Dictionary<int, JsonElement> _itemCards = new();
    private readonly Dictionary<int, JsonElement> _itemUsability = new();
    private bool _communicationReady;
    public IReadOnlyDictionary<int, JsonElement> ItemCards => _itemCards;
    public IReadOnlyDictionary<int, JsonElement> ItemUsability => _itemUsability;
    public event Action<JsonElement>? Communication;
    public bool OwnsCommunication => Compatible && InSession && _communicationReady && Mode("Anomaly.Comm.Line") == GmcpMode.Active;
    private bool HasMilestone2 => Version.TryParse(ServerContract, out Version? version) && version >= new Version(1, 1, 0);
    private readonly Dictionary<long, JsonElement> _static = new();
    private readonly Dictionary<long, JsonElement> _live = new();
    private readonly Dictionary<string, GmcpMode> _modes = new(StringComparer.Ordinal);
    public string? Boot { get; private set; }
    public string? ServerContract { get; private set; }
    public string? CurrentRoom { get; private set; }
    public bool Compatible { get; private set; }
    public bool InSession { get; private set; }
    public int SessionGeneration { get; private set; }
    public event Action<string>? Changed;
    public event Action<string>? Diagnostic;
    public IReadOnlyDictionary<long, JsonElement> StaticMonsters => _static;
    public IReadOnlyDictionary<long, JsonElement> LiveMonsters => _live;
    public JsonElement? Snapshot(string package) => _snapshots.TryGetValue(package, out JsonElement value) ? value : null;
    public GmcpMode Mode(string package) => _modes.GetValueOrDefault(package, GmcpMode.Shadow);

    public void SetMode(string package, GmcpMode mode)
    {
        _modes[package] = mode;
        Changed?.Invoke(package);
    }

    public bool Owns(string package) => Compatible && InSession && Mode(package) == GmcpMode.Active && _snapshots.ContainsKey(package)
        && (package != "Anomaly.Room.Occupants" || RosterComplete);

    public bool RosterComplete => Snapshot("Anomaly.Room.Occupants") is { } occupants
        && occupants.GetProperty("mobs").EnumerateArray().All(m => _static.ContainsKey(m.GetProperty("uid").GetInt64()) && _live.ContainsKey(m.GetProperty("uid").GetInt64()));

    public void Disconnected()
    {
        ClearAll(newSession: false);
        Boot = null;
        ServerContract = null;
        Compatible = false;
        InSession = false;
        Changed?.Invoke("*");
    }

    public void Receive(GmcpMessage message)
    {
        string package = message.Package;
        JsonElement value = message.Payload;
        if (!GmcpContract.IsKnown(package)) return;
        if (!GmcpContract.IsValid(package, value))
        {
            if (package is "Anomaly.Server" or "Anomaly.Session.Reset") Disconnected();
            else Invalidate(package);
            Diagnostic?.Invoke($"Rejected {package}: invalid contract payload; text fallback restored for affected state.");
            Changed?.Invoke(package);
            return;
        }
        if (package == "Anomaly.Server")
        {
            string boot = value.GetProperty("boot").GetString()!;
            if (Boot != boot) ClearAll();
            Boot = boot;
            ServerContract = value.GetProperty("contract").GetString();
            Compatible = Version.TryParse(ServerContract, out Version? version) && version.Major == 1;
            InSession = false;
            if (!Compatible) { ClearAll(); Diagnostic?.Invoke($"GMCP contract {ServerContract} is incompatible with {ContractVersion}; using text."); }
            Changed?.Invoke("*");
            return;
        }
        if (!Compatible) return;
        if (package == "Anomaly.Session.Reset")
        {
            switch (value.GetProperty("scope").GetString())
            {
                case "all": ClearAll(); InSession = value.GetProperty("reason").GetString() != "leave"; _communicationReady = InSession && HasMilestone2; break;
                case "package": Invalidate(value.GetProperty("package").GetString()!); break;
                case "room": ClearRoom(); CurrentRoom = value.GetProperty("room").GetString(); break;
            }
            Changed?.Invoke("*");
            return;
        }
        if (!InSession) return;
        if (value.TryGetProperty("room", out JsonElement room) && room.GetString() != CurrentRoom) return;
        if (package == "Anomaly.Comm.Line")
        {
            // A reset or malformed event latches text ownership until a new session.
            // Events have no baseline that can safely arbitrate a mid-message switch.
            if (_communicationReady) Communication?.Invoke(value.Clone());
            return;
        }
        if (package is "Anomaly.Items.Detail" or "Anomaly.Items.Usable" && !value.GetProperty("num").TryGetInt32(out _))
        {
            Invalidate(package);
            Diagnostic?.Invoke($"Rejected {package}: item number exceeds the supported range.");
            Changed?.Invoke(package);
            return;
        }
        if (package == "Anomaly.Items.Detail") _itemCards[value.GetProperty("num").GetInt32()] = value.Clone();
        if (package == "Anomaly.Items.Usable") _itemUsability[value.GetProperty("num").GetInt32()] = value.Clone();
        if ((package.StartsWith("Anomaly.Char.", StringComparison.Ordinal) || package is "Anomaly.Items.List" or "Anomaly.Spells.List" or "Anomaly.Shop.List")
            && (!_snapshots.TryGetValue(package, out JsonElement previous) || !JsonElement.DeepEquals(previous, value)))
            _itemUsability.Clear();
        if (package == "Anomaly.Mob.Info") _static[value.GetProperty("uid").GetInt64()] = value.Clone();
        else if (package == "Anomaly.Mob.Update")
        {
            long uid = value.GetProperty("uid").GetInt64();
            bool full = value.GetProperty("full").GetBoolean();
            if (!full && !_live.ContainsKey(uid))
            {
                Diagnostic?.Invoke($"Ignored delta without baseline for monster {uid}.");
                return;
            }
            if (full) _live[uid] = value.Clone();
            else
            {
                Dictionary<string, JsonElement> merged = _live[uid].EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
                foreach (JsonProperty field in value.EnumerateObject()) merged[field.Name] = field.Value;
                _live[uid] = JsonSerializer.SerializeToElement(merged);
            }
        }
        else if (package == "Anomaly.Mob.Gone")
        {
            long uid = value.GetProperty("uid").GetInt64();
            _live.Remove(uid);
            if (value.GetProperty("why").GetString() != "left") _static.Remove(uid);
            if (Snapshot("Anomaly.Room.Occupants") is { } occupants)
                _snapshots["Anomaly.Room.Occupants"] = JsonSerializer.SerializeToElement(new
                {
                    room = CurrentRoom,
                    mobs = occupants.GetProperty("mobs").EnumerateArray().Where(m => m.GetProperty("uid").GetInt64() != uid).ToArray(),
                    players = occupants.GetProperty("players")
                });
        }
        else
        {
            _snapshots[package] = value.Clone();
            if (package == "Anomaly.Room.Occupants")
            {
                HashSet<long> present = value.GetProperty("mobs").EnumerateArray().Select(m => m.GetProperty("uid").GetInt64()).ToHashSet();
                foreach (long uid in _live.Keys.Where(uid => !present.Contains(uid)).ToArray()) _live.Remove(uid);
            }
        }
        Changed?.Invoke(package);
    }

    private void Invalidate(string family)
    {
        if (family.StartsWith("Anomaly.Char", StringComparison.Ordinal) || family == "Anomaly.Items.List") _itemUsability.Clear();
        if (family is "Anomaly.Items" or "Anomaly.Items.Detail") _itemCards.Clear();
        if (family is "Anomaly.Items" or "Anomaly.Items.Usable") _itemUsability.Clear();
        if (family is "Anomaly.Comm" or "Anomaly.Comm.Line") _communicationReady = false;
        foreach (string key in _snapshots.Keys.Where(k => k == family || k.StartsWith(family + ".", StringComparison.Ordinal)).ToArray()) _snapshots.Remove(key);
        if (family == "Anomaly.Mob" || family == "Anomaly.Mob.Info") { _static.Clear(); _live.Clear(); }
        if (family == "Anomaly.Mob.Update") _live.Clear();
        if (family == "Anomaly.Room") { ClearRoom(); CurrentRoom = null; }
    }

    private void ClearRoom()
    {
        foreach (string key in _snapshots.Keys.Where(k => (k.StartsWith("Anomaly.Room.", StringComparison.Ordinal) || k.StartsWith("Anomaly.Shop.", StringComparison.Ordinal))).ToArray()) _snapshots.Remove(key);
        _live.Clear();
    }

    private void ClearAll(bool newSession = true) { if (newSession) SessionGeneration++; _snapshots.Clear(); _itemCards.Clear(); _itemUsability.Clear(); _communicationReady = false; _static.Clear(); _live.Clear(); CurrentRoom = null; }
}
