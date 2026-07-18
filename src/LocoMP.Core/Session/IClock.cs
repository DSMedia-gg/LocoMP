using System.Diagnostics;

namespace LocoMP.Core.Session;

/// <summary>
/// Monotonic millisecond clock. The server owns the authoritative time (03 §5); clients keep an
/// NTP-style offset against it. Abstracted so session tests advance time by hand and stay
/// deterministic (03 §11) — no wall-clock, no threads.
/// </summary>
public interface IClock
{
    long NowMs { get; }
}

/// <summary>Real monotonic clock for live sessions — Stopwatch, immune to system-clock changes.</summary>
public sealed class SystemClock : IClock
{
    private readonly Stopwatch _sw = Stopwatch.StartNew();
    public long NowMs => _sw.ElapsedMilliseconds;
}

/// <summary>Manually-advanced clock for tests; time only moves when the test moves it.</summary>
public sealed class ManualClock : IClock
{
    public long NowMs { get; set; }
    public void Advance(long ms) => NowMs += ms;
}
