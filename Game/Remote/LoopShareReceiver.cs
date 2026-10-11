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
// 2026-10-11). When we hold a loop of that name the two are compared by signature
// (LoopSignature: the route, every room's settings, and the two loop-wide settings,
// the sender's against this character's). The same: rejected, nothing saved or
// changed. Different: saved as new under "<name> 2" (the next free number), and the
// sender's two loop settings become this character's for that new loop alone. With
// no loop of that name it is saved under it, the sender's two settings with it.
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
    private DateTimeOffset _acceptUntil = DateTimeOffset.MinValue;
    private string? _expectedSender;
    private bool _disposed;

    public LoopShareReceiver(ChatRouter chat, LoopManager loops, Action<string> notice,
        LogService? log = null, Func<DateTimeOffset>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(chat);
        ArgumentNullException.ThrowIfNull(loops);
        ArgumentNullException.ThrowIfNull(notice);
        _chat = chat;
        _loops = loops;
        _notice = notice;
        _log = log;
        _now = clock ?? (() => DateTimeOffset.Now);
        _chat.EntryClassified += Ingest;
    }

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
        Loop loop;
        try { loop = LoopShareCodec.Decode(parts!); }
        catch (FormatException ex)
        {
            _log?.Warn("LoopShare", $"loop from {sender} couldn't be read: {ex.Message}");
            _notice($"[Loop from {sender} arrived damaged, not saved]");
            return;
        }
        Save(loop, sender);
    }

    private void Save(Loop loop, string sender)
    {
        if (_loops.SetName is null)
        {
            _log?.Warn("LoopShare", $"loop '{loop.Name}' from {sender} not saved — no game-data set is active");
            _notice($"[Received loop '{loop.Name}' {loop.Waypoints.Count} rooms from {sender}, not saved: no game data set is loaded]");
            return;
        }

        // A loop of that name another client saved a moment ago counts for the
        // clash checks below.
        _loops.TakeInOutsideChanges();
        // The sender's loop as the sender runs it: its route with the two settings it
        // sent. Ours, below, is the loop of that name as this character runs it.
        string theirs = LoopSignature.Of(loop);
        string theirCode = LoopSignature.Short(theirs);
        int rooms = loop.Waypoints.Count;
        if (_loops.Get(loop.Name) is not { } existing)
        {
            // Save keeps the two loop settings the sender offered as this character's
            // for the loop, and the rest in the loop file.
            _loops.Save(loop);
            _log?.Info("LoopShare", $"saved loop '{loop.Name}' [{theirCode}] ({rooms} rooms) received from {sender}.");
            _notice($"[Received loop '{loop.Name}' {rooms} rooms from {sender}]");
            return;
        }

        string ours = LoopSignature.Of(existing);
        if (ours == theirs)
        {
            _log?.Info("LoopShare",
                $"loop '{loop.Name}' [{theirCode}] from {sender} is the loop we have under that name — nothing saved, nothing changed.");
            _notice($"[Received loop '{loop.Name}' {rooms} rooms from {sender}, already have it ({theirCode})]");
            return;
        }

        // The name is taken by a different loop: other rooms, other room settings or
        // other loop settings. Ours stays as it is, with this character's settings
        // for it, and theirs is saved beside it under a numbered name. Taking their
        // two settings for the loop we have would change a run already under way.
        string original = loop.Name;
        loop.Name = NumberedName(original);
        _loops.Save(loop);
        string ourCode = LoopSignature.Short(ours);
        _log?.Info("LoopShare",
            $"saved loop '{loop.Name}' [{theirCode}] ({rooms} rooms) received from {sender}: "
            + $"'{existing.Name}' here is a different loop [{ourCode}] and is left as it is.");
        _notice($"[Received loop '{original}' {rooms} rooms from {sender}, saved as '{loop.Name}' ({theirCode}): "
            + $"your '{existing.Name}' is a different loop ({ourCode})]");
    }

    // "<name> 2", or the next number up that no loop has. Loop names are matched
    // without case, as the catalogue matches them.
    private string NumberedName(string name)
    {
        for (int n = 2; ; n++)
        {
            string candidate = $"{name} {n}";
            if (_loops.Get(candidate) is null) return candidate;
        }
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
