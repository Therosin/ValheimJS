using System;
using System.Diagnostics;

namespace ValheimJS.Scripting.Binding;

/// <summary>
/// One cadence for every binding. The host already ticks bindings on the game thread; a
/// <see cref="Ticker"/> just decides whether this pump is due, using real elapsed time and
/// evaluating at most once per pump (no catch-up after a stall).
/// <para>
/// First <see cref="Due"/> call only starts the clock, mirroring the existing observer semantics,
/// so a binding installed mid-session does not fire immediately.
/// </para>
/// </summary>
public sealed class Ticker
{
    private readonly Stopwatch _clock = new();
    private readonly double _intervalMs;
    private long _lastTimestamp;
    private double _elapsedMs;

    public Ticker(double intervalMs)
    {
        if (double.IsNaN(intervalMs) || intervalMs <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(intervalMs), "ticker interval must be a positive number of milliseconds");
        }

        _intervalMs = intervalMs;
    }

    public double IntervalMs => _intervalMs;

    /// <summary>True when the interval has elapsed; consumes the whole accumulated window.</summary>
    public bool Due()
    {
        var deltaMs = Sample();
        if (deltaMs <= 0)
        {
            return false;
        }

        _elapsedMs += deltaMs;
        if (_elapsedMs < _intervalMs)
        {
            return false;
        }

        _elapsedMs = 0;
        return true;
    }

    /// <summary>
    /// Milliseconds since the previous sample; the first call starts the clock and returns 0.
    /// Bindings that advance work themselves (observers, pathing checks) use this instead of
    /// <see cref="Due"/>.
    /// </summary>
    public double Sample()
    {
        if (!_clock.IsRunning)
        {
            _clock.Start();
            _lastTimestamp = 0;
            return 0;
        }

        var timestamp = _clock.ElapsedTicks;
        var deltaMs = (timestamp - _lastTimestamp) / (double)Stopwatch.Frequency * 1000d;
        _lastTimestamp = timestamp;
        return deltaMs > 0 ? deltaMs : 0;
    }

    public void Reset()
    {
        _elapsedMs = 0;
        _lastTimestamp = _clock.IsRunning ? _clock.ElapsedTicks : 0;
    }
}
