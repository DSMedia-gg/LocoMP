using System;
using System.Collections.Generic;

namespace LocoMP.Core.Career;

/// <summary>One generatable job shape: the deterministic generator picks a type, a distinct
/// station pair, and a car count in [MinCars, MaxCars]; payout scales linearly with car count.</summary>
public sealed class JobTypeSpec
{
    public JobTypeSpec(string jobType, string cargoKind, long payoutPerCarCents, int minCars, int maxCars,
        IReadOnlyList<string>? requiredLicenses = null)
    {
        if (payoutPerCarCents < 0) throw new ArgumentOutOfRangeException(nameof(payoutPerCarCents));
        if (minCars < 1 || maxCars < minCars) throw new ArgumentOutOfRangeException(nameof(maxCars));
        JobType = jobType ?? throw new ArgumentNullException(nameof(jobType));
        CargoKind = cargoKind ?? throw new ArgumentNullException(nameof(cargoKind));
        PayoutPerCarCents = payoutPerCarCents;
        MinCars = minCars;
        MaxCars = maxCars;
        RequiredLicenses = requiredLicenses ?? Array.Empty<string>();
    }

    public string JobType { get; }
    public string CargoKind { get; }
    public long PayoutPerCarCents { get; }
    public int MinCars { get; }
    public int MaxCars { get; }
    public IReadOnlyList<string> RequiredLicenses { get; }
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
}
