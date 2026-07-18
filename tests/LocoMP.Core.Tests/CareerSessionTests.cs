using System.Collections.Generic;
using System.Linq;
using LocoMP.Core.Career;
using LocoMP.Core.Net;
using LocoMP.Core.Protocol;
using LocoMP.Core.Session;
using LocoMP.Transport;
using Xunit;

namespace LocoMP.Core.Tests;

/// <summary>
/// End-to-end career sync over the Loopback hub — the same NetServer/NetClient stack a live
/// session runs: career/board join burst, claim/task/complete with policy-routed wallet updates in
/// both presets, rejection UX, the stable-key handshake rules, and the M3 reconnect story (rejoin
/// within grace restores the claim EXACTLY; grace expiry releases it for everyone to see).
/// </summary>
public class CareerSessionTests
{
    private static readonly HandshakeRequest Identity = new(ProtocolVersion.Current, "B99.7", "0.0.2");

    private static CareerConfig Career(ProgressionPreset preset = ProgressionPreset.PerPlayer) => new()
    {
        Preset = preset,
        StartingBalanceCents = 500_00,
        ClaimTtlMs = 60_000,
        ReconnectGraceMs = 10_000,
        TargetAvailableJobs = 3,
        JobSeed = 7,
        Stations = new[] { "SM", "GF", "HB" },
        JobTypes = new[] { new JobTypeSpec("FH", "steel", 100_00, 2, 4) },
        LicensePrices = new Dictionary<string, long> { ["hazmat"] = 150_00 },
    };

    private static void Pump(NetServer server, IEnumerable<NetClient> clients, int rounds = 6)
    {
        for (int i = 0; i < rounds; i++)
        {
            server.Poll();
            foreach (NetClient c in clients) c.Poll();
        }
    }

    private static (LoopbackNetwork hub, ManualClock clock, NetServer server, NetClient a, NetClient b)
        Session(ProgressionPreset preset = ProgressionPreset.PerPlayer)
    {
        var hub = new LoopbackNetwork();
        var clock = new ManualClock();
        var server = new NetServer(hub.Server, new ServerConfig(Identity, career: Career(preset)), clock);
        var a = new NetClient(hub.Connect(out _), Identity, "Alice", clock, playerKey: "key-alice");
        var b = new NetClient(hub.Connect(out _), Identity, "Bob", clock, playerKey: "key-bob");
        Pump(server, new[] { a, b });
        return (hub, clock, server, a, b);
    }

    [Fact]
    public void Join_burst_delivers_career_state_and_the_whole_board()
    {
        var (_, _, _, a, b) = Session();

        Assert.Equal(ProgressionPreset.PerPlayer, a.Career.Preset);
        Assert.Equal(500_00, a.Career.BalanceCents);
        Assert.Equal(3, a.Career.Jobs.Count);                 // TargetAvailableJobs, generated on first poll
        Assert.Equal(3, b.Career.Jobs.Count);
        Assert.All(a.Career.Jobs.Values, j => Assert.Equal(JobLifecycle.Available, j.State));
    }

    [Fact]
    public void Claim_commits_on_the_server_and_mirrors_to_everyone_with_peer_identity()
    {
        var (_, _, server, a, b) = Session();
        int jobId = a.Career.Jobs.Keys.First();

        a.Career.ClaimJob(jobId);
        Pump(server, new[] { a, b });

        Assert.Equal(JobLifecycle.Claimed, b.Career.Jobs[jobId].State);
        Assert.Equal(a.LocalId, b.Career.Jobs[jobId].ClaimantPeerId);
        Assert.Equal("Alice", b.Career.Jobs[jobId].ClaimantName);
        Assert.Equal("key-alice", server.Career.Registry.Jobs[jobId].ClaimantKey); // key stays server-side
    }

    [Fact]
    public void Full_job_loop_pays_only_the_claimant_in_per_player()
    {
        var (_, _, server, a, b) = Session();
        int jobId = a.Career.Jobs.Keys.First();
        int tasks = a.Career.Jobs[jobId].Def.Tasks.Count;
        long payout = a.Career.Jobs[jobId].Def.PayoutCents;

        var events = new List<(EconomyEventKind kind, long cents)>();
        a.Career.EconomyEventReceived += (kind, cents, _) => events.Add((kind, cents));

        a.Career.ClaimJob(jobId);
        Pump(server, new[] { a, b });
        for (int i = 0; i < tasks; i++)
        {
            a.Career.ReportTask(jobId, i);
            Pump(server, new[] { a, b });
        }

        Assert.Equal(500_00 + payout, a.Career.BalanceCents);
        Assert.Equal(500_00, b.Career.BalanceCents);          // 02 §4: nothing lands anywhere else
        Assert.False(a.Career.Jobs.ContainsKey(jobId));       // completed jobs leave the mirror too
        Assert.Contains((EconomyEventKind.JobPayout, payout), events);
        Assert.True(server.Career.Registry.Ledger.ConservationHolds);
    }

    [Fact]
    public void Shared_career_broadcasts_the_one_wallet_to_everyone()
    {
        var (_, _, server, a, b) = Session(ProgressionPreset.SharedCareer);
        int jobId = a.Career.Jobs.Keys.First();
        int tasks = a.Career.Jobs[jobId].Def.Tasks.Count;
        long payout = a.Career.Jobs[jobId].Def.PayoutCents;

        a.Career.ClaimJob(jobId);
        Pump(server, new[] { a, b });
        for (int i = 0; i < tasks; i++)
        {
            a.Career.ReportTask(jobId, i);
            Pump(server, new[] { a, b });
        }

        Assert.Equal(500_00 + payout, a.Career.BalanceCents);
        Assert.Equal(500_00 + payout, b.Career.BalanceCents); // one shared wallet, both mirrors move
    }

    [Fact]
    public void Rejections_reach_the_requester_with_the_exact_reason()
    {
        var (_, _, server, a, b) = Session();

        string? reason = null;
        a.Career.RequestRejected += r => reason = r;

        a.Career.ClaimJob(9999);
        Pump(server, new[] { a, b });

        Assert.Equal("claim: unknown job 9999", reason);
    }

    [Fact]
    public void License_purchase_updates_scope_and_wallet()
    {
        var (_, _, server, a, b) = Session();

        a.Career.PurchaseLicense("hazmat");
        Pump(server, new[] { a, b });

        Assert.Contains("hazmat", a.Career.Licenses);
        Assert.Equal(350_00, a.Career.BalanceCents);
        Assert.DoesNotContain("hazmat", b.Career.Licenses);   // per-player scope is private
        Assert.Equal(500_00, b.Career.BalanceCents);
    }

    [Fact]
    public void Duplicate_or_invalid_player_keys_are_refused_at_the_door()
    {
        var (hub, clock, server, a, b) = Session();

        var dup = new NetClient(hub.Connect(out _), Identity, "Mallory", clock, playerKey: "key-alice");
        Pump(server, new[] { a, b, dup });
        Assert.Equal("player key already in session", dup.RejectReason);

        var bad = new NetClient(hub.Connect(out _), Identity, "Eve", clock, playerKey: "@shared");
        Pump(server, new[] { a, b, bad });
        Assert.Equal("invalid player key", bad.RejectReason);
    }

    [Fact]
    public void Rejoin_within_grace_restores_wallet_claim_and_progress_exactly()
    {
        var (hub, clock, server, a, b) = Session();
        int jobId = a.Career.Jobs.Keys.First();
        a.Career.ClaimJob(jobId);
        Pump(server, new[] { a, b });
        a.Career.ReportTask(jobId, 0);
        a.Career.PurchaseLicense("hazmat");                   // some money movement to restore too
        Pump(server, new[] { a, b });
        long balanceBefore = a.Career.BalanceCents;

        a.Leave();
        Pump(server, new[] { a, b });
        Assert.Equal(1, server.PlayerCount);
        Assert.Equal(0, b.Career.Jobs[jobId].ClaimantPeerId); // claimant offline: peer 0, name kept
        Assert.Equal("Alice", b.Career.Jobs[jobId].ClaimantName);

        clock.Advance(9_000);                                 // still inside the 10 s test hold
        var a2 = new NetClient(hub.Connect(out _), Identity, "Alice", clock, playerKey: "key-alice");
        Pump(server, new[] { a2, b });

        Assert.True(a2.Joined);
        Assert.Equal(balanceBefore, a2.Career.BalanceCents);
        Assert.Contains("hazmat", a2.Career.Licenses);
        Assert.Equal(JobLifecycle.Claimed, a2.Career.Jobs[jobId].State);
        Assert.Equal(a2.LocalId, a2.Career.Jobs[jobId].ClaimantPeerId);
        Assert.Equal(1, a2.Career.Jobs[jobId].NextTaskIndex); // the progress survived untouched

        a2.Career.ReportTask(jobId, 1);                       // and the job continues mid-stream
        Pump(server, new[] { a2, b });
        Assert.Equal(2, b.Career.Jobs[jobId].NextTaskIndex);
    }

    [Fact]
    public void Grace_expiry_releases_the_claim_for_everyone()
    {
        var (_, clock, server, a, b) = Session();
        int jobId = a.Career.Jobs.Keys.First();
        a.Career.ClaimJob(jobId);
        Pump(server, new[] { a, b });

        a.Leave();
        Pump(server, new[] { a, b });
        clock.Advance(10_001);
        Pump(server, new[] { b });

        Assert.Equal(JobLifecycle.Available, b.Career.Jobs[jobId].State);
        Assert.Equal(0, b.Career.Jobs[jobId].ClaimantPeerId);
        Assert.Equal(0, b.Career.Jobs[jobId].NextTaskIndex);
    }

    [Fact]
    public void Hard_disconnect_also_starts_the_grace_hold()
    {
        var (hub, clock, server, a, b) = Session();
        int jobId = a.Career.Jobs.Keys.First();
        a.Career.ClaimJob(jobId);
        Pump(server, new[] { a, b });

        hub.Disconnect(a.LocalId!.Value);                     // transport drop, no graceful Leave
        Pump(server, new[] { b });

        Assert.Equal(1, server.PlayerCount);
        Assert.Equal(JobLifecycle.Claimed, server.Career.Registry.Jobs[jobId].State);

        clock.Advance(9_000);
        var a2 = new NetClient(hub.Connect(out _), Identity, "Alice", clock, playerKey: "key-alice");
        Pump(server, new[] { a2, b });
        Assert.Equal(a2.LocalId, a2.Career.Jobs[jobId].ClaimantPeerId);
    }
}
