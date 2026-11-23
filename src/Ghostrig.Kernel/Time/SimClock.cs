namespace Ghostrig.Kernel.Time;

/// <summary>
/// The deterministic simulation clock. It only moves forward, and only when the kernel bills work or
/// idles; it never pauses because of UI state (Doc 1 §4.3, Doc 3 §2.2).
/// </summary>
public sealed class SimClock
{
    public SimClock(SimTime start = default)
    {
        if (start.Nanoseconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(start), "Simulation time cannot be negative.");
        }

        Now = start;
    }

    public SimTime Now { get; private set; }

    /// <summary>Advances the clock by a non-negative duration and returns the new instant.</summary>
    public SimTime Advance(SimDuration duration)
    {
        if (duration.Nanoseconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), "The simulation clock cannot run backwards.");
        }

        Now += duration;
        return Now;
    }
}
