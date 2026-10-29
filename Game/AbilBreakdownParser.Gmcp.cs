using System.Text.Json;

namespace MudPlay.Game;

// The server's ability breakdowns (Anomaly.Char.Abilities): raised as the same
// BreakdownParsed the text read raises, for each ability that changed and, with no
// contributions, for each one that's gone.
public sealed partial class AbilBreakdownParser
{
    public Func<bool>? GmcpOwnsAbilities { get; set; }
    private readonly Dictionary<int, JsonElement> _serverAbilities = new();

    public void ApplyGmcpAbilities(JsonElement? value)
    {
        if (value is null)
        {
            _serverAbilities.Clear();
            return;
        }

        HashSet<int> present = new();
        foreach (JsonElement row in value.Value.GetProperty("abilities").EnumerateArray())
        {
            int id = row.GetProperty("id").GetInt32();
            present.Add(id);
            if (_serverAbilities.TryGetValue(id, out JsonElement previous) && JsonElement.DeepEquals(previous, row)) continue;
            _serverAbilities[id] = row.Clone();
            // A null source isn't a contribution; a zero one is.
            List<AbilContribution> sources = new();
            foreach (JsonProperty source in row.GetProperty("sources").EnumerateObject())
                if (source.Value.ValueKind != JsonValueKind.Null) sources.Add(new(source.Name, source.Value.GetInt32()));
            BreakdownParsed?.Invoke(new(id, sources, row.GetProperty("total").GetInt32(), row.GetProperty("aggregation").GetString()!));
        }

        foreach (int absent in _serverAbilities.Keys.Where(id => !present.Contains(id)).ToArray())
        {
            _serverAbilities.Remove(absent);
            BreakdownParsed?.Invoke(new(absent, Array.Empty<AbilContribution>(), 0));
        }
    }
}
