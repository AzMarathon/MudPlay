using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MudPlay.Game.Gmcp;

// The embedded contract checks required fields before a snapshot can disable text parsing.
// Unknown fields remain acceptable so an additive server version stays compatible.
internal static class GmcpContract
{
    private static readonly Dictionary<string, JsonElement> Schemas = Load();

    private static Dictionary<string, JsonElement> Load()
    {
        Dictionary<string, JsonElement> result = new(StringComparer.Ordinal);
        Assembly assembly = typeof(GmcpContract).Assembly;
        foreach (string resource in assembly.GetManifestResourceNames().Where(n => n.Contains(".Gmcp.Schemas.", StringComparison.Ordinal)))
        {
            using Stream stream = assembly.GetManifestResourceStream(resource)!;
            using JsonDocument document = JsonDocument.Parse(stream);
            JsonElement schema = document.RootElement.Clone();
            result.Add(schema.GetProperty("title").GetString()!, schema);
        }
        return result;
    }

    public static bool IsKnown(string package) => Schemas.ContainsKey(package);
    public static bool IsValid(string package, JsonElement value) => Schemas.TryGetValue(package, out JsonElement schema) && Matches(schema, value);

    private static bool Matches(JsonElement schema, JsonElement value)
    {
        if (schema.TryGetProperty("type", out JsonElement type))
        {
            bool Kind(string name) => name switch
            {
                "object" => value.ValueKind == JsonValueKind.Object,
                "array" => value.ValueKind == JsonValueKind.Array,
                "string" => value.ValueKind == JsonValueKind.String,
                "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
                "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                "null" => value.ValueKind == JsonValueKind.Null,
                _ => false
            };
            if (!(type.ValueKind == JsonValueKind.Array ? type.EnumerateArray().Any(t => Kind(t.GetString()!)) : Kind(type.GetString()!))) return false;
        }
        if (schema.TryGetProperty("const", out JsonElement constant) && !JsonElement.DeepEquals(constant, value)) return false;
        if (schema.TryGetProperty("enum", out JsonElement choices) && !choices.EnumerateArray().Any(c => JsonElement.DeepEquals(c, value))) return false;
        if (schema.TryGetProperty("oneOf", out JsonElement alternatives) && alternatives.EnumerateArray().Count(s => Matches(s, value)) != 1) return false;
        if (schema.TryGetProperty("allOf", out JsonElement rules) && !rules.EnumerateArray().All(s => Matches(s, value))) return false;
        if (schema.TryGetProperty("if", out JsonElement condition) && Matches(condition, value)
            && schema.TryGetProperty("then", out JsonElement consequence) && !Matches(consequence, value)) return false;
        if (value.ValueKind == JsonValueKind.Object)
        {
            if (schema.TryGetProperty("required", out JsonElement required) && required.EnumerateArray().Any(p => !value.TryGetProperty(p.GetString()!, out _))) return false;
            if (schema.TryGetProperty("minProperties", out JsonElement minimum) && value.EnumerateObject().Count() < minimum.GetInt32()) return false;
            HashSet<string> seen = new(StringComparer.Ordinal);
            foreach (JsonProperty property in value.EnumerateObject())
            {
                if (!seen.Add(property.Name)) return false;
                if (schema.TryGetProperty("properties", out JsonElement properties) && properties.TryGetProperty(property.Name, out JsonElement field) && !Matches(field, property.Value)) return false;
            }
        }
        if (value.ValueKind == JsonValueKind.Array && schema.TryGetProperty("items", out JsonElement item) && value.EnumerateArray().Any(v => !Matches(item, v))) return false;
        if (value.ValueKind == JsonValueKind.Number && schema.TryGetProperty("minimum", out JsonElement min) && value.GetInt64() < min.GetInt64()) return false;
        if (value.ValueKind == JsonValueKind.String && schema.TryGetProperty("pattern", out JsonElement pattern)
            && !Regex.IsMatch(value.GetString()!, pattern.GetString()!, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100))) return false;
        return true;
    }
}
