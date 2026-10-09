using Mono.Cecil;
using Mono.Cecil.Cil;
using MudPlay.Game.Emotes;
using MudPlay.Services;
using MudPlay.ViewModels;
using Xunit;

namespace MudPlay.Tests;

// The collection only serialises the classes that join it, and a class that forgets fails
// someone else's test, one run in several: an emote test added to another class brought the
// race back days after the collection was made. So membership is checked rather than remembered.
public sealed class EmoteRuntimeCollectionTests
{
    // The static state, what publishes to it and what answers a publish with a Rebuild.
    private static readonly HashSet<string> Touching = new()
    {
        typeof(EmoteRuntime).FullName!,
        typeof(EmoteStore).FullName!,
        typeof(ConversationViewModel).FullName!,
    };

    [Fact]
    public void EveryTestClassTouchingEmoteRuntime_JoinsTheCollection()
    {
        using AssemblyDefinition asm = AssemblyDefinition.ReadAssembly(typeof(EmoteRuntimeCollectionTests).Assembly.Location);

        List<string> touching = new();
        List<string> outside = new();
        foreach (TypeDefinition type in asm.MainModule.Types)
        {
            // This class names the three types to look for them.
            if (type.FullName == typeof(EmoteRuntimeCollectionTests).FullName) continue;
            if (!Touches(type)) continue;
            touching.Add(type.FullName);
            if (!InCollection(type)) outside.Add(type.FullName);
        }

        // Sanity: the scan finds the classes the collection was made for.
        Assert.Contains(typeof(EmoteStoreTests).FullName, touching);
        Assert.Contains(typeof(ConversationViewModelTests).FullName, touching);

        Assert.True(outside.Count == 0,
            $"These classes use EmoteRuntime, EmoteStore or ConversationViewModel and so need "
            + $"[Collection(EmoteRuntimeCollection.Name)]:\n  " + string.Join("\n  ", outside));
    }

    // Lambdas and async bodies compile into nested types, so those count as the class's own.
    private static bool Touches(TypeDefinition type)
    {
        foreach (MethodDefinition method in type.Methods)
        {
            if (!method.HasBody) continue;
            foreach (Instruction instr in method.Body.Instructions)
            {
                TypeReference? used = instr.Operand switch
                {
                    TypeReference t => t,
                    MemberReference { DeclaringType: { } declaring } => declaring,
                    _ => null,
                };
                if (used is not null && Touching.Contains(used.GetElementType().FullName)) return true;
            }
        }
        return type.NestedTypes.Any(Touches);
    }

    private static bool InCollection(TypeDefinition type) =>
        type.CustomAttributes.Any(a =>
            a.AttributeType.FullName == typeof(CollectionAttribute).FullName
            && a.ConstructorArguments.Count == 1
            && a.ConstructorArguments[0].Value as string == EmoteRuntimeCollection.Name);
}
