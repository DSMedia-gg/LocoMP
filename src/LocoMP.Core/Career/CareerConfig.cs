using System;
using System.Collections.Generic;

namespace LocoMP.Core.Career;

/// <summary>One generatable job shape: the deterministic generator picks a type, a distinct
/// station pair, and a car count in [MinCars, MaxCars]; payout scales with car count and — when
/// the host supplies a distance table — with the real route distance.</summary>
public sealed class JobTypeSpec
{
    public JobTypeSpec(string jobType, string cargoKind, long payoutPerCarCents, int minCars, int maxCars,
        IReadOnlyList<string>? requiredLicenses = null,
        IReadOnlyList<string>? origins = null, IReadOnlyList<string>? destinations = null,
        long payoutPerCarKmCents = 0)
    {
        if (payoutPerCarCents < 0) throw new ArgumentOutOfRangeException(nameof(payoutPerCarCents));
        if (payoutPerCarKmCents < 0) throw new ArgumentOutOfRangeException(nameof(payoutPerCarKmCents));
        if (minCars < 1 || maxCars < minCars) throw new ArgumentOutOfRangeException(nameof(maxCars));
        JobType = jobType ?? throw new ArgumentNullException(nameof(jobType));
        CargoKind = cargoKind ?? throw new ArgumentNullException(nameof(cargoKind));
        PayoutPerCarCents = payoutPerCarCents;
        MinCars = minCars;
        MaxCars = maxCars;
        RequiredLicenses = requiredLicenses ?? Array.Empty<string>();
        Origins = origins ?? Array.Empty<string>();
        Destinations = destinations ?? Array.Empty<string>();
        PayoutPerCarKmCents = payoutPerCarKmCents;
    }

    public string JobType { get; }
    public string CargoKind { get; }

    /// <summary>Flat payout per car; the distance term below adds on top.</summary>
    public long PayoutPerCarCents { get; }

    public int MinCars { get; }
    public int MaxCars { get; }
    public IReadOnlyList<string> RequiredLicenses { get; }

    /// <summary>Stations this shape can start from; empty = any. Lets the host mirror the real
    /// map's per-station cargo routes (a spec per origin/cargo-group) instead of uniform pairs.</summary>
    public IReadOnlyList<string> Origins { get; }

    /// <summary>Stations this shape can deliver to; empty = any (the origin is always excluded).</summary>
    public IReadOnlyList<string> Destinations { get; }

    /// <summary>Per car per km of route distance (from <see cref="CareerConfig.StationDistancesKm"/>;
    /// pairs missing from the table contribute nothing).</summary>
    public long PayoutPerCarKmCents { get; }
}

/// <summary>A station's absolute world position (the same coordinate space presence poses use), for
/// the server-side task proximity check. Plain data — Core never touches Unity types.</summary>
public readonly struct StationLocation
{
    public StationLocation(float x, float y, float z)
    {
        X = x;
        Y = y;
        Z = z;
    }

    public float X { get; }
    public float Y { get; }
    public float Z { get; }
}

/// <summary>
/// Host-chosen career knobs (02 §6's server-config surface). Stations and job shapes are DATA so
/// Core stays game-free: the host/extractor supplies the real map's stations and DV's job/license
/// economy in M3.3; tests and the dedicated server feed synthetic ones. A config with fewer than
/// two stations (the default) simply generates no jobs. Set everything before constructing the
/// server — the career reads these once and never watches for changes.
/// </summary>
public sealed class CareerConfig
{
    /// <summary>D3/O2: per-player careers is the shipped default; shared = "classic co-op".</summary>
    public ProgressionPreset Preset { get; set; } = ProgressionPreset.PerPlayer;

    /// <summary>Minted for every first-seen player (per-player preset) or ONCE for the shared
    /// wallet (02 §6 — starting money for new joiners, P0 for per-player mode).</summary>
    public long StartingBalanceCents { get; set; } = 500_00;

    /// <summary>Licenses every new profile (or the shared set, once) starts with.</summary>
    public IReadOnlyList<string> StartingLicenses { get; set; } = Array.Empty<string>();

    /// <summary>Max simultaneous claims per player (02 §4 anti-grief).</summary>
    public int MaxConcurrentClaims { get; set; } = 3;

    /// <summary>How long a claim may sit unfinished before the job returns to the board.</summary>
    public long ClaimTtlMs { get; set; } = 45 * 60_000L;

    /// <summary>How long a disconnected player's claims are held for them (07 §M3: 10 minutes).</summary>
    public long ReconnectGraceMs { get; set; } = 10 * 60_000L;

    /// <summary>The generator keeps this many jobs available on the board.</summary>
    public int TargetAvailableJobs { get; set; } = 12;

    /// <summary>Generator seed: same seed + same config ⇒ the same job stream, on any runtime.</summary>
    public uint JobSeed { get; set; } = 1;

    /// <summary>Stations jobs run between. Fewer than two ⇒ generation is off.</summary>
    public IReadOnlyList<string> Stations { get; set; } = Array.Empty<string>();

    public IReadOnlyList<JobTypeSpec> JobTypes { get; set; } = Array.Empty<JobTypeSpec>();

    /// <summary>Purchasable licenses and their prices. Fees are burned, never moved (ledger).</summary>
    public IReadOnlyDictionary<string, long> LicensePrices { get; set; } = new Dictionary<string, long>(StringComparer.Ordinal);

    /// <summary>Route distances keyed by <see cref="DistanceKey"/>; either direction is looked up.
    /// Feeds the per-km payout term — missing pairs simply contribute nothing.</summary>
    public IReadOnlyDictionary<string, float> StationDistancesKm { get; set; } = new Dictionary<string, float>(StringComparer.Ordinal);

    /// <summary>Absolute station positions for the task proximity check (02 §4 — the server
    /// validates task transitions from owner-reported world state, and presence poses ARE that
    /// state). Stations missing here are simply not proximity-checked.</summary>
    public IReadOnlyDictionary<string, StationLocation> StationLocations { get; set; } = new Dictionary<string, StationLocation>(StringComparer.Ordinal);

    /// <summary>How close (metres, horizontal) the claimant must be to a task's station to report
    /// that step. 0 (default) disables the check.</summary>
    public float TaskProximityRadiusM { get; set; }

    /// <summary>D13 host-capture: accept jobs registered by the session's world source (the game's
    /// own generator running on the host) instead of/alongside the deterministic generator. Leave
    /// false on the dedicated server, where the core generator is the source.</summary>
    public bool AcceptExternalJobs { get; set; }

    public static string DistanceKey(string a, string b) => a + "|" + b;
}
