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
    private readonly long _intervalMs;
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

    /// <summary>Write a save if the interval elapsed. Cheap no-op otherwise.</summary>
    public void Tick()
    {
        if (_clock.NowMs < _nextDueMs) return;
        _storage.Save(_capture());
        _nextDueMs = _clock.NowMs + _intervalMs;
        SavesWritten++;
    }

    /// <summary>Save right now (session end, world unload) and restart the interval.</summary>
    public void SaveNow()
    {
        _storage.Save(_capture());
        _nextDueMs = _clock.NowMs + _intervalMs;
        SavesWritten++;
    }
}
