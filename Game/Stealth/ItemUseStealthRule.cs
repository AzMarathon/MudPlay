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
// Where the client can't tell (an item it can't place in the pack or the game data,
// words that could name more than one held item, or a refusal only the game sees: not
// worn, no charges left, a protected room) the sneak counts as ended. Both mistakes
// cost something. Counted as ended when it wasn't, the `sn` that follows throws away
// the sneak the character still had and rolls again, and can be refused. Believed in
// after it has gone, the next move goes out with no `sn` at all and a backstab is
// planned from nothing, which is the worse of the two.
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
    private readonly Func<string, IReadOnlyList<Facts>> _factsOf;
    private readonly Action<string>? _debug;

    // heldItems: the names of everything carried, worn and on the key ring. factsOf:
    // the game data's word on every item of a name (several items can share one), empty
    // for a name it doesn't know. debug: where each verdict is written.
    public ItemUseStealthRule(
        Func<IReadOnlyList<string>> heldItems,
        Func<string, IReadOnlyList<Facts>> factsOf,
        Action<string>? debug = null)
    {
        ArgumentNullException.ThrowIfNull(heldItems);
        ArgumentNullException.ThrowIfNull(factsOf);
        _heldItems = heldItems;
        _factsOf = factsOf;
        _debug = debug;
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
        bool ends = Decide(verb, args, out string why);
        string command = $"{verb.ToString().ToLowerInvariant()} {args}".TrimEnd();
        _debug?.Invoke($"'{command}': {why}: sneak {(ends ? "ended" : "intact")}");
        return ends;
    }

    private bool Decide(Verb verb, string args, out string why)
    {
        string[] words = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            why = "no item named (a syntax line, or the light level)";
            return false;
        }

        // The item is the longest run of leading words that names something held:
        // "iron key n" before "iron key" before "iron". Taking only the first word
        // read `use iron key n` as the iron ration carried beside the key, and `use
        // healing potion <name>` as the healing herbs.
        IReadOnlyList<string> held = _heldItems();
        List<string> named = new();
        int itemWords = words.Length;
        for (; itemWords >= 1; itemWords--)
        {
            string run = string.Join(' ', words, 0, itemWords);
            if (run.Length < 2) break;
            foreach (string name in ItemChargeTracker.HeldMatching(held, run))
                if (!named.Contains(name, StringComparer.OrdinalIgnoreCase)) named.Add(name);
            if (named.Count > 0) break;
        }

        if (named.Count == 0)
        {
            // Nothing held by that name. A `read` then looks at the thing instead (a
            // sign, a plaque), which ends nothing. For the others the pack may simply
            // be unread.
            why = verb == Verb.Read ? "nothing held by that name, so a look" : "nothing held by that name";
            return verb != Verb.Read;
        }

        string? targetWord = itemWords < words.Length ? words[itemWords] : null;
        // Words that fit several held items, or a name several items share: which one
        // the game takes isn't known, so one that ends the sneak is enough.
        string? intact = null;
        foreach (string name in named)
        {
            IReadOnlyList<Facts> items = _factsOf(name);
            if (items.Count == 0)
            {
                why = $"{name} isn't in the game data";
                return true;
            }
            foreach (Facts facts in items)
            {
                if (EndsWith(verb, facts, targetWord, out string detail))
                {
                    why = $"{name} {detail}";
                    return true;
                }
                intact ??= $"{name} {detail}";
            }
        }
        why = named.Count > 1 ? $"{intact} (and none of the {named.Count} items it could name casts)" : intact!;
        return false;
    }

    private static bool EndsWith(Verb verb, Facts facts, string? targetWord, out string detail)
    {
        // `eat` finds only food, `drink` only drinks and `light` only lights.
        int? needs = verb switch
        {
            Verb.Eat => FoodItemType,
            Verb.Drink => DrinkItemType,
            Verb.Light => LightItemType,
            _ => null,
        };
        if (needs is int kind && facts.ItemType != kind)
        {
            detail = $"is item type {facts.ItemType}, which `{verb.ToString().ToLowerInvariant()}` doesn't find";
            return false;
        }
        if (!facts.Casts)
        {
            detail = "casts nothing on use";
            return false;
        }

        // A word after the item can name a player or a monster, and a cast on either
        // clears both as well. What the word turns out to name only the game knows.
        if (targetWord is not null && (verb is Verb.Use or Verb.Read))
        {
            detail = $"casts, and '{targetWord}' after it may name a target";
            return true;
        }
        if (facts.CastTargets is not int targets)
        {
            detail = "casts a spell the game data doesn't have";
            return true;
        }
        bool cast = CastsWithNoTarget(targets);
        detail = cast
            ? $"casts with no target (Targets {targets})"
            : $"casts a spell that needs a target (Targets {targets}), so the use is refused";
        return cast;
    }

    // A spell used with no target is cast only for these Targets; any other is turned
    // away with `You must specify a target for that spell!` before anything is cleared.
    private static bool CastsWithNoTarget(int targets) =>
        targets is 1 or 2 or 3 or 5 or 6 or 9 or 10 or 11 or 12 or 13;
}
