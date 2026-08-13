using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Undertow.Protocol;
using Undertow.Transports;

namespace Undertow.Server.Tests;

public class FrameLimitTests
{
    /// <summary>
    /// One frame limit, three observable places: the enforced inbound cap,
    /// IConnected's maxMessageSize (twice — top level and
    /// serviceConfiguration), and the Engine.IO handshake's maxPayload. All
    /// wired from one config field, asserted here so they cannot drift.
    /// </summary>
    [Fact]
    public void FrameCap_AgreesAcrossAllThreeObservables()
    {
        const long cap = 16_777_216;

        // Engine.IO handshake.
        var open = Encoding.UTF8.GetString(SocketIoTransport.EncodeOpenFrame("SID", cap));
        using var openJson = JsonDocument.Parse(open.AsSpan(1).ToString());
        Assert.Equal(cap, openJson.RootElement.GetProperty("maxPayload").GetInt64());

        // IConnected.
        var claims = new Signet.TokenClaims(
            "doc", [Signet.Scope.DocRead], "fluid",
            new Signet.User("u", Microsoft.FSharp.Collections.MapModule.Empty<string, Json>()),
            0, 1, "1.0", Microsoft.FSharp.Core.FSharpOption<string>.None);
        var connected = Encoding.UTF8.GetString(DocumentProtocol.connectedResponse(
            claims, "CID", "write", false, [], [], [], "", 0, 0, cap));
        using var connectedJson = JsonDocument.Parse(connected);
        Assert.Equal(cap, connectedJson.RootElement.GetProperty("maxMessageSize").GetInt64());
        Assert.Equal(cap, connectedJson.RootElement
            .GetProperty("serviceConfiguration").GetProperty("maxMessageSize").GetInt64());

        // The enforced cap is the same config value by construction
        // (UndertowConfig.MaxFrameBytes feeds both transports); pin the default.
        Assert.Equal(cap, UndertowConfig.DefaultMaxFrameBytes);
    }

    /// <summary>
    /// The Socket.IO pong deadline, separately from the coordinator sweep:
    /// different mechanisms, different windows (45 s vs 60 s).
    /// </summary>
    [Fact]
    public void PongDeadline_IsIntervalPlusTimeout()
    {
        var time = new FakeTimeProvider();
        var lastInbound = time.GetTimestamp();

        // Just before the 45 s allowance: not overdue (a pong arriving right
        // before a tick must not be judged stale by the next one).
        time.Advance(TimeSpan.FromMilliseconds(SocketIoTransport.PingIntervalMs + SocketIoTransport.PingTimeoutMs - 1));
        Assert.False(SocketIoTransport.PongOverdue(time, lastInbound));

        // Past it: overdue, and the transport closes the socket itself.
        time.Advance(TimeSpan.FromMilliseconds(2));
        Assert.True(SocketIoTransport.PongOverdue(time, lastInbound));
    }
}
