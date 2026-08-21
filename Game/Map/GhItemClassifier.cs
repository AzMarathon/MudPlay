using System.Globalization;
using System.Text.RegularExpressions;
using MudPlay.Game.GameData;
using MudPlay.Models.Profile;
using MudPlay.Services;

namespace MudPlay.Game.Map;

// Resolves a GroundItemTracker floor entry to its item category for Roomba
// Mode room-label matching. Reuses ItemNameStore's existing name→Number
// reverse index (FindByName, already used by AppServices.IsKnownGroundItem
// to resolve the same "You notice" wording) rather than a new item model —
// the MDB import already carries ItemType / WeaponType / ArmourType / Worn
// as real, populated columns. (The GhItemClass facet struct it returns lives in
// GhItemClass.cs.)
public static class GhItemClassifier
{
    // Classify a floor entry (e.g. "a long sword", "5 gold ring"), or null
    // when it doesn't resolve to a known item (cash is already filtered out
    // by GroundItemTracker before this is ever called).
    public static GhItemClass? Classify(ItemNameStore names, string groundEntry)
    {
        if (names.FindByName(groundEntry) is not int number) return null;
        if (names.ItemTypeOf(number) is not int itemType) return null;
        return new GhItemClass(itemType, names.WeaponTypeOf(number), names.ArmourTypeOf(number), names.WornOf(number));
    }

    // True when a single rule admits item. Worn-slot rules match ANY
    // ItemType at that equip location; ItemType rules match the top-level
    // category and, when the rule narrows further, the WeaponType /
    // ArmourType subtype too. ArmourType and Worn are compared by their
    // RESOLVED DISPLAY NAME rather than raw code — several distinct MDB
    // codes render as the same label (ArmourType 3/4/5/6 all show "Leather";
    // Worn 4/13 both show "Finger", 14/17 both show "Wrist"), so a raw-int
    // comparison would silently miss items using a different code for the
    // same displayed category.
    public static bool Matches(GhCategoryRule rule, GhItemClass item)
    {
        if (rule.Worn is int w)
            return item.Worn is int iw && SameWornSlot(w, iw);

        if (rule.ItemType != item.ItemType) return false;
        if (rule.WeaponType is { } wt && wt != item.WeaponType) return false;
        if (rule.ArmourType is { } at)
            return item.ArmourType is int ia && SameArmourType(at, ia);
        return true;
    }

    // True when label admits item under any one of its rules (OR semantics).
    public static bool MatchesAny(GhRoomLabel label, GhItemClass item)
    {
        foreach (GhCategoryRule rule in label.Rules)
            if (Matches(rule, item)) return true;
        return false;
    }

    private static bool SameWornSlot(int a, int b) =>
        a == b || LookupEnums.FormatWornSlot(a) == LookupEnums.FormatWornSlot(b);

    private static bool SameArmourType(int a, int b) =>
        a == b || LookupEnums.FormatArmourType(a.ToString(CultureInfo.InvariantCulture))
                == LookupEnums.FormatArmourType(b.ToString(CultureInfo.InvariantCulture));

    private static readonly Regex EmblemNamePattern = new(@"\bemblem\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Gang-house guards stand down only while the player carries that house's matching
    // emblem (see GAME_MECHANICS.md "Gang houses & guard emblems") — sweeping one away as
    // clutter risks the guards turning hostile. No ItemType/flag distinguishes an emblem in
    // the imported data; the name pattern is the only known signal, so it's excluded from
    // sort eligibility outright rather than left to category matching.
    public static bool IsGuardEmblem(string groundEntry) => EmblemNamePattern.IsMatch(groundEntry);
}
