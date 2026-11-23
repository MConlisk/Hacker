/// Time on the world calendar (Document 7 §13.8). Instants count nanoseconds from the campaign epoch,
/// Friday 17 November 2056 23:42:00 (world time, no time zones or leap seconds); DateTime is the calendar view.
module Std.Time;

enum Weekday { Monday, Tuesday, Wednesday, Thursday, Friday, Saturday, Sunday }

/// A calendar date and time with millisecond precision (proleptic Gregorian).
class DateTime : Comparable<DateTime>, Equatable<DateTime>, Hashable {
    DateTime(int year, int month, int day, int hour, int minute, int second);
    /// The campaign epoch (Instant 0).
    static DateTime Epoch { get; }
    static DateTime FromInstant(Instant instant);
    /// ISO 8601: "2056-11-14T23:42:00", optional ".fff" and "Z"; a bare date means midnight.
    static DateTime Parse(string text);
    static DateTime? TryParse(string text);
    int Year { get; }
    int Month { get; }
    int Day { get; }
    int Hour { get; }
    int Minute { get; }
    int Second { get; }
    int Millisecond { get; }
    Weekday DayOfWeek { get; }
    int DayOfYear { get; }
    /// Midnight at the start of the same day.
    DateTime Date { get; }
    DateTime Add(Duration duration);
    DateTime AddDays(int days);
    DateTime AddMonths(int months);
    /// this − other.
    Duration Subtract(DateTime other);
    Instant ToInstant();
    /// Custom format with yyyy MM dd HH mm ss fff ddd MMM tokens: Format("dd MMM yyyy HH:mm").
    string Format(string pattern);
    /// ISO 8601 without fractional seconds: "2056-11-14T23:42:00".
    string ToString();
    int CompareTo(DateTime other);
    bool Equals(DateTime other);
    int HashCode();
}

// ---------------------------------------------------------------- Instant and Duration arithmetic (Time.Add(…), …)

Instant Add(Instant instant, Duration duration);
Duration Between(Instant from, Instant to);
/// "1h30m", "250ms", "2d4h", "1.5s", "90" (seconds).
Duration ParseDuration(string text);
/// "1h 30m 5s" style.
string FormatDuration(Duration duration);
Duration Scale(Duration duration, float factor);

/// Measures elapsed simulated time.
class Stopwatch {
    Stopwatch();
    /// A stopwatch that is already running.
    static Stopwatch StartNew();
    bool IsRunning { get; }
    Duration Elapsed { get; }
    void Start();
    void Stop();
    void Reset();
    void Restart();
    string ToString();
}

/// A five-field cron schedule: minute hour day-of-month month day-of-week, with *, lists, ranges, steps
/// and names (JAN–DEC, SUN–SAT; 0 and 7 are Sunday). Used by Proc.Schedule.
class Cron {
    static Cron Parse(string expression);
    string Expression { get; }
    bool Matches(DateTime time);
    /// The first matching minute strictly after the given time.
    DateTime Next(DateTime after);
    DateTime[] NextN(DateTime after, int count);
    string ToString();
}
