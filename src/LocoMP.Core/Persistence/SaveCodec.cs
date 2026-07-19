using System;
using System.Collections.Generic;
using System.IO;
using LocoMP.Core.Career;
using LocoMP.Core.Protocol;
using LocoMP.Core.Trains;

namespace LocoMP.Core.Persistence;

/// <summary>
/// The versioned binary server store ("LMPS", 03 §7). Hand-rolled over PacketWriter/Reader like
/// the LMPW topology codec — zero new dependencies in the mod payload, and the wire codecs are
/// reused for the shared shapes so the store and the wire can't drift apart. (03 §7 sketched
/// MessagePack here; the deviation is deliberate and flagged in STATE.md — MessagePack stays
/// reserved for bulk transfer if join-snapshot sizes ever demand it.) Reads are bounds-checked and
/// count-capped like every packet read: a truncated or hostile file throws InvalidDataException /
/// EndOfStreamException rather than yielding garbage — callers fall back to a backup rotation.
/// </summary>
public static class SaveCodec
{
    /// <summary>Bump on ANY incompatible layout change; the reader refuses other schemas.</summary>
    /// <remarks>v2: JobDef gained GameId (D13 host-capture). Pre-release, so no v1 migration —
    /// a v1 file is refused cleanly and the host starts fresh (backups keep the old bytes).</remarks>
    public const uint SchemaVersion = 2;

    private const int MaxCollection = 100_000; // hygiene cap for any one saved collection

    private static readonly byte[] Magic = { (byte)'L', (byte)'M', (byte)'P', (byte)'S' };

    public static byte[] Write(ServerSaveData save)
    {
        if (save is null) throw new ArgumentNullException(nameof(save));
        var w = new PacketWriter(4096);
        foreach (byte b in Magic) w.WriteByte(b);
        w.WriteVarUInt(SchemaVersion);
        WriteCareer(w, save.Career);
        WriteTrains(w, save.Trains);
        return w.ToArray();
    }

    public static ServerSaveData Read(byte[] data)
    {
        if (data is null) throw new ArgumentNullException(nameof(data));
        var r = new PacketReader(data);
        foreach (byte expected in Magic)
        {
            if (r.ReadByte() != expected) throw new InvalidDataException("not a LocoMP save (bad magic)");
        }
        uint schema = r.ReadVarUInt();
        if (schema != SchemaVersion)
            throw new InvalidDataException($"save schema v{schema} not supported (this build reads v{SchemaVersion})");
        CareerSaveData career = ReadCareer(r);
        TrainsSaveData trains = ReadTrains(r);
        return new ServerSaveData(career, trains);
    }

    // ── career half ──

    private static void WriteCareer(PacketWriter w, CareerSaveData c)
    {
        w.WriteByte((byte)c.Preset);
        w.WriteVarUInt((uint)c.Accounts.Count);
        foreach (KeyValuePair<string, long> kv in c.Accounts)
        {
            w.WriteString(kv.Key);
            w.WriteInt64(kv.Value);
        }
        w.WriteInt64(c.Minted);
        w.WriteInt64(c.Burned);

        w.WriteVarUInt((uint)c.Profiles.Count);
        foreach (ProfileSave p in c.Profiles)
        {
            w.WriteString(p.Key);
            w.WriteString(p.Name);
            CareerCodec.WriteLicenses(w, p.Licenses);
        }

        CareerCodec.WriteLicenses(w, c.SharedLicenses);
        w.WriteByte(c.SharedGrantIssued ? (byte)1 : (byte)0);

        w.WriteVarUInt((uint)c.Jobs.Count);
        foreach (JobSave j in c.Jobs)
        {
            CareerCodec.WriteJobDef(w, j.Def);
            w.WriteByte((byte)j.State);
            w.WriteString(j.ClaimantKey);
            w.WriteVarUInt((uint)j.NextTaskIndex);
            w.WriteInt64(j.ClaimRemainingMs);
        }

        w.WriteVarUInt((uint)c.GraceRemainingMs.Count);
        foreach (KeyValuePair<string, long> kv in c.GraceRemainingMs)
        {
            w.WriteString(kv.Key);
            w.WriteInt64(kv.Value);
        }

        w.WriteVarUInt((uint)c.NextJobId);
        w.WriteVarUInt(c.RngState);
    }

    private static CareerSaveData ReadCareer(PacketReader r)
    {
        var c = new CareerSaveData { Preset = (ProgressionPreset)r.ReadByte() };

        int accounts = ReadCount(r, "accounts");
        for (int i = 0; i < accounts; i++)
        {
            string key = r.ReadString();
            c.Accounts[key] = r.ReadInt64();
        }
        c.Minted = r.ReadInt64();
        c.Burned = r.ReadInt64();

        int profiles = ReadCount(r, "profiles");
        for (int i = 0; i < profiles; i++)
        {
            string key = r.ReadString();
            string name = r.ReadString();
            var licenses = new List<string>(CareerCodec.ReadLicenses(r));
            c.Profiles.Add(new ProfileSave(key, name, licenses));
        }

        c.SharedLicenses.AddRange(CareerCodec.ReadLicenses(r));
        c.SharedGrantIssued = r.ReadByte() != 0;

        int jobs = ReadCount(r, "jobs");
        for (int i = 0; i < jobs; i++)
        {
            JobDef def = CareerCodec.ReadJobDef(r);
            var state = (JobLifecycle)r.ReadByte();
            string claimant = r.ReadString();
            int nextTask = (int)r.ReadVarUInt();
            long remaining = r.ReadInt64();
            c.Jobs.Add(new JobSave(def, state, claimant, nextTask, remaining));
        }

        int grace = ReadCount(r, "grace holds");
        for (int i = 0; i < grace; i++)
        {
            string key = r.ReadString();
            c.GraceRemainingMs[key] = r.ReadInt64();
        }

        c.NextJobId = (int)r.ReadVarUInt();
        c.RngState = r.ReadVarUInt();
        return c;
    }

    // ── world half ──

    private static void WriteTrains(PacketWriter w, TrainsSaveData t)
    {
        w.WriteVarUInt((uint)t.Sets.Count);
        foreach (TrainsetDef def in t.Sets) TrainCodec.WriteDef(w, def);

        w.WriteVarUInt((uint)t.LatestSnapshots.Count);
        foreach (TrainsetSnapshot snap in t.LatestSnapshots) TrainCodec.WriteSnapshot(w, snap);

        w.WriteVarUInt((uint)t.Junctions.Count);
        foreach (KeyValuePair<uint, byte> j in t.Junctions)
        {
            w.WriteVarUInt(j.Key);
            w.WriteByte(j.Value);
        }

        w.WriteVarUInt((uint)t.Turntables.Count);
        foreach (KeyValuePair<uint, float> tt in t.Turntables)
        {
            w.WriteVarUInt(tt.Key);
            w.WriteSingle(tt.Value);
        }

        w.WriteVarUInt((uint)t.NextTrainsetId);
        w.WriteVarUInt((uint)t.NextCarId);
    }

    private static TrainsSaveData ReadTrains(PacketReader r)
    {
        var t = new TrainsSaveData();

        int sets = ReadCount(r, "trainsets");
        for (int i = 0; i < sets; i++) t.Sets.Add(TrainCodec.ReadDef(r));

        int snaps = ReadCount(r, "snapshots");
        for (int i = 0; i < snaps; i++)
        {
            TrainsetSnapshot snap = TrainCodec.ReadSnapshot(r);
            t.LatestSnapshots.Add(snap);
        }

        int junctions = ReadCount(r, "junctions");
        for (int i = 0; i < junctions; i++)
        {
            uint id = r.ReadVarUInt();
            t.Junctions[id] = r.ReadByte();
        }

        int turntables = ReadCount(r, "turntables");
        for (int i = 0; i < turntables; i++)
        {
            uint id = r.ReadVarUInt();
            t.Turntables[id] = r.ReadSingle();
        }

        t.NextTrainsetId = (int)r.ReadVarUInt();
        t.NextCarId = (int)r.ReadVarUInt();
        return t;
    }

    private static int ReadCount(PacketReader r, string what)
    {
        int count = (int)r.ReadVarUInt();
        if (count < 0 || count > MaxCollection)
            throw new InvalidDataException($"{what} count {count} out of range");
        return count;
    }
}
