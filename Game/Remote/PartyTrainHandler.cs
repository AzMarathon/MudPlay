using System.Globalization;
using MudPlay.Game.Train;
using MudPlay.Models.GameData;

namespace MudPlay.Game.Remote;

// Wire side of the party auto-train handshake — `@ptrain <sub> …`, parsed here and
// handed to PartyTrainCoordinator, which decides whether to act (its own toggle,
// sender is the leader, …):
//
//   st <payload>            member → leader: a PartyTrainStatus report
//   ask                     leader → member: send your report
//   train <target> [m/r]    leader → member: train at trainer room m/r (walking there
//                           first if it isn't in it), up to level <target>
//   give <copper> <name>    leader → member: cover <copper> of <name>'s fee
//   with <copper>           leader → member: withdraw your fee at this bank
//   done <levels>           member → leader: trained and back in the party
//
// Party-whitelist gated (catalog None). A malformed order is dropped silently:
// the only sender is another MudPlay client, and a reply would just be chatter.
public sealed class PartyTrainHandler : IDisposable
{
    private const string Command = "@ptrain";

    private readonly RemoteCommandManager _engine;
    private readonly PartyTrainCoordinator _coordinator;
    private bool _disposed;

    public PartyTrainHandler(RemoteCommandManager engine, PartyTrainCoordinator coordinator)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(coordinator);
        _engine = engine;
        _coordinator = coordinator;

        if (!RemoteCommandCatalog.TryGetCategory(Command, out PlayerRemoteControls category))
            throw new InvalidOperationException($"RemoteCommandCatalog missing entry for '{Command}'.");
        _engine.RegisterHandler(Command, category, OnPartyTrain);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _engine.UnregisterHandler(Command);
    }

    private void OnPartyTrain(RemoteCommandContext ctx)
    {
        IReadOnlyList<string> a = ctx.Args;
        if (a.Count == 0) return;
        switch (a[0].ToLowerInvariant())
        {
            case "st":
                _coordinator.ReceiveStatus(ctx.Sender, string.Join(' ', a.Skip(1)));
                break;
            case "ask":
                _coordinator.ReceiveAsk(ctx.Sender);
                break;
            case "train" when a.Count >= 2 && TryInt(a[1], out long target):
                // Optional trainer room ("train 2 1/2147") so a member that didn't make
                // it in can walk there itself.
                Game.Map.RoomKey? room = a.Count >= 3 && Game.Map.RoomKey.TryParseWire(a[2], out Game.Map.RoomKey k)
                    ? k : null;
                _coordinator.ReceiveTrain(ctx.Sender, (int)Math.Clamp(target, 0, int.MaxValue), room);
                break;
            case "give" when a.Count >= 3 && TryInt(a[1], out long copper) && copper > 0:
                _coordinator.ReceiveGive(ctx.Sender, copper, a[2]);
                break;
            case "with" when a.Count >= 2 && TryInt(a[1], out long copper):
                _coordinator.ReceiveWithdraw(ctx.Sender, copper);
                break;
            case "done" when a.Count >= 2 && TryInt(a[1], out long levels):
                _coordinator.ReceiveDone(ctx.Sender, (int)Math.Clamp(levels, 0, int.MaxValue));
                break;
        }
    }

    private static bool TryInt(string text, out long value) =>
        long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
}
