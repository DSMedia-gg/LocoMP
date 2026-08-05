using LocoMP.Core.Trains;
using Xunit;

namespace LocoMP.Core.Tests;

/// <summary>
/// Dead reckoning (03 §5), the pure spline-space half of the smoother's extrapolation. The Shim
/// feeds the result to DV's TryGetLocalPoint (which clamps S into its edge and keeps the car on the
/// rail); the arithmetic that decides HOW FAR to coast — and when to stop — lives here, game-free.
/// </summary>
public class DeadReckoningTests
{
    [Fact]
    public void Under_the_cap_S_advances_linearly_with_signed_velocity()
    {
        Assert.Equal(102f, DeadReckoning.ExtrapolateS(s: 100f, v: 20f, elapsedSeconds: 0.1f), 4);
        // Negative velocity (the bogie runs toward the edge's start) coasts backward.
        Assert.Equal(98f, DeadReckoning.ExtrapolateS(s: 100f, v: -20f, elapsedSeconds: 0.1f), 4);
    }

    [Fact]
    public void Elapsed_is_capped_so_a_stalled_stream_never_runs_the_car_away()
    {
        // 1 s elapsed at 20 m/s would be +20 m; the 250 ms ceiling holds it at +5 m.
        Assert.Equal(100f + 20f * DeadReckoning.MaxExtrapolationSeconds,
                     DeadReckoning.ExtrapolateS(s: 100f, v: 20f, elapsedSeconds: 1.0f), 4);
    }

    [Fact]
    public void A_stopped_car_never_drifts()
    {
        Assert.Equal(100f, DeadReckoning.ExtrapolateS(s: 100f, v: 0f, elapsedSeconds: 5.0f), 4);
    }

    [Fact]
    public void A_negative_elapsed_clock_skew_never_rewinds()
    {
        Assert.Equal(100f, DeadReckoning.ExtrapolateS(s: 100f, v: 20f, elapsedSeconds: -0.5f), 4);
    }

    [Fact]
    public void An_explicit_cap_is_respected()
    {
        Assert.Equal(100f + 20f * 0.05f,
                     DeadReckoning.ExtrapolateS(s: 100f, v: 20f, elapsedSeconds: 1.0f, capSeconds: 0.05f), 4);
    }
}
