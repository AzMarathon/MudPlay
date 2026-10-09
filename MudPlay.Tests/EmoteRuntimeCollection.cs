using Xunit;

namespace MudPlay.Tests;

// EmoteRuntime is process-wide static state. Every EmoteStore construction and Commit publishes
// to it, and every live ConversationViewModel answers a publish with a full Rebuild on the
// publisher's thread. The app only ever does both on the UI thread, but xUnit runs test classes
// in parallel, so an EmoteStore test would rebuild a Conversation test's rows from another thread
// mid-assertion. Test classes that publish to or subscribe to EmoteRuntime join this collection,
// which makes them run one after another. EmoteRuntimeCollectionTests fails when a class
// that uses EmoteRuntime, EmoteStore or ConversationViewModel is left out.
[CollectionDefinition(Name)]
public sealed class EmoteRuntimeCollection
{
    public const string Name = "EmoteRuntime";
}
