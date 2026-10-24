using System.Net;
using System.Net.Sockets;
using MudPlay.Net;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// A link that drops without anyone asking must be noticed and said so: what ended
// the read, whether the server had gone silent, and which Reconnect-when tick box
// would have redialed it (report paradigm-20261022-074654, where the terminal kept
// the last round box for 16 minutes after the link died).
public sealed class ConnectionLossTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 7, 30, 19, TimeSpan.FromHours(-7));

    [Fact]
    public async Task ServerClosingTheLink_EndsTheRead_AsServerClosed()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        await using TelnetClient client = new();
        TaskCompletionSource dropped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource read = new(TaskCreationOptions.RunContinuationsAsynchronously);
        client.DataReceived += _ => read.TrySetResult();
        client.Disconnected += () => dropped.TrySetResult();

        Task<TcpClient> accept = listener.AcceptTcpClientAsync();
        await client.ConnectAsync("127.0.0.1", port);
        using (TcpClient server = await accept)
        {
            await server.GetStream().WriteAsync("[HP=756/MA=229]:"u8.ToArray());
            await read.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        await dropped.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(TelnetClient.ServerClosed, client.EndedBecause);
        Assert.NotEqual(DateTimeOffset.MinValue, client.LastDataReceived);

        ConnectionLoss loss = ConnectionLoss.Of("bbs", port, 20, client.LastDataReceived, client.EndedBecause, DateTimeOffset.UtcNow);
        Assert.False(loss.WentSilent);
        Assert.Equal("Carrier is lost mid-session", loss.Trigger);
        Assert.Contains(TelnetClient.ServerClosed, loss.LogLine);
    }

    [Fact]
    public async Task ResetLink_EndsTheRead_WithTheSocketError()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        await using TelnetClient client = new();
        TaskCompletionSource dropped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Disconnected += () => dropped.TrySetResult();

        Task<TcpClient> accept = listener.AcceptTcpClientAsync();
        await client.ConnectAsync("127.0.0.1", port);
        using (TcpClient server = await accept)
        {
            // A zero timeout closes with a RST, as a remote host forcibly closing does.
            server.Client.Close(0);
        }
        await dropped.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(string.IsNullOrWhiteSpace(client.EndedBecause));
        Assert.NotEqual(TelnetClient.ServerClosed, client.EndedBecause);
        Assert.NotEqual(TelnetClient.ClosedByClient, client.EndedBecause);
    }

    [Fact]
    public void Silence_PastTheIdleWindowAndGrace_IsNoResponse()
    {
        // The report's drop: 41 s after the last byte, with a 20 s idle window.
        DateTimeOffset lastRead = Now - TimeSpan.FromSeconds(41);
        ConnectionLoss loss = ConnectionLoss.Of("paramud.mudinfo.net", 2323, 20, lastRead, "reset", Now);

        Assert.True(loss.WentSilent);
        Assert.Equal("Server stops responding", loss.Trigger);
        Assert.Equal("[CONNECTION LOST: paramud.mudinfo.net 2323 - NOTHING FROM THE SERVER FOR 41s]", loss.TerminalLine);
        Assert.Contains("\"SERVER STOPS RESPONDING\" IS OFF", loss.NotReconnectingLine);
    }

    [Fact]
    public void Silence_InsideTheGrace_OrWithKeepaliveOff_IsCarrierLost()
    {
        Assert.False(ConnectionLoss.Silent(20, Now - TimeSpan.FromSeconds(24), Now));
        Assert.True(ConnectionLoss.Silent(20, Now - TimeSpan.FromSeconds(25), Now));
        Assert.False(ConnectionLoss.Silent(0, Now - TimeSpan.FromMinutes(10), Now));
        // Nothing ever read on a connection with keepalive on: silent from the start.
        Assert.True(ConnectionLoss.Silent(20, DateTimeOffset.MinValue, Now));
    }

    [Fact]
    public void ShortSilence_LeavesTheTerminalLineBare()
    {
        ConnectionLoss loss = ConnectionLoss.Of("bbs", 23, 20, Now - TimeSpan.FromSeconds(2), TelnetClient.ServerClosed, Now);
        Assert.Equal("[CONNECTION LOST: bbs 23]", loss.TerminalLine);
    }

    [Fact]
    public void Summary_CarriesTheReconnectOutcome()
    {
        ConnectionLoss loss = ConnectionLoss.Of("bbs", 23, 20, Now - TimeSpan.FromSeconds(41), "reset", Now)
            with { Reconnect = "not armed: 'Server stops responding' is off" };
        string summary = loss.Summary(Now + TimeSpan.FromMinutes(16));

        Assert.Contains("no response", summary);
        Assert.Contains("nothing from the server for 41s", summary);
        Assert.Contains("the read ended: reset", summary);
        Assert.EndsWith("reconnect: not armed: 'Server stops responding' is off", summary);
    }
}
