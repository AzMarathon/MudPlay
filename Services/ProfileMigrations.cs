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

        // v3 → v4: the mana-regen buff (+ its reroll config) and the room-light spell
        // also move off the Spells tab into the unified buff list — mana-regen as a
        // maintained CastOnSelf slot carrying its reroll threshold / cap, room-light as
        // a CastOnSelf slot flagged "only when dark" (its prior reactive behaviour).
        if (profile.SchemaVersion < 4)
        {
            FoldManaRegenAndLightIntoUnifiedList(profile);
            profile.SchemaVersion = 4;
            changed = true;
        }

        // v4 → v5: "bless if above", "bless self while resting" and "bless self
        // during combat" stopped being one setting for every buff and became three
        // conditions on each buff slot. Copy what the character had onto every slot
        // it already has, so nothing casts differently after the update.
        if (profile.SchemaVersion < 5)
        {
            CopyBlessGatesOntoBuffSlots(profile);
            profile.SchemaVersion = 5;
            changed = true;
        }

        // v5 → v6: "bless party while resting / during combat" went the same way.
        // A buff's own two switches now cover its casts on the party as well as on
        // the character, so the party pair is folded into the slots that cast there.
        if (profile.SchemaVersion < 6)
        {
            FoldPartyBlessGatesIntoBuffSlots(profile);
            profile.SchemaVersion = 6;
            changed = true;
        }

        return changed;
    }

    // Fold the stored Party rest / combat switches into the buff slots, which carry
    // the self pair from the step before. A slot cast only on party members takes the
    // party values. A slot cast both ways (on the character and on members, or a
    // whole-party spell also cast solo) keeps a switch on when either side had it on,
    // so no cast it used to make is lost. A slot cast only on the character is left.
    //
    // A whole-party spell can't be told from the slot alone (that needs the spell
    // record), so it is read off the targeting: nothing aimed, the Party toggle on.
    private static void FoldPartyBlessGatesIntoBuffSlots(CharacterProfile profile)
    {
        if (profile.PartyBuffs is not { Slots.Count: > 0 } buffs) return;
        PartySettings party = ReadStored<PartySettings>(profile, "Party");
        foreach (BuffSlot slot in buffs.Slots)
        {
            bool aimedAtMembers = slot.AllMembers || slot.Targets.Count > 0;
            bool wholeParty = !slot.CastOnSelf && !aimedAtMembers && slot.WholePartyOn;
            if (!aimedAtMembers && !wholeParty) continue;
            bool alsoOnSelf = wholeParty ? slot.CastSolo : slot.CastOnSelf;
            slot.BlessWhileResting = party.BlessWhileResting || (alsoOnSelf && slot.BlessWhileResting);
            slot.BlessDuringCombat = party.BlessDuringCombat || (alsoOnSelf && slot.BlessDuringCombat);
        }
    }

    // Stamp the Health "bless if above" and Spells rest / combat switches onto every
    // existing buff slot. They come from the character's own stored sections, the
    // same place the casting engine read them from (and the active combat profile
    // mirrors those sections, so its values are the ones copied). The stored values
    // stay where they are, unread.
    private static void CopyBlessGatesOntoBuffSlots(CharacterProfile profile)
    {
        if (profile.PartyBuffs is not { Slots.Count: > 0 } buffs) return;
        HealthSettings health = ReadStored<HealthSettings>(profile, "Health");
        SpellsSettings spells = ReadStored<SpellsSettings>(profile, "Spells");
        foreach (BuffSlot slot in buffs.Slots)
        {
            slot.BlessIfAboveMa = health.BlessIfAboveMa;
            slot.BlessWhileResting = spells.SelfBlessWhileResting;
            slot.BlessDuringCombat = spells.SelfBlessDuringCombat;
        }
    }

    // A stored settings section, or its defaults when the profile has none or it
    // doesn't parse.
    private static T ReadStored<T>(CharacterProfile profile, string key) where T : new()
    {
        if (profile.Settings is not { } settings || !settings.TryGetValue(key, out JsonElement json)) return new T();
        try { return JsonSerializer.Deserialize<T>(json.GetRawText()) ?? new T(); }
        catch (JsonException) { return new T(); }   // an unreadable section migrates as defaults
    }

    // Move MaRegenSpell (with ManaRegenRerollThreshold / Cap) + RoomLightSpell out of
    // the stored "Spells" section and into CharacterProfile.PartyBuffs as CastOnSelf
    // slots, appended after the already-folded self-bless slots. Clears the migrated
    // fields.
    private static void FoldManaRegenAndLightIntoUnifiedList(CharacterProfile profile)
    {
        if (profile.Settings is not { } settings) return;
        if (!settings.TryGetValue("Spells", out JsonElement json)) return;

        SpellsSettings? spells;
        try { spells = JsonSerializer.Deserialize<SpellsSettings>(json.GetRawText()); }
        catch { spells = null; }
        if (spells is null) return;

        List<BuffSlot> folded = new();
        if (!string.IsNullOrWhiteSpace(spells.MaRegenSpell))
            folded.Add(new BuffSlot
            {
                Spell = spells.MaRegenSpell!.Trim(),
                CastOnSelf = true,
                // Old behaviour: maintained downtime buff (recast on expiry), not
                // pre-rest-only. Reroll config rides along on the slot.
                RerollThreshold = spells.ManaRegenRerollThreshold,
                RerollCount = spells.ManaRegenRerollCap,
            });
        if (!string.IsNullOrWhiteSpace(spells.RoomLightSpell))
            folded.Add(new BuffSlot
            {
                Spell = spells.RoomLightSpell!.Trim(),
                CastOnSelf = true,
                OnlyWhenDark = true,   // keep the reactive "cast when the room is dark" behaviour
            });

        if (folded.Count > 0)
        {
            profile.PartyBuffs ??= new BuffSettings();
            profile.PartyBuffs.Slots.AddRange(folded);
        }

        // Always clear the migrated fields, even when nothing was configured, so the
        // (now removed) Spells-tab pickers can't leave stale values behind.
        spells.MaRegenSpell = null;
        spells.RoomLightSpell = null;
        spells.ManaRegenRerollThreshold = null;
        settings["Spells"] = JsonSerializer.SerializeToElement(spells);
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

        List<BuffSlot> folded = new();
        foreach (KeyValuePair<int, string> kv in spells.BlessSlots.OrderBy(k => k.Key))
        {
            if (string.IsNullOrWhiteSpace(kv.Value)) continue;
            int margin = spells.BlessSlotRecastMargins.TryGetValue(kv.Key, out int m)
                ? m : SpellsSettings.DefaultBlessRecastMarginSec;
            folded.Add(new BuffSlot { Spell = kv.Value.Trim(), CastOnSelf = true, RecastMarginSec = margin });
        }
        if (!string.IsNullOrWhiteSpace(spells.WhenHpFullSpell))
            folded.Add(new BuffSlot { Spell = spells.WhenHpFullSpell!.Trim(), CastOnSelf = true, OnlyWhenHpFull = true });
        if (!string.IsNullOrWhiteSpace(spells.WhenMaFullSpell))
            folded.Add(new BuffSlot { Spell = spells.WhenMaFullSpell!.Trim(), CastOnSelf = true, OnlyWhenMaFull = true });

        if (folded.Count == 0) return;

        profile.PartyBuffs ??= new BuffSettings();
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
