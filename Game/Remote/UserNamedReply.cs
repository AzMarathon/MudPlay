namespace MudPlay.Game.Remote;

// The replies of ours that carry a name a player chose: a loop's (`@loop send`)
// or an event's (@events, @event). Such a name can say anything, so a reply
// holding one can read as some other reply: a loop named "I can't come", an
// event named "Sell run (= 5 copper)". Every reader of a braced reply that goes
// by the words inside it (a @wealth's, a @where's, a @version's, a @status's, a
// @comeback's answer) asks here first and leaves these alone.
//
// A new reply that carries a user's name is added here once, by its fixed
// opening words, and every reader skips it.
public static class UserNamedReply
{
    // For the text inside the braces.
    public static bool IsLine(string inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        return LoopShareHandler.IsLoopSendLine(inner) || EventRemoteHandler.IsEventReplyLine(inner);
    }

    // The same, for a reply as it arrives, braces and all.
    public static bool IsMessage(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        string body = message.Trim();
        if (body.Length >= 2 && body[0] == '{' && body[^1] == '}') body = body[1..^1].Trim();
        return IsLine(body);
    }
}
