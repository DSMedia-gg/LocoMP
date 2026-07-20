using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using LocoMP.Core.Career;
using LocoMP.Core.Items;
using LocoMP.Core.Net;
using LocoMP.Core.Presence;
using LocoMP.Core.Protocol;
using LocoMP.Core.Session;
using LocoMP.Core.Trains;
using LocoMP.Transport;
using Xunit;
using Xunit.Abstractions;

namespace LocoMP.Core.Tests;

/// <summary>
/// Headless measurement of the 02 §9 performance budgets on the Loopback/Core rig — no game, no VR,
/// no second PC (CLAUDE.md hard rule 8). The audit (2026-07-19 §6) flagged that these budgets have
/// "no measurement harness or recorded numbers"; this is that harness. It answers two live questions
/// with data instead of vibes:
///   • Late-join snapshot size vs the ≤10 MB budget — the number that decides whether M3.2 (phased +
///     COMPRESSED + chunked join, plus a join queue) is pressing or correctly deferred (07 M3 / audit §5).
///   • Steady-state per-client bandwidth vs ≤128 kbps at 32 players — quantifies the known interest-
///     management gap (D10 / audit §6): today the server BROADCASTS every pose + snapshot to everyone.
///
/// Size measurements are DETERMINISTIC (a pure function of the messages the server sends) and asserted
/// against the budget. The one timing measurement (host tick cost) is machine-dependent, so it is
/// RECORDED and only loosely bounded. Run with:
///   dotnet test --filter FullyQualifiedName~BudgetBench -c Release -l "console;verbosity=detailed"
/// and read the numbers off the test output (also transcribed into docs/PERF-BASELINE.md).
/// </summary>
public sealed class BudgetBench
{
    private static readonly HandshakeRequest Identity = new(ProtocolVersion.Current, "B99.7", "0.0.2");

    // 02 §9 budgets.
    private const long JoinSnapshotBudgetBytes = 10L * 1024 * 1024; // ≤ 10 MB compressed
    private const double BandwidthBudgetKbps = 128.0;               // ≤ 128 kbps down/client @ 32
    private const double HostTickBudgetMs = 2.0;                    // ≤ 2 ms/tick host overhead

    private const int SnapshotHz = 30; // S-channel target (02 §1); no rate-tiering yet, so broadcast-all

    private readonly ITestOutputHelper _out;
    public BudgetBench(ITestOutputHelper output) => _out = output;

    // Realistic-ish livery ids and item prefabs so string payloads size like the real wire.
    private static readonly string[] Liveries =
        { "LocoDiesel", "LocoDH4", "LocoS060", "LocoS282A", "BoxcarBrown", "FlatbedEmpty",
          "TankOrange", "AutorackRed", "HopperTeal", "CabooseRed", "Refrigerator", "Gondola" };
    private static readonly string[] Prefabs =
        { "Lantern", "Boombox", "Map", "CommsRadio", "Wallet", "EOT_Lantern", "RemoteController",
          "Flashlight", "CashRegisterMoney", "Banana" };

    private static CareerConfig Career(int targetJobs) => new()
    {
        Preset = ProgressionPreset.PerPlayer,
        StartingBalanceCents = 500_00,
        ClaimTtlMs = 600_000,
        ReconnectGraceMs = 600_000,
        TargetAvailableJobs = targetJobs,
        JobSeed = 7,
        Stations = new[] { "SM", "GF", "HB", "MF", "FF", "CS", "OR", "CM" },
        JobTypes = new[]
        {
            new JobTypeSpec("FH", "steel", 100_00, 2, 6),
            new JobTypeSpec("LH", "logs", 120_00, 3, 8),
            new JobTypeSpec("SU", "cars", 90_00, 2, 5),
        },
    };

    private static ItemConfig Items() => new()
    {
        ShopPrices = new Dictionary<string, long>(),
        PickupRadiusM = 0f,
        AcceptExternalItems = true,
    };

    private static Pose PoseAt(float x, float z) => new(x, 0f, z, 0f, 0f, 0f, 1f);

    private static void Pump(NetServer server, IEnumerable<NetClient> clients, int rounds = 8)
    {
        for (int i = 0; i < rounds; i++)
        {
            server.Poll();
            foreach (NetClient c in clients) c.Poll();
        }
    }

    /// <summary>Seed a mature world: the host (world source) registers <paramref name="trainsets"/>
    /// consists (each with a position snapshot), <paramref name="items"/> world items, and the career
    /// board auto-fills toward <paramref name="targetJobs"/>. Returns the assembled rig; the host is at
    /// peer id 1 (first to connect). Extra idle players pad the roster.</summary>
    private (LoopbackNetwork hub, CountingTransport counted, NetServer server, NetClient host, List<NetClient> extras, ManualClock clock)
        SeedWorld(int trainsets, int carsPerSet, int items, int targetJobs, int extraPlayers)
    {
        var hub = new LoopbackNetwork();
        var counted = new CountingTransport(hub.Server);
        var clock = new ManualClock();
        var server = new NetServer(counted, new ServerConfig(Identity, career: Career(targetJobs), items: Items()), clock);

        var host = new NetClient(hub.Connect(out _), Identity, "Host", clock, playerKey: "key-host");
        Pump(server, new[] { host });

        // Trains: register each consist, capture the server-assigned def by token, push a snapshot.
        var defsByToken = new Dictionary<uint, TrainsetDef>();
        host.Trains.TrainsetRegistered += (token, def) => defsByToken[token] = def;
        for (uint t = 1; t <= (uint)trainsets; t++)
        {
            var cars = new CarDef[carsPerSet];
            for (int c = 0; c < carsPerSet; c++)
                cars[c] = new CarDef(0, Liveries[(t + c) % Liveries.Length]);
            host.Trains.RegisterTrainset(t, cars);
            Pump(server, new[] { host }, 2);
            if (defsByToken.TryGetValue(t, out TrainsetDef? def))
            {
                var snap = new CarSnapshot[def.Cars.Count];
                for (int i = 0; i < snap.Length; i++)
                    snap[i] = CarSnapshot.Railed(
                        new BogieState(100 + t, 10f + i * 20f, 5f),
                        new BogieState(100 + t, 2f + i * 20f, 5f));
                host.Trains.SendSnapshot(new TrainsetSnapshot(def.Id, def.Epoch, clock.NowMs, snap));
            }
        }

        // Items: world-dropped, host is the world source.
        for (int i = 0; i < items; i++)
            host.Items.RegisterWorldItem(Prefabs[i % Prefabs.Length], PoseAt(i * 3f, i * 2f), "", token: (uint)(1000 + i));

        // Extra idle players — roster weight.
        var extras = new List<NetClient>();
        for (int p = 0; p < extraPlayers; p++)
            extras.Add(new NetClient(hub.Connect(out _), Identity, $"P{p}", clock, playerKey: $"key-extra-{p}"));

        var all = new List<NetClient> { host };
        all.AddRange(extras);
        // Fill the board + settle: generous pump, and advance the clock in case refill is time-gated.
        for (int round = 0; round < 6; round++) { clock.Advance(1000); Pump(server, all, 6); }

        return (hub, counted, server, host, extras, clock);
    }

    [Fact]
    public void Measure_and_report_the_section9_budgets()
    {
        _out.WriteLine("=== LocoMP §9 budget baseline (headless, Loopback/Core) ===");
        _out.WriteLine($"protocol v{ProtocolVersion.Current} · budgets: join ≤ {JoinSnapshotBudgetBytes / 1024 / 1024} MB · " +
                       $"bandwidth ≤ {BandwidthBudgetKbps} kbps/client@32 · host tick ≤ {HostTickBudgetMs} ms");
        _out.WriteLine("");

        // ---- 1. LATE-JOIN SNAPSHOT SIZE across world scales (deterministic) ----
        _out.WriteLine("-- Late-join snapshot (bytes the server sends to ONE joining client) --");
        _out.WriteLine("scale        | trains | cars | jobs | items | players | join bytes | msgs | KB");
        (string name, int sets, int cars, int items, int jobs, int extra)[] scales =
        {
            ("small(4p)",   8, 5,  30,  20, 3),
            ("medium(8p)", 30, 5, 150,  50, 7),
            ("large(16p)", 60, 6, 400, 100, 15),
        };

        long largestJoin = 0;
        foreach (var s in scales)
        {
            var (hub, counted, server, host, extras, clock) = SeedWorld(s.sets, s.cars, s.items, s.jobs, s.extra);

            int seededJobs = host.Career.Jobs.Count;
            int seededItems = host.Items.Items.Count;

            counted.Reset();
            var joiner = new NetClient(hub.Connect(out int joinerId), Identity, "Joiner", clock, playerKey: "key-joiner");
            Pump(server, AllOf(host, extras, joiner), 12);
            Assert.True(joiner.Joined, $"{s.name}: joiner never completed the handshake");

            long joinBytes = counted.BytesTo(joinerId);
            int joinMsgs = counted.MessagesTo(joinerId);
            largestJoin = Math.Max(largestJoin, joinBytes);

            _out.WriteLine($"{s.name,-12} | {s.sets,6} | {s.sets * s.cars,4} | {seededJobs,4} | {seededItems,5} | " +
                           $"{s.extra + 2,7} | {joinBytes,10:N0} | {joinMsgs,4} | {joinBytes / 1024.0,6:F1}");

            joiner.Dispose();
            server.Dispose();
        }
        _out.WriteLine("");

        // ---- 2. PER-MESSAGE RELAY SIZES (deterministic) — the steady-state model's inputs ----
        var (h2, c2, srv2, host2, extras2, _) = SeedWorld(trainsets: 20, carsPerSet: 5, items: 0, targetJobs: 0, extraPlayers: 3);
        int otherId = extras2.Count > 0 ? 1 + 1 : 1; // an id other than the sender; peer 2 is the first extra

        // one pose relay: host moves, server relays to the others. Measure bytes to one other peer.
        c2.Reset();
        host2.SendPose(PoseAt(123.4f, 567.8f));
        Pump(srv2, AllOf(host2, extras2), 2);
        long poseRelayBytes = FirstNonZeroPeerBytes(c2, senderIsPeer1: true);

        // one trainset snapshot relay: host owns set 1; re-send its snapshot; measure bytes to one other.
        c2.Reset();
        // Re-send a snapshot for a known set (id 1, epoch from the seed). Rebuild a 5-car snapshot.
        var snapCars = Enumerable.Range(0, 5)
            .Select(i => CarSnapshot.Railed(new BogieState(101, 10f + i * 20f, 5f), new BogieState(101, 2f + i * 20f, 5f)))
            .ToArray();
        host2.Trains.SendSnapshot(new TrainsetSnapshot(1, 1, 0L, snapCars));
        Pump(srv2, AllOf(host2, extras2), 2);
        long snapRelayBytes = FirstNonZeroPeerBytes(c2, senderIsPeer1: true);

        _out.WriteLine("-- Per-message relay sizes (bytes to one recipient) --");
        _out.WriteLine($"player pose relay      : {poseRelayBytes} B");
        _out.WriteLine($"5-car trainset snapshot: {snapRelayBytes} B");
        _out.WriteLine("");

        // ---- 3. DERIVED STEADY-STATE BANDWIDTH (current broadcast-all model, no interest mgmt) ----
        _out.WriteLine("-- Steady-state DOWN bandwidth per client (model: everything broadcast at 30 Hz) --");
        _out.WriteLine("Assumes every other player emits a pose each tick and every moving consist emits a");
        _out.WriteLine("snapshot each tick, all relayed to every client (D10 interest mgmt NOT yet active).");
        _out.WriteLine("players | moving trains | kbps down/client");
        (int players, int movingTrains)[] loads = { (8, 30), (16, 60), (32, 100), (32, 200) };
        double worstKbps = 0;
        foreach (var (players, movingTrains) in loads)
        {
            // A client receives: (players-1) poses/tick + movingTrains snapshots/tick, at SnapshotHz.
            double bytesPerTick = (players - 1) * poseRelayBytes + movingTrains * snapRelayBytes;
            double kbps = bytesPerTick * SnapshotHz * 8 / 1000.0;
            worstKbps = Math.Max(worstKbps, kbps);
            string flag = kbps > BandwidthBudgetKbps ? "  <-- OVER budget" : "";
            _out.WriteLine($"{players,7} | {movingTrains,13} | {kbps,10:F1}{flag}");
        }
        _out.WriteLine("");

        // ---- 4. HOST TICK COST (machine-dependent — recorded, loosely bounded) ----
        var (h3, c3, srv3, host3, extras3, _) = SeedWorld(trainsets: 30, carsPerSet: 5, items: 150, targetJobs: 50, extraPlayers: 7);
        var all3 = AllOf(host3, extras3).ToArray();
        // Each player emits a pose per tick so Poll relays real traffic.
        var sw = Stopwatch.StartNew();
        const int ticks = 2000;
        for (int i = 0; i < ticks; i++)
        {
            host3.SendPose(PoseAt(i % 500, (i * 7) % 500));
            foreach (var e in extras3) e.SendPose(PoseAt(i % 300, (i * 3) % 300));
            srv3.Poll();
            foreach (var c in all3) c.Poll();
        }
        sw.Stop();
        double usPerTick = sw.Elapsed.TotalMilliseconds * 1000.0 / ticks;
        _out.WriteLine("-- Host tick cost (server.Poll + relay, 8 players active) --");
        _out.WriteLine($"{usPerTick:F1} µs/tick over {ticks} ticks (budget {HostTickBudgetMs * 1000:F0} µs; machine-dependent, recorded not gated)");
        srv3.Dispose();

        // ---- Assertions: hard on deterministic size budgets, loose on timing ----
        Assert.True(largestJoin < JoinSnapshotBudgetBytes,
            $"largest join snapshot {largestJoin:N0} B exceeds the {JoinSnapshotBudgetBytes:N0} B budget");
        Assert.True(usPerTick < 50_000, $"host tick {usPerTick:F0} µs is pathologically slow (>50 ms)");

        _out.WriteLine("");
        _out.WriteLine($"VERDICT join: largest measured {largestJoin:N0} B ({largestJoin / 1024.0 / 1024.0:F2} MB) vs 10 MB budget — " +
                       (largestJoin < JoinSnapshotBudgetBytes / 2 ? "COMFORTABLE (M3.2 compression not yet pressing)" : "APPROACHING (schedule M3.2)"));
        _out.WriteLine($"VERDICT bandwidth: worst modelled {worstKbps:F0} kbps vs 128 kbps — " +
                       (worstKbps > BandwidthBudgetKbps ? "OVER at scale → interest management (D10) is the real gap, as audit §6 says" : "within budget"));
    }

    /// <summary>D10 Burst 1 proof, MEASURED (not modelled): with spatial interest on, a client only
    /// receives the pose stream of players near it. Two equal clusters ~2 km apart, all streaming; we
    /// weigh the bytes the server actually sends to a probe in one cluster, interest OFF vs ON.</summary>
    [Fact]
    public void Interest_management_cuts_a_distant_clients_pose_bandwidth()
    {
        const int perCluster = 8;
        long off = MeasureProbePoseBytes(interestEnabled: false, perCluster);
        long on = MeasureProbePoseBytes(interestEnabled: true, perCluster);

        _out.WriteLine("-- Pose interest management (D10 Burst 1), MEASURED over a steady interval --");
        _out.WriteLine($"{perCluster} players near the probe + {perCluster} players ~2 km away, all streaming poses at 30 Hz");
        _out.WriteLine($"interest OFF (broadcast-all): {off:N0} B to the probe");
        _out.WriteLine($"interest ON  (spatial)      : {on:N0} B to the probe   ({(off == 0 ? 0 : 100.0 * on / off):F0}% of broadcast-all)");
        _out.WriteLine("");

        Assert.True(off > 0, "the probe must receive poses when filtering is off");
        Assert.True(on > 0, "the probe must still receive its NEAR cluster when filtering is on");
        // The far cluster (half the traffic) is gated out — on should be well under broadcast-all.
        Assert.True(on < off * 0.6, $"interest ON ({on:N0} B) should be well under broadcast-all ({off:N0} B)");
    }

    /// <summary>Run the two-cluster scenario once and return the bytes the server sent to the probe
    /// over a fixed streaming interval, after relevance has settled.</summary>
    private long MeasureProbePoseBytes(bool interestEnabled, int perCluster)
    {
        var hub = new LoopbackNetwork();
        var counted = new CountingTransport(hub.Server);
        var clock = new ManualClock();
        var interest = new InterestConfig
        {
            Enabled = interestEnabled,
            FilterPlayers = true,
            EnterRadiusM = 200f,
            LeaveRadiusM = 300f,
            RecomputeIntervalMs = 1,
        };
        var server = new NetServer(counted, new ServerConfig(Identity, interest: interest), clock);

        var probe = new NetClient(hub.Connect(out int probeId), Identity, "Probe", clock, playerKey: "key-probe");
        var near = new List<NetClient>();
        var far = new List<NetClient>();
        for (int i = 0; i < perCluster; i++)
            near.Add(new NetClient(hub.Connect(out _), Identity, $"N{i}", clock, playerKey: $"key-n{i}"));
        for (int i = 0; i < perCluster; i++)
            far.Add(new NetClient(hub.Connect(out _), Identity, $"F{i}", clock, playerKey: $"key-f{i}"));
        var all = new List<NetClient> { probe };
        all.AddRange(near);
        all.AddRange(far);
        PumpClocked(clock, server, all, 8);

        // Establish positions and let relevance settle: near cluster around the origin, far cluster ~2 km east.
        for (int r = 0; r < 12; r++)
        {
            probe.SendPose(PoseAt(10f, 0f));
            for (int i = 0; i < near.Count; i++) near[i].SendPose(PoseAt(i * 5f, 0f));
            for (int i = 0; i < far.Count; i++) far[i].SendPose(PoseAt(2000f + i * 5f, 0f));
            PumpClocked(clock, server, all, 2);
        }

        // Weigh a steady streaming interval: everyone moves a touch each tick; count bytes to the probe.
        counted.Reset();
        const int ticks = 20;
        for (int k = 0; k < ticks; k++)
        {
            probe.SendPose(PoseAt(10f, k * 0.1f));
            for (int i = 0; i < near.Count; i++) near[i].SendPose(PoseAt(i * 5f, k * 0.1f));
            for (int i = 0; i < far.Count; i++) far[i].SendPose(PoseAt(2000f + i * 5f, k * 0.1f));
            PumpClocked(clock, server, all, 1);
        }

        long bytes = counted.BytesTo(probeId);
        server.Dispose();
        return bytes;
    }

    private static void PumpClocked(ManualClock clock, NetServer server, IEnumerable<NetClient> clients, int rounds)
    {
        for (int i = 0; i < rounds; i++)
        {
            clock.Advance(50);
            server.Poll();
            foreach (NetClient c in clients) c.Poll();
        }
    }

    private static IEnumerable<NetClient> AllOf(NetClient host, List<NetClient> extras, NetClient? joiner = null)
    {
        yield return host;
        foreach (var e in extras) yield return e;
        if (joiner != null) yield return joiner;
    }

    // The sender is peer 1 (host); return the byte count to the first OTHER peer that received anything.
    private static long FirstNonZeroPeerBytes(CountingTransport counted, bool senderIsPeer1)
    {
        for (int peer = 1; peer <= 40; peer++)
        {
            if (senderIsPeer1 && peer == 1) continue;
            long b = counted.BytesTo(peer);
            if (b > 0) return b;
        }
        return 0;
    }
}
