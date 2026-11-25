namespace ZTX16.Platform.Runtime;

/// <summary>
/// Decides when simulation ticks run at the nominal 60 Hz rate.
/// </summary>
/// <remarks>
/// No catch-up: if a tick overruns, the schedule restarts from the moment the late
/// tick began instead of running extra ticks to recover lost time. The simulation
/// therefore slows down rather than skipping or bursting.
/// </remarks>
internal sealed class TickScheduler
{
    /// <summary>Nominal tick rate.</summary>
    public const int NominalTicksPerSecond = 60;

    private readonly long _ticksPerSecondOfClock;
    private readonly long _period;
    private long _nextTickTime;
    private bool _started;

    /// <param name="clockFrequency">Clock units per second (e.g. Stopwatch.Frequency).</param>
    public TickScheduler(long clockFrequency)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(clockFrequency, NominalTicksPerSecond);
        _ticksPerSecondOfClock = clockFrequency;
        _period = clockFrequency / NominalTicksPerSecond;
    }

    /// <summary>Length of one tick in clock units.</summary>
    public long Period => _period;

    /// <summary>Returns true if a tick is due at clock time <paramref name="now"/>.</summary>
    public bool IsTickDue(long now)
    {
        if (!_started)
        {
            _started = true;
            _nextTickTime = now;
        }

        return now >= _nextTickTime;
    }

    /// <summary>
    /// Records that a due tick started at <paramref name="startedAt"/> and schedules the next one.
    /// </summary>
    public void TickStarted(long startedAt)
    {
        // Lateness under one period is ordinary scheduling jitter: keep the cadence.
        // A full period or more means an overrun: drop the debt so no catch-up ticks follow.
        bool overrun = startedAt - _nextTickTime >= _period;
        _nextTickTime = (overrun ? startedAt : _nextTickTime) + _period;
    }

    /// <summary>Clock units remaining until the next tick (0 if one is due).</summary>
    public long TimeUntilNextTick(long now) => Math.Max(0, _nextTickTime - now);

    /// <summary>Converts clock units to a <see cref="TimeSpan"/>.</summary>
    public TimeSpan ToTimeSpan(long clockUnits) =>
        TimeSpan.FromTicks(clockUnits * TimeSpan.TicksPerSecond / _ticksPerSecondOfClock);
}
