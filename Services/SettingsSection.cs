using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using MudPlay.Models.Profile;
using MudPlay.Models.Settings;

namespace MudPlay.Services;

// The one reader and writer of the tab-keyed sections each tier's Settings
// dictionary holds (CharacterProfile / BbsProfile / GlobalSettings .Settings), so
// the Settings window, the engines and the bug report all read a section the same
// way. Each reader used to deserialize with the serializer's defaults and return
// a whole default DTO on any failure, unsaid: an enum written by name (a hand edit,
// a MegaMUD import) failed the whole section, and the engines ran on defaults while
// the window, reading another way, showed the real values.
//
// A read goes through SettingsValueGuard: numbers held to their ranges, a field the
// section can't take read as its default and warned, and a section that can't be
// read at all warned before its defaults are used. The engines read a section on
// every line, so a section is checked once per stored element and the checked form
// kept until the element is replaced (sections are only ever replaced whole).
public static class SettingsSection
{
    // How a section is written: enums as their numbers, as every section on disk has
    // always held them. A read takes a number or a name.
    private static readonly JsonSerializerOptions WriteOptions = MakeWriteOptions();

    private static JsonSerializerOptions MakeWriteOptions()
    {
        JsonSerializerOptions options = new(JsonStore.Options) { WriteIndented = false };
        for (int i = options.Converters.Count - 1; i >= 0; i--)
            if (options.Converters[i] is JsonStringEnumConverter) options.Converters.RemoveAt(i);
        return options;
    }

    // The section as the character's own tier holds it.
    public static T Read<T>(CharacterProfile? profile, string key) where T : new() =>
        Read<T>(profile?.Settings, key, "Character", profile?.Name);

    // The section as the Global tier holds it.
    public static T Read<T>(GlobalSettings global, string key) where T : new() =>
        Read<T>(global.Settings, key, "Global", null);

    // The section key in one tier's dictionary, or its defaults when the tier has
    // none. owner names whose tier it is (the character) for the log.
    public static T Read<T>(IReadOnlyDictionary<string, JsonElement>? sections, string key, string tier, string? owner)
        where T : new()
    {
        if (sections is null || !sections.TryGetValue(key, out JsonElement section)) return new T();
        if (section.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return new T();

        (Type, string, string) slot = (typeof(T), key, tier);
        if (LastChecked.TryGetValue(slot, out Checked? last) && JsonEntryIdentity.Same(last.Source, section))
            return last.Readable is { } readable ? readable.Deserialize<T>(JsonStore.Options) ?? new T() : new T();

        string where = string.IsNullOrEmpty(owner) ? tier : $"{tier} ({owner})";
        bool read = SettingsValueGuard.TryReadSection(section, typeof(T), key, where, SettingsValueGuard.FieldDefault, out object? value);
        T result = read && value is T typed ? typed : new T();
        // The checked value, written back as an element, is what the next reads of
        // the same stored element take.
        LastChecked[slot] = new Checked(section, read ? JsonSerializer.SerializeToElement(result, JsonStore.Options) : null);
        return result;
    }

    // The section merged over every tier (SettingsResolver.Resolve).
    public static T Read<T>(JsonObject merged, string key) where T : new() =>
        SettingsValueGuard.TryReadSection(merged, typeof(T), key, "Settings (all tiers)", SettingsValueGuard.TierBelowOrDefault, out object? value)
        && value is T typed ? typed : new T();

    // value as a section is stored.
    public static JsonElement ToElement<T>(T value) => JsonSerializer.SerializeToElement(value, WriteOptions);

    // The last stored element of each section read, by DTO, key and tier, with what
    // it reads as (null: nothing, all defaults).
    private sealed record Checked(JsonElement Source, JsonElement? Readable);

    private static readonly ConcurrentDictionary<(Type, string, string), Checked> LastChecked = new();
}
