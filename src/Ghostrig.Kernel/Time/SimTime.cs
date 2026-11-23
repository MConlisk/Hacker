namespace Ghostrig.Kernel.Time;

/// <summary>
/// A span of simulated time in integer nanoseconds. Simulation time is never derived from the
/// host wall clock (Doc 3 §2.2) and is represented with integers so it is bit-identical on every platform.
/// </summary>
public readonly record struct SimDuration(long Nanoseconds) : IComparable<SimDuration>
{
    public const long NanosPerMicrosecond = 1_000;
    public const long NanosPerMillisecond = 1_000_000;
    public const long NanosPerSecond = 1_000_000_000;

    public static readonly SimDuration Zero = new(0);

    public static SimDuration FromMicroseconds(long value) => new(checked(value * NanosPerMicrosecond));
    public static SimDuration FromMilliseconds(long value) => new(checked(value * NanosPerMillisecond));
    public static SimDuration FromSeconds(long value) => new(checked(value * NanosPerSecond));

    public double TotalSeconds => Nanoseconds / (double)NanosPerSecond;

    public static SimDuration operator +(SimDuration a, SimDuration b) => new(checked(a.Nanoseconds + b.Nanoseconds));
    public static SimDuration operator -(SimDuration a, SimDuration b) => new(checked(a.Nanoseconds - b.Nanoseconds));
    public static bool operator <(SimDuration a, SimDuration b) => a.Nanoseconds < b.Nanoseconds;
    public static bool operator >(SimDuration a, SimDuration b) => a.Nanoseconds > b.Nanoseconds;
    public static bool operator <=(SimDuration a, SimDuration b) => a.Nanoseconds <= b.Nanoseconds;
    public static bool operator >=(SimDuration a, SimDuration b) => a.Nanoseconds >= b.Nanoseconds;

    public int CompareTo(SimDuration other) => Nanoseconds.CompareTo(other.Nanoseconds);

    public override string ToString() => $"{TotalSeconds:0.000000}s";
}

/// <summary>An instant on the simulation clock, measured in nanoseconds since the campaign epoch.</summary>
public readonly record struct SimTime(long Nanoseconds) : IComparable<SimTime>
{
    public static readonly SimTime Epoch = new(0);

    public static SimTime operator +(SimTime t, SimDuration d) => new(checked(t.Nanoseconds + d.Nanoseconds));
    public static SimDuration operator -(SimTime a, SimTime b) => new(checked(a.Nanoseconds - b.Nanoseconds));
    public static bool operator <(SimTime a, SimTime b) => a.Nanoseconds < b.Nanoseconds;
    public static bool operator >(SimTime a, SimTime b) => a.Nanoseconds > b.Nanoseconds;
    public static bool operator <=(SimTime a, SimTime b) => a.Nanoseconds <= b.Nanoseconds;
    public static bool operator >=(SimTime a, SimTime b) => a.Nanoseconds >= b.Nanoseconds;

    public int CompareTo(SimTime other) => Nanoseconds.CompareTo(other.Nanoseconds);

    public override string ToString() => $"T+{Nanoseconds}ns";
}
