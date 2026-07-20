using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LocoMP.Core.Career;
using LocoMP.Core.Protocol;
using LocoMP.Core.Session;
using Xunit;

namespace LocoMP.Core.Tests;

/// <summary>
/// The career-config file codec (LMPC) — how a dedicated server loads a real DV career via --config. A
/// round-trip must preserve every field (a config the Shim exporter writes and the server reads can't
/// drift), the round-tripped config must generate an IDENTICAL board (the functional proof — same seed +
/// same config ⇒ same job stream), and a foreign/future/truncated file must be refused cleanly so the
/// server can fall back to its built-in default rather than load garbage.
/// </summary>
public class CareerConfigCodecTests
{
    /// <summary>A config touching every field the codec must carry — both presets' scalars, job shapes
    /// with origins/destinations/licenses and a per-km term, the price catalog, route distances, and
    /// station world-locations for the proximity gate.</summary>
    private static CareerConfig FullConfig() => new()
    {
        Preset = ProgressionPreset.SharedCareer,
        StartingBalanceCents = 2000_00,
        StartingLicenses = new[] { "FreightHaul", "Shunting" },
        MaxConcurrentClaims = 99,
        ClaimTtlMs = 2 * 60 * 60_000L,
        ReconnectGraceMs = 15 * 60_000L,
        TargetAvailableJobs = 15,
        JobSeed = 42,
        Stations = new[] { "SM", "GF", "HB", "CM" },
        JobTypes = new[]
        {
            new JobTypeSpec("FreightHaul", "steel", 100_00, 2, 6,
                requiredLicenses: new[] { "FreightHaul" },
                origins: new[] { "SM" }, destinations: new[] { "GF", "HB" },
                payoutPerCarKmCents: 120),
            new JobTypeSpec("Shunting", "boxcar", 25_00, 1, 3,
                requiredLicenses: new[] { "Shunting" }),
        },
        LicensePrices = new Dictionary<string, long> { ["FreightHaul"] = 0, ["Shunting"] = 120_00, ["Hazmat1"] = 300_00 },
        StationDistancesKm = new Dictionary<string, float>
        {
            [CareerConfig.DistanceKey("SM", "GF")] = 12.5f,
            [CareerConfig.DistanceKey("GF", "HB")] = 8.25f,
        },
        StationLocations = new Dictionary<string, StationLocation>
        {
            ["SM"] = new StationLocation(100.5f, 12f, -340.25f),
            ["GF"] = new StationLocation(-50f, 8f, 900f),
        },
        TaskProximityRadiusM = 500f,
        AcceptExternalJobs = false,
    };

    [Fact]
    public void Round_trips_every_field()
    {
        CareerConfig original = FullConfig();
        CareerConfig back = CareerConfigCodec.Read(CareerConfigCodec.Write(original));

        Assert.Equal(original.Preset, back.Preset);
        Assert.Equal(original.StartingBalanceCents, back.StartingBalanceCents);
        Assert.Equal(original.StartingLicenses, back.StartingLicenses);
        Assert.Equal(original.MaxConcurrentClaims, back.MaxConcurrentClaims);
        Assert.Equal(original.ClaimTtlMs, back.ClaimTtlMs);
        Assert.Equal(original.ReconnectGraceMs, back.ReconnectGraceMs);
        Assert.Equal(original.TargetAvailableJobs, back.TargetAvailableJobs);
        Assert.Equal(original.JobSeed, back.JobSeed);
        Assert.Equal(original.Stations, back.Stations);
        Assert.Equal(original.TaskProximityRadiusM, back.TaskProximityRadiusM);
        Assert.Equal(original.AcceptExternalJobs, back.AcceptExternalJobs);

        Assert.Equal(original.JobTypes.Count, back.JobTypes.Count);
        for (int i = 0; i < original.JobTypes.Count; i++)
        {
            JobTypeSpec a = original.JobTypes[i], b = back.JobTypes[i];
            Assert.Equal(a.JobType, b.JobType);
            Assert.Equal(a.CargoKind, b.CargoKind);
            Assert.Equal(a.PayoutPerCarCents, b.PayoutPerCarCents);
            Assert.Equal(a.MinCars, b.MinCars);
            Assert.Equal(a.MaxCars, b.MaxCars);
            Assert.Equal(a.RequiredLicenses, b.RequiredLicenses);
            Assert.Equal(a.Origins, b.Origins);
            Assert.Equal(a.Destinations, b.Destinations);
            Assert.Equal(a.PayoutPerCarKmCents, b.PayoutPerCarKmCents);
        }

        Assert.Equal(original.LicensePrices, back.LicensePrices);
        Assert.Equal(original.StationDistancesKm, back.StationDistancesKm);
        Assert.Equal(original.StationLocations.Count, back.StationLocations.Count);
        foreach (KeyValuePair<string, StationLocation> kv in original.StationLocations)
        {
            StationLocation got = back.StationLocations[kv.Key];
            Assert.Equal(kv.Value.X, got.X);
            Assert.Equal(kv.Value.Y, got.Y);
            Assert.Equal(kv.Value.Z, got.Z);
        }
    }

    [Fact]
    public void A_round_tripped_config_generates_an_identical_board()
    {
        // Same seed + same config ⇒ the same deterministic job stream on any runtime. So if the codec
        // preserved the config faithfully, a registry built from the round-tripped copy produces the
        // exact same board as one built from the original.
        var original = new CareerRegistry(FullConfig(), new ManualClock());
        var reloaded = new CareerRegistry(CareerConfigCodec.Read(CareerConfigCodec.Write(FullConfig())), new ManualClock());
        original.Tick();
        reloaded.Tick();

        List<JobRecord> boardA = Board(original);
        List<JobRecord> boardB = Board(reloaded);
        Assert.NotEmpty(boardA);
        Assert.Equal(boardA.Count, boardB.Count);
        for (int i = 0; i < boardA.Count; i++)
        {
            JobDef a = boardA[i].Def, b = boardB[i].Def;
            Assert.Equal(a.JobType, b.JobType);
            Assert.Equal(a.Origin, b.Origin);
            Assert.Equal(a.Destination, b.Destination);
            Assert.Equal(a.CargoKind, b.CargoKind);
            Assert.Equal(a.CarCount, b.CarCount);
            Assert.Equal(a.PayoutCents, b.PayoutCents);
            Assert.Equal(a.RequiredLicenses, b.RequiredLicenses);
        }
    }

    private static List<JobRecord> Board(CareerRegistry career) =>
        career.Jobs.Values.Where(j => j.State == JobLifecycle.Available).OrderBy(j => j.Def.Id).ToList();

    [Fact]
    public void Rejects_a_foreign_file()
    {
        var junk = new byte[] { (byte)'N', (byte)'O', (byte)'P', (byte)'E', 1, 2, 3 };
        Assert.Throws<InvalidDataException>(() => CareerConfigCodec.Read(junk));
    }

    [Fact]
    public void Rejects_a_future_schema()
    {
        byte[] bytes = CareerConfigCodec.Write(FullConfig());
        // The schema is a single-byte varint right after the 4 magic bytes (v1 < 0x80). Bump it.
        bytes[4] = (byte)(CareerConfigCodec.SchemaVersion + 1);
        InvalidDataException ex = Assert.Throws<InvalidDataException>(() => CareerConfigCodec.Read(bytes));
        Assert.Contains("schema", ex.Message);
    }

    [Fact]
    public void Rejects_a_truncated_file()
    {
        byte[] bytes = CareerConfigCodec.Write(FullConfig());
        byte[] cut = bytes.Take(bytes.Length / 2).ToArray();
        Assert.ThrowsAny<Exception>(() => CareerConfigCodec.Read(cut)); // EndOfStream or InvalidData
    }
}
