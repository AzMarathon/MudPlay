using MudPlay.Services;

namespace MudPlay.Game.Combat;

// A running HP estimate for each monster in the room: its max HP, less the damage the
// round ledger saw it take, plus its regen — sharpening the coarse wound band a `look`
// gives into a single number.
//
// Instances follow the room roster (NoteRoomEntities). Two monsters sharing a name
// are told apart by their order in "Also here:": an attack or a `look` at a shared
// name goes to the FIRST of them (user, 2026-09-29), so damage and looks land on the
// first instance, a death removes the first, and an arrival joins at the end.
//
// Regen: a hurt monster regains its HPRegen every regen tick — 30 s on Paradigm, 90 s
// on Stock (GAME_MECHANICS "Monster HP regen"). The tick's phase isn't visible, so it's
// counted from when the monster was first hurt, and re-timed whenever a `look` shows a
// tick fired: the band rose since the last look, or the damage we saw says the band
// should have dropped and it didn't (user, 2026-09-29). A `look`'s wound band is the
// truth: an estimate outside it is pulled to its nearest edge, which also absorbs a
// heal or damage the ledger missed.
//
// UI thread only (fed from the room classifier and the round ledger).
public sealed class MonsterHpTracker
{
    public const string LogCategory = "MonsterHp";

    public static readonly TimeSpan ParadigmRegenInterval = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan StockRegenInterval = TimeSpan.FromSeconds(90);

    private readonly Func<int, int?> _maxHp;
    private readonly Func<int, int> _regen;
    private readonly Func<bool> _isParadigm;
    private readonly Func<DateTimeOffset> _now;
    private readonly LogService? _log;

    private List<Instance> _room = new();

    // Raised when an instance's estimate moves on damage, with its name — the status
    // bar re-reads the looked-at target.
    public event Action<string>? EstimateChanged;

    public MonsterHpTracker(Func<int, int?> maxHp, Func<int, int> regen, Func<bool> isParadigm,
        Func<DateTimeOffset>? clock = null, LogService? log = null)
    {
        _maxHp = maxHp;
        _regen = regen;
        _isParadigm = isParadigm;
        _now = clock ?? (static () => DateTimeOffset.Now);
        _log = log;
    }

    private TimeSpan RegenInterval => _isParadigm() ? ParadigmRegenInterval : StockRegenInterval;

    // The room's occupants changed. Carry each monster's state over by name and order:
    // a death drops the first of that name (the one we were hitting), anything else
    // that shrinks a name's count drops the last, and newcomers join at full HP.
    public void NoteRoomEntities(RoomEntitiesObservation obs)
    {
        if (obs.Source == RoomObservationSource.RoomChange) { _room = new(); return; }

        Dictionary<string, Queue<Instance>> old = new(StringComparer.OrdinalIgnoreCase);
        foreach (Instance i in _room)
        {
            if (!old.TryGetValue(i.Name, out Queue<Instance>? q)) old[i.Name] = q = new();
            q.Enqueue(i);
        }

        Dictionary<string, int> newCount = new(StringComparer.OrdinalIgnoreCase);
        foreach (RoomEntity e in obs.Entities)
            if (e.Kind == EntityKind.Monster && e.MonsterNumber is not null)
                newCount[e.RawName] = newCount.GetValueOrDefault(e.RawName) + 1;
        foreach ((string name, Queue<Instance> q) in old)
        {
            int keep = newCount.GetValueOrDefault(name);
            List<Instance> list = q.ToList();
            while (list.Count > keep)
            {
                if (obs.Source == RoomObservationSource.Death) list.RemoveAt(0);
                else list.RemoveAt(list.Count - 1);
            }
            old[name] = new Queue<Instance>(list);
        }

        List<Instance> next = new();
        foreach (RoomEntity e in obs.Entities)
        {
            if (e.Kind != EntityKind.Monster || e.MonsterNumber is not int number) continue;
            if (old.TryGetValue(e.RawName, out Queue<Instance>? q) && q.Count > 0)
            {
                next.Add(q.Dequeue());
                continue;
            }
            if (_maxHp(number) is not int max) continue;
            next.Add(new Instance(e.RawName, e.ResolvedName, max, _regen(number)));
        }
        _room = next;
    }

    // A damage line the round ledger read. Damage to a monster lands on the first of
    // that name in the room.
    public void NoteDamage(DamageAttribution a)
    {
        if (a.Target is not { } name || a.Target == DamageLineAttributor.Self || a.Amount <= 0) return;
        if (First(name) is not { } i) return;
        DateTimeOffset now = _now();
        Advance(i, now);
        if (i.RegenFrom is null) i.RegenFrom = now;
        i.Hp = Math.Max(0, i.Hp - a.Amount);
        i.Hurt = true;
        EstimateChanged?.Invoke(i.Name);
    }

    // Our room spell hit every monster in the room for about amount.
    public void NoteAreaDamage(int amount)
    {
        if (amount <= 0) return;
        DateTimeOffset now = _now();
        foreach (Instance i in _room)
        {
            Advance(i, now);
            if (i.RegenFrom is null) i.RegenFrom = now;
            i.Hp = Math.Max(0, i.Hp - amount);
            i.Hurt = true;
            EstimateChanged?.Invoke(i.Name);
        }
    }

    // The running estimate for the first monster of this name, or null when there's
    // no such monster.
    public int? Estimate(string name)
    {
        if (First(name) is not { } i) return null;
        Advance(i, _now());
        return i.Hp;
    }

    // A `look` read the first monster of this name at band: the band is the truth, so
    // the estimate is pulled inside it. Null when the monster isn't tracked.
    public MonsterHpRead? OnLook(string name, MonsterHpEstimate band)
    {
        if (First(name) is not { } i || band.Mortal) return null;
        DateTimeOffset now = _now();
        Advance(i, now);
        int before = i.Hp;

        // A regen tick fired: the band rose since the last look, or the damage we saw
        // put the estimate below the band. Pay it if the clock hasn't since the last
        // look, and re-time the cycle from now.
        bool bandRose = i.LastBand is { } prev && band.Low > prev.High;
        bool regenSeen = i.Regen > 0 && (bandRose || i.Hp < band.Low);
        if (regenSeen)
        {
            if (i.TicksSinceLook == 0) i.Hp = Math.Min(i.MaxHp, i.Hp + i.Regen);
            i.RegenFrom = now;
            _log?.Info(LogCategory, $"'{i.Name}' regen tick seen ({(bandRose ? "band rose" : "band held up")}) — re-timing regen from now");
        }

        i.Hp = Math.Clamp(i.Hp, band.Low, band.High);
        if (i.Hp < i.MaxHp) i.RegenFrom ??= now;
        else i.RegenFrom = null;
        i.LastBand = band;
        i.TicksSinceLook = 0;
        if (i.Hp != before)
            _log?.Info(LogCategory, $"look moved '{i.Name}' from ~{before} to ~{i.Hp} (band {band.Describe()})");
        return new MonsterHpRead(i.MaxHp, i.Hp);
    }

    // One line per tracked monster, for the bug report.
    public IReadOnlyList<string> Describe()
    {
        DateTimeOffset now = _now();
        return _room.Select(i =>
        {
            Advance(i, now);
            return $"{i.Name}: ~{i.Hp}/{i.MaxHp} (regen {i.Regen} per tick, {(i.Hurt ? "hurt" : "untouched")})";
        }).ToList();
    }

    // The first monster shown under this name, falling back to its base name (a look
    // may echo either).
    private Instance? First(string name)
    {
        foreach (Instance i in _room)
            if (i.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return i;
        foreach (Instance i in _room)
            if (i.BaseName.Equals(name, StringComparison.OrdinalIgnoreCase)) return i;
        return null;
    }

    // Pay the regen ticks due since the monster was hurt.
    private void Advance(Instance i, DateTimeOffset now)
    {
        if (i.RegenFrom is not { } from || i.Regen <= 0 || i.Hp >= i.MaxHp) return;
        TimeSpan interval = RegenInterval;
        long ticks = (long)((now - from).Ticks / interval.Ticks);
        if (ticks <= 0) return;
        i.Hp = (int)Math.Min(i.MaxHp, i.Hp + ticks * i.Regen);
        i.TicksSinceLook += (int)ticks;
        i.RegenFrom = i.Hp >= i.MaxHp ? null : from + TimeSpan.FromTicks(ticks * interval.Ticks);
    }

    private sealed class Instance(string name, string baseName, int maxHp, int regen)
    {
        public string Name { get; } = name;
        public string BaseName { get; } = baseName;
        public int MaxHp { get; } = maxHp;
        public int Regen { get; } = regen;
        public int Hp = maxHp;
        public bool Hurt;
        // When regen started counting: first hurt, or the last tick paid.
        public DateTimeOffset? RegenFrom;
        // The last look's band, and the regen ticks the clock paid since it.
        public MonsterHpEstimate? LastBand;
        public int TicksSinceLook;
    }
}
