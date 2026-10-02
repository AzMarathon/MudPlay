using System.Text.Json;

namespace MudPlay.Models.Settings;

// The status bar under the terminal: one or more rows, each with items placed on
// its left, in its centre and on its right. Global tier — the same bar for every
// character. Stored only once the user changes it; an absent entry is the default.
public sealed class StatusBarSettings
{
    public const int MaxRows = 4;

    // The key under GlobalSettings.Settings.
    public const string SectionKey = "StatusBar";

    public List<StatusBarRow> Rows { get; set; } = new() { StatusBarRow.Default() };

    // The saved layout, or the default when none is saved or it can't be read. A
    // saved layout with no rows also falls back: the bar never disappears.
    public static StatusBarSettings Read(GlobalSettings global)
    {
        if (global.Settings is { } bucket && bucket.TryGetValue(SectionKey, out JsonElement json))
        {
            try
            {
                if (JsonSerializer.Deserialize<StatusBarSettings>(json) is { Rows.Count: > 0 } saved) return saved;
            }
            catch (JsonException)
            {
                // A malformed entry shows the default bar rather than none.
            }
        }
        return new StatusBarSettings();
    }

    // Store this layout, or drop the entry when it is the default.
    public void WriteTo(GlobalSettings global)
    {
        if (IsDefault())
        {
            global.Settings?.Remove(SectionKey);
            return;
        }
        global.Settings ??= new Dictionary<string, JsonElement>();
        global.Settings[SectionKey] = JsonSerializer.SerializeToElement(this);
    }

    // A method, not a property: it serializes this object, and a property would be
    // serialized along with it.
    public bool IsDefault() => JsonSerializer.Serialize(this) == JsonSerializer.Serialize(new StatusBarSettings());
}

// One row of the status bar.
public sealed class StatusBarRow
{
    public List<StatusBarEntry> Left { get; set; } = new();
    public List<StatusBarEntry> Center { get; set; } = new();
    public List<StatusBarEntry> Right { get; set; } = new();

    // Show the row as one line of text crawling sideways, so it can carry more
    // than fits the window.
    public bool Marquee { get; set; }

    // The bar as it has always been: engine chip, location, exp rate and time to
    // level on the left, the looked-at target in the middle, the statline warning,
    // ticks and connection light on the right.
    public static StatusBarRow Default() => new()
    {
        Left = { new("engine"), new("location"), new("exprate"), new("tnl") },
        Center = { new("target") },
        Right = { new("statline"), new("tick"), new("hptick"), new("matick"), new("connection") },
    };
}

// One item on a row: a catalogue id, plus the text the user typed when the item is
// their own ("text"), which may name other items in braces — "Lap {lap} of {loop}".
public sealed class StatusBarEntry
{
    public string Item { get; set; } = string.Empty;
    public string? Text { get; set; }

    public StatusBarEntry() { }

    public StatusBarEntry(string item, string? text = null)
    {
        Item = item;
        Text = text;
    }
}
