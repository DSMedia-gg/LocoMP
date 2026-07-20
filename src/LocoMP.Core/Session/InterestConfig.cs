using System;

namespace LocoMP.Core.Session;

/// <summary>
/// Per-client spatial interest-management knobs (D10, 03 §11.4 — "nothing is broadcast-by-default
/// except tiny global state"). The server keeps a per-client relevance set and only relays a spatial
/// entity's stream to clients whose player is within range of it, cutting the broadcast-everything
/// bandwidth the perf baseline flagged (docs/PERF-BASELINE.md §3: 6–42× over budget at scale).
///
/// <para><b>Off by default.</b> With <see cref="Enabled"/> false the manager is inert —
/// <c>IsRelevant</c> returns true for everyone and <c>Recompute</c> is a no-op — so a server that
/// doesn't opt in behaves exactly as before. A host/dedicated server enables it explicitly.</para>
///
/// <para>Burst 1 gates only player POSES (<see cref="FilterPlayers"/>); the world-object kinds (items,
/// railed trains) join in Burst 2 — <see cref="FilterItems"/>/<see cref="FilterTrains"/> are the wire-
/// stable knobs reserved for them, inert until those paths are gated.</para>
/// </summary>
public sealed class InterestConfig
{
    /// <summary>Master switch. False (default) ⇒ the manager never filters — identical to broadcast-all.</summary>
    public bool Enabled { get; set; }

    /// <summary>Horizontal (X/Z) distance, metres, at which an out-of-scope entity ENTERS a client's
    /// relevance set. Must be &lt; <see cref="LeaveRadiusM"/> so the two form a hysteresis band.</summary>
    public float EnterRadiusM { get; set; } = 500f;

    /// <summary>Distance, metres, at which an in-scope entity LEAVES the relevance set. The gap over
    /// <see cref="EnterRadiusM"/> is the hysteresis band that kills flicker at the boundary.</summary>
    public float LeaveRadiusM { get; set; } = 750f;

    /// <summary>How often (ms) the server re-evaluates relevance as players move (throttled in
    /// <c>NetServer.Poll</c>). A 30 m/s train moves ~15 m per 500 ms — far under the radius, so a few
    /// Hz is ample; the hot relay reads the cached set, so a slightly stale scope is invisible.</summary>
    public int RecomputeIntervalMs { get; set; } = 400;

    /// <summary>Gate player pose relays by relevance (a far player's avatar is hidden until you near
    /// them). Default OFF: poses are only ~4% of the bandwidth, so this is opt-in even when
    /// <see cref="Enabled"/>; the real win (railed trains, ~96%) lands in Burst 2.</summary>
    public bool FilterPlayers { get; set; }

    /// <summary>Reserved for Burst 2 — gate world-item sends by relevance. Wire-stable knob; no effect
    /// until the item paths are gated.</summary>
    public bool FilterItems { get; set; } = true;

    /// <summary>Reserved for Burst 2 — gate railed-train snapshot relays by relevance (the ~96%). Wire-
    /// stable knob; no effect until train geometry + gating land.</summary>
    public bool FilterTrains { get; set; } = true;

    /// <summary>Validate the radii form a proper hysteresis band. Called by the manager at construction
    /// so a misconfiguration fails loudly rather than flickering entities in production.</summary>
    public void Validate()
    {
        if (EnterRadiusM <= 0f) throw new ArgumentOutOfRangeException(nameof(EnterRadiusM), "enter radius must be positive");
        if (LeaveRadiusM <= EnterRadiusM)
            throw new ArgumentOutOfRangeException(nameof(LeaveRadiusM), "leave radius must exceed enter radius (hysteresis band)");
        if (RecomputeIntervalMs < 1) throw new ArgumentOutOfRangeException(nameof(RecomputeIntervalMs));
    }
}
