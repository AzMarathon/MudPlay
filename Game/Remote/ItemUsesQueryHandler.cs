using System.Collections.Generic;
using System.Linq;
using MudPlay.Game.Inventory;

namespace MudPlay.Game.Remote;

// Read-only handler for `@uses` — with no argument it lists every carried limited-use
// item and its remaining charges; with a name (shorthand welcome, best-match) it reports
// just that one. Reads the shared CarriedChargeReadout (Paradigm: authoritative look
// counts; stock: max − counted-uses). Gated as an inventory query. Never touches the
// wire beyond its reply.
public sealed class ItemUsesQueryHandler : IDisposable
{
    private static readonly string[] RegisteredCommands = { "@uses" };

    private readonly RemoteCommandManager _engine;
    private readonly CarriedChargeReadout _charges;
    private bool _disposed;

    public ItemUsesQueryHandler(RemoteCommandManager engine, CarriedChargeReadout charges)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(charges);
        _engine = engine;
        _charges = charges;
        Register("@uses", OnUses);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (string cmd in RegisteredCommands) _engine.UnregisterHandler(cmd);
    }

    private void Register(string command, Action<RemoteCommandContext> handler)
    {
        if (!RemoteCommandCatalog.TryGetCategory(command, out Models.GameData.PlayerRemoteControls category))
            throw new InvalidOperationException(
                $"RemoteCommandCatalog missing entry for '{command}'. Add it to the Map before registering.");
        _engine.RegisterHandler(command, category, handler);
    }

    private void OnUses(RemoteCommandContext ctx)
    {
        string query = string.Join(' ', ctx.Args).Trim();

        // Bare @uses — every carried charged item and its remaining charges.
        if (query.Length == 0)
        {
            IReadOnlyList<CarriedChargeReadout.ChargedItem> all = _charges.AllCharged();
            ctx.Reply(all.Count == 0
                ? "no limited-use items carried"
                : "item uses - " + string.Join(", ", all.Select(c => $"{c.Name}: {Show(c.Remaining)}")));
            return;
        }

        if (_charges.ResolveCarried(query) is not { } name)
        {
            ctx.Reply($"no carried item matches '{query}'");
            return;
        }
        int? remaining = _charges.RemainingForName(name);
        if (remaining is { } n)
            ctx.Reply($"{name}: {n} use(s) remaining");
        else if (_charges.IsLimitedUse(name))
            ctx.Reply($"{name}: charges not read yet");   // Paradigm — look at it, or wait for the auto-look
        else
            ctx.Reply($"{name} isn't a limited-use item");
    }

    private static string Show(int? remaining) => remaining is { } n ? n.ToString() : "?";
}
