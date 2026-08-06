using LocoMP.Core.Trains;
using Xunit;

namespace LocoMP.Core.Tests;

/// <summary>
/// The reconcile poll's decision kernel (2026-08-06 finding #3). Pins the split the Shim relies on:
/// a set with no data recovers fast and logs once; a baselined-but-unspawned parked set polls slowly
/// and SILENTLY, so it can no longer spam "requesting a baseline replay" and re-pull a full replay
/// every 10 s forever.
/// </summary>
public class BaselineReplayPolicyTests
{
    private const float Stale = 5f;

    [Fact]
    public void A_spawned_set_never_requests()
    {
        var d = BaselineReplayPolicy.Evaluate(
            spawned: true, everBaselined: true, secondsSinceSnapshot: 999f,
            staleAfter: Stale, now: 100f, nextAllowed: 0f, alreadyLogged: false);
        Assert.False(d.Request);
    }

    [Fact]
    public void A_live_stream_is_left_to_Apply()
    {
        // Under the stale threshold — snapshots are still flowing, so the poll stands down.
        var d = BaselineReplayPolicy.Evaluate(
            spawned: false, everBaselined: false, secondsSinceSnapshot: 1f,
            staleAfter: Stale, now: 100f, nextAllowed: 0f, alreadyLogged: false);
        Assert.False(d.Request);
    }

    [Fact]
    public void The_per_set_throttle_holds_off_a_retry()
    {
        var d = BaselineReplayPolicy.Evaluate(
            spawned: false, everBaselined: false, secondsSinceSnapshot: 999f,
            staleAfter: Stale, now: 100f, nextAllowed: 105f, alreadyLogged: false);
        Assert.False(d.Request);
    }

    [Fact]
    public void A_never_baselined_stale_set_requests_and_logs_once_at_the_fast_cadence()
    {
        var d = BaselineReplayPolicy.Evaluate(
            spawned: false, everBaselined: false, secondsSinceSnapshot: 999f,
            staleAfter: Stale, now: 100f, nextAllowed: 0f, alreadyLogged: false);
        Assert.True(d.Request);
        Assert.True(d.Log);
        Assert.Equal(100f + BaselineReplayPolicy.FreshRetrySeconds, d.NextAllowed, 4);
    }

    [Fact]
    public void A_never_baselined_retry_still_requests_but_stays_quiet()
    {
        // The recovery keeps trying, but only the first request is announced.
        var d = BaselineReplayPolicy.Evaluate(
            spawned: false, everBaselined: false, secondsSinceSnapshot: 999f,
            staleAfter: Stale, now: 100f, nextAllowed: 0f, alreadyLogged: true);
        Assert.True(d.Request);
        Assert.False(d.Log);
    }

    [Fact]
    public void A_baselined_parked_set_polls_silently_at_the_slow_cadence()
    {
        // The finding-#3 case: position data in hand, still unspawned (parked/far). It must NOT log —
        // even on its very first poll — and must back off to the gentle keep-alive interval.
        var d = BaselineReplayPolicy.Evaluate(
            spawned: false, everBaselined: true, secondsSinceSnapshot: 999f,
            staleAfter: Stale, now: 100f, nextAllowed: 0f, alreadyLogged: false);
        Assert.True(d.Request);
        Assert.False(d.Log);
        Assert.Equal(100f + BaselineReplayPolicy.BaselinedPollSeconds, d.NextAllowed, 4);
    }

    [Fact]
    public void The_baselined_keep_alive_is_slower_than_the_fresh_recovery()
    {
        // A guard on the two cadences so a future edit can't silently collapse them back together.
        Assert.True(BaselineReplayPolicy.BaselinedPollSeconds > BaselineReplayPolicy.FreshRetrySeconds);
    }
}
