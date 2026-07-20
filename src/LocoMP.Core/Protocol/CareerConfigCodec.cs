using System;
using System.Collections.Generic;
using System.IO;
using LocoMP.Core.Career;

namespace LocoMP.Core.Protocol;

/// <summary>
/// The versioned binary career-config file ("LMPC"). A dedicated server loads one via <c>--config</c> to
/// run a REAL Derail Valley career (actual yards, cargo economy, license gates, route distances, station
/// world-locations) instead of the synthetic Alpha/Bravo placeholder. Hand-rolled over PacketWriter/Reader
/// like the LMPW topology and LMPS save codecs — zero new dependencies, and the SAME bytes are written by
/// the Shim exporter (net48, reads the live game world) and read by the server (net8), so the format can't
/// drift between the two sides (hard rule 3/4). Unlike the D13 host-capture path, this carries the full
/// config INCLUDING job shapes: on a dedicated server the deterministic core generator is the job source,
/// so the board needs <see cref="CareerConfig.JobTypes"/>. Reads are bounds-checked and count-capped like
/// every packet read — a truncated/foreign/future-schema file throws InvalidDataException rather than
/// yielding garbage, and the server falls back to its built-in default.
/// </summary>
public static class CareerConfigCodec
{
    /// <summary>Bump on ANY incompatible layout change; the reader refuses other schemas (no migration —
    /// pre-release, like LMPS/LMPW).</summary>
    public const uint SchemaVersion = 1;

    private const int MaxCollection = 100_000; // hygiene cap for any one collection in the file

    private static readonly byte[] Magic = { (byte)'L', (byte)'M', (byte)'P', (byte)'C' };

    public static byte[] Write(CareerConfig cfg)
    {
        if (cfg is null) throw new ArgumentNullException(nameof(cfg));
        var w = new PacketWriter(1024);
        foreach (byte b in Magic) w.WriteByte(b);
        w.WriteVarUInt(SchemaVersion);

        w.WriteByte((byte)cfg.Preset);
        w.WriteInt64(cfg.StartingBalanceCents);
        WriteStrings(w, cfg.StartingLicenses);
        w.WriteVarUInt((uint)cfg.MaxConcurrentClaims);
        w.WriteInt64(cfg.ClaimTtlMs);
        w.WriteInt64(cfg.ReconnectGraceMs);
        w.WriteVarUInt((uint)cfg.TargetAvailableJobs);
        w.WriteVarUInt(cfg.JobSeed);
        WriteStrings(w, cfg.Stations);

        w.WriteVarUInt((uint)cfg.JobTypes.Count);
        foreach (JobTypeSpec spec in cfg.JobTypes)
        {
            w.WriteString(spec.JobType);
            w.WriteString(spec.CargoKind);
            w.WriteInt64(spec.PayoutPerCarCents);
            w.WriteVarUInt((uint)spec.MinCars);
            w.WriteVarUInt((uint)spec.MaxCars);
            WriteStrings(w, spec.RequiredLicenses);
            WriteStrings(w, spec.Origins);
            WriteStrings(w, spec.Destinations);
            w.WriteInt64(spec.PayoutPerCarKmCents);
        }

        w.WriteVarUInt((uint)cfg.LicensePrices.Count);
        foreach (KeyValuePair<string, long> kv in cfg.LicensePrices)
        {
            w.WriteString(kv.Key);
            w.WriteInt64(kv.Value);
        }

        w.WriteVarUInt((uint)cfg.StationDistancesKm.Count);
        foreach (KeyValuePair<string, float> kv in cfg.StationDistancesKm)
        {
            w.WriteString(kv.Key); // the "a|b" DistanceKey, stored verbatim
            w.WriteSingle(kv.Value);
        }

        w.WriteVarUInt((uint)cfg.StationLocations.Count);
        foreach (KeyValuePair<string, StationLocation> kv in cfg.StationLocations)
        {
            w.WriteString(kv.Key);
            w.WriteSingle(kv.Value.X);
            w.WriteSingle(kv.Value.Y);
            w.WriteSingle(kv.Value.Z);
        }

        w.WriteSingle(cfg.TaskProximityRadiusM);
        w.WriteByte(cfg.AcceptExternalJobs ? (byte)1 : (byte)0);
        return w.ToArray();
    }

    public static CareerConfig Read(byte[] data)
    {
        if (data is null) throw new ArgumentNullException(nameof(data));
        var r = new PacketReader(data);
        foreach (byte expected in Magic)
        {
            if (r.ReadByte() != expected) throw new InvalidDataException("not a LocoMP career config (bad magic)");
        }
        uint schema = r.ReadVarUInt();
        if (schema != SchemaVersion)
            throw new InvalidDataException($"career config schema v{schema} not supported (this build reads v{SchemaVersion})");

        var cfg = new CareerConfig
        {
            Preset = (ProgressionPreset)r.ReadByte(),
            StartingBalanceCents = r.ReadInt64(),
        };
        cfg.StartingLicenses = ReadStrings(r, "starting licenses");
        cfg.MaxConcurrentClaims = (int)r.ReadVarUInt();
        cfg.ClaimTtlMs = r.ReadInt64();
        cfg.ReconnectGraceMs = r.ReadInt64();
        cfg.TargetAvailableJobs = (int)r.ReadVarUInt();
        cfg.JobSeed = r.ReadVarUInt();
        cfg.Stations = ReadStrings(r, "stations");

        int jobTypes = ReadCount(r, "job types");
        var specs = new JobTypeSpec[jobTypes];
        for (int i = 0; i < jobTypes; i++)
        {
            string jobType = r.ReadString();
            string cargoKind = r.ReadString();
            long payoutPerCar = r.ReadInt64();
            int minCars = (int)r.ReadVarUInt();
            int maxCars = (int)r.ReadVarUInt();
            string[] required = ReadStrings(r, "required licenses");
            string[] origins = ReadStrings(r, "origins");
            string[] destinations = ReadStrings(r, "destinations");
            long payoutPerCarKm = r.ReadInt64();
            try
            {
                specs[i] = new JobTypeSpec(jobType, cargoKind, payoutPerCar, minCars, maxCars,
                    required, origins, destinations, payoutPerCarKm);
            }
            catch (ArgumentException e)
            {
                throw new InvalidDataException($"invalid job type spec {i}: {e.Message}");
            }
        }
        cfg.JobTypes = specs;

        int prices = ReadCount(r, "license prices");
        var priceMap = new Dictionary<string, long>(StringComparer.Ordinal);
        for (int i = 0; i < prices; i++)
        {
            string key = r.ReadString();
            priceMap[key] = r.ReadInt64();
        }
        cfg.LicensePrices = priceMap;

        int distances = ReadCount(r, "route distances");
        var distanceMap = new Dictionary<string, float>(StringComparer.Ordinal);
        for (int i = 0; i < distances; i++)
        {
            string key = r.ReadString();
            distanceMap[key] = r.ReadSingle();
        }
        cfg.StationDistancesKm = distanceMap;

        int locations = ReadCount(r, "station locations");
        var locationMap = new Dictionary<string, StationLocation>(StringComparer.Ordinal);
        for (int i = 0; i < locations; i++)
        {
            string key = r.ReadString();
            float x = r.ReadSingle();
            float y = r.ReadSingle();
            float z = r.ReadSingle();
            locationMap[key] = new StationLocation(x, y, z);
        }
        cfg.StationLocations = locationMap;

        cfg.TaskProximityRadiusM = r.ReadSingle();
        cfg.AcceptExternalJobs = r.ReadByte() != 0;
        return cfg;
    }

    private static void WriteStrings(PacketWriter w, IReadOnlyList<string> items)
    {
        w.WriteVarUInt((uint)items.Count);
        foreach (string s in items) w.WriteString(s);
    }

    private static string[] ReadStrings(PacketReader r, string what)
    {
        int n = ReadCount(r, what);
        var arr = new string[n];
        for (int i = 0; i < n; i++) arr[i] = r.ReadString();
        return arr;
    }

    private static int ReadCount(PacketReader r, string what)
    {
        int count = (int)r.ReadVarUInt();
        if (count < 0 || count > MaxCollection)
            throw new InvalidDataException($"{what} count {count} out of range");
        return count;
    }
}
