using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using MudPlay.Models.Profile;
using MudPlay.Models.Settings;

namespace MudPlay.Services;

// The one reader and writer of the tab-keyed sections each tier's Settings
// dictionary holds (CharacterProfile / BbsProfile / GlobalSettings .Settings). The
// Settings window, the engines and the bug report must see the same values, so
// they all read here, through SettingsValueGuard: a section's numbers held to their
// ranges, a field it can't take read as its default (warned), and a section that
// can't be read at all warned before its defaults are used.
//
// The engines read a section on every line, so a section is checked once per
// stored element and the checked form kept until the element is replaced: a section
// is only ever replaced whole, by a fresh element.
//
// Writing is where a section can be lost. A write a user didn't make (a toggle, an
// @-command, a profile upgrade) is built from a read, and a read of a section past
// reading is all defaults: Write leaves such a section as stored instead.
public static class SettingsSection
{
    // How a section is written: enums as their numbers, as every section on disk has
    // always held them, so an older client reads it. A read takes a number or a name.
    private static readonly JsonSerializerOptions WriteOptions = MakeWriteOptions();

    private static JsonSerializerOptions MakeWriteOptions()
    {
        JsonSerializerOptions options = new(JsonStore.Options) { WriteIndented = false };
        for (int i = options.Converters.Count - 1; i >= 0; i--)
            if (options.Converters[i] is JsonStringEnumConverter) options.Converters.RemoveAt(i);
        return options;
    }

    // The BBS each loaded profile lives under, for the log: two characters of one
    // name on two BBSes are told apart. ProfileService says it on every load.
    private static readonly ConditionalWeakTable<CharacterProfile, string> Homes = new();

    public static void SetHome(CharacterProfile profile, string? bbs)
    {
        if (string.IsNullOrWhiteSpace(bbs)) Homes.Remove(profile);
        else Homes.AddOrUpdate(profile, bbs);
    }

    // The section as the character's own tier holds it.
    public static T Read<T>(CharacterProfile? profile, string key) where T : new() =>
        Read<T>(profile?.Settings, key, "Character", profile);

    // The section as the Global tier holds it.
    public static T Read<T>(GlobalSettings global, string key) where T : new() =>
        Read<T>(global.Settings, key, "Global", null);

    // The section merged over every tier (SettingsResolver.Resolve).
    public static T Read<T>(JsonObject merged, string key) where T : new() =>
        SettingsValueGuard.TryReadSection(merged, typeof(T), key, "Settings (all tiers merged)", out object? value)
        && value is T typed ? typed : new T();

    // Store value as the character's section key, unless the section stored there
    // can't be read at all: writer (what is writing, for the log) didn't read it, so
    // its value is defaults that would replace it. False, with a Warn, when the
    // section is left as stored. A Settings tab's own Save writes over it: that is
    // the user's choice, made looking at the defaults the tab shows.
    public static bool Write<T>(CharacterProfile profile, string key, T value, string writer) where T : new()
    {
        profile.Settings ??= new Dictionary<string, JsonElement>();
        if (profile.Settings.TryGetValue(key, out JsonElement stored) && !Check<T>(stored, key, "Character", profile).Read)
        {
            SettingsValueGuard.Say(
                $"{Where("Character", profile)} section {key} can't be read, so {writer} didn't write over it; it is left as stored.");
            return false;
        }
        profile.Settings[key] = ToElement(value);
        return true;
    }

    // value as a section is stored.
    public static JsonElement ToElement<T>(T value) => JsonSerializer.SerializeToElement(value, WriteOptions);

    private static T Read<T>(IReadOnlyDictionary<string, JsonElement>? sections, string key, string tier, CharacterProfile? owner)
        where T : new()
    {
        if (sections is null || !sections.TryGetValue(key, out JsonElement section)) return new T();
        Checked found = Check<T>(section, key, tier, owner);
        if (found.Fresh is T fresh) return fresh;
        return found.Readable is { } readable ? readable.Deserialize<T>(JsonStore.Options) ?? new T() : new T();
    }

    // The checked form of section: from the last check of the same stored element,
    // or checked now (then Fresh holds the value read, for the read that asked).
    private static Checked Check<T>(JsonElement section, string key, string tier, CharacterProfile? owner) where T : new()
    {
        if (section.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return Nothing;
        (Type, string, string) slot = (typeof(T), key, tier);
        if (LastChecked.TryGetValue(slot, out Checked? last) && JsonEntryIdentity.Same(last.Source, section)) return last;

        bool read = SettingsValueGuard.TryReadSection(section, typeof(T), key, Where(tier, owner), out object? value);
        T result = read && value is T typed ? typed : new T();
        Checked now = new(section, read ? JsonSerializer.SerializeToElement(result, JsonStore.Options) : null, read);
        LastChecked[slot] = now;
        return now with { Fresh = result };
    }

    private static string Where(string tier, CharacterProfile? owner)
    {
        if (owner is null || string.IsNullOrEmpty(owner.Name)) return tier;
        return Homes.TryGetValue(owner, out string? bbs) ? $"{tier} ({owner.Name} on {bbs})" : $"{tier} ({owner.Name})";
    }

    // The last stored element of a section checked, by DTO, key and tier: what it
    // reads as (null: all defaults), and whether it could be read at all. Fresh is
    // the value of the read that made it, never kept.
    private sealed record Checked(JsonElement Source, JsonElement? Readable, bool Read)
    {
        public object? Fresh { get; init; }
    }

    private static readonly Checked Nothing = new(default, null, true);

    private static readonly ConcurrentDictionary<(Type, string, string), Checked> LastChecked = new();
}
