using System.Collections.Generic;
using LocoMP.Bot;
using LocoMP.Core.Career;
using LocoMP.Core.Presence;
using LocoMP.Core.Protocol;
using LocoMP.Core.Session;
using LocoMP.Transport;
using Xunit;

namespace LocoMP.Core.Tests;

/// <summary>
/// The bot harness's lifecycle logic, verified over Loopback with a manual clock — fully
/// deterministic. This is what keeps tools/LocoMP.Bot from rotting: its join/churn/reject/reconnect
/// paths are pinned in the game-free suite even as the protocol evolves.
/// </summary>
public class BotClientTests
{
    private static readonly HandshakeRequest Identity = new(ProtocolVersion.Current, "B99.7", "0.0.2");

    private static void Pump(NetServer server, BotClient bot, ManualClock clock, int rounds = 8, long msPerRound = 50)
    {
        for (int i = 0; i < rounds; i++)
        {
            clock.Advance(msPerRound);
            bot.Tick(msPerRound / 1000.0);
            server.Poll();
        }
    }

    [Fact]
    public void Bot_joins_and_streams_orbit_poses_the_server_applies()
    {
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        using var server = new NetServer(hub.Server, new ServerConfig(Identity), clock);

        var center = new Pose(100f, 50f, 200f, 0f, 0f, 0f, 1f);
        using var bot = new BotClient("TestBot", () => hub.Connect(out _), Identity,
            new OrbitBehavior(center, radius: 5, speedMetresPerSecond: 2), clock, _ => { });

        Pump(server, bot, clock);

        Assert.True(bot.Joined);
        Assert.Equal(1, server.PlayerCount);
        Assert.True(bot.PosesSent > 0);

        // The server's roster holds a live orbit pose: on the circle (radius 5 in XZ) at centre height.
        PlayerState state = server.Players.Values.Single();
        Assert.Equal("TestBot", state.Name);
        double dx = state.Pose.Px - center.Px, dz = state.Pose.Pz - center.Pz;
        Assert.Equal(5.0, Math.Sqrt(dx * dx + dz * dz), precision: 3);
        Assert.Equal(center.Py, state.Pose.Py);
    }

    [Fact]
    public void Churn_bot_leaves_and_rejoins_on_schedule()
    {
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        using var server = new NetServer(hub.Server, new ServerConfig(Identity), clock);

        using var bot = new BotClient("Churner", () => hub.Connect(out _), Identity,
            new IdleBehavior(Pose.Identity), clock, _ => { }, churnSeconds: 2);

        Pump(server, bot, clock);                        // join #1
        Assert.True(bot.Joined);
        Assert.Equal(1, bot.JoinCount);

        Pump(server, bot, clock, rounds: 60, msPerRound: 100);  // ride through leave + rejoin cycles

        Assert.True(bot.JoinCount >= 2, $"expected a rejoin, got {bot.JoinCount} join(s)");
        Assert.Equal(1, server.PlayerCount);             // never more than one roster entry per bot
    }

    [Fact]
    public void A_rejected_bot_stops_instead_of_retrying()
    {
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        // Server expects a different game build — the bot must be refused by the handshake.
        var serverSide = new HandshakeRequest(ProtocolVersion.Current, "B100", "0.0.2");
        using var server = new NetServer(hub.Server, new ServerConfig(serverSide), clock);

        using var bot = new BotClient("Mismatch", () => hub.Connect(out _), Identity,
            new IdleBehavior(Pose.Identity), clock, _ => { });

        Pump(server, bot, clock);
        Assert.False(bot.Joined);
        Assert.NotNull(bot.RejectReason);
        Assert.Contains("build", bot.RejectReason);

        // Long after the rejection (past any timeout/backoff), it must NOT have re-attempted.
        int joinsAfterReject = (int)bot.JoinCount;
        Pump(server, bot, clock, rounds: 40, msPerRound: 1000);
        Assert.Equal(joinsAfterReject, (int)bot.JoinCount);
        Assert.Equal(0, server.PlayerCount);
    }

    [Fact]
    public void A_bot_with_no_server_times_out_and_retries_with_backoff()
    {
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        // No NetServer is attached to the hub's server endpoint — joins go unanswered.
        int attempts = 0;
        using var bot = new BotClient("Lonely", () => { attempts++; return hub.Connect(out _); }, Identity,
            new IdleBehavior(Pose.Identity), clock, _ => { });

        // Ride well past connect-timeout + backoff a few times over.
        for (int i = 0; i < 50; i++)
        {
            clock.Advance(1000);
            bot.Tick(1.0);
        }

        Assert.False(bot.Joined);
        Assert.Null(bot.RejectReason);                   // no server ≠ rejected — keep trying
        Assert.True(attempts >= 3, $"expected repeated attempts, got {attempts}");
    }

    [Fact]
    public void A_reconnecting_claimant_re_arms_from_the_board_and_resumes_reporting()
    {
        // R3-5: on Loopback the whole join burst — including the restored claim's JobState — is
        // ingested in the reconnecting bot's FIRST Poll, before its first SessionTick can wire the
        // RemoteActor's handlers. That is the live race's losing side, deterministic here: without
        // the board re-scan the relaunched claimant holds its claim mute and never reports.
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        var career = new CareerConfig
        {
            Preset = ProgressionPreset.PerPlayer,
            StartingBalanceCents = 500_00,
            ClaimTtlMs = 60_000,
            ReconnectGraceMs = 10_000,
            TargetAvailableJobs = 3,
            JobSeed = 7,
            Stations = new[] { "SM", "GF", "HB" },
            JobTypes = new[] { new JobTypeSpec("FH", "steel", 100_00, 2, 4) },
        };
        using var server = new NetServer(hub.Server, new ServerConfig(Identity, career: career), clock);

        var logs = new List<string>();
        var claimOnly = new BotOptions { ClaimFirst = true };            // claim, but never report —
        var actor = new RemoteActor(claimOnly, "Claimer", logs.Add);     // the job must survive phase 1
        using (var bot = new BotClient("Claimer", () => hub.Connect(out _), Identity,
                   new IdleBehavior(Pose.Identity), clock, logs.Add, playerKey: "claimer-key"))
        {
            bot.SessionTick = actor.Tick;
            Pump(server, bot, clock, rounds: 12, msPerRound: 500);
            Assert.Contains(logs, l => l.Contains("claimed job"));
        }
        server.Poll();                                                   // graceful leave → grace hold
        Assert.Contains(server.Career.Registry.Jobs.Values, j => j.State == JobLifecycle.Claimed);

        logs.Clear();
        var reporting = new BotOptions { ClaimFirst = true, ReportIntervalSeconds = 1 };
        var actor2 = new RemoteActor(reporting, "Claimer", logs.Add);    // fresh process, same key
        using var bot2 = new BotClient("Claimer", () => hub.Connect(out _), Identity,
            new IdleBehavior(Pose.Identity), clock, logs.Add, playerKey: "claimer-key");
        bot2.SessionTick = actor2.Tick;
        Pump(server, bot2, clock, rounds: 40, msPerRound: 100);

        Assert.Contains(logs, l => l.Contains("restored claim on job"));
        Assert.Contains(logs, l => l.Contains("reporting delivery on job"));
    }
}
