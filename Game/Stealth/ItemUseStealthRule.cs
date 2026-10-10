using System.Text.Json;
using MudPlay.Game.GameData;
using MudPlay.Game.Inventory;

namespace MudPlay.Game.Stealth;

// Whether an item command (`use`, `read`, `eat`, `drink`, `light`) ends a sneak and a
// hide. None of them clears either on its own: the engine clears both inside the cast
// of the item's spell, and prints nothing when it does (GAME_MECHANICS "Sneaking —
// commands, equip order, and the sneak state machine" → "What ends a sneak", the item
// commands). So the answer is whether this use casts. A torch, a key and a
// learn-spell scroll cast nothing and the sneak holds; a waterskin, a potion and food
// cast, and it ends.
//
// Where the client can't tell (an item it can't place in the pack or the game data, or
// a refusal only the game sees: not worn, no charges left, a protected room) the sneak
// counts as ended. A needless `sn` is cheap; a sneak believed in after it has gone
// sends the next move out unsneaked and lets a backstab be planned from nothing.
public sealed class ItemUseStealthRule
{
    public enum Verb { Use, Read, Eat, Drink, Light }

    // An item's kind and what a use of it casts. Casts is false when a use casts
    // nothing; CastTargets is the cast spell's Targets, null when the spell isn't in
    // the game data.
    public readonly record struct Facts(int ItemType, bool Casts, int? CastTargets)
    {
        // A use walks the item's ability slots in order and stops at a proc marker,
        // so it casts only when the first cast slot is a plain on-use one.
        public static Facts Read(JsonElement itemRow, Func<int, int?> spellTargets)
        {
            ArgumentNullException.ThrowIfNull(spellTargets);
            int itemType = itemRow.TryGetProperty("ItemType", out JsonElement t)
                && t.ValueKind == JsonValueKind.Number && t.TryGetInt32(out int kind) ? kind : 0;
            IReadOnlyList<ItemCastSpells.CastEntry> casts = ItemCastSpells.ReadCasts(itemRow);
            bool castsOnUse = casts.Count > 0 && casts[0].Trigger == ItemCastSpells.CastTrigger.OnUse;
            return new Facts(itemType, castsOnUse, castsOnUse ? spellTargets(casts[0].SpellNumber) : null);
        }
    }

    private const int FoodItemType = 4;
    private const int DrinkItemType = 5;
    private const int LightItemType = 6;

    private readonly Func<IReadOnlyList<string>> _heldItems;
    private readonly Func<string, Facts?> _factsOf;

    // heldItems: the names of everything carried, worn and on the key ring. factsOf:
    // the game data's word on an item by name, null for one it doesn't know.
    public ItemUseStealthRule(Func<IReadOnlyList<string>> heldItems, Func<string, Facts?> factsOf)
    {
        ArgumentNullException.ThrowIfNull(heldItems);
        ArgumentNullException.ThrowIfNull(factsOf);
        _heldItems = heldItems;
        _factsOf = factsOf;
    }

    // The item command a first word is, by the spellings the game takes (GAME_MECHANICS
    // "Command words and abbreviations"), or null for any other word.
    public static Verb? VerbOf(string word) => word.ToLowerInvariant() switch
    {
        "us" or "use" => Verb.Use,
        "read" => Verb.Read,
        "eat" => Verb.Eat,
        "dri" or "drin" or "drink" => Verb.Drink,
        "lig" or "ligh" or "light" => Verb.Light,
        _ => null,
    };

    // True when sending `<verb> <args>` ends a sneak and a hide.
    public bool EndsStealth(Verb verb, string args)
    {
        // The bare word is a syntax line, or for `light` the light level.
        if (args.Length == 0) return false;

        string? name = ItemChargeTracker.ResolveHeld(_heldItems(), args, out bool wholeArg);
        // Nothing held by that name. A `read` then looks at the thing instead (a sign,
        // a plaque), which ends nothing. For the others the pack may simply be unread.
        if (name is null) return verb != Verb.Read;
        if (_factsOf(name) is not { } facts) return true;

        // `eat` finds only food, `drink` only drinks and `light` only lights.
        int? needs = verb switch
        {
            Verb.Eat => FoodItemType,
            Verb.Drink => DrinkItemType,
            Verb.Light => LightItemType,
            _ => null,
        };
        if (needs is int kind && facts.ItemType != kind) return false;
        if (!facts.Casts) return false;

        // A word after the item can name a player or a monster, and a cast on either
        // clears both as well. What the word turns out to name only the game knows.
        if (!wholeArg && (verb is Verb.Use or Verb.Read)) return true;
        return facts.CastTargets is not int targets || CastsWithNoTarget(targets);
    }

    // A spell used with no target is cast only for these Targets; any other is turned
    // away with `You must specify a target for that spell!` before anything is cleared.
    private static bool CastsWithNoTarget(int targets) =>
        targets is 1 or 2 or 3 or 5 or 6 or 9 or 10 or 11 or 12 or 13;
}
