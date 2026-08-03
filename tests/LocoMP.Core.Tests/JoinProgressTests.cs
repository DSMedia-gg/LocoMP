using System.Collections.Generic;
using LocoMP.Core.Net;
using LocoMP.Core.Protocol;
using LocoMP.Core.Session;
using LocoMP.Transport;
using Xunit;

namespace LocoMP.Core.Tests;

/// <summary>
/// M5.1 join progress + structured rejects. The join interstitial's readiness gate clears ONLY on
/// <see cref="NetClient.JoinSettled"/> (the server's JoinBurstComplete sentinel) — these tests pin
/// that the sentinel arrives after the burst, that inferred stages advance monotonically, and that
/// a refusal carries a machine-readable kind + have/need instead of only prose.
/// </summary>
public class JoinProgressTests
{
    private static readonly HandshakeRequest Identity = new(ProtocolVersion.Current, "B99.7", "0.0.2");

    private static void Pump(NetServer server, IEnumerable<NetClient> clients, int rounds = 6)
    {
        for (int i = 0; i < rounds; i++)
        {
            server.Poll();
            foreach (NetClient c in clients) c.Poll();
        }
    }

    [Fact]
    public void Join_stages_advance_in_order_and_settle_on_the_sentinel()
    {
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        using var server = new NetServer(hub.Server, new ServerConfig(Identity), clock);

        using var client = new NetClient(hub.Connect(out _), Identity, "Alice", clock);
        var stages = new List<JoinStage>();
        client.JoinStageChanged += s => stages.Add(s);

        Assert.Equal(JoinStage.None, client.Stage);
        Assert.False(client.JoinSettled);
        Pump(server, new[] { client });

        Assert.True(client.Joined);
        Assert.True(client.JoinSettled);
        Assert.Equal(JoinStage.Complete, client.Stage);

        // Every transition observed, strictly forward: the ordered channel plus the monotonic guard
        // means the recorded sequence can never wobble, whatever each burst family happened to send.
        for (int i = 1; i < stages.Count; i++) Assert.True(stages[i] > stages[i - 1]);
        Assert.Equal(JoinStage.Connecting, stages[0]);
        Assert.Contains(JoinStage.World, stages);
        Assert.Contains(JoinStage.Career, stages);   // CareerState is always sent in the burst
        Assert.Contains(JoinStage.Items, stages);    // the shop catalog is always the item burst's head
        Assert.Equal(JoinStage.Complete, stages[^1]);
    }

    [Fact]
    public void The_sentinel_arrives_after_the_career_burst_it_seals()
    {
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        using var server = new NetServer(hub.Server, new ServerConfig(Identity), clock);

        using var client = new NetClient(hub.Connect(out _), Identity, "Alice", clock);
        bool careerBeforeComplete = false;
        bool careerArrived = false;
        client.Career.CareerStateReceived += () =>
        {
            careerArrived = true;
            careerBeforeComplete = client.Stage != JoinStage.Complete;
        };
        bool checkedAtComplete = false;
        client.JoinStageChanged += s =>
        {
            // When the sentinel lands, the career burst must already be delivered — the barrier claim.
            if (s == JoinStage.Complete) { Assert.True(careerArrived); checkedAtComplete = true; }
        };
        Pump(server, new[] { client });

        Assert.True(client.JoinSettled);
        Assert.True(careerBeforeComplete);
        Assert.True(checkedAtComplete);
    }

    [Fact]
    public void In_session_career_traffic_never_regresses_a_settled_stage()
    {
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        using var server = new NetServer(hub.Server, new ServerConfig(Identity), clock);

        using var client = new NetClient(hub.Connect(out _), Identity, "Alice", clock);
        Pump(server, new[] { client });
        Assert.True(client.JoinSettled);

        // A live career round-trip (grant or reject, either way a career-family reply) must leave
        // the stage settled — the inference path is a no-op after the sentinel.
        client.Career.PurchaseLicense("DE2");
        Pump(server, new[] { client });

        Assert.Equal(JoinStage.Complete, client.Stage);
        Assert.True(client.JoinSettled);
    }

    [Fact]
    public void Disconnect_resets_the_stage_to_none()
    {
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        var server = new NetServer(hub.Server, new ServerConfig(Identity), clock);

        var client = new NetClient(hub.Connect(out _), Identity, "Alice", clock);
        Pump(server, new[] { client });
        Assert.True(client.JoinSettled);

        JoinStage? last = null;
        client.JoinStageChanged += s => last = s;
        server.Dispose();
        hub.Server.Dispose();
        for (int i = 0; i < 4; i++) client.Poll();

        Assert.Equal(JoinStage.None, client.Stage);
        Assert.Equal(JoinStage.None, last);          // the reset is announced, not silent
        Assert.False(client.JoinSettled);
        client.Dispose();
    }

    [Fact]
    public void A_password_reject_carries_its_kind_and_no_have_need()
    {
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        using var server = new NetServer(hub.Server, new ServerConfig(Identity, password: "hunter2"), clock);

        using var client = new NetClient(hub.Connect(out _), Identity, "Alice", clock, password: "wrong");
        Pump(server, new[] { client });

        Assert.False(client.Joined);
        Assert.NotNull(client.RejectDetail);
        RejectInfo info = client.RejectDetail!.Value;
        Assert.Equal(RejectKind.Password, info.Kind);
        Assert.False(info.IsVersionMismatch);
        Assert.Null(info.ClientHas);
        Assert.Null(info.ServerNeeds);
    }

    [Fact]
    public void A_build_mismatch_reject_carries_exact_have_need()
    {
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        using var server = new NetServer(hub.Server, new ServerConfig(Identity), clock);

        var wrongBuild = new HandshakeRequest(ProtocolVersion.Current, "B100", "0.0.2");
        using var client = new NetClient(hub.Connect(out _), wrongBuild, "Alice", clock);
        Pump(server, new[] { client });

        Assert.False(client.Joined);
        RejectInfo info = client.RejectDetail!.Value;
        Assert.Equal(RejectKind.GameBuild, info.Kind);
        Assert.True(info.IsVersionMismatch);
        Assert.Equal("B100", info.ClientHas);
        Assert.Equal("B99.7", info.ServerNeeds);
        // The transport stays up after a reject (the reason must be deliverable), so the stage sits
        // at Connecting — it must never claim burst progress for a join that was refused.
        Assert.Equal(JoinStage.Connecting, client.Stage);
    }

    [Fact]
    public void A_protocol_mismatch_reject_names_both_versions()
    {
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        using var server = new NetServer(hub.Server, new ServerConfig(Identity), clock);

        var oldProtocol = new HandshakeRequest(ProtocolVersion.Current - 1, "B99.7", "0.0.2");
        using var client = new NetClient(hub.Connect(out _), oldProtocol, "Alice", clock);
        Pump(server, new[] { client });

        RejectInfo info = client.RejectDetail!.Value;
        Assert.Equal(RejectKind.Protocol, info.Kind);
        Assert.Equal($"v{ProtocolVersion.Current - 1}", info.ClientHas);
        Assert.Equal($"v{ProtocolVersion.Current}", info.ServerNeeds);
    }

    [Fact]
    public void A_full_server_rejects_with_the_server_full_kind()
    {
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        using var server = new NetServer(hub.Server, new ServerConfig(Identity, maxPlayers: 1), clock);

        using var first = new NetClient(hub.Connect(out _), Identity, "Alice", clock);
        Pump(server, new[] { first });
        Assert.True(first.Joined);

        using var second = new NetClient(hub.Connect(out _), Identity, "Bob", clock);
        Pump(server, new[] { first, second });

        Assert.False(second.Joined);
        Assert.Equal(RejectKind.ServerFull, second.RejectDetail!.Value.Kind);
    }

    [Fact]
    public void VersionHandshake_check_carries_structured_have_need()
    {
        var server = new HandshakeRequest(13, "B99.7", "0.0.2");

        HandshakeResult protocol = VersionHandshake.Check(new HandshakeRequest(12, "B99.7", "0.0.2"), server);
        Assert.Equal(RejectKind.Protocol, protocol.Kind);
        Assert.Equal("v12", protocol.ClientHas);
        Assert.Equal("v13", protocol.ServerNeeds);

        HandshakeResult mods = VersionHandshake.Check(new HandshakeRequest(13, "B99.7", "0.0.3"), server);
        Assert.Equal(RejectKind.ModVersion, mods.Kind);
        Assert.Equal("0.0.3", mods.ClientHas);
        Assert.Equal("0.0.2", mods.ServerNeeds);

        HandshakeResult ok = VersionHandshake.Check(server, server);
        Assert.True(ok.Compatible);
        Assert.Equal(RejectKind.Other, ok.Kind);
    }
}
