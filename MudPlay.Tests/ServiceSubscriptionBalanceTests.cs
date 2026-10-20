using Mono.Cecil;
using Mono.Cecil.Cil;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// A view-model made per window open that hooks a handler onto an app-lifetime service and
// never unhooks it is kept alive by that service after its window closes, visual tree and
// all, one copy per open. Scan the IL of every disposable type in the presentation layers
// for `_services.X.Event += Handler` and require the matching `-=` in the same type. Types
// that aren't disposable (the main window's view-model, the terminal control) live as long
// as the app and are left out.
public sealed class ServiceSubscriptionBalanceTests
{
    [Fact]
    public void EveryServiceEventHookedByAWindowOrViewModel_IsUnhooked()
    {
        using AssemblyDefinition asm = AssemblyDefinition.ReadAssembly(typeof(AppServices).Assembly.Location);

        List<string> unbalanced = new();
        int seen = 0;
        foreach (TypeDefinition type in asm.MainModule.Types)
        {
            if (type.Namespace is not { } ns) continue;
            if (!(ns.StartsWith("MudPlay.ViewModels", StringComparison.Ordinal)
                  || ns.StartsWith("MudPlay.Views", StringComparison.Ordinal)
                  || ns.StartsWith("MudPlay.Controls", StringComparison.Ordinal))) continue;
            if (!IsDisposable(type)) continue;

            HashSet<string> added = new(), removed = new();
            Collect(type, added, removed);
            seen += added.Count;
            foreach (string sub in added)
                if (!removed.Contains(sub)) unbalanced.Add($"{type.FullName}: {sub}");
        }

        Assert.True(seen > 40, $"the scan found only {seen} service subscriptions; the IL shape it reads has changed");
        Assert.True(unbalanced.Count == 0,
            "These handlers are hooked onto an AppServices service and never unhooked:\n  "
            + string.Join("\n  ", unbalanced));
    }

    private static bool IsDisposable(TypeDefinition? type)
    {
        for (; type is not null; type = type.BaseType?.Resolve())
            if (type.Interfaces.Any(i => i.InterfaceType.FullName is "System.IDisposable" or "System.IAsyncDisposable"))
                return true;
        return false;
    }

    private static void Collect(TypeDefinition type, HashSet<string> added, HashSet<string> removed)
    {
        foreach (MethodDefinition method in type.Methods)
        {
            if (!method.HasBody) continue;
            IList<Instruction> il = method.Body.Instructions;
            for (int i = 4; i < il.Count; i++)
            {
                if (il[i].Operand is not MethodReference { Name: { } name }) continue;
                bool add = name.StartsWith("add_", StringComparison.Ordinal);
                if (!add && !name.StartsWith("remove_", StringComparison.Ordinal)) continue;
                if (il[i - 1].OpCode != OpCodes.Newobj) continue;
                if (il[i - 2].OpCode != OpCodes.Ldftn && il[i - 2].OpCode != OpCodes.Ldvirtftn) continue;
                if (il[i - 2].Operand is not MethodReference handler) continue;
                int receiver = il[i - 2].OpCode == OpCodes.Ldvirtftn ? i - 4 : i - 3;
                if (receiver < 1 || il[receiver].OpCode != OpCodes.Ldarg_0) continue;
                // A compiler-made method is a lambda's body, which `-=` can't name.
                if (handler.Name.StartsWith('<')) continue;
                if (ServicePath(il, receiver - 1) is not { } path) continue;
                string key = $"{path}.{name[(add ? 4 : 7)..]} -> {handler.Name}";
                (add ? added : removed).Add(key);
            }
        }
        foreach (TypeDefinition nested in type.NestedTypes) Collect(nested, added, removed);
    }

    // The getter chain the event source was read through, when it starts at AppServices
    // (a field of that type, or one of its own properties); null for anything else.
    private static string? ServicePath(IList<Instruction> il, int at)
    {
        List<string> getters = new();
        while (at >= 0 && il[at].Operand is MethodReference { Name: { } g, Parameters.Count: 0 } getter
               && (il[at].OpCode == OpCodes.Callvirt || il[at].OpCode == OpCodes.Call)
               && g.StartsWith("get_", StringComparison.Ordinal))
        {
            getters.Insert(0, g[4..]);
            if (getter.DeclaringType.FullName == typeof(AppServices).FullName)
                return string.Join(".", getters);
            at--;
        }
        if (at >= 0 && getters.Count > 0 && il[at].OpCode == OpCodes.Ldfld
            && il[at].Operand is FieldReference f && f.FieldType.FullName == typeof(AppServices).FullName)
            return string.Join(".", getters);
        return null;
    }
}
