namespace MudPlay.Game.Remote;

// Per-invocation context handed to each RemoteCommandManager handler. Carries
// the sender / parsed args / channel + the two callbacks that answer on the
// channel the command arrived on (telepath in → telepath reply, etc.).
//
// Handlers never talk to Net.TelnetClient directly — they call Reply / Refuse for
// chat-channel responses or any other service-mediated path. Keeping the handler
// ignorant of the wire lets the engine swap reply formatting per channel without
// every handler having to know the wire-syntax for each chat type.
//
// Args is the post-command tokens split on whitespace. For @goto Newhaven Cabin
// the args are ["Newhaven", "Cabin"]; for @party rest the args are ["rest"] and
// the @party <sub> handler inspects args[0] to route.
//
//   Sender          — player name as classified by ChatRouter.
//   Command         — the @-prefixed command as the sender typed it, less a
//                     trailing `!` (case is kept; the registry lookup ignores it).
//                     A local invocation lower-cases it.
//   Args            — post-command tokens split on whitespace; empty when the
//                     command stands alone.
//   OriginalMessage — the full chat-message text after the speaker prefix (e.g.
//                     "@goto Newhaven Cabin").
//   Channel         — which channel the command arrived on; determines where
//                     Reply routes.
//   Reply           — routes Reply(text) back through Channel. Engine-supplied —
//                     handlers shouldn't construct their own.
public readonly record struct RemoteCommandContext(
    string Sender,
    string Command,
    IReadOnlyList<string> Args,
    string OriginalMessage,
    RemoteChannel Channel,
    Action<string> Reply)
{
    // Where a failure answer goes. The engine binds it to its denial path, which
    // sends nothing while Settings → Talk "Warn sender on invalid / denied remote
    // command" is off and falls back to the configured failure message for a
    // null reason. Unbound (a context built by hand in a test), it is Reply.
    public Action<string?>? Refusal { get; init; }

    // Answer that the command was not carried out, and why. This is the one way a
    // handler says no: every invalid / denied reply obeys the Warn setting, so no
    // handler tests the setting itself. A null reason sends the generic failure
    // message.
    //
    // What does NOT go through here is an answer another MudPlay client reads to
    // decide what to do next. Those are protocol, sent with Reply whatever the
    // Warn setting says, and each is marked where it is sent:
    //   - PartyComebackManager's answers to @comeback, the "can't come" ones
    //     included: the follower's ComebackRequester reads every one, to give the
    //     follow up at once, to give up after a failed pickup, or to keep waiting.
    //     (Three are refusals all the same, by the user's ruling of 2026-10-10:
    //     a loop's "I can't" at a gate, and the "I can't yet" of a train trip and
    //     of Auto-Lair.)
    //   - @trap's acceptance and result, a failed disarm included: the asking
    //     client's walk reads TrapReply.
    //   - @wealth's "wealth unknown" and @level's "level unknown": the leader's
    //     wealth and level probes count that member as having answered.
    //   - @get-stash's "ok - …" and "busy …" lines, and every @deposit-all answer:
    //     a leader's stash transfer waits for one from each member.
    public void Refuse(string? reason = null)
    {
        if (Refusal is { } refuse) refuse(reason);
        else if (!string.IsNullOrEmpty(reason)) Reply(reason);
    }
}
