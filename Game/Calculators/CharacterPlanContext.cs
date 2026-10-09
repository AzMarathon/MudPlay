using System;
using System.Text.Json;
using MudPlay.Game.Inventory;
using MudPlay.Game.Spells;
using MudPlay.Models.Profile;
using MudPlay.Services;

namespace MudPlay.Game.Calculators;

// The live inputs a CP plan is computed against: the raw-base baseline (the `stat`
// screen's values less equipment and the effects that screen listed, floored at
// the race minimum), how far that reading can be trusted, the race min/max bounds,
// and the realm. Resolved from PlayerStats + game data + equipment + the message
// catalogue; shared by the CP Allocation tab and the auto-train engine so the
// baseline math lives in exactly one place.
public readonly record struct CharacterPlanContext(
    bool HasCharacter,
    CpPlanEntry Baseline,
    CpPlanEntry RaceMin,
    CpPlanEntry RaceMax,
    RealmType Realm,
    UnmodifiedStats Reading)
{
    // Resolve the plan context for the live character. HasCharacter is false
    // (entries defaulted) when no race resolves — no character or no game-data
    // set loaded.
    public static CharacterPlanContext Resolve(PlayerStats stats, GameDataCache gameData,
                                               InventoryManager inventory, MessageStore messages)
    {
        ArgumentNullException.ThrowIfNull(stats);
        ArgumentNullException.ThrowIfNull(gameData);
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(messages);

        RealmType realm = gameData.ActiveRealm;
        JsonElement? raceOpt = gameData.FindRowByName("Races", stats.Race);
        if (raceOpt is not JsonElement race || string.IsNullOrEmpty(stats.Race))
            return new CharacterPlanContext(false, new CpPlanEntry(), new CpPlanEntry(), new CpPlanEntry(),
                                            realm, UnmodifiedStats.None);

        var raceMin = new CpPlanEntry(0,
            GetInt(race, "mSTR"), GetInt(race, "mINT"), GetInt(race, "mWIL"),
            GetInt(race, "mAGL"), GetInt(race, "mHEA"), GetInt(race, "mCHM"));
        var raceMax = new CpPlanEntry(0,
            GetInt(race, "xSTR"), GetInt(race, "xINT"), GetInt(race, "xWIL"),
            GetInt(race, "xAGL"), GetInt(race, "xHEA"), GetInt(race, "xCHM"));

        EquipmentStatSummary eq = CharacterCalculator
            .AggregateEquipmentStats(inventory.Snapshot.EquippedItems, gameData).Totals;
        int[] shown = { stats.Strength, stats.Intellect, stats.Willpower, stats.Agility, stats.Health, stats.Charm };
        int[] gear = { eq.PlusStrength, eq.PlusIntellect, eq.PlusWillpower, eq.PlusAgility, eq.PlusHealth, eq.PlusCharm };

        // The effect list only matters when the screen marked something, and
        // matching it walks the whole message catalogue.
        IReadOnlyList<ListedEffect> effects = stats.ModifiedMarksRead && stats.ModifiedStats != StatSet.None
            ? ListedEffectReader.Read(stats.ActiveEffects, realm, messages.Messages,
                                      number => gameData.FindRowByNumber("Spells", number))
            : Array.Empty<ListedEffect>();
        UnmodifiedStats reading = UnmodifiedStatResolver.Resolve(
            shown, gear, stats.ModifiedMarksRead, stats.ModifiedStats, effects);

        int[] b = reading.Base;
        var baseline = new CpPlanEntry(stats.Level,
            Math.Max(raceMin.Strength, b[0]), Math.Max(raceMin.Intellect, b[1]),
            Math.Max(raceMin.Willpower, b[2]), Math.Max(raceMin.Agility, b[3]),
            Math.Max(raceMin.Health, b[4]), Math.Max(raceMin.Charm, b[5]));

        return new CharacterPlanContext(true, baseline, raceMin, raceMax, realm, reading);
    }

    private static int GetInt(JsonElement row, string property)
    {
        if (row.ValueKind != JsonValueKind.Object) return 0;
        if (!row.TryGetProperty(property, out JsonElement v)) return 0;
        return v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n) ? n : 0;
    }
}
