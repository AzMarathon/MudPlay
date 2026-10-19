using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using MudPlay.Models.Profile;
using MudPlay.Models.Settings;

namespace MudPlay.Services;

// Keeps every number in a settings file inside what its field may hold, as the
// file is read. global.json, a bbs.json and a profile.json are the files a user
// edits by hand, imports or carries between machines, and a value nobody would
// set (a 100000-column terminal, a cleanup window of two billion days) crashed
// startup or had it allocate tens of gigabytes. JsonStore.Load hands each of the
// three to Deserialize, which walks the file against its DTO's shape before
// deserializing it:
//
//   - a number outside its field's SettingRange is pulled to the nearer end;
//   - a number its field's type can't hold (1e300 in an int) is pulled into the
//     type's range, where it would otherwise fail the whole file;
//   - a fraction in a whole-number field is rounded, for the same reason;
//   - anything else one field holds that its type can't take (text in a number,
//     a list where an object goes) is dropped, so that field takes its default.
//
// One bad field used to fail the whole file: a profile that wouldn't parse came up
// as the default profile, the character's whole workspace missing for the session.
// Now only that field is lost. The values used are what the client reads, and what
// the next save of that file writes; the file itself is not rewritten here. Each
// value moved or dropped is raised once on Clamped (AppServices writes it to the
// program log at Warn), naming the field, the tier, the value found and the value
// used. A file that isn't JSON at all still fails to load, as it always did.
public static class SettingsValueGuard
{
    // Raised with a line for the program log per value moved. A line already raised
    // is not raised again while it is remembered: the same file is read again on
    // every profile listing, heartbeat merge and reload.
    public static event Action<string>? Clamped;

    // The tab-keyed sections every tier's Settings dictionary holds, by key: what a
    // section's values are checked against. A key not listed is left as it is.
    public static readonly IReadOnlyDictionary<string, Type> Sections = new Dictionary<string, Type>(StringComparer.Ordinal)
    {
        ["AutoLair"] = typeof(AutoLairSettings),
        ["AutoLight"] = typeof(AutoLightSettings),
        ["AutoTrainer"] = typeof(AutoTrainerSettings),
        ["Cash"] = typeof(CashSettings),
        ["Combat"] = typeof(CombatSettings),
        ["Confirm"] = typeof(ConfirmSettings),
        ["ContextMenu"] = typeof(ContextMenuSettings),
        ["General"] = typeof(GeneralSettings),
        ["Health"] = typeof(HealthSettings),
        ["HelpWebsites"] = typeof(HelpWebsitesSettings),
        ["LogDiagnostics"] = typeof(LogDiagnosticsSettings),
        ["Other"] = typeof(OtherSettings),
        ["Party"] = typeof(PartySettings),
        [PeriodicDamageRoomSpellSettings.TabKey] = typeof(PeriodicDamageRoomSpellSettings),
        ["Pvp"] = typeof(PvpSettings),
        ["Sounds"] = typeof(SoundSettings),
        ["Spells"] = typeof(SpellsSettings),
        ["Statline"] = typeof(StatlineSettings),
        ["Talk"] = typeof(TalkSettings),
        ["Teleports"] = typeof(TeleportSettings),
        ["Toolbar"] = typeof(ToolbarSettings),
    };

    // The tier a file deserialized as type is, or null when type is not one of the
    // settings files.
    public static string? TierOf(Type type) =>
        type == typeof(GlobalSettings) ? "Global"
        : type == typeof(BbsProfile) ? "BBS"
        : type == typeof(CharacterProfile) ? "Character"
        : null;

    // Deserialize json, read from path, as T with its numbers kept in range and
    // any field it can't take dropped.
    public static T? Deserialize<T>(string json, string path, string tier) where T : class
    {
        List<SettingsClamp> clamps = new();
        T? result;
        try
        {
            JsonNode? node = JsonNode.Parse(json, nodeOptions: null, NodeDocumentOptions);
            Clamp(node, typeof(T), clamps);
            result = (T?)DeserializeDroppingBadFields(node, typeof(T), "", clamps);
        }
        catch (ArgumentException ex)
        {
            // A property named twice: the node tree refuses it where the serializer
            // takes the last, so the file is read as it always was, unchecked.
            Raise($"{tier} settings in {path} couldn't be checked for values out of range ({ex.Message}); read as written.");
            return JsonSerializer.Deserialize<T>(json, JsonStore.Options);
        }
        foreach (SettingsClamp c in clamps)
            Raise($"{tier} setting {c.Field} in {path} was {c.Found}, {c.Why}; using {c.Used} (written back on the next save).");
        return result;
    }

    // Pull every number in node, read as type, into range, in place. What was moved
    // is added to clamps.
    private static void Clamp(JsonNode? node, Type type, List<SettingsClamp> clamps) =>
        Fix(node, type, range: null, path: "", clamps);

    // The most fields one read drops before it gives up and lets the file fail as a
    // whole: past that it isn't a field or two gone bad, it's another kind of file.
    private const int MostFieldsDropped = 100;

    // Deserialize node as type; a field the serializer refuses is taken out (its
    // path is in the refusal) and the rest tried again.
    private static object? DeserializeDroppingBadFields(JsonNode? node, Type type, string at, List<SettingsClamp> clamps)
    {
        for (int dropped = 0; ; dropped++)
        {
            try
            {
                return node.Deserialize(type, JsonStore.Options);
            }
            catch (JsonException ex) when (dropped < MostFieldsDropped && Detach(node, ex.Path) is { } removed)
            {
                clamps.Add(new SettingsClamp(Join(at, removed.Path), Shown(removed.Node),
                    $"which it can't hold ({Reason(ex)})", "its default"));
            }
        }
    }

    // A section is held as raw JSON and read by its own reader when it is wanted, so
    // it is tried here as its DTO: a field it can't take is dropped now, instead of
    // every read of the section falling back to defaults.
    private static void CheckSection(JsonObject sections, string key, Type type, string at, List<SettingsClamp> clamps)
    {
        if (sections[key] is not JsonObject section) return;
        Fix(section, type, null, at, clamps);
        DeserializeDroppingBadFields(section, type, at, clamps);
    }

    private static string Reason(JsonException ex)
    {
        string message = ex.Message;
        int cut = message.IndexOf(" Path:", StringComparison.Ordinal);
        return (cut > 0 ? message[..cut] : message).TrimEnd('.');
    }

    private static string Shown(JsonNode? node)
    {
        string text = node?.ToJsonString() ?? "null";
        return text.Length <= 80 ? text : text[..77] + "...";
    }

    private static string Join(string at, string path) =>
        at.Length == 0 ? path : path.StartsWith('[') ? at + path : $"{at}.{path}";

    // Take the node at a serializer path ($.A.B[2], $['odd key']) out of root.
    // Returns it with the path as the log shows it, or null when the path names
    // nothing that can be taken out (the root itself, or a node not there).
    private static (string Path, JsonNode? Node)? Detach(JsonNode? root, string? path)
    {
        if (root is null || path is null || !path.StartsWith('$')) return null;
        List<object> steps = new();
        int i = 1;
        while (i < path.Length)
        {
            if (path[i] == '.')
            {
                int end = i + 1;
                while (end < path.Length && path[end] is not ('.' or '[')) end++;
                steps.Add(path[(i + 1)..end]);
                i = end;
            }
            else if (path[i] == '[' && i + 1 < path.Length && path[i + 1] == '\'')
            {
                int end = path.IndexOf("']", i + 2, StringComparison.Ordinal);
                if (end < 0) return null;
                steps.Add(path[(i + 2)..end]);
                i = end + 2;
            }
            else if (path[i] == '[')
            {
                int end = path.IndexOf(']', i);
                if (end < 0 || !int.TryParse(path[(i + 1)..end], out int index)) return null;
                steps.Add(index);
                i = end + 1;
            }
            else return null;
        }
        if (steps.Count == 0) return null;

        JsonNode? parent = root;
        for (int k = 0; k < steps.Count - 1 && parent is not null; k++)
            parent = steps[k] is int n
                ? parent is JsonArray a && n < a.Count ? a[n] : null
                : (parent as JsonObject)?[(string)steps[k]];
        string shown = string.Concat(steps.Select((step, k) =>
            step is int n ? $"[{n}]" : k == 0 ? (string)step : "." + step));
        switch (parent, steps[^1])
        {
            case (JsonObject obj, string name) when obj.ContainsKey(name):
                JsonNode? taken = obj[name];
                obj.Remove(name);
                return (shown, taken);
            case (JsonArray array, int index) when index < array.Count:
                JsonNode? item = array[index];
                array.RemoveAt(index);
                return (shown, item);
            default:
                return null;
        }
    }

    private static readonly JsonDocumentOptions NodeDocumentOptions = new()
    {
        AllowTrailingCommas = JsonStore.Options.AllowTrailingCommas,
        CommentHandling = JsonStore.Options.ReadCommentHandling,
    };

    // Returns the node to put in node's place when it had to change, else null.
    private static JsonValue? Fix(JsonNode? node, Type type, SettingRangeAttribute? range, string path, List<SettingsClamp> clamps)
    {
        if (node is null) return null;
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (NumberKind.Of(type) is { } kind)
            return node is JsonValue value && value.GetValueKind() == JsonValueKind.Number
                ? FixNumber(value, kind, range, path, clamps)
                : null;
        if (ElementType(type) is { } element)
        {
            if (node is JsonArray array)
                for (int i = 0; i < array.Count; i++)
                    if (Fix(array[i], element, range, $"{path}[{i}]", clamps) is { } changed) array[i] = changed;
            return null;
        }
        if (DictionaryValueType(type) is { } valueType)
        {
            if (node is JsonObject map)
                foreach (string key in map.Select(p => p.Key).ToList())
                    if (Fix(map[key], valueType, range, $"{path}[{key}]", clamps) is { } changed) map[key] = changed;
            return null;
        }
        if (node is JsonObject obj && Shape(type) is { } shape)
        {
            foreach (string name in obj.Select(p => p.Key).ToList())
            {
                if (!shape.TryGetValue(name, out Member member)) continue;
                string at = path.Length == 0 ? name : $"{path}.{name}";
                if (member.HoldsSections)
                {
                    if (obj[name] is JsonObject sections)
                        foreach (string key in sections.Select(p => p.Key).ToList())
                            if (Sections.TryGetValue(key, out Type? sectionType))
                                CheckSection(sections, key, sectionType, $"{at}.{key}", clamps);
                    continue;
                }
                if (Fix(obj[name], member.Type, member.Range, at, clamps) is { } changed) obj[name] = changed;
            }
        }
        return null;
    }

    private static JsonValue? FixNumber(JsonValue value, NumberKind kind, SettingRangeAttribute? range, string path, List<SettingsClamp> clamps)
    {
        decimal lo = kind.Min, hi = kind.Max;
        if (range is not null)
        {
            lo = Math.Max(lo, ToDecimal(range.Min));
            hi = Math.Min(hi, ToDecimal(range.Max));
        }
        string raw = value.ToJsonString();
        decimal used;
        if (decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal found))
        {
            decimal whole = kind.Whole ? decimal.Round(found, MidpointRounding.AwayFromZero) : found;
            used = Math.Clamp(whole, lo, hi);
            if (used == whole && whole != found)
            {
                clamps.Add(new SettingsClamp(path, raw, "not a whole number", Show(used)));
                return kind.Create(used);
            }
            if (used == found)
            {
                // In range, but a whole-number field written as 5.0 or 5e0 still
                // fails the serializer: the same value, written as it reads it.
                return kind.Whole && raw.Any(c => c is '.' or 'e' or 'E') ? kind.Create(used) : null;
            }
        }
        else
        {
            // Past decimal's ±7.9e28 is past every field's range: only the sign matters.
            used = raw.StartsWith('-') ? lo : hi;
        }
        clamps.Add(new SettingsClamp(path, raw, $"outside {Show(lo)} to {Show(hi)}", Show(used)));
        return kind.Create(used);
    }

    // A bound as decimal, pinned to decimal's range (a range attribute may say
    // double.MaxValue for "no upper bound").
    private static decimal ToDecimal(double bound) =>
        bound >= (double)decimal.MaxValue ? decimal.MaxValue
        : bound <= (double)decimal.MinValue ? decimal.MinValue
        : (decimal)bound;

    private static string Show(decimal d) => d.ToString(CultureInfo.InvariantCulture);

    private static Type? ElementType(Type type)
    {
        if (type.IsArray) return type.GetElementType();
        if (type.IsGenericType && type.GetGenericTypeDefinition() is var g
            && (g == typeof(List<>) || g == typeof(IList<>) || g == typeof(IReadOnlyList<>) || g == typeof(HashSet<>)))
            return type.GetGenericArguments()[0];
        return null;
    }

    private static Type? DictionaryValueType(Type type) =>
        type.IsGenericType && type.GetGenericTypeDefinition() is var g
            && (g == typeof(Dictionary<,>) || g == typeof(IDictionary<,>) || g == typeof(IReadOnlyDictionary<,>))
            ? type.GetGenericArguments()[1]
            : null;

    // ----- Shapes -----------------------------------------------------------

    private readonly record struct Member(Type Type, SettingRangeAttribute? Range, bool HoldsSections);

    // A type's properties as the serializer names them, cached: the walk meets the
    // same record types once per element of every list a profile keeps.
    private static readonly ConcurrentDictionary<Type, Dictionary<string, Member>?> Shapes = new();

    private static Dictionary<string, Member>? Shape(Type type) => Shapes.GetOrAdd(type, static t =>
    {
        // Only the client's own DTOs (classes and records, structs among them) have a
        // shape worth walking; a JsonElement, a string or a framework type is left alone.
        if (t.IsEnum || t.IsPrimitive || t.Namespace?.StartsWith("MudPlay", StringComparison.Ordinal) != true) return null;
        Dictionary<string, Member> members = new(StringComparer.Ordinal);
        foreach (PropertyInfo p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            // Get-only too: a positional record's are filled through its constructor.
            if (!p.CanRead || p.GetIndexParameters().Length > 0) continue;
            if (p.GetCustomAttribute<JsonIgnoreAttribute>() is { Condition: JsonIgnoreCondition.Always }) continue;
            string name = p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? p.Name;
            members[name] = new Member(p.PropertyType, p.GetCustomAttribute<SettingRangeAttribute>(), HoldsSections(p));
        }
        return members;
    });

    // The Settings dictionary on each tier's file: its values are sections, typed
    // by their key (Sections), not by the dictionary's JsonElement.
    private static bool HoldsSections(PropertyInfo p) =>
        p.Name == nameof(GlobalSettings.Settings)
        && p.PropertyType == typeof(Dictionary<string, JsonElement>)
        && TierOf(p.DeclaringType!) is not null;

    // ----- Number kinds -------------------------------------------------------

    private sealed record NumberKind(decimal Min, decimal Max, bool Whole, Func<decimal, JsonValue> Create)
    {
        private static readonly Dictionary<Type, NumberKind> Kinds = new()
        {
            [typeof(int)] = new(int.MinValue, int.MaxValue, true, d => JsonValue.Create((int)d)),
            [typeof(long)] = new(long.MinValue, long.MaxValue, true, d => JsonValue.Create((long)d)),
            [typeof(short)] = new(short.MinValue, short.MaxValue, true, d => JsonValue.Create((short)d)),
            [typeof(byte)] = new(byte.MinValue, byte.MaxValue, true, d => JsonValue.Create((byte)d)),
            [typeof(uint)] = new(uint.MinValue, uint.MaxValue, true, d => JsonValue.Create((uint)d)),
            [typeof(double)] = new(decimal.MinValue, decimal.MaxValue, false, d => JsonValue.Create((double)d)),
            [typeof(float)] = new(decimal.MinValue, decimal.MaxValue, false, d => JsonValue.Create((float)d)),
            [typeof(decimal)] = new(decimal.MinValue, decimal.MaxValue, false, d => JsonValue.Create(d)),
        };

        public static NumberKind? Of(Type type) => Kinds.GetValueOrDefault(type);
    }

    // ----- Reporting ----------------------------------------------------------

    private static readonly HashSet<string> Said = new(StringComparer.Ordinal);
    private static readonly Queue<string> SaidInOrder = new();
    private const int SaidAtMost = 500;

    private static void Raise(string line)
    {
        Action<string>? handler = Clamped;
        // Nobody listening yet (a read before the log is up): not remembered, so the
        // next read of the file says it.
        if (handler is null) return;
        lock (Said)
        {
            if (!Said.Add(line)) return;
            SaidInOrder.Enqueue(line);
            if (SaidInOrder.Count > SaidAtMost) Said.Remove(SaidInOrder.Dequeue());
        }
        handler(line);
    }
}

// One value SettingsValueGuard moved or dropped: where it sits in the file, what
// the file held, why it couldn't stand, and what the client uses instead.
public readonly record struct SettingsClamp(string Field, string Found, string Why, string Used);
