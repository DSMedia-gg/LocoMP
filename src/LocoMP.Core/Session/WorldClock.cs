namespace LocoMP.Core.Session;

/// <summary>
/// The server's authoritative WORLD time-of-day (02 §3 — distinct from <see cref="IClock"/>, the
/// monotonic session clock). Holds the last committed world time as an OADate (the same DateTime
/// encoding DV's own save uses) plus the day length, and FLOWS it forward at the world rate between
/// commits — a full 24 h day passes in <see cref="DayLengthMinutes"/> real minutes — so a
/// restatement is always current no matter when it is read. In host-embedded mode the host's
/// reports re-anchor it (the host's sky is the truth and flows at the same rate, so drift between
/// reports is bounded by clock skew alone); a dedicated server anchors it once at startup and lets
/// it flow. <see cref="Freeze"/> pins it for D19's host pause: a paused host's sky stops, so the
/// server's derivation must stop with it or every restatement would run ahead of the truth.
/// </summary>
public sealed class WorldClock
{
    /// <summary>DV's stock day length (TOD_Time.initialDayLengthInMinutes): 24 world-hours in 30
    /// real minutes.</summary>
    public const float DefaultDayLengthMinutes = 30f;

    private const double SecondsPerDay = 86400.0;

    private readonly IClock _clock;
    private double _baseOa;       // world time at the anchor …
    private long _baseMs;         // … which was current at this session-clock instant
    private float _dayLengthMinutes = DefaultDayLengthMinutes;
    private bool _frozen;

    public WorldClock(IClock clock)
    {
        _clock = clock ?? throw new System.ArgumentNullException(nameof(clock));
    }

    /// <summary>Has a time ever been committed? Until then there is nothing to broadcast (a
    /// host-embedded session before the first report, which lands with the world registration).</summary>
    public bool HasValue { get; private set; }

    /// <summary>Is the flow pinned (D19 host pause)?</summary>
    public bool Frozen => _frozen;

    /// <summary>Real minutes per full world day. 0 never happens: <see cref="Set"/> rejects it.</summary>
    public float DayLengthMinutes => _dayLengthMinutes;

    /// <summary>How many world seconds pass per real second (1440 world-minutes spread over
    /// <see cref="DayLengthMinutes"/> real minutes — stock 30 → 48×).</summary>
    public double WorldSecondsPerRealSecond => 1440.0 / _dayLengthMinutes;

    /// <summary>The current world time as an OADate — the anchor advanced at the world rate, or the
    /// pinned value while frozen. 0 until the first <see cref="Set"/>.</summary>
    public double CurrentOa
    {
        get
        {
            if (!HasValue) return 0;
            if (_frozen) return _baseOa;
            double elapsedRealSeconds = (_clock.NowMs - _baseMs) / 1000.0;
            return _baseOa + elapsedRealSeconds * (1440.0 / _dayLengthMinutes) / SecondsPerDay;
        }
    }

    /// <summary>Commit an authoritative world time + day length (a world-source report, or the
    /// dedicated server's startup anchor). Rejects a non-positive day length (a corrupt report must
    /// not stall the sky). Returns false when rejected.</summary>
    public bool Set(double oaDate, float dayLengthMinutes)
    {
        if (!(dayLengthMinutes > 0f) || double.IsNaN(oaDate) || double.IsInfinity(oaDate)) return false;
        _baseOa = oaDate;
        _baseMs = _clock.NowMs;
        _dayLengthMinutes = dayLengthMinutes;
        HasValue = true;
        return true;
    }

    /// <summary>Pin the flow at the current value (D19: the host's sky stopped). Idempotent.</summary>
    public void Freeze()
    {
        if (_frozen) return;
        _baseOa = CurrentOa;      // latch before the flag flips the derivation
        _baseMs = _clock.NowMs;
        _frozen = true;
    }

    /// <summary>Resume the flow from the pinned value. Idempotent.</summary>
    public void Unfreeze()
    {
        if (!_frozen) return;
        _baseMs = _clock.NowMs;   // rebase: no time passed while frozen
        _frozen = false;
    }
}
