using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LocoMP.Core.Career;
using LocoMP.Core.Persistence;
using LocoMP.Core.Presence;
using LocoMP.Core.Protocol;
using LocoMP.Core.Session;
using LocoMP.Core.Trains;
using LocoMP.Transport;
using Xunit;

namespace LocoMP.Core.Tests;

/// <summary>
/// Persistence v1 (03 §7): the LMPS codec round-trips every field, file storage replaces
/// atomically and rotates backups, the autosaver runs on the clock, and — the M3 exit criterion —
/// a COLD RESTART resumes the world: wallets, licenses, claims-with-progress, the board, consists
/// (parked, with baseline positions), junctions, and the id counters all survive a full
/// serialize → new-process-equivalent → rejoin cycle.
/// </summary>
public class PersistenceTests
{
    private static readonly HandshakeRequest Identity = new(ProtocolVersion.Current, "B99.7", "0.0.2");

    private sealed class MemoryStorage : ISaveStorage
    {
        public byte[]? Data;
        public int Writes;
        public byte[]? TryLoad() => Data;

        public void Save(byte[] data)
        {
            Data = data;
            Writes++;
        }
    }

    private static CareerConfig Career() => new()
    {
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

    [Fact]
    public void Save_codec_round_trips_every_field()
    {
        var career = new CareerSaveData
        {
            Preset = ProgressionPreset.SharedCareer,
            Minted = 1000_00,
            Burned = 150_00,
            SharedGrantIssued = true,
            NextJobId = 42,
            RngState = 0xDEADBEEF,
        };
        career.Accounts["@shared"] = 850_00;
        career.Profiles.Add(new ProfileSave("key-alice", "Alice", new List<string> { "hazmat" }));
        career.SharedLicenses.Add("SH");
        var def = new JobDef(7, "FH", "SM", "GF", "steel", 3, 300_00, new[] { "hazmat" }, new[]
        {
            new JobTaskDef(JobTaskKind.Load, "SM"),
            new JobTaskDef(JobTaskKind.Haul, "GF"),
        });
        career.Jobs.Add(new JobSave(def, JobLifecycle.Claimed, "key-alice", 1, 12_345));
        career.GraceRemainingMs["key-bob"] = 9_000;

        var trains = new TrainsSaveData { NextTrainsetId = 5, NextCarId = 9 };
        trains.Sets.Add(new TrainsetDef(3, epoch: 4, ownerId: 2, new[] { new CarDef(1, "loco"), new CarDef(2, "boxcar", derailed: true) }));
        trains.LatestSnapshots.Add(new TrainsetSnapshot(3, 4, 777L, new[]
        {
            CarSnapshot.Railed(new BogieState(10, 5f, 1f), new BogieState(10, 4f, 1f)),
            CarSnapshot.OffRail(Pose.Identity),
        }));
        trains.Junctions[563] = 1;
        trains.Turntables[2] = 42.5f;

        byte[] bytes = SaveCodec.Write(new ServerSaveData(career, trains));
        ServerSaveData restored = SaveCodec.Read(bytes);

        Assert.Equal(ProgressionPreset.SharedCareer, restored.Career.Preset);
        Assert.Equal(850_00, restored.Career.Accounts["@shared"]);
        Assert.Equal(1000_00, restored.Career.Minted);
        Assert.Equal(150_00, restored.Career.Burned);
        Assert.True(restored.Career.SharedGrantIssued);
        ProfileSave profile = restored.Career.Profiles.Single();
        Assert.Equal("key-alice", profile.Key);
        Assert.Equal("hazmat", profile.Licenses.Single());
        Assert.Equal("SH", restored.Career.SharedLicenses.Single());
        JobSave job = restored.Career.Jobs.Single();
        Assert.Equal(7, job.Def.Id);
        Assert.Equal(3, job.Def.CarCount);
        Assert.Equal(2, job.Def.Tasks.Count);
        Assert.Equal(JobTaskKind.Haul, job.Def.Tasks[1].Kind);
        Assert.Equal(JobLifecycle.Claimed, job.State);
        Assert.Equal(1, job.NextTaskIndex);
        Assert.Equal(12_345, job.ClaimRemainingMs);
        Assert.Equal(9_000, restored.Career.GraceRemainingMs["key-bob"]);
        Assert.Equal(42, restored.Career.NextJobId);
        Assert.Equal(0xDEADBEEF, restored.Career.RngState);

        TrainsetDef set = restored.Trains.Sets.Single();
        Assert.Equal(3, set.Id);
        Assert.Equal(4u, set.Epoch);
        Assert.True(set.Cars[1].Derailed);
        TrainsetSnapshot snap = restored.Trains.LatestSnapshots.Single();
        Assert.Equal(777L, snap.ServerTimeMs);
        Assert.True(snap.Cars[1].Derailed);
        Assert.Equal((byte)1, restored.Trains.Junctions[563u]);
        Assert.Equal(42.5f, restored.Trains.Turntables[2u]);
        Assert.Equal(5, restored.Trains.NextTrainsetId);
        Assert.Equal(9, restored.Trains.NextCarId);
    }

    [Fact]
    public void Save_codec_refuses_foreign_bytes_and_future_schemas()
    {
        Assert.Throws<InvalidDataException>(() => SaveCodec.Read(new byte[] { 1, 2, 3, 4, 5 }));

        byte[] future = { (byte)'L', (byte)'M', (byte)'P', (byte)'S', 99 };
        Assert.Throws<InvalidDataException>(() => SaveCodec.Read(future));
    }

    [Fact]
    public void File_storage_replaces_atomically_and_rotates_backups()
    {
        string dir = Path.Combine(Path.GetTempPath(), "locomp-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            string path = Path.Combine(dir, "world.lmps");
            var storage = new FileSaveStorage(path, backups: 2);

            Assert.Null(storage.TryLoad());
            storage.Save(new byte[] { 1 });
            storage.Save(new byte[] { 2 });
            storage.Save(new byte[] { 3 });
            storage.Save(new byte[] { 4 });

            Assert.Equal(new byte[] { 4 }, storage.TryLoad());
            Assert.Equal(new byte[] { 3 }, File.ReadAllBytes(path + ".1"));
            Assert.Equal(new byte[] { 2 }, File.ReadAllBytes(path + ".2"));
            Assert.False(File.Exists(path + ".3"));            // rotation is bounded
            Assert.False(File.Exists(path + ".tmp"));          // nothing half-written left behind
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Autosaver_writes_on_the_interval_and_on_demand()
    {
        var clock = new ManualClock();
        var storage = new MemoryStorage();
        var saver = new Autosaver(clock, intervalMs: 5_000, storage, () => new byte[] { 7 });

        saver.Tick();
        clock.Advance(4_999);
        saver.Tick();
        Assert.Equal(0, storage.Writes);

        clock.Advance(1);
        saver.Tick();
        Assert.Equal(1, storage.Writes);

        saver.SaveNow();
        Assert.Equal(2, storage.Writes);
        clock.Advance(4_999);
        saver.Tick();                                          // SaveNow restarted the interval
        Assert.Equal(2, storage.Writes);
    }

    [Fact]
    public void Cold_restart_resumes_the_world_and_a_rejoin_continues_mid_job()
    {
        // ── session 1: build up world + career state ──
        var hub1 = new LoopbackNetwork();
        var clock1 = new ManualClock();
        var server1 = new NetServer(hub1.Server, new ServerConfig(Identity, career: Career()), clock1);
        var a1 = new NetClient(hub1.Connect(out _), Identity, "Alice", clock1, playerKey: "key-alice");
        Pump(server1, new[] { a1 });

        a1.Trains.RegisterTrainset(token: 1, new[] { new CarDef(0, "loco"), new CarDef(0, "boxcar") });
        Pump(server1, new[] { a1 });
        TrainsetDef set = server1.Trains.Registry.Sets.Values.Single();
        a1.Trains.SendSnapshot(new TrainsetSnapshot(set.Id, set.Epoch, 0L, set.Cars
            .Select((_, i) => CarSnapshot.Railed(new BogieState(1, 10f + i * 20f, 0f), new BogieState(1, 2f + i * 20f, 0f)))
            .ToArray()));
        a1.Trains.ThrowJunction(563, 1);
        int jobId = a1.Career.Jobs.Keys.First();
        a1.Career.ClaimJob(jobId);
        Pump(server1, new[] { a1 });
        a1.Career.ReportTask(jobId, 0);
        a1.Career.PurchaseLicense("hazmat");
        Pump(server1, new[] { a1 });
        long balance = a1.Career.BalanceCents;
        var boardIds = server1.Career.Registry.Jobs.Keys.OrderBy(id => id).ToArray();

        // ── the restart: capture → bytes → fresh transport/clock/server, as a new process would ──
        byte[] saved = SaveCodec.Write(server1.CaptureSave());
        var hub2 = new LoopbackNetwork();
        var clock2 = new ManualClock();
        var server2 = new NetServer(hub2.Server, new ServerConfig(Identity, career: Career()), clock2,
            SaveCodec.Read(saved));

        Assert.Equal(boardIds, server2.Career.Registry.Jobs.Keys.OrderBy(id => id).ToArray());
        TrainsetDef restored = server2.Trains.Registry.Sets.Values.Single();
        Assert.Equal(set.Id, restored.Id);
        Assert.Equal(set.Epoch, restored.Epoch);
        Assert.Equal(0, restored.OwnerId);                     // everyone is offline: parked
        Assert.Equal((byte)1, server2.Trains.Junctions[563u]);
        Assert.True(server2.Career.Registry.Ledger.ConservationHolds);

        // ── the rejoin: same key, next "day" — claim, progress, wallet, licenses all intact ──
        var a2 = new NetClient(hub2.Connect(out _), Identity, "Alice", clock2, playerKey: "key-alice");
        Pump(server2, new[] { a2 });

        Assert.Equal(balance, a2.Career.BalanceCents);
        Assert.Contains("hazmat", a2.Career.Licenses);
        Assert.Equal(JobLifecycle.Claimed, a2.Career.Jobs[jobId].State);
        Assert.Equal(a2.LocalId, a2.Career.Jobs[jobId].ClaimantPeerId);
        Assert.Equal(1, a2.Career.Jobs[jobId].NextTaskIndex);
        Assert.True(a2.Trains.View.Sets.ContainsKey(set.Id));  // the world came back too...
        Assert.True(a2.Trains.View.LatestSnapshots.ContainsKey(set.Id)); // ...WITH its position
        Assert.Equal((byte)1, a2.Trains.Junctions[563u]);

        int tasks = a2.Career.Jobs[jobId].Def.Tasks.Count;
        for (int i = 1; i < tasks; i++)
        {
            a2.Career.ReportTask(jobId, i);
            Pump(server2, new[] { a2 });
        }
        Assert.True(a2.Career.BalanceCents > balance);         // delivered and paid, mid-job across a restart

        // Id counters survived: a new registration can never collide with the restored world.
        a2.Trains.RegisterTrainset(token: 2, new[] { new CarDef(0, "flatbed") });
        Pump(server2, new[] { a2 });
        Assert.True(server2.Trains.Registry.Sets.Keys.Max() > set.Id);
    }

    [Fact]
    public void Restore_refuses_a_preset_mismatch_loudly()
    {
        var clock = new ManualClock();
        var perPlayer = new CareerRegistry(Career(), clock);
        perPlayer.Connect("alice", "Alice");
        CareerSaveData save = perPlayer.Capture();

        CareerConfig shared = Career();
        shared.Preset = ProgressionPreset.SharedCareer;
        Assert.Throws<InvalidDataException>(() => new CareerRegistry(shared, clock, save));
    }
}
