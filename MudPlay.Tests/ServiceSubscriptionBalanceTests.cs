using Mono.Cecil;
using Mono.Cecil.Cil;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// A window, view-model or control made per open that hooks a handler onto a long-lived
// service and never unhooks it is kept alive by that service after it closes, visual tree
// and all, one copy per open (the Navigation window leaked this way). So scan the IL of the
// presentation layers for every `source.Event += handler` whose source is a service (a type
// in MudPlay.Services or MudPlay.Game, however it was reached: a field, a getter chain off
// AppServices, a parameter, a local, or a static event) and require the matching `-=`
// somewhere in the same type, its lambdas and closures included. A lambda that captures the
// subscriber can't be unhooked at all unless it was kept in a variable first, so that fails
// too, and so does a hook whose IL the scan can't follow, rather than slipping past.
//
// What it can't see: a `-=` is matched by source type, event and handler, not by instance,
// so unhooking from another object of the same type would pass; and a handler handed to a
// helper that subscribes on the caller's behalf is invisible. The types that live as long
// as the app are named below and left out.
public sealed class ServiceSubscriptionBalanceTests
{
    // The main window, its view-model and its terminal: built once and kept for the whole
    // session, so their service hooks never need undoing.
    private static readonly HashSet<string> AppLifetime = new(StringComparer.Ordinal)
    {
        "MudPlay.ViewModels.MainWindowViewModel",
        "MudPlay.Views.MainWindow",
        "MudPlay.Controls.TerminalControl",
    };

    [Fact]
    public void EveryServiceEventHookedByAWindowOrViewModel_IsUnhooked()
    {
        using AssemblyDefinition asm = AssemblyDefinition.ReadAssembly(typeof(AppServices).Assembly.Location);

        List<string> problems = new();
        int seen = 0;
        foreach (TypeDefinition type in asm.MainModule.Types)
        {
            if (type.Namespace is not { } ns) continue;
            if (!(ns.StartsWith("MudPlay.ViewModels", StringComparison.Ordinal)
                  || ns.StartsWith("MudPlay.Views", StringComparison.Ordinal)
                  || ns.StartsWith("MudPlay.Controls", StringComparison.Ordinal))) continue;
            if (AppLifetime.Contains(type.FullName)) continue;

            Dictionary<string, int> added = new(), removed = new();
            List<string> lambdas = new(), unreadable = new();
            Collect(type, added, removed, lambdas, unreadable);
            seen += added.Count;
            foreach (string sub in added.Keys)
                if (!removed.ContainsKey(sub)) problems.Add($"{type.FullName}: never unhooked: {sub}");
            foreach (string sub in lambdas)
                problems.Add($"{type.FullName}: lambda that can't be unhooked: {sub}");
            foreach (string site in unreadable)
                problems.Add($"{type.FullName}: hook the scan can't read (write it plainly or teach the scan): {site}");
        }

        // The scan reads one IL shape; if the compiler's output moves under it, the count
        // collapses rather than the test silently passing.
        Assert.True(seen > 200, $"the scan found only {seen} service subscriptions; the IL shape it reads has changed");
        Assert.True(problems.Count == 0,
            "Service event hooks in a window, view-model or control that outlive it:\n  "
            + string.Join("\n  ", problems));
    }

    private static void Collect(TypeDefinition type, Dictionary<string, int> added, Dictionary<string, int> removed,
                                List<string> lambdas, List<string> unreadable)
    {
        foreach (MethodDefinition method in type.Methods)
        {
            if (!method.HasBody) continue;
            IList<Instruction> il = method.Body.Instructions;
            for (int i = 1; i < il.Count; i++)
            {
                if (il[i].OpCode != OpCodes.Callvirt && il[i].OpCode != OpCodes.Call) continue;
                if (il[i].Operand is not MethodReference { Name: { } name } accessor) continue;
                bool add = name.StartsWith("add_", StringComparison.Ordinal);
                if (!add && !name.StartsWith("remove_", StringComparison.Ordinal)) continue;
                if (accessor.Parameters.Count != 1) continue;
                string eventName = name[(add ? 4 : 7)..];

                // A static lambda the compiler caches in its `<>c` singleton holds nothing.
                if (il[i - 1].OpCode == OpCodes.Stsfld && IsCompilerType(((FieldReference)il[i - 1].Operand).DeclaringType))
                    continue;

                // A branch inside the hook's expressions (`(a ?? b).E += h`) stops the walk.
                string site = $"{method.Name}: {name}";
                int handlerStart = ExpressionStart(il, i);
                if (handlerStart < 0) { unreadable.Add(site); continue; }
                string? source;
                if (accessor.HasThis)
                {
                    int sourceStart = ExpressionStart(il, handlerStart);
                    if (sourceStart < 0) { unreadable.Add(site); continue; }
                    source = ServiceSource(method, il, sourceStart, handlerStart);
                }
                else
                {
                    source = IsService(accessor.DeclaringType) ? accessor.DeclaringType.FullName : null;
                }
                if (source is null) continue;

                string? handler = Handler(il, handlerStart, i, out bool capturingLambda);
                if (handler is null) { unreadable.Add($"{site} on {source}"); continue; }
                string key = $"{source}.{eventName} -> {handler}";
                if (capturingLambda)
                {
                    if (add) lambdas.Add(key);
                    continue;
                }
                Dictionary<string, int> into = add ? added : removed;
                into[key] = into.GetValueOrDefault(key) + 1;
            }
        }
        foreach (TypeDefinition nested in type.NestedTypes) Collect(nested, added, removed, lambdas, unreadable);
    }

    // Describe the delegate built by il[start..end): a method group by its method, a
    // delegate read from a field (a lambda kept to unhook later, or a closure-hoisted
    // local) by that field. A lambda built in place is flagged when it captures anything:
    // `-=` can never name it.
    private static string? Handler(IList<Instruction> il, int start, int end, out bool capturingLambda)
    {
        capturingLambda = false;
        Instruction last = il[end - 1];
        if (last.OpCode == OpCodes.Ldfld || last.OpCode == OpCodes.Ldsfld)
            return "field " + ((FieldReference)last.Operand).Name;
        if (last.OpCode != OpCodes.Newobj || end - 2 < start) return null;
        Instruction ftn = il[end - 2];
        if ((ftn.OpCode != OpCodes.Ldftn && ftn.OpCode != OpCodes.Ldvirtftn) || ftn.Operand is not MethodReference target)
            return null;
        // A local function (`<Ctor>g__Name|n`) is named again by `-= Name`, like a method.
        if (target.Name.StartsWith('<') && !target.Name.Contains(">g__", StringComparison.Ordinal))
        {
            capturingLambda = !IsCompilerType(target.DeclaringType) || CapturesThis(target.DeclaringType);
            return "lambda " + target.Name;
        }
        return $"{target.DeclaringType.Name}.{target.Name}";
    }

    // The source an event was hooked on, by its type, when that type is a service; null
    // otherwise. A cast is looked through to what was cast.
    private static string? ServiceSource(MethodDefinition method, IList<Instruction> il, int start, int end)
    {
        int at = end - 1;
        while (at > start && (il[at].OpCode == OpCodes.Castclass || il[at].OpCode == OpCodes.Isinst)) at--;
        TypeReference? type = SourceType(method, il[at]);
        if (type is not null && IsService(type)) return type.FullName;
        // A collection or other object a service owns and hands out (a store's Entries).
        if (il[at].Operand is MemberReference { DeclaringType: { } owner } member
            && il[at].OpCode != OpCodes.Newobj && IsService(owner))
            return $"{owner.FullName}.{member.Name}";
        return null;
    }

    private static TypeReference? SourceType(MethodDefinition method, Instruction instr)
    {
        if (instr.Operand is FieldReference f) return f.FieldType;
        if (instr.Operand is MethodReference m && (instr.OpCode == OpCodes.Call || instr.OpCode == OpCodes.Callvirt))
            return m.ReturnType;
        if (instr.Operand is VariableDefinition v) return v.VariableType;
        if (instr.Operand is ParameterDefinition p) return p.ParameterType;
        int local = instr.OpCode.Code switch
        {
            Code.Ldloc_0 => 0, Code.Ldloc_1 => 1, Code.Ldloc_2 => 2, Code.Ldloc_3 => 3, _ => -1,
        };
        if (local >= 0) return method.Body.Variables[local].VariableType;
        int arg = instr.OpCode.Code switch
        {
            Code.Ldarg_1 => 1, Code.Ldarg_2 => 2, Code.Ldarg_3 => 3, _ => -1,
        };
        if (arg >= 0) return method.Parameters[method.HasThis ? arg - 1 : arg].ParameterType;
        return null;
    }

    private static bool IsService(TypeReference type)
    {
        string ns = type.GetElementType().Namespace ?? "";
        if (type.IsNested) ns = type.DeclaringType.GetElementType().Namespace ?? "";
        return ns.StartsWith("MudPlay.Services", StringComparison.Ordinal)
            || ns.StartsWith("MudPlay.Game", StringComparison.Ordinal);
    }

    private static bool IsCompilerType(TypeReference type) => type.Name.StartsWith('<');

    private static bool CapturesThis(TypeReference closure) =>
        closure.Resolve() is { } t && t.Fields.Any(f => f.Name.Contains("__this", StringComparison.Ordinal));

    // The first instruction of the expression that leaves the one value consumed at il[end]:
    // walk back, netting each instruction's pushes against its pops, until that value is
    // accounted for. -1 when the walk meets something it can't net (a branch target in
    // between, or the method's start).
    private static int ExpressionStart(IList<Instruction> il, int end)
    {
        int need = 1;
        for (int k = end - 1; k >= 0; k--)
        {
            if (!Effect(il[k], out int pop, out int push)) return -1;
            need = need - push + pop;
            if (need == 0) return k;
            if (need < 0) return -1;
        }
        return -1;
    }

    private static bool Effect(Instruction instr, out int pop, out int push)
    {
        OpCode op = instr.OpCode;
        if (op.FlowControl is FlowControl.Branch or FlowControl.Cond_Branch or FlowControl.Return or FlowControl.Throw)
        {
            pop = push = 0;
            return false;
        }
        if (instr.Operand is MethodReference m && (op == OpCodes.Call || op == OpCodes.Callvirt || op == OpCodes.Newobj))
        {
            pop = m.Parameters.Count + (m.HasThis && op != OpCodes.Newobj ? 1 : 0);
            push = op == OpCodes.Newobj || m.ReturnType.FullName != "System.Void" ? 1 : 0;
            return true;
        }
        pop = op.StackBehaviourPop switch
        {
            StackBehaviour.Pop0 => 0,
            StackBehaviour.Pop1 or StackBehaviour.Popi or StackBehaviour.Popref => 1,
            StackBehaviour.Pop1_pop1 or StackBehaviour.Popi_pop1 or StackBehaviour.Popi_popi or StackBehaviour.Popi_popi8
                or StackBehaviour.Popi_popr4 or StackBehaviour.Popi_popr8 or StackBehaviour.Popref_pop1
                or StackBehaviour.Popref_popi => 2,
            StackBehaviour.Popi_popi_popi or StackBehaviour.Popref_popi_popi or StackBehaviour.Popref_popi_popi8
                or StackBehaviour.Popref_popi_popr4 or StackBehaviour.Popref_popi_popr8 or StackBehaviour.Popref_popi_popref
                => 3,
            _ => -1,
        };
        push = op.StackBehaviourPush switch
        {
            StackBehaviour.Push0 => 0,
            StackBehaviour.Push1 or StackBehaviour.Pushi or StackBehaviour.Pushi8 or StackBehaviour.Pushr4
                or StackBehaviour.Pushr8 or StackBehaviour.Pushref => 1,
            StackBehaviour.Push1_push1 => 2,
            _ => -1,
        };
        return pop >= 0 && push >= 0;
    }
}
