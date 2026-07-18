using System;
using System.Collections.Generic;

namespace LocoMP.Core.Career;

/// <summary>What one job step is, mechanically. Core validates ORDER only (steps are strictly
/// sequential); what "done" means in the world is owner-reported and checked against live game
/// state by the Shim side (02 §4 — server validates task transitions from reported world state).</summary>
public enum JobTaskKind : byte
{
    Load = 1,
    Haul = 2,
    Unload = 3,
}

/// <summary>One step of a job. <see cref="Param"/> is an opaque token (a station/track name) the
/// Shim resolves against the live world — Core never interprets it, same posture as CarDef.Kind.</summary>
public sealed class JobTaskDef
{
    public JobTaskDef(JobTaskKind kind, string param)
    {
        Kind = kind;
        Param = param ?? throw new ArgumentNullException(nameof(param));
    }

    public JobTaskKind Kind { get; }
    public string Param { get; }
}

/// <summary>
/// An immutable job offer: what to haul, where, for how much, behind which licenses. Generated
/// deterministically SERVER-SIDE (02 §4 — job generation moves out of the game client and into the
/// core); clients only ever receive these, never create them.
/// </summary>
public sealed class JobDef
{
    public JobDef(int id, string jobType, string origin, string destination, string cargoKind,
        int carCount, long payoutCents, IReadOnlyList<string> requiredLicenses, IReadOnlyList<JobTaskDef> tasks)
    {
        if (carCount < 1) throw new ArgumentOutOfRangeException(nameof(carCount));
        if (payoutCents < 0) throw new ArgumentOutOfRangeException(nameof(payoutCents));
        if (tasks is null || tasks.Count == 0) throw new ArgumentException("a job has at least one task", nameof(tasks));
        Id = id;
        JobType = jobType ?? throw new ArgumentNullException(nameof(jobType));
        Origin = origin ?? throw new ArgumentNullException(nameof(origin));
        Destination = destination ?? throw new ArgumentNullException(nameof(destination));
        CargoKind = cargoKind ?? throw new ArgumentNullException(nameof(cargoKind));
        CarCount = carCount;
        PayoutCents = payoutCents;
        RequiredLicenses = requiredLicenses ?? throw new ArgumentNullException(nameof(requiredLicenses));
        Tasks = tasks;
    }

    /// <summary>Server-assigned job id, unique for the world (persists across restarts).</summary>
    public int Id { get; }

    /// <summary>Opaque job-type token (e.g. "FH" freight haul). Core never interprets it.</summary>
    public string JobType { get; }

    public string Origin { get; }
    public string Destination { get; }
    public string CargoKind { get; }
    public int CarCount { get; }
    public long PayoutCents { get; }

    /// <summary>Licenses the claimant's scope must hold — gated server-side at claim time (02 §4).</summary>
    public IReadOnlyList<string> RequiredLicenses { get; }

    /// <summary>Ordered steps; the claimant reports each in sequence.</summary>
    public IReadOnlyList<JobTaskDef> Tasks { get; }

    public override string ToString() => $"job {Id} {JobType} {Origin}→{Destination} ({CarCount}× {CargoKind})";
}
