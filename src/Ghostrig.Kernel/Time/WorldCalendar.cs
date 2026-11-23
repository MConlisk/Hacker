using System.Globalization;

namespace Ghostrig.Kernel.Time;

/// <summary>
/// Maps simulation instants onto the in-world calendar (e.g. November 2056) for log timestamps and UI.
/// Pure presentation: it never feeds back into simulation state.
/// </summary>
public sealed class WorldCalendar
{
    public WorldCalendar(DateTime epochUtc)
    {
        if (epochUtc.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("The world epoch must be expressed in UTC.", nameof(epochUtc));
        }

        EpochUtc = epochUtc;
    }

    /// <summary>Default campaign epoch: Friday 17 November 2056, 23:42 — seventy-two hours after Wren vanished at 23:42 on Tuesday 14 November.</summary>
    public static WorldCalendar Default { get; } = new(new DateTime(2056, 11, 17, 23, 42, 0, DateTimeKind.Utc));

    public DateTime EpochUtc { get; }

    public DateTime ToWorldTime(SimTime time) => EpochUtc.AddTicks(time.Nanoseconds / 100);

    /// <summary>Formats an instant as a structured-log timestamp: <c>2056-11-17T23:42:13</c>.</summary>
    public string FormatTimestamp(SimTime time) =>
        ToWorldTime(time).ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
}
