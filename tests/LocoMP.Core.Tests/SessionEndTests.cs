using System.Collections.Generic;
using LocoMP.Core.Net;
using LocoMP.Core.Protocol;
using LocoMP.Core.Session;
using LocoMP.Transport;
using Xunit;

namespace LocoMP.Core.Tests;

/// <summary>
/// M5.2 clean session end: <see cref="NetServer.AnnounceSessionEnd"/> tells every admitted AND queued
/// peer the session is over on purpose (Save &amp; Stop, dedicated shutdown), so their UI can say so
/// instead of inferring a dead link. Announce-only: teardown stays with the caller's save/dispose path.
/// </summary>
public class SessionEndTests
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
    public void The_announcement_reaches_every_admitted_and_queued_peer_exactly_once()
    {
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        using var server = new NetServer(hub.Server, new ServerConfig(Identity, maxPlayers: 1), clock);

        using var host = new NetClient(hub.Connect(out _), Identity, "Host", clock, playerKey: "kH");
        Pump(server, new[] { host });
        using var bob = new NetClient(hub.Connect(out _), Identity, "Bob", clock, playerKey: "kB");
        Pump(server, new[] { host, bob });
        Assert.True(host.JoinSettled);
        Assert.Equal(1, bob.QueuePosition);            // the cap holds Bob in the admission queue

        var hostNotices = new List<(AdminNoticeKind Kind, string Arg)>();
        var bobNotices = new List<(AdminNoticeKind Kind, string Arg)>();
        host.AdminNotice += (k, a) => hostNotices.Add((k, a));
        bob.AdminNotice += (k, a) => bobNotices.Add((k, a));

        server.AnnounceSessionEnd("server shutting down");
        Pump(server, new[] { host, bob });

        Assert.Equal(new[] { (AdminNoticeKind.SessionEnded, "server shutting down") }, hostNotices);
        Assert.Equal(new[] { (AdminNoticeKind.SessionEnded, "server shutting down") }, bobNotices);
    }

    [Fact]
    public void The_announcement_tears_nothing_down_itself()
    {
        // The announce-only contract: saving must run AFTER the announcement with the session state
        // intact (so the final save is identical to a plain leave), and the links only die when the
        // caller disposes the transport.
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        using var server = new NetServer(hub.Server, new ServerConfig(Identity), clock);

        using var host = new NetClient(hub.Connect(out _), Identity, "Host", clock, playerKey: "kH");
        using var bob = new NetClient(hub.Connect(out _), Identity, "Bob", clock, playerKey: "kB");
        Pump(server, new[] { host, bob });
        Assert.Equal(2, server.PlayerCount);

        server.AnnounceSessionEnd("bye");
        Pump(server, new[] { host, bob });

        Assert.Equal(2, server.PlayerCount);
        Assert.True(host.Joined);
        Assert.True(bob.Joined);
    }
}
