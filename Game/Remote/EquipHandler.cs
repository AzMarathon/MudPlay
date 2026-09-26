using System;
using MudPlay.Game.Inventory;
using MudPlay.Models.GameData;
using MudPlay.Models.Profile;

namespace MudPlay.Game.Remote;

// @equip <set> — a permitted party member asks us to swap to one of our saved gear
// sets; @equip <set> update rewrites that set to what we're wearing right now.
// @equip-all wears the Default set. The set is resolved by keyword, then name, then
// the built-in short names (default / backstab / resthp / restma / moving / bossing).
//
// The dashed @equip-<set> form is still accepted (a party member on an older client
// sends it) through the prefix router, which folds the suffix in as Args[0] — so both
// forms reach OnEquip with the set first and "update" second. @equip-all rides that
// same prefix.
//
// ExecuteCommands-gated per the catalog — a "do something on my behalf" action,
// like @do / @train. Failure replies (unknown set, busy) obey WarnOnDenial; the
// success acknowledgement is sent unconditionally.
public sealed class EquipHandler : IDisposable
{
    private const string Command = "@equip";
    private const string Prefix = "@equip-";

    private readonly RemoteCommandManager _engine;
    private readonly EquipmentManager _equipment;
    private bool _disposed;

    public EquipHandler(RemoteCommandManager engine, EquipmentManager equipment)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(equipment);
        _engine = engine;
        _equipment = equipment;

        if (!RemoteCommandCatalog.TryGetCategory(Command, out PlayerRemoteControls category))
            throw new InvalidOperationException($"RemoteCommandCatalog missing entry for '{Command}'.");
        _engine.RegisterHandler(Command, category, OnEquip);
        _engine.RegisterPrefixHandler(Prefix, category, OnEquip);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _engine.UnregisterHandler(Command);
        _engine.UnregisterPrefixHandler(Prefix);
    }

    private void OnEquip(RemoteCommandContext ctx)
    {
        if (ctx.Args.Count == 0)
        {
            if (_engine.WarnOnDenial) ctx.Reply("usage: @equip <set> [update]");
            return;
        }

        string keyword = ctx.Args[0];
        bool update = ctx.Args.Count > 1
            && string.Equals(ctx.Args[1], "update", StringComparison.OrdinalIgnoreCase);

        if (update)
        {
            UpdateFromWorn(ctx, keyword);
            return;
        }

        // @equip-all is the remote twin of the "Equip All" action-menu item, which
        // applies the Default gear set — NOT a lookup of a set literally named
        // "all" (report stock-20260730-214959). Route it to the same trigger apply.
        if (string.Equals(keyword, "all", StringComparison.OrdinalIgnoreCase))
        {
            switch (_equipment.ApplyByTrigger(EquipTriggerType.Default))
            {
                case EquipResult.Applied:  ctx.Reply("equipping all (default gear set)"); break;
                case EquipResult.NoChange: ctx.Reply("default gear set already worn"); break;
                case EquipResult.NotFound: if (_engine.WarnOnDenial) ctx.Reply("no default gear set configured"); break;
                case EquipResult.Busy:     if (_engine.WarnOnDenial) ctx.Reply("busy equipping"); break;
            }
            return;
        }

        switch (_equipment.ApplyByKeyword(keyword))
        {
            case EquipResult.Applied:
                ctx.Reply($"equipping gear set '{keyword}'");
                break;
            case EquipResult.NoChange:
                ctx.Reply($"gear set '{keyword}' already worn");
                break;
            case EquipResult.NotFound:
                if (_engine.WarnOnDenial) ctx.Reply($"no gear set '{keyword}'");
                break;
            case EquipResult.Busy:
                if (_engine.WarnOnDenial) ctx.Reply("busy equipping");
                break;
        }
    }

    private void UpdateFromWorn(RemoteCommandContext ctx, string keyword)
    {
        EquipUpdateResult r = _equipment.UpdateSetFromWorn(keyword);
        switch (r.Outcome)
        {
            case EquipUpdateOutcome.Updated:
                ctx.Reply($"gear set '{r.SetName}' updated to what I'm wearing ({r.Slots} slot{(r.Slots == 1 ? "" : "s")})");
                break;
            case EquipUpdateOutcome.NotFound:
                if (_engine.WarnOnDenial) ctx.Reply($"no gear set '{keyword}'");
                break;
            case EquipUpdateOutcome.Busy:
                if (_engine.WarnOnDenial) ctx.Reply("busy equipping — try again when the swap finishes");
                break;
            case EquipUpdateOutcome.InventoryUnknown:
                if (_engine.WarnOnDenial) ctx.Reply("haven't read my inventory yet — try again after an 'i'");
                break;
        }
    }
}
