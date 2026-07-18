using System;
using System.Collections.Generic;
using System.IO;
using LocoMP.Core.Career;

namespace LocoMP.Core.Protocol;

/// <summary>The scope a wallet/license message refers to. Per-player state is only ever sent to
/// its owner; shared state is broadcast — so a scope byte is all the addressing needed and player
/// keys never touch the wire.</summary>
public enum EconomyScope : byte
{
    Personal = 0,
    Shared = 1,
}

/// <summary>Why money moved, for client UI/log. The amounts are informational — the authoritative
/// balance always arrives in the accompanying WalletState.</summary>
public enum EconomyEventKind : byte
{
    StartingGrant = 1,
    JobPayout = 2,
    LicenseFee = 3,
}

/// <summary>
/// Shared (de)serialization for career sync (hand-rolled, hard rule 4). Mirrors
/// <see cref="TrainCodec"/>'s role for trains and is reused by the save codec, so the wire and the
/// store can't drift apart. Read sides are untrusted (03 §9): counts are capped, violations throw
/// for the session layer's catch-and-drop.
/// </summary>
internal static class CareerCodec
{
    /// <summary>Cap on licenses in one list (a whole license catalog is far smaller).</summary>
    public const int MaxLicenses = 64;

    /// <summary>Cap on tasks in one job (generated jobs carry 3; leave headroom for M3.3).</summary>
    public const int MaxTasksPerJob = 32;

    public static void WriteJobDef(PacketWriter w, JobDef def)
    {
        w.WriteVarUInt((uint)def.Id);
        w.WriteString(def.JobType);
        w.WriteString(def.Origin);
        w.WriteString(def.Destination);
        w.WriteString(def.CargoKind);
        w.WriteVarUInt((uint)def.CarCount);
        w.WriteInt64(def.PayoutCents);
        WriteLicenses(w, def.RequiredLicenses);
        w.WriteVarUInt((uint)def.Tasks.Count);
        foreach (JobTaskDef task in def.Tasks)
        {
            w.WriteByte((byte)task.Kind);
            w.WriteString(task.Param);
        }
    }

    public static JobDef ReadJobDef(PacketReader r)
    {
        int id = (int)r.ReadVarUInt();
        string jobType = r.ReadString();
        string origin = r.ReadString();
        string destination = r.ReadString();
        string cargoKind = r.ReadString();
        int carCount = (int)r.ReadVarUInt();
        if (carCount < 1 || carCount > TrainCodec.MaxCarsPerTrainset)
            throw new InvalidDataException($"car count {carCount} out of range");
        long payout = r.ReadInt64();
        if (payout < 0) throw new InvalidDataException("negative payout");
        IReadOnlyList<string> licenses = ReadLicenses(r);
        int taskCount = (int)r.ReadVarUInt();
        if (taskCount < 1 || taskCount > MaxTasksPerJob)
            throw new InvalidDataException($"task count {taskCount} out of range");
        var tasks = new JobTaskDef[taskCount];
        for (int i = 0; i < taskCount; i++)
        {
            var kind = (JobTaskKind)r.ReadByte();
            string param = r.ReadString();
            tasks[i] = new JobTaskDef(kind, param);
        }
        return new JobDef(id, jobType, origin, destination, cargoKind, carCount, payout, licenses, tasks);
    }

    public static void WriteLicenses(PacketWriter w, IReadOnlyCollection<string> licenses)
    {
        w.WriteVarUInt((uint)licenses.Count);
        foreach (string lic in licenses) w.WriteString(lic);
    }

    public static IReadOnlyList<string> ReadLicenses(PacketReader r)
    {
        int count = (int)r.ReadVarUInt();
        if (count > MaxLicenses) throw new InvalidDataException($"license count {count} out of range");
        if (count == 0) return Array.Empty<string>();
        var licenses = new string[count];
        for (int i = 0; i < count; i++) licenses[i] = r.ReadString();
        return licenses;
    }
}
