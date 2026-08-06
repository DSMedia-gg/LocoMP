using System.Collections.Generic;
using LocoMP.Core.Net;
using LocoMP.Core.Protocol;
using LocoMP.Core.Session;
using LocoMP.Transport;
using Xunit;

namespace LocoMP.Core.Tests;

/// <summary>
/// The M5.2 diagnostics snapshot: <see cref="NetServer.CaptureDiagnostics"/> reflects live session state
/// (roster, queue, moderation) drawn from the counters the server already keeps. Bandwidth/RTT are out of
/// scope here (transport-level).
/// </summary>
public class ServerDiagnosticsTests
{
    private static readonly HandshakeRequest Identity = new(ProtocolVersion.Current, "B99.7", "0.0.2");

    private static void Pump(NetServer server, IEnumerable<NetClient> clients, int rounds = 8)
    {
        for (int i = 0; i < rounds; i++)
        {
            server.Poll();
            foreach (NetClient c in clients) c.Poll();
        }
    }

    [Fact]
    public void A_fresh_server_reports_an_empty_healthy_session()
    {
        var hub = new LoopbackNetwork();
        using var server = new NetServer(hub.Server, new ServerConfig(Identity), new ManualClock());

        ServerDiagnostics d = server.CaptureDiagnostics();
        Assert.Equal(0, d.Players);
        Assert.Equal(0, d.Queued);
        Assert.Equal(0, d.Admins);
        Assert.Equal(0, d.BannedKeys);
        Assert.False(d.JoinsPaused);
        Assert.True(d.MoneyConservationHolds);
        Assert.True(d.ItemConservationHolds);
    }

    [Fact]
    public void Roster_and_owner_show_up_after_admits()
    {
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        using var server = new NetServer(hub.Server, new ServerConfig(Identity), clock);

        using var host = new NetClient(hub.Connect(out _), Identity, "Host", clock, playerKey: "kH");
        using var bob = new NetClient(hub.Connect(out _), Identity, "Bob", clock, playerKey: "kB");
        Pump(server, new[] { host, bob });

        ServerDiagnostics d = server.CaptureDiagnostics();
        Assert.Equal(2, d.Players);
        Assert.Equal(1, d.Admins);   // just the owner
    }

    [Fact]
    public void Moderation_state_is_reflected()
    {
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        using var server = new NetServer(hub.Server, new ServerConfig(Identity), clock);

        using var host = new NetClient(hub.Connect(out _), Identity, "Host", clock, playerKey: "kH");
        Pump(server, new[] { host });
        using var bob = new NetClient(hub.Connect(out int bobId), Identity, "Bob", clock, playerKey: "kB");
        Pump(server, new[] { host, bob });

        host.PauseJoins();
        host.Ban(bobId);
        Pump(server, new[] { host, bob });

        ServerDiagnostics d = server.CaptureDiagnostics();
        Assert.True(d.JoinsPaused);
        Assert.Equal(1, d.BannedKeys);
        Assert.Equal(1, d.Players);  // Bob was kicked by the ban
    }

    [Fact]
    public void Promotion_raises_the_admin_count()
    {
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        using var server = new NetServer(hub.Server, new ServerConfig(Identity), clock);

        using var host = new NetClient(hub.Connect(out _), Identity, "Host", clock, playerKey: "kH");
        Pump(server, new[] { host });
        using var bob = new NetClient(hub.Connect(out int bobId), Identity, "Bob", clock, playerKey: "kB");
        Pump(server, new[] { host, bob });

        host.Promote(bobId);
        Pump(server, new[] { host, bob });

        Assert.Equal(2, server.CaptureDiagnostics().Admins);
    }
}
