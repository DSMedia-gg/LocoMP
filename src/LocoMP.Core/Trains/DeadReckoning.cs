namespace LocoMP.Core.Trains;

/// <summary>
/// Dead reckoning (03 §5): a remote bogie's spline position is only known at snapshot instants;
/// between them the smoother extrapolates it ALONG THE RAIL from the snapshot's signed spline
/// velocity, so a constant-speed consist's smoothing target tracks reality instead of chasing the
/// last (already latency-old) snapshot — the steady-state lag collapses toward zero without any
/// protocol change. It stays on the rail because it advances S, not a straight world-space line
/// (which would throw a car off the outside of a curve).
///
/// <para>Bounded by <see cref="MaxExtrapolationSeconds"/>: a stalled stream (dropped owner, packet
/// loss) coasts at most that far before the target holds, so a car never runs away on a lost owner —
/// the worst case is a small overshoot the next snapshot corrects. The GAME side clamps the
/// extrapolated S into its edge (DV's TryGetLocalPoint), so walking past an edge end just holds at
/// the boundary rather than crossing a junction we cannot resolve here.</para>
/// </summary>
public static class DeadReckoning
{
    /// <summary>The 03 §5 extrapolation ceiling — how long a bogie may coast on one snapshot's
    /// velocity before the smoother stops advancing it. Snapshots arrive far faster than this in
    /// health; the cap only bites when the stream goes quiet.</summary>
    public const float MaxExtrapolationSeconds = 0.25f;

    /// <summary>Extrapolated distance-along-edge for a bogie: <paramref name="s"/> advanced by its
    /// signed velocity <paramref name="v"/> over the time since the snapshot, that elapsed CLAMPED to
    /// [0, <paramref name="capSeconds"/>]. A negative elapsed (clock skew) never rewinds the car; a
    /// zero velocity never drifts a stopped one. The result may fall outside the edge — clamping it
    /// into a real edge is the caller's job (the game's spline lookup does it).</summary>
    public static float ExtrapolateS(float s, float v, float elapsedSeconds,
                                     float capSeconds = MaxExtrapolationSeconds)
    {
        float dt = elapsedSeconds < 0f ? 0f
                 : elapsedSeconds > capSeconds ? capSeconds
                 : elapsedSeconds;
        return s + v * dt;
    }
}
