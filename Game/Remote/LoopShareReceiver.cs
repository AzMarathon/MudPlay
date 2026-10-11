using System.Collections.Generic;
using System.Linq;
using MudPlay.Game.Map;
using MudPlay.Services;

namespace MudPlay.Game.Remote;

// Requester side of `@loop send`: collects the `@loopdata <id> <i>/<n> <chunk>` lines
// a player sends after we answered their offer with `@loop send yes`, reassembles
// them (LoopShareCodec — a loop is only useful whole), and saves the loop to our
// catalogue.
//
// Lines are adopted only inside a window our OWN outbound `@loop send yes` opens
// (NoteSendConfirmed, from the outbound-chat watcher), and — when we confirmed by
// telepath, so we know who to — only from that player. The permission gate is on the
// sender, who only offered because they grant us @loop; we just confirm we asked.
//
// A received loop never overwrites one of ours, nor changes how we run one (user,
// 2026-10-11). It is compared by signature (LoopSignature: the route, every room's
// settings, and the two loop-wide settings, the sender's against this character's)
// with the loop we hold of that name and with the numbered copies saved beside it.
// The same as any of them: rejected, nothing saved or changed. Different from all:
// saved under the name when it is free, else as new under "<name> 2" (the next free
// number), and the sender's two loop settings become this character's for the loop
// saved alone.
//
// Before any of that the loop is checked against the code the sender put on its
// offer, and when the transfer is over the sender is told, once, what became of it
// (Tell): the two ends can see they hold the same loop.
public sealed class LoopShareReceiver : IDisposable
{
    // Long enough for a big loop's paced lines (~800ms apart) and a slow sender.
    private static readonly TimeSpan AcceptWindow = TimeSpan.FromMinutes(2);

    // A loop worth sending fits well under this; the cap bounds what a stream of
    // bogus lines can make us buffer.
    private const int MaxChunks = 100;

    private readonly ChatRouter _chat;
    private readonly LoopManager _loops;
    private readonly Action<string> _notice;
    private readonly Func<DateTimeOffset> _now;
    private readonly LogService? _log;
    private readonly Dictionary<(string Sender, string Id), string?[]> _partial = new();

    // The code on the last offer each player made us (null: the offer had none),
    // by given name. The loop that player then sends is checked against it.
    private readonly Dictionary<string, (string? Code, DateTimeOffset At)> _offered =
        new(StringComparer.OrdinalIgnoreCase);

    // An offer lapses on the sender's side after two minutes and a transfer has
    // AcceptWindow to arrive; an offer older than both was for some earlier ask.
    private static readonly TimeSpan OfferKept = TimeSpan.FromMinutes(5);

    private DateTimeOffset _acceptUntil = DateTimeOffset.MinValue;
    private string? _expectedSender;
    private bool _disposed;

    // Sends the sender our one answer when a transfer is over: channel, their name,
    // the text, and whether it is a failure. False when it was held back (the master
    // switch, or the Warn setting for a failure): RemoteCommandManager.SendOutcome.
    private readonly Func<RemoteChannel, string, string, bool, bool>? _tell;

    public LoopShareReceiver(ChatRouter chat, LoopManager loops, Action<string> notice,
        LogService? log = null, Func<DateTimeOffset>? clock = null,
        Func<RemoteChannel, string, string, bool, bool>? tell = null)
    {
        ArgumentNullException.ThrowIfNull(chat);
        ArgumentNullException.ThrowIfNull(loops);
        ArgumentNullException.ThrowIfNull(notice);
        _chat = chat;
        _loops = loops;
        _notice = notice;
        _log = log;
        _now = clock ?? (() => DateTimeOffset.Now);
        _tell = tell;
        _chat.EntryClassified += Ingest;
    }

    // Tell the sender what became of the loop, on the channel it came by, once. A
    // sender on this version shows it beside its own code for the loop, so both
    // players see the two ends hold the same loop, or that it has to be sent again
    // (user, 2026-10-11). An older sender's client shows the line and does nothing
    // with it.
    //
    // Every transfer that completes gets exactly one answer, the two that fail
    // before a loop is in hand included: lines that could not be put together, and
    // nowhere to save (decided 2026-10-11). With the master switch off the loop is
    // still taken, the user having typed the yes, and the sender is not told: with
    // that switch off nothing goes out by itself (decided 2026-10-11).
    private void Tell(RemoteChannel channel, string sender, string? code, string outcome, bool failed = false)
    {
        if (_tell is null) return;
        bool told = _tell(channel, sender, LoopShareHandler.ResultLine(code, outcome), failed);
        _log?.Info("LoopShare", told
            ? $"told {sender}: {outcome}"
            : $"{sender} was not told ({outcome}): the master switch is off"
              + (failed ? ", or answers about a failed command are switched off in Settings → Talk" : string.Empty) + ".");
    }

    private static RemoteChannel ChannelOf(ChatChannel channel) => channel switch
    {
        ChatChannel.Gangpath => RemoteChannel.Gangpath,
        ChatChannel.Local => RemoteChannel.Local,
        _ => RemoteChannel.Telepath,
    };

    // We just sent `@loop send yes`. sender is the telepath recipient, or null when we
    // confirmed on gangpath / say (then any player on that channel may answer).
    public void NoteSendConfirmed(string? sender)
    {
        _acceptUntil = _now() + AcceptWindow;
        _expectedSender = string.IsNullOrWhiteSpace(sender) ? null : sender;
        _partial.Clear();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _chat.EntryClassified -= Ingest;
    }

    internal void Ingest(ChatLogEntry e)
    {
        if (e.Channel is not (ChatChannel.TelepathIncoming or ChatChannel.Gangpath or ChatChannel.Local))
            return;
        if (e.Speaker is not { Length: > 0 } sender) return;
        if (LoopShareHandler.TryParseOffer(e.Message, out string? offeredCode))
        {
            // Kept whether or not we go on to say yes: the code is what the loop
            // that arrives is checked against.
            _offered[Given(sender)] = (offeredCode, _now());
            return;
        }
        if (!TryParse(e.Message, out string id, out int index, out int count, out string chunk)) return;
        if (_now() > _acceptUntil)
        {
            _log?.Debug("LoopShare", $"ignoring @loopdata from {sender} — no @loop send yes sent recently");
            return;
        }
        if (_expectedSender is { } expected && !SameGiven(expected, sender))
        {
            _log?.Debug("LoopShare", $"ignoring @loopdata from {sender} — we confirmed with {expected}");
            return;
        }

        (string, string) key = (sender, id);
        if (!_partial.TryGetValue(key, out string?[]? parts))
        {
            parts = new string?[count];
            _partial[key] = parts;
        }
        else if (parts.Length != count)
        {
            _log?.Warn("LoopShare", $"discarding @loopdata from {sender} — its line count changed mid-transfer");
            _partial.Remove(key);
            return;
        }
        parts[index - 1] = chunk;
        if (parts.Any(p => p is null)) return;

        _partial.Remove(key);
        _acceptUntil = DateTimeOffset.MinValue;   // one transfer per yes
        // The offer this transfer answers: one per transfer, as the yes is.
        string? offered = _offered.Remove(Given(sender), out (string? Code, DateTimeOffset At) offer)
            && _now() - offer.At <= OfferKept ? offer.Code : null;
        RemoteChannel channel = ChannelOf(e.Channel);
        Loop loop;
        bool lairWaitUnknown;
        try { loop = LoopShareCodec.Decode(parts!, out lairWaitUnknown); }
        catch (FormatException ex)
        {
            _log?.Warn("LoopShare", $"loop from {sender} couldn't be read: {ex.Message}");
            _notice($"[Loop from {sender} arrived damaged, not saved]");
            Tell(channel, sender, code: null, "not saved: it arrived damaged - send it again", failed: true);
            return;
        }
        Save(loop, sender, channel, offered, lairWaitUnknown);
    }

    private void Save(Loop loop, string sender, RemoteChannel channel, string? offered, bool lairWaitUnknown)
    {
        // The sender's loop as the sender runs it: its route with the two settings it
        // sent. Ours, further down, is a loop here as this character runs it.
        string theirs = LoopSignature.Of(loop);
        string theirCode = LoopSignature.Short(theirs);
        int rooms = loop.Waypoints.Count;

        // The loop that arrived against the code the sender put on its offer. They
        // differ when the transfer was damaged past what the packing catches, or
        // when lines of two transfers were mixed: what arrived is then not what we
        // said yes to, and it is not saved.
        string verification;
        if (lairWaitUnknown)
        {
            // Its lair wait is a later version's and was read as off, so the loop in
            // hand differs from the one offered in that setting, knowingly. Taken as
            // it always was, unchecked, and the notice says why (decided 2026-10-11;
            // the other course was to turn it away as not the loop offered).
            verification = "not verified: it uses a setting this version does not know";
        }
        else if (offered is null)
        {
            verification = "not verified: no code came with the offer";
        }
        else if (theirs.StartsWith(offered, StringComparison.Ordinal))
        {
            verification = "verified";
        }
        else
        {
            _log?.Warn("LoopShare",
                $"loop '{loop.Name}' from {sender} is not the loop offered [received {theirCode}, offered {offered}] — not saved.");
            _notice($"[Received loop '{loop.Name}' {rooms} rooms from {sender} does not match the loop offered "
                + $"(received {theirCode}, offered {offered}), not saved]");
            Tell(channel, sender, theirCode, $"not saved: it is not the loop offered ({offered}) - send it again", failed: true);
            return;
        }
        string checkedAs = $"({theirCode}, {verification})";

        if (_loops.SetName is null)
        {
            _log?.Warn("LoopShare", $"loop '{loop.Name}' from {sender} not saved — no game-data set is active");
            _notice($"[Received loop '{loop.Name}' {rooms} rooms from {sender}, not saved: no game data set is loaded]");
            Tell(channel, sender, theirCode, "not saved: no game data set is loaded here", failed: true);
            return;
        }

        // A loop of that name another client saved a moment ago counts for the
        // clash checks below.
        _loops.TakeInOutsideChanges();

        // Held already, under that name or under one of the numbered names an
        // earlier copy of it was saved as: rejected. Without the numbered ones, the
        // same loop sent twice to someone whose own loop of that name differs piled
        // up as "<name> 2", "<name> 3" (user, 2026-10-11). Each is compared as this
        // character runs it, with its own two settings for that loop.
        if (_loops.WithNumberedCopies(loop.Name).FirstOrDefault(held => LoopSignature.Of(held) == theirs) is { } same)
        {
            _log?.Info("LoopShare",
                $"loop '{loop.Name}' {checkedAs} from {sender} is the loop we have as '{same.Name}' — nothing saved, nothing changed.");
            _notice($"[Received loop '{loop.Name}' {rooms} rooms from {sender} {checkedAs}, already have it as '{same.Name}']");
            Tell(channel, sender, theirCode, $"already have it as '{same.Name}'");
            return;
        }

        if (_loops.Get(loop.Name) is not { } existing)
        {
            // Save keeps the two loop settings the sender offered as this character's
            // for the loop, and the rest in the loop file.
            _loops.Save(loop);
            _log?.Info("LoopShare", $"saved loop '{loop.Name}' {checkedAs} ({rooms} rooms) received from {sender}.");
            _notice($"[Received loop '{loop.Name}' {rooms} rooms from {sender} {checkedAs}]");
            Tell(channel, sender, theirCode, $"saved as '{loop.Name}'");
            return;
        }

        // The name is taken by a different loop: other rooms, other room settings or
        // other loop settings. Ours stays as it is, with this character's settings
        // for it, and theirs is saved beside it under a numbered name. Taking their
        // two settings for the loop we have would change a run already under way.
        string original = loop.Name;
        loop.Name = _loops.NumberedName(original);
        _loops.Save(loop);
        string ourCode = LoopSignature.ShortOf(existing);
        _log?.Info("LoopShare",
            $"saved loop '{loop.Name}' {checkedAs} ({rooms} rooms) received from {sender}: "
            + $"'{existing.Name}' here is a different loop [{ourCode}] and is left as it is.");
        _notice($"[Received loop '{original}' {rooms} rooms from {sender} {checkedAs}, saved as '{loop.Name}': "
            + $"your '{existing.Name}' is a different loop ({ourCode})]");
        Tell(channel, sender, theirCode, $"saved as '{loop.Name}'");
    }

    // Parse "@loopdata <id> <i>/<n> <chunk>", tolerating the {} a remote reply is
    // wrapped in.
    private static bool TryParse(string message, out string id, out int index, out int count, out string chunk)
    {
        id = chunk = string.Empty;
        index = count = 0;
        string body = message.Trim();
        if (body.Length >= 2 && body[0] == '{' && body[^1] == '}') body = body[1..^1].Trim();

        string[] parts = body.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 4) return false;
        if (!parts[0].Equals(LoopShareHandler.DataToken, StringComparison.OrdinalIgnoreCase)) return false;
        string[] fraction = parts[2].Split('/');
        if (fraction.Length != 2
            || !int.TryParse(fraction[0], out index) || !int.TryParse(fraction[1], out count)
            || count < 1 || count > MaxChunks || index < 1 || index > count)
            return false;
        id = parts[1];
        chunk = parts[3];
        return true;
    }

    private static bool SameGiven(string a, string b) =>
        Given(a).Equals(Given(b), StringComparison.OrdinalIgnoreCase);

    private static string Given(string name)
    {
        int space = name.IndexOf(' ');
        return space >= 0 ? name[..space] : name;
    }
}
