using System.Globalization;
using MudPlay.Models.GameData;
using MudPlay.Services;

namespace MudPlay.Game.Inventory;

// One item's own Auto-Get Items rules, Auto-collect and Max to get, found by a
// name typed in chat and read or changed for the loaded character.
//
// A change is the Game Data item editor's save at the "only for this character"
// tier: the item's whole resolved record with the asked fields changed, through
// GameDataOverrideApplier, so the editor shows it at that tier and
// AutoGetItemsManager, which resolves an item's rules on every survey, acts on
// it from the next one.
public sealed class AutoGetItemRules
{
    private const string Table = "Items";

    private readonly SettingsResolver _resolver;
    private readonly ItemNameStore _names;
    private readonly ItemOverlaySeedStore _seed;

    public AutoGetItemRules(SettingsResolver resolver, ItemNameStore names, ItemOverlaySeedStore seed)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(seed);
        _resolver = resolver;
        _names = names;
        _seed = seed;
    }

    // An item's rules as they resolve. Max is null when there is none (blank, or
    // "All"): the engine then collects every copy it sees.
    public readonly record struct State(bool AutoCollect, int? Max, bool CannotBeTaken);

    // The items the typed name stands for (ItemNameStore.FindAllByName).
    public IReadOnlyList<int> Find(string typed) => _names.FindAllByName(typed);

    public string NameOf(int number) =>
        _names.GetName(number) ?? $"item #{number.ToString(CultureInfo.InvariantCulture)}";

    // Only a named character has a tier of its own to hold the change.
    public bool CanChange => _resolver.CanWriteAt(SettingsTier.Character);

    public State Read(int number) => StateOf(Resolve(number));

    // Set Auto-collect (when autoCollect is given) and Max to get (when setMax is,
    // a null max clearing it) at the character tier, and return the rules as they
    // now resolve.
    public State Change(int number, bool? autoCollect, bool setMax, int? max)
    {
        string id = Id(number);
        ItemOverlay seed = _seed.GetOverlay(number);
        ItemOverlay existing = _resolver.ResolveGameData(Table, id, seed);
        ItemOverlay updated = existing;
        // Off is written as false, not left null as the editor's untick leaves it:
        // the batch editor's way, so a lower tier's tick can't show through and
        // the Auto-Light pickup reads it as turned off.
        if (autoCollect is { } on) updated = updated with { AutoCollect = on };
        if (setMax) updated = updated with { MaxToGet = max?.ToString(CultureInfo.InvariantCulture) };
        if (updated == existing) return StateOf(existing);

        GameDataOverrideApplier.Apply(_resolver, Table, id, SettingsTier.Character,
            updated, updated.Equals(seed));
        ItemOverlay now = Resolve(number);
        // A change that brings the record back to its installed defaults clears the
        // character's override, which lets a Global or BBS rule for the item show
        // through. Asked for this character, it has to hold for this character.
        if (now.AutoCollect != updated.AutoCollect || now.MaxToGet != updated.MaxToGet)
        {
            _resolver.WriteGameDataAt(SettingsTier.Character, Table, id, updated);
            now = Resolve(number);
        }
        return StateOf(now);
    }

    private ItemOverlay Resolve(int number) =>
        _resolver.ResolveGameData(Table, Id(number), _seed.GetOverlay(number));

    private static string Id(int number) => number.ToString(CultureInfo.InvariantCulture);

    private static State StateOf(ItemOverlay o)
    {
        int cap = ItemOverlay.ParseCount(o.MaxToGet, int.MaxValue);
        return new State(o.AutoCollect ?? false, cap == int.MaxValue ? null : cap, o.CannotBeTaken ?? false);
    }
}
