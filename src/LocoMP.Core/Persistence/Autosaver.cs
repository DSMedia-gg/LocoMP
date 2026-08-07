using System;
using LocoMP.Core.Session;

namespace LocoMP.Core.Persistence;

/// <summary>
/// Interval-driven autosave (03 §7), shared by both frontends: the host loop and the dedicated
/// server just call <see cref="Tick"/> every frame/poll. Clock-driven like everything else in the
/// session so tests advance time by hand; capture is a delegate so this class needs to know
/// nothing about what a save contains.
/// </summary>
public sealed class Autosaver
{
    private readonly IClock _clock;
    private long _intervalMs;
    private readonly ISaveStorage _storage;
    private readonly Func<byte[]> _capture;
    private long _nextDueMs;

    public Autosaver(IClock clock, long intervalMs, ISaveStorage storage, Func<byte[]> capture)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        if (intervalMs < 1) throw new ArgumentOutOfRangeException(nameof(intervalMs));
        _intervalMs = intervalMs;
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _capture = capture ?? throw new ArgumentNullException(nameof(capture));
        _nextDueMs = _clock.NowMs + intervalMs;
    }

    public long SavesWritten { get; private set; }

    /// <summary>The live interval (M5.2 session control reads it back for the panel).</summary>
    public long IntervalMs => _intervalMs;

    /// <summary>Retune the cadence live (M5.2 session control). The next save is re-scheduled from
    /// NOW at the new interval — shortening it never fires an immediate save (a fat-fingered slider
    /// mustn't thrash the disk), lengthening it never lets a stale short deadline linger.</summary>
    public void SetInterval(long intervalMs)
    {
        if (intervalMs < 1) throw new ArgumentOutOfRangeException(nameof(intervalMs));
        _intervalMs = intervalMs;
        _nextDueMs = _clock.NowMs + intervalMs;
    }

    /// <summary>Saves that threw instead of writing (unwritable path, disk full, AV lock).</summary>
    public long SaveFailures { get; private set; }

    /// <summary>The most recent failure, kept for postmortem even after a later success.</summary>
    public Exception? LastSaveError { get; private set; }

    /// <summary>A save attempt threw. Fired for BOTH the interval path and SaveNow, so a frontend
    /// subscribes once and every failure is logged — a save failure must be a logged error, never
    /// an unhandled crash out of the tick loop or the shutdown path (the real-exe finding: an
    /// unwritable --save killed the server with an UnauthorizedAccessException on the way down).</summary>
    public event Action<Exception>? SaveFailed;

    /// <summary>Write a save if the interval elapsed. Cheap no-op otherwise. Never throws — a
    /// failing disk is retried on the autosave cadence, not hammered every tick.</summary>
    public void Tick()
    {
        if (_clock.NowMs < _nextDueMs) return;
        TrySave();
    }

    /// <summary>Save right now (session end, world unload) and restart the interval. Returns
    /// whether the bytes actually reached storage — callers gate their "saved" message on it, so a
    /// failure can't masquerade as success in a log a runbook greps.</summary>
    public bool SaveNow() => TrySave();

    private bool TrySave()
    {
        // The interval restarts on failure too: the next attempt is one autosave period away.
        _nextDueMs = _clock.NowMs + _intervalMs;
        try
        {
            _storage.Save(_capture());
        }
        catch (Exception e)
        {
            SaveFailures++;
            LastSaveError = e;
            SaveFailed?.Invoke(e);
            return false;
        }
        SavesWritten++;
        return true;
    }
}
