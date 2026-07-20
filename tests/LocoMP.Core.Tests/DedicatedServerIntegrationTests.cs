using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using LocoMP.Core.Career;
using LocoMP.Core.Persistence;
using LocoMP.Core.Protocol;
using LocoMP.Core.Session;
using LocoMP.Core.Trains;
using LocoMP.Core.World;
using LocoMP.Server;
using LocoMP.Transport;
using Xunit;

namespace LocoMP.Core.Tests;

/// <summary>
/// End-to-end proof that the pulled-forward dedicated server (M6-B.1) is genuinely joinable and
/// persistent — over real LiteNetLib UDP, in one process, no game (hard rule 8). It exercises the REAL
/// server pieces (<see cref="DefaultCareer"/> + the wiring <c>Program</c> does) so a solo joiner gets a
/// populated job board and the world survives a cold restart. Mirrors LiteNetLibIntegrationTests: ephemeral
/// ports so parallel runs never collide, bounded spin-pumps so a hung socket fails fast.
/// </summary>
public class DedicatedServerIntegrationTests
{
    private static readonly HandshakeRequest Identity = new(ProtocolVersion.Current, "99-build2702", "0.0.2");
    private const string Key = "locomp-test";

    private static bool SpinUntil(Func<bool> cond, int timeoutMs, params Action[] pumps)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            foreach (Action p in pumps) p();
            if (cond()) return true;
            Thread.Sleep(10);
        }
        foreach (Action p in pumps) p();
        return cond();
    }

    [Fact]
    public void A_solo_client_joins_over_udp_and_receives_a_populated_job_board()
    {
        var config = new ServerConfig(Identity, career: DefaultCareer.Build(ProgressionPreset.PerPlayer));
        using var serverT = LiteNetLibTransport.StartServer(0, Key);
        using var server = new NetServer(serverT, config, new ManualClock());

        using var cT = LiteNetLibTransport.ConnectClient("127.0.0.1", serverT.Port, Key);
        using var c = new NetClient(cT, Identity, "Solo", new ManualClock(), playerKey: "solo-key");

        Action[] pumps = { server.Poll, c.Poll };
        Assert.True(SpinUntil(() => c.Joined && server.PlayerCount == 1, 5000, pumps),
            "the solo client should complete the handshake over UDP");
        // The deterministic generator fills the board on Poll() with no host peer — the join burst carries it.
        Assert.True(SpinUntil(() => c.Career.Jobs.Count > 0, 5000, pumps),
            "a solo joiner should receive a non-empty job board");
    }

    [Fact]
    public void A_server_owned_kinematic_train_is_seen_moving_by_a_udp_client()
    {
        // The repo ships an extracted topology under tests/data; ServerOptions probes for it.
        string? worldPath = new ServerOptions().ResolveWorldFile();
        Assert.NotNull(worldPath);
        WorldTopology topo = TopologyCodec.Read(File.ReadAllBytes(worldPath!));

        using var serverT = LiteNetLibTransport.StartServer(0, Key);
        using var server = new NetServer(serverT, new ServerConfig(Identity), new ManualClock());
        var train = new ServerKinematicTrain(server.Trains, topo, carCount: 3, speed: 10, seed: 1);

        using var cT = LiteNetLibTransport.ConnectClient("127.0.0.1", serverT.Port, Key);
        using var c = new NetClient(cT, Identity, "Solo", new ManualClock(), playerKey: "k");

        // The server advances the train every tick (0.1 s of sim per pump); the client just polls.
        Action[] pumps = { () => { server.Poll(); train.Tick(0.1); }, c.Poll };

        Assert.True(SpinUntil(() => c.Joined && c.Trains.View.Sets.ContainsKey(train.TrainsetId), 5000, pumps),
            "the client should join and receive the server-owned trainset");
        Assert.True(SpinUntil(() => c.Trains.View.LatestSnapshots.ContainsKey(train.TrainsetId), 5000, pumps),
            "the server train should stream a position once its trail history is built");

        BogieState start = c.Trains.View.LatestSnapshots[train.TrainsetId].Cars[0].Front;
        Assert.True(SpinUntil(() =>
        {
            BogieState now = c.Trains.View.LatestSnapshots[train.TrainsetId].Cars[0].Front;
            return now.EdgeId != start.EdgeId || System.Math.Abs(now.S - start.S) > 1f;
        }, 5000, pumps), "the client's replica of the server train should visibly move");
    }

    [Fact]
    public void The_world_survives_a_cold_restart_through_the_save_file()
    {
        string savePath = Path.Combine(Path.GetTempPath(), $"locomp-srv-{Guid.NewGuid():N}.save");
        var storage = new FileSaveStorage(savePath);
        var config = new ServerConfig(Identity, career: DefaultCareer.Build(ProgressionPreset.PerPlayer));

        try
        {
            int boardCount;

            // --- Run 1: stand up, let a client join, capture the world to disk. ---
            using (var serverT = LiteNetLibTransport.StartServer(0, Key))
            using (var server = new NetServer(serverT, config, new ManualClock()))
            using (var cT = LiteNetLibTransport.ConnectClient("127.0.0.1", serverT.Port, Key))
            using (var c = new NetClient(cT, Identity, "Solo", new ManualClock(), playerKey: "solo-key"))
            {
                Action[] pumps = { server.Poll, c.Poll };
                Assert.True(SpinUntil(() => c.Joined && c.Career.Jobs.Count > 0, 5000, pumps));
                boardCount = server.Career.Registry.Jobs.Count;
                Assert.True(boardCount > 0);

                storage.Save(SaveCodec.Write(server.CaptureSave())); // the autosaver's capture, invoked directly
                c.Leave();
                SpinUntil(() => server.PlayerCount == 0, 2000, pumps);
            }

            // --- Run 2: cold start from the file; the board must be intact and re-served on join. ---
            ServerSaveData restore = SaveCodec.Read(storage.TryLoad()!);
            Assert.Equal(boardCount, restore.Career.Jobs.Count); // Core-generated jobs (empty GameId) all persist

            using (var serverT2 = LiteNetLibTransport.StartServer(0, Key))
            using (var server2 = new NetServer(serverT2, config, new ManualClock(), restore))
            using (var cT2 = LiteNetLibTransport.ConnectClient("127.0.0.1", serverT2.Port, Key))
            using (var c2 = new NetClient(cT2, Identity, "Solo", new ManualClock(), playerKey: "solo-key"))
            {
                Assert.Equal(boardCount, server2.Career.Registry.Jobs.Count); // restored, not regenerated fresh
                Action[] pumps = { server2.Poll, c2.Poll };
                Assert.True(SpinUntil(() => c2.Joined && c2.Career.Jobs.Count > 0, 5000, pumps),
                    "the rejoining client should receive the restored board");
            }
        }
        finally
        {
            // FileSaveStorage rotates backups as .1/.2/.3 — clean them all up.
            foreach (string p in new[] { savePath, savePath + ".1", savePath + ".2", savePath + ".3", savePath + ".tmp" })
                if (File.Exists(p)) File.Delete(p);
        }
    }
}
