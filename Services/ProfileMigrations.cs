using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using MudPlay.Models.Profile;

namespace MudPlay.Services;

// One-time, per-profile schema upgrades. Applied on load (ProfileService.Load)
// before ProfileLoaded fires, so per-character services see the migrated shape.
// Each step is gated on CharacterProfile.SchemaVersion and bumps it, so a
// profile migrates exactly once and re-running Apply is a no-op.
public static class ProfileMigrations
{
    // Bring profile up to CharacterProfile.CurrentSchemaVersion. Returns true
    // when anything changed, so the caller can persist the upgraded profile.
    public static bool Apply(CharacterProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        bool changed = false;

        // v1 → v2: the default keybindings and toolbar layout were overhauled.
        // Reset every existing profile onto the new defaults by dropping its
        // stored keybind + toolbar-layout deltas. Auto-mode and all other
        // settings are deliberately left untouched.
        if (profile.SchemaVersion < 2)
        {
            if (profile.BuiltInKeybindings is not null)
                profile.BuiltInKeybindings = null;
            ResetToolbarLayout(profile);
            profile.SchemaVersion = 2;
            changed = true;
        }

        // v2 → v3: the self-bless slots and the "when HP/MA full" downtime buffs
        // moved off the Spells settings tab into the character's ONE unified buff
        // list (CharacterProfile.PartyBuffs), edited live in the Buff Watchdog.
        // Fold the stored config into that list as CastOnSelf slots and clear the
        // migrated fields so the engine reads them from a single place. (Mana-regen
        // and the room-light spell keep their own consumers for now and fold later.)
        if (profile.SchemaVersion < 3)
        {
            FoldSelfBlessIntoUnifiedList(profile);
            profile.SchemaVersion = 3;
            changed = true;
        }

        return changed;
    }

    // Move BlessSlots (in slot order) + WhenHpFull / WhenMaFull out of the profile's
    // stored "Spells" section and into CharacterProfile.PartyBuffs as CastOnSelf
    // slots — bless slots keep their per-slot recast lead; the when-full buffs carry
    // the matching OnlyWhenHpFull / OnlyWhenMaFull condition. Prepended so the old
    // "self buffs before party buffs" priority survives. Clears the migrated fields.
    private static void FoldSelfBlessIntoUnifiedList(CharacterProfile profile)
    {
        if (profile.Settings is not { } settings) return;
        if (!settings.TryGetValue("Spells", out JsonElement json)) return;

        SpellsSettings? spells;
        try { spells = JsonSerializer.Deserialize<SpellsSettings>(json.GetRawText()); }
        catch { spells = null; }
        if (spells is null) return;

        List<PartyBuffSlot> folded = new();
        foreach (KeyValuePair<int, string> kv in spells.BlessSlots.OrderBy(k => k.Key))
        {
            if (string.IsNullOrWhiteSpace(kv.Value)) continue;
            int margin = spells.BlessSlotRecastMargins.TryGetValue(kv.Key, out int m)
                ? m : SpellsSettings.DefaultBlessRecastMarginSec;
            folded.Add(new PartyBuffSlot { Spell = kv.Value.Trim(), CastOnSelf = true, RecastMarginSec = margin });
        }
        if (!string.IsNullOrWhiteSpace(spells.WhenHpFullSpell))
            folded.Add(new PartyBuffSlot { Spell = spells.WhenHpFullSpell!.Trim(), CastOnSelf = true, OnlyWhenHpFull = true });
        if (!string.IsNullOrWhiteSpace(spells.WhenMaFullSpell))
            folded.Add(new PartyBuffSlot { Spell = spells.WhenMaFullSpell!.Trim(), CastOnSelf = true, OnlyWhenMaFull = true });

        if (folded.Count == 0) return;

        profile.PartyBuffs ??= new PartyBuffSettings();
        profile.PartyBuffs.Slots.InsertRange(0, folded);

        spells.BlessSlots = new();
        spells.BlessSlotRecastMargins = new();
        spells.WhenHpFullSpell = null;
        spells.WhenMaFullSpell = null;
        settings["Spells"] = JsonSerializer.SerializeToElement(spells);
    }

    // Null out the Layout inside the profile's stored "Toolbar" settings section
    // so it falls back to ToolbarDefaults, while preserving the user's Visible /
    // Position choices. No-op when the profile has no Toolbar section (already
    // on defaults).
    private static void ResetToolbarLayout(CharacterProfile profile)
    {
        if (profile.Settings is not { } settings) return;
        if (!settings.TryGetValue("Toolbar", out JsonElement json)) return;

        ToolbarSettings? dto;
        try { dto = JsonSerializer.Deserialize<ToolbarSettings>(json.GetRawText()); }
        catch { dto = null; }
        if (dto?.Layout is null) return;

        dto.Layout = null;
        settings["Toolbar"] = JsonSerializer.SerializeToElement(dto);
    }
}
