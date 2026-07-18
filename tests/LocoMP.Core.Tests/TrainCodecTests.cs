using System.IO;
using LocoMP.Core.Presence;
using LocoMP.Core.Protocol;
using LocoMP.Core.Trains;
using Xunit;

namespace LocoMP.Core.Tests;

public class TrainCodecTests
{
    [Fact]
    public void Trainset_definition_round_trips()
    {
        var def = new TrainsetDef(7, epoch: 42, ownerId: 3, new[]
        {
            new CarDef(10, "LocoDE6"),
            new CarDef(11, "FlatbedEmpty", derailed: true),
            new CarDef(12, "Boxcar"),
        });

        var w = new PacketWriter();
        TrainCodec.WriteDef(w, def);
        TrainsetDef read = TrainCodec.ReadDef(new PacketReader(w.ToArray()));

        Assert.Equal(def.Id, read.Id);
        Assert.Equal(def.Epoch, read.Epoch);
        Assert.Equal(def.OwnerId, read.OwnerId);
        Assert.Equal(def.Cars.Count, read.Cars.Count);
        for (int i = 0; i < def.Cars.Count; i++)
        {
            Assert.Equal(def.Cars[i].Id, read.Cars[i].Id);
            Assert.Equal(def.Cars[i].Kind, read.Cars[i].Kind);
            Assert.Equal(def.Cars[i].Derailed, read.Cars[i].Derailed);
        }
    }

    [Fact]
    public void Snapshot_with_mixed_railed_and_derailed_cars_round_trips()
    {
        var snap = new TrainsetSnapshot(5, epoch: 9, serverTimeMs: 123456789L, new[]
        {
            CarSnapshot.Railed(new BogieState(100, 12.5f, 8.25f), new BogieState(100, 4.5f, 8.25f)),
            CarSnapshot.OffRail(new Pose(1f, 2f, 3f, 0f, 0.7071f, 0f, 0.7071f)),
            CarSnapshot.Railed(new BogieState(101, 0.5f, -3f), new BogieState(102, 88f, -3f)),
        });

        var w = new PacketWriter();
        TrainCodec.WriteSnapshot(w, snap);
        TrainsetSnapshot read = TrainCodec.ReadSnapshot(new PacketReader(w.ToArray()));

        Assert.Equal(snap.TrainsetId, read.TrainsetId);
        Assert.Equal(snap.Epoch, read.Epoch);
        Assert.Equal(snap.ServerTimeMs, read.ServerTimeMs);
        Assert.Equal(snap.Cars.Length, read.Cars.Length);

        Assert.False(read.Cars[0].Derailed);
        Assert.Equal(snap.Cars[0].Front, read.Cars[0].Front);
        Assert.Equal(snap.Cars[0].Rear, read.Cars[0].Rear);

        Assert.True(read.Cars[1].Derailed);
        Assert.Equal(snap.Cars[1].Pose, read.Cars[1].Pose);

        Assert.Equal(snap.Cars[2].Front, read.Cars[2].Front);
    }

    [Fact]
    public void Merge_transaction_round_trips()
    {
        var product = new TrainsetDef(9, epoch: 4, ownerId: 2, new[]
        {
            new CarDef(1, "LocoDE6"),
            new CarDef(2, "Boxcar"),
        });
        var txn = new TrainsetTransaction(TrainsetTransactionType.Merge, new[] { 3, 6 }, new[] { product });

        var w = new PacketWriter();
        TrainCodec.WriteTransaction(w, txn);
        TrainsetTransaction read = TrainCodec.ReadTransaction(new PacketReader(w.ToArray()));

        Assert.Equal(TrainsetTransactionType.Merge, read.Type);
        Assert.Equal(new[] { 3, 6 }, read.RetiredIds);
        Assert.Single(read.Products);
        Assert.Equal(9, read.Products[0].Id);
        Assert.Equal(4u, read.Products[0].Epoch);
    }

    [Fact]
    public void A_hostile_car_count_is_refused_not_allocated()
    {
        // A definition claiming an absurd car count must throw before any large allocation.
        byte[] hostile = new PacketWriter()
            .WriteVarUInt(1)            // id
            .WriteVarUInt(1)            // epoch
            .WriteVarUInt(1)            // owner
            .WriteVarUInt(1_000_000)    // car count — way past the cap
            .ToArray();

        Assert.Throws<InvalidDataException>(() => TrainCodec.ReadDef(new PacketReader(hostile)));
    }

    [Fact]
    public void A_truncated_snapshot_throws_instead_of_returning_garbage()
    {
        var snap = new TrainsetSnapshot(5, 9, 0L, new[]
        {
            CarSnapshot.Railed(new BogieState(100, 12.5f, 8.25f), new BogieState(100, 4.5f, 8.25f)),
        });
        var w = new PacketWriter();
        TrainCodec.WriteSnapshot(w, snap);
        byte[] full = w.ToArray();
        byte[] cut = new byte[full.Length - 5];
        System.Array.Copy(full, cut, cut.Length);

        Assert.Throws<EndOfStreamException>(() => TrainCodec.ReadSnapshot(new PacketReader(cut)));
    }
}
