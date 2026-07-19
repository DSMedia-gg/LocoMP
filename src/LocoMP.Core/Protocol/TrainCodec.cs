using System;
using System.IO;
using LocoMP.Core.Trains;

namespace LocoMP.Core.Protocol;

/// <summary>
/// Shared (de)serialization for train sync (hand-rolled, hard rule 4 — this IS the hot path).
/// Mirrors <see cref="PresenceCodec"/>'s role for presence. Read sides are untrusted (03 §9): all
/// counts are capped so a hostile length prefix cannot force a huge allocation, and any violation
/// throws for the session layer's catch-and-drop.
/// </summary>
internal static class TrainCodec
{
    /// <summary>Longest consist we will ever describe in one packet (DV trains top out far below this).</summary>
    public const int MaxCarsPerTrainset = 256;

    /// <summary>A transaction touches at most two parents (merge) and two products (split).</summary>
    public const int MaxTransactionSets = 8;

    private const byte FlagDerailed = 0x01;

    // ── definitions ──

    public static void WriteCarDef(PacketWriter w, CarDef car)
    {
        w.WriteVarUInt((uint)car.Id);
        w.WriteString(car.Kind);
        w.WriteByte(car.Derailed ? FlagDerailed : (byte)0);
        w.WriteString(car.GameId);
        w.WriteString(car.GameGuid);
        w.WriteString(car.CargoId);
        w.WriteSingle(car.CargoAmount);
    }

    public static CarDef ReadCarDef(PacketReader r)
    {
        int id = (int)r.ReadVarUInt();
        string kind = r.ReadString();
        byte flags = r.ReadByte();
        string gameId = r.ReadString();
        string gameGuid = r.ReadString();
        string cargoId = r.ReadString();
        float cargoAmount = r.ReadSingle();
        return new CarDef(id, kind, (flags & FlagDerailed) != 0, gameId, gameGuid, cargoId, cargoAmount);
    }

    public static void WriteDef(PacketWriter w, TrainsetDef def)
    {
        w.WriteVarUInt((uint)def.Id);
        w.WriteVarUInt(def.Epoch);
        w.WriteVarUInt((uint)def.OwnerId);
        w.WriteVarUInt((uint)def.Cars.Count);
        foreach (CarDef car in def.Cars) WriteCarDef(w, car);
    }

    public static TrainsetDef ReadDef(PacketReader r)
    {
        int id = (int)r.ReadVarUInt();
        uint epoch = r.ReadVarUInt();
        int owner = (int)r.ReadVarUInt();
        int count = (int)r.ReadVarUInt();
        if (count < 1 || count > MaxCarsPerTrainset) throw new InvalidDataException($"car count {count} out of range");
        var cars = new CarDef[count];
        for (int i = 0; i < count; i++) cars[i] = ReadCarDef(r);
        return new TrainsetDef(id, epoch, owner, cars);
    }

    public static void WriteTransaction(PacketWriter w, TrainsetTransaction txn)
    {
        w.WriteByte((byte)txn.Type);
        w.WriteVarUInt((uint)txn.RetiredIds.Length);
        foreach (int id in txn.RetiredIds) w.WriteVarUInt((uint)id);
        w.WriteVarUInt((uint)txn.Products.Length);
        foreach (TrainsetDef def in txn.Products) WriteDef(w, def);
    }

    public static TrainsetTransaction ReadTransaction(PacketReader r)
    {
        var type = (TrainsetTransactionType)r.ReadByte();
        int retiredCount = (int)r.ReadVarUInt();
        if (retiredCount > MaxTransactionSets) throw new InvalidDataException($"retired count {retiredCount} out of range");
        var retired = new int[retiredCount];
        for (int i = 0; i < retiredCount; i++) retired[i] = (int)r.ReadVarUInt();

        int productCount = (int)r.ReadVarUInt();
        if (productCount < 1 || productCount > MaxTransactionSets) throw new InvalidDataException($"product count {productCount} out of range");
        var products = new TrainsetDef[productCount];
        for (int i = 0; i < productCount; i++) products[i] = ReadDef(r);

        return new TrainsetTransaction(type, retired, products);
    }

    // ── snapshots ──

    public static void WriteBogie(PacketWriter w, BogieState b)
    {
        w.WriteVarUInt(b.EdgeId);
        w.WriteSingle(b.S);
        w.WriteSingle(b.V);
    }

    public static BogieState ReadBogie(PacketReader r) =>
        new(r.ReadVarUInt(), r.ReadSingle(), r.ReadSingle());

    public static void WriteCarSnapshot(PacketWriter w, CarSnapshot car)
    {
        w.WriteByte(car.Derailed ? FlagDerailed : (byte)0);
        if (car.Derailed)
        {
            PresenceCodec.WritePose(w, car.Pose);
        }
        else
        {
            WriteBogie(w, car.Front);
            WriteBogie(w, car.Rear);
        }
    }

    public static CarSnapshot ReadCarSnapshot(PacketReader r)
    {
        byte flags = r.ReadByte();
        if ((flags & FlagDerailed) != 0) return CarSnapshot.OffRail(PresenceCodec.ReadPose(r));
        BogieState front = ReadBogie(r);
        BogieState rear = ReadBogie(r);
        return CarSnapshot.Railed(front, rear);
    }

    public static void WriteSnapshot(PacketWriter w, TrainsetSnapshot snap)
    {
        w.WriteVarUInt((uint)snap.TrainsetId);
        w.WriteVarUInt(snap.Epoch);
        w.WriteInt64(snap.ServerTimeMs);
        w.WriteVarUInt((uint)snap.Cars.Length);
        foreach (CarSnapshot car in snap.Cars) WriteCarSnapshot(w, car);
    }

    public static TrainsetSnapshot ReadSnapshot(PacketReader r)
    {
        int id = (int)r.ReadVarUInt();
        uint epoch = r.ReadVarUInt();
        long time = r.ReadInt64();
        int count = (int)r.ReadVarUInt();
        if (count < 1 || count > MaxCarsPerTrainset) throw new InvalidDataException($"car count {count} out of range");
        var cars = new CarSnapshot[count];
        for (int i = 0; i < count; i++) cars[i] = ReadCarSnapshot(r);
        return new TrainsetSnapshot(id, epoch, time, cars);
    }
}
