namespace ZTX16;

/// <summary>
/// The small platform-facing Machine API: time, exit and suspend requests.
/// Machine does not duplicate subsystem state.
/// </summary>
public sealed class Machine
{
    private const int ThroughputWindowSeconds = 1;

    private readonly Queue<TimeSpan> _recentTickTimes = new();

    internal Machine()
    {
    }

    /// <summary>Number of simulation ticks elapsed since session start.</summary>
    public long TickCount { get; private set; }

    /// <summary>Real wall-clock time elapsed since session start.</summary>
    public TimeSpan ElapsedTime { get; private set; }

    /// <summary>Measured simulation throughput over the last second (nominal 60).</summary>
    public double TicksPerSecond { get; private set; }

    /// <summary>True once the cartridge has requested exit.</summary>
    internal bool ExitRequested { get; private set; }

    /// <summary>True while a suspend request is pending for the runtime to consider.</summary>
    internal bool SuspendRequested { get; set; }

    /// <summary>Requests session termination. The runtime decides when it happens.</summary>
    public void Exit() => ExitRequested = true;

    /// <summary>
    /// Requests session suspension. The runtime may honor or ignore the request;
    /// resume is never cartridge-controlled.
    /// </summary>
    public void Suspend() => SuspendRequested = true;

    /// <summary>Records the start of a tick at wall-clock time <paramref name="elapsed"/>.</summary>
    internal void BeginTick(TimeSpan elapsed)
    {
        ElapsedTime = elapsed;

        _recentTickTimes.Enqueue(elapsed);
        TimeSpan windowStart = elapsed - TimeSpan.FromSeconds(ThroughputWindowSeconds);
        while (_recentTickTimes.Count > 0 && _recentTickTimes.Peek() <= windowStart)
        {
            _recentTickTimes.Dequeue();
        }

        TicksPerSecond = elapsed.TotalSeconds >= ThroughputWindowSeconds
            ? _recentTickTimes.Count / (double)ThroughputWindowSeconds
            : _recentTickTimes.Count / Math.Max(elapsed.TotalSeconds, 1e-9);
    }

    /// <summary>Records the end of a tick.</summary>
    internal void EndTick() => TickCount++;
}
