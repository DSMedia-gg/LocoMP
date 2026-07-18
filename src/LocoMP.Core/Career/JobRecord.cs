namespace LocoMP.Core.Career;

/// <summary>A job's lifecycle. Abandon and claim/grace expiry RETURN a job to Available with its
/// progress reset — there is no dead "abandoned" state a board slot can leak into. Completed jobs
/// leave the board (the record is handed to the caller for the final broadcast).</summary>
public enum JobLifecycle : byte
{
    Available = 0,
    Claimed = 1,
    Completed = 2,

    /// <summary>Reserved for board-level expiry of unclaimed jobs (not used in M3.1).</summary>
    Expired = 3,
}

/// <summary>
/// The server's mutable record of one job on the board. All mutation happens inside
/// <see cref="CareerRegistry"/> so the claim/progress/complete rules live in one place (03 §3 state
/// authority — mirrors how the epoch rules live only in TrainsetRegistry); everyone else gets a
/// read-only view.
/// </summary>
public sealed class JobRecord
{
    internal JobRecord(JobDef def) => Def = def;

    public JobDef Def { get; }

    public JobLifecycle State { get; internal set; } = JobLifecycle.Available;

    /// <summary>Claimant's stable player key; null when unclaimed. Server-side only — the wire
    /// carries the claimant's session peer id and display name, never the key.</summary>
    public string? ClaimantKey { get; internal set; }

    /// <summary>Next task step the claimant must report (0-based). Reset when a claim is released.</summary>
    public int NextTaskIndex { get; internal set; }

    /// <summary>Server-clock deadline for the claim (claim TTL, 02 §4 anti-grief). Only meaningful
    /// while Claimed. Persisted as REMAINING time — the monotonic clock restarts with the process.</summary>
    public long ClaimExpiresAtMs { get; internal set; }

    public override string ToString() => $"{Def} [{State}]";
}
