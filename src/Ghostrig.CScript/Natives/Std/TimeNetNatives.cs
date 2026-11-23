using System.Globalization;
using System.Text;
using Ghostrig.CScript.Runtime;
using Ghostrig.CScript.Vm;
using static Ghostrig.CScript.Natives.Std.StdSupport;

namespace Ghostrig.CScript.Natives.Std;

#region States

internal sealed class DateState(long ticks) : ImmutableNativeState, IStructuralState
{
    /// <summary>.NET ticks (100 ns since 0001-01-01), used purely as integer arithmetic on the proleptic calendar.</summary>
    public long Ticks { get; } = ticks;
    public DateTime Value => new(Ticks, DateTimeKind.Unspecified);
    public bool StructurallyEquals(IStructuralState other) => other is DateState d && d.Ticks == Ticks;
    public long StructuralHash() => (long)Mix((ulong)Ticks);
    public int? StructuralCompare(IStructuralState other) => other is DateState d ? Ticks.CompareTo(d.Ticks) : null;
}

internal sealed class StopwatchState : NativeState
{
    public long Accumulated { get; set; }
    public long? StartedAt { get; set; }

    protected internal override object CaptureSnapshot() => (Accumulated, StartedAt);

    protected internal override void RestoreSnapshot(object snapshot) => (Accumulated, StartedAt) = ((long, long?))snapshot;
}

internal sealed record CronState(string Expression, bool[] Minutes, bool[] Hours, bool[] Days, bool[] Months, bool[] Weekdays, bool AnyDay, bool AnyWeekday);

internal sealed class IpState(uint address) : ImmutableNativeState, IStructuralState
{
    public uint Address { get; } = address;
    public bool StructurallyEquals(IStructuralState other) => other is IpState o && o.Address == Address;
    public long StructuralHash() => (long)Mix(Address);
    public int? StructuralCompare(IStructuralState other) => other is IpState o ? Address.CompareTo(o.Address) : null;
    public override string ToString() => $"{Address >> 24}.{(Address >> 16) & 255}.{(Address >> 8) & 255}.{Address & 255}";
}

internal sealed class CidrState(uint network, int prefix) : ImmutableNativeState, IStructuralState
{
    public uint Mask { get; } = prefix == 0 ? 0 : uint.MaxValue << (32 - prefix);
    public uint Network { get; } = network & (prefix == 0 ? 0 : uint.MaxValue << (32 - prefix));
    public int Prefix { get; } = prefix;
    public uint Broadcast => Network | ~Mask;
    public bool Contains(uint address) => (address & Mask) == Network;
    public bool StructurallyEquals(IStructuralState other) => other is CidrState o && o.Network == Network && o.Prefix == Prefix;
    public long StructuralHash() => (long)Mix(((ulong)Network << 8) | (uint)Prefix);
    public override string ToString() => $"{new IpState(Network)}/{Prefix}";
}

internal sealed class MacState(byte[] bytes) : ImmutableNativeState, IStructuralState
{
    public byte[] Bytes { get; } = bytes;
    public bool StructurallyEquals(IStructuralState other) => other is MacState o && o.Bytes.AsSpan().SequenceEqual(Bytes);
    public long StructuralHash() => StableHash.Of(Bytes);
    public override string ToString() => string.Join(":", Bytes.Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));
}

internal sealed record UrlState(string Scheme, string User, string Host, int ExplicitPort, string Path, string Query, string Fragment) : IStructuralState
{
    public int Port => ExplicitPort >= 0 ? ExplicitPort : Scheme switch { "http" => 80, "https" => 443, "ftp" => 21, "ssh" => 22, _ => -1 };

    public IEnumerable<(string Name, string Value)> Params() =>
        Query.Split('&', StringSplitOptions.RemoveEmptyEntries).Select(p =>
        {
            var eq = p.IndexOf('=', StringComparison.Ordinal);
            return eq < 0 ? (Uri.UnescapeDataString(p), string.Empty) : (Uri.UnescapeDataString(p[..eq]), Uri.UnescapeDataString(p[(eq + 1)..].Replace('+', ' ')));
        });

    public override string ToString() =>
        $"{Scheme}://{(User.Length > 0 ? User + "@" : string.Empty)}{Host}{(ExplicitPort >= 0 ? ":" + ExplicitPort.ToString(CultureInfo.InvariantCulture) : string.Empty)}{Path}{(Query.Length > 0 ? "?" + Query : string.Empty)}{(Fragment.Length > 0 ? "#" + Fragment : string.Empty)}";

    public bool StructurallyEquals(IStructuralState other) => other is UrlState u && u.ToString() == ToString();
    public long StructuralHash() => StableHash.Of(ToString());
}

internal sealed record HeadersState((string Name, string Value)[] Fields);

internal sealed record Ipv4State(uint Source, uint Destination, int Protocol, int Ttl, int Identification, byte[] Payload, bool ChecksumValid);

#endregion

/// <summary>Host implementations of <c>Std.Time</c>, <c>Std.Path</c> and <c>Std.Net</c>.</summary>
internal static class TimeNetNatives
{
    private static readonly long EpochTicks = new DateTime(2056, 11, 17, 23, 42, 0, DateTimeKind.Unspecified).Ticks;
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static void Register(NativeRegistry r)
    {
        RegisterDateTime(r.Scope("Std.Time.DateTime"));
        RegisterTimeFunctions(r.Scope("Std.Time"));
        RegisterStopwatch(r.Scope("Std.Time.Stopwatch"));
        RegisterCron(r.Scope("Std.Time.Cron"));
        RegisterPath(r.Scope("Std.Path"));
        RegisterIp(r);
        RegisterNetText(r);
        RegisterPackets(r);
    }

    #region Time

    private static Value Date(NativeContext c, long ticks) =>
        ticks >= DateTime.MinValue.Ticks && ticks <= DateTime.MaxValue.Ticks
            ? c.NewInstance(c.Class("Std.Time", "DateTime"), new DateState(ticks))
            : throw c.Throw("ArgumentError", "The date is outside years 1–9999.");

    private static DateState D(NativeContext c, Value v) => c.State<DateState>(v);

    private static DateTime? ParseIso(string text)
    {
        string[] formats = ["yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF", "yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd"];
        var trimmed = text.Trim().TrimEnd('Z');
        return DateTime.TryParseExact(trimmed, formats, Invariant, DateTimeStyles.None, out var value) ? value : null;
    }

    /// <summary>A whitelist formatter: only calendar tokens, so no culture or time-zone data can leak in.</summary>
    internal static string FormatDate(DateTime d, string pattern)
    {
        string[] months = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];
        string[] days = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];
        var sb = new StringBuilder();
        for (var i = 0; i < pattern.Length;)
        {
            int Run(char ch)
            {
                var n = 0;
                while (i + n < pattern.Length && pattern[i + n] == ch)
                {
                    n++;
                }

                return n;
            }

            var c = pattern[i];
            if (c == '\'')
            {
                var close = pattern.IndexOf('\'', i + 1);
                close = close < 0 ? pattern.Length : close;
                sb.Append(pattern, i + 1, close - i - 1);
                i = close + 1;
                continue;
            }

            var n = Run(c);
            var token = (c, n) switch
            {
                ('y', >= 4) => d.Year.ToString("D4", Invariant),
                ('y', _) => (d.Year % 100).ToString("D2", Invariant),
                ('M', >= 4) => months[d.Month - 1] switch { "May" => "May", var m => m },
                ('M', 3) => months[d.Month - 1],
                ('M', 2) => d.Month.ToString("D2", Invariant),
                ('M', _) => d.Month.ToString(Invariant),
                ('d', >= 4) => days[(int)d.DayOfWeek],
                ('d', 3) => days[(int)d.DayOfWeek][..3],
                ('d', 2) => d.Day.ToString("D2", Invariant),
                ('d', _) => d.Day.ToString(Invariant),
                ('H', >= 2) => d.Hour.ToString("D2", Invariant),
                ('H', _) => d.Hour.ToString(Invariant),
                ('m', >= 2) => d.Minute.ToString("D2", Invariant),
                ('m', _) => d.Minute.ToString(Invariant),
                ('s', >= 2) => d.Second.ToString("D2", Invariant),
                ('s', _) => d.Second.ToString(Invariant),
                ('f', _) => d.Millisecond.ToString("D3", Invariant)[..Math.Min(3, n)],
                _ => new string(c, n),
            };
            sb.Append(token);
            i += n;
        }

        return sb.ToString();
    }

    private static void RegisterDateTime(Action<string, StdFunction> F)
    {
        F("DateTime/6", (c, a) =>
        {
            try
            {
                return CollectionNatives.Init(a[0], new DateState(new DateTime((int)a[1].Bits, (int)a[2].Bits, (int)a[3].Bits, (int)a[4].Bits, (int)a[5].Bits, (int)a[6].Bits).Ticks));
            }
            catch (ArgumentOutOfRangeException)
            {
                throw c.Throw("ArgumentError", "Invalid date or time components.");
            }
        });
        F("get_Epoch/0", (c, _) => Date(c, EpochTicks));
        F("FromInstant/1", (c, a) => Date(c, EpochTicks + (a[0].Bits / 100)));
        F("Parse/1", (c, a) => ParseIso(a[0].AsString) is { } d ? Date(c, d.Ticks) : throw c.Throw("FormatError", $"'{a[0].AsString}' is not an ISO 8601 date/time."));
        F("TryParse/1", (c, a) => ParseIso(a[0].AsString) is { } d ? Date(c, d.Ticks) : Value.Null);
        F("get_Year/0", (c, a) => Value.FromInt(D(c, a[0]).Value.Year));
        F("get_Month/0", (c, a) => Value.FromInt(D(c, a[0]).Value.Month));
        F("get_Day/0", (c, a) => Value.FromInt(D(c, a[0]).Value.Day));
        F("get_Hour/0", (c, a) => Value.FromInt(D(c, a[0]).Value.Hour));
        F("get_Minute/0", (c, a) => Value.FromInt(D(c, a[0]).Value.Minute));
        F("get_Second/0", (c, a) => Value.FromInt(D(c, a[0]).Value.Second));
        F("get_Millisecond/0", (c, a) => Value.FromInt(D(c, a[0]).Value.Millisecond));
        F("get_DayOfWeek/0", (c, a) => Value.FromEnum(((int)D(c, a[0]).Value.DayOfWeek + 6) % 7, c.Class("Std.Time", "Weekday")));
        F("get_DayOfYear/0", (c, a) => Value.FromInt(D(c, a[0]).Value.DayOfYear));
        F("get_Date/0", (c, a) => Date(c, D(c, a[0]).Value.Date.Ticks));
        F("Add/1", (c, a) => Date(c, D(c, a[0]).Ticks + (a[1].Bits / 100)));
        F("AddDays/1", (c, a) => Date(c, D(c, a[0]).Ticks + (a[1].Bits * TimeSpan.TicksPerDay)));
        F("AddMonths/1", (c, a) =>
        {
            try
            {
                return Date(c, D(c, a[0]).Value.AddMonths((int)Math.Clamp(a[1].Bits, -120000, 120000)).Ticks);
            }
            catch (ArgumentOutOfRangeException)
            {
                throw c.Throw("ArgumentError", "The date is outside years 1–9999.");
            }
        });
        F("Subtract/1", (c, a) => Value.FromDuration((D(c, a[0]).Ticks - D(c, a[1]).Ticks) * 100));
        F("ToInstant/0", (c, a) => Value.FromInstant((D(c, a[0]).Ticks - EpochTicks) * 100));
        F("Format/1", (c, a) => Value.FromString(FormatDate(D(c, a[0]).Value, a[1].AsString)));
        F("ToString/0", (c, a) => Value.FromString(FormatDate(D(c, a[0]).Value, "yyyy-MM-dd'T'HH:mm:ss")));
        F("CompareTo/1", (c, a) => Value.FromInt(D(c, a[0]).Ticks.CompareTo(D(c, a[1]).Ticks)));
        F("Equals/1", (c, a) => Value.FromBool(D(c, a[0]).Ticks == D(c, a[1]).Ticks));
        F("HashCode/0", (c, a) => Value.FromInt(D(c, a[0]).StructuralHash()));
    }

    internal static long ParseDuration(NativeContext c, string text)
    {
        var s = text.Trim().Replace(" ", string.Empty, StringComparison.Ordinal);
        if (s.Length > 0 && s.All(ch => char.IsAsciiDigit(ch) || ch == '.'))
        {
            return (long)(decimal.Parse(s, Invariant) * 1_000_000_000m);
        }

        var total = 0m;
        var i = 0;
        var any = false;
        while (i < s.Length)
        {
            var start = i;
            while (i < s.Length && (char.IsAsciiDigit(s[i]) || s[i] == '.'))
            {
                i++;
            }

            var unitStart = i;
            while (i < s.Length && char.IsAsciiLetter(s[i]) || (i < s.Length && s[i] == 'µ'))
            {
                i++;
            }

            if (start == unitStart || unitStart == i || !decimal.TryParse(s.AsSpan(start, unitStart - start), NumberStyles.AllowDecimalPoint, Invariant, out var number))
            {
                throw c.Throw("FormatError", $"'{text}' is not a duration (e.g. 1h30m, 250ms).");
            }

            total += number * s[unitStart..i].ToLowerInvariant() switch
            {
                "d" => 86_400_000_000_000m,
                "h" => 3_600_000_000_000m,
                "m" or "min" => 60_000_000_000m,
                "s" => 1_000_000_000m,
                "ms" => 1_000_000m,
                "us" or "µs" => 1_000m,
                "ns" => 1m,
                var unit => throw c.Throw("FormatError", $"Unknown duration unit '{unit}'."),
            };
            any = true;
        }

        return any ? (long)total : throw c.Throw("FormatError", $"'{text}' is not a duration.");
    }

    internal static string FormatDuration(long ns)
    {
        if (ns == 0)
        {
            return "0s";
        }

        var parts = new List<string>();
        var rest = Math.Abs(ns);
        foreach (var (unit, size) in new[] { ("d", 86_400_000_000_000L), ("h", 3_600_000_000_000L), ("m", 60_000_000_000L), ("s", 1_000_000_000L), ("ms", 1_000_000L), ("µs", 1_000L), ("ns", 1L) })
        {
            if (rest >= size)
            {
                parts.Add($"{rest / size}{unit}");
                rest %= size;
            }
        }

        return (ns < 0 ? "-" : string.Empty) + string.Join(" ", parts);
    }

    private static void RegisterTimeFunctions(Action<string, StdFunction> G)
    {
        G("Add/2", (c, a) => Value.FromInstant(unchecked(a[0].Bits + a[1].Bits)));
        G("Between/2", (c, a) => Value.FromDuration(unchecked(a[1].Bits - a[0].Bits)));
        G("ParseDuration/1", (c, a) => Value.FromDuration(ParseDuration(c, a[0].AsString)));
        G("FormatDuration/1", (c, a) => Value.FromString(FormatDuration(a[0].Bits)));
        G("Scale/2", (c, a) => Value.FromDuration((long)Math.Round(a[0].Bits * MathNatives.D(a[1]))));
    }

    private static void RegisterStopwatch(Action<string, StdFunction> F)
    {
        static long Elapsed(NativeContext c, StopwatchState s) => s.Accumulated + (s.StartedAt is { } t ? c.Now - t : 0);
        static Value Edit(NativeContext c, Value v, Action<StopwatchState> edit) => Mutate<StopwatchState>(c, v, s => Void(() => edit(s)));
        F("Stopwatch/0", (c, a) => CollectionNatives.Init(a[0], new StopwatchState()));
        F("StartNew/0", (c, _) => c.NewInstance(c.Class("Std.Time", "Stopwatch"), new StopwatchState { StartedAt = c.Now }));
        F("get_IsRunning/0", (c, a) => Value.FromBool(c.State<StopwatchState>(a[0]).StartedAt is not null));
        F("get_Elapsed/0", (c, a) => Value.FromDuration(Elapsed(c, c.State<StopwatchState>(a[0]))));
        F("Start/0", (c, a) => Edit(c, a[0], s => s.StartedAt ??= c.Now));
        F("Stop/0", (c, a) => Edit(c, a[0], s =>
        {
            s.Accumulated = Elapsed(c, s);
            s.StartedAt = null;
        }));
        F("Reset/0", (c, a) => Edit(c, a[0], s => (s.Accumulated, s.StartedAt) = (0, null)));
        F("Restart/0", (c, a) => Edit(c, a[0], s => (s.Accumulated, s.StartedAt) = (0, c.Now)));
        F("ToString/0", (c, a) => Value.FromString(FormatDuration(Elapsed(c, c.State<StopwatchState>(a[0])))));
    }

    private static bool[] CronField(NativeContext c, string field, int min, int max, string[]? names = null)
    {
        var set = new bool[max + 1];
        foreach (var part in field.Split(','))
        {
            var (range, step) = part.Split('/') switch
            {
                [var x] => (x, 1),
                [var x, var y] when int.TryParse(y, Invariant, out var s) && s > 0 => (x, s),
                _ => throw c.Throw("FormatError", $"Invalid cron field '{field}'."),
            };
            int Value(string token)
            {
                var index = names is null ? -1 : System.Array.IndexOf(names, token.ToUpperInvariant());
                return index >= 0 ? index + min : int.TryParse(token, Invariant, out var v) && v >= min && v <= max
                    ? v
                    : throw c.Throw("FormatError", $"'{token}' is out of range {min}–{max} in cron field '{field}'.");
            }

            var (from, to) = range switch
            {
                "*" => (min, max),
                _ when range.Contains('-') => (Value(range.Split('-')[0]), Value(range.Split('-')[1])),
                _ => (Value(range), part.Contains('/') ? max : Value(range)),
            };
            for (var v = from; v <= to; v += step)
            {
                set[v] = true;
            }
        }

        return set;
    }

    /// <summary>Standard cron rule: when both day-of-month and day-of-week are restricted, either may match.</summary>
    private static bool DateMatches(CronState s, DateTime date)
    {
        var dayOk = s.Days[date.Day];
        var weekdayOk = s.Weekdays[(int)date.DayOfWeek];
        var dateOk = (s.AnyDay, s.AnyWeekday) switch
        {
            (true, true) => true,
            (true, false) => weekdayOk,
            (false, true) => dayOk,
            _ => dayOk || weekdayOk,
        };
        return dateOk && s.Months[date.Month];
    }

    private static bool CronMatches(CronState s, DateTime t) => DateMatches(s, t) && s.Hours[t.Hour] && s.Minutes[t.Minute];

    private static DateTime CronNext(NativeContext c, CronState s, DateTime after)
    {
        var start = new DateTime(after.Year, after.Month, after.Day, after.Hour, after.Minute, 0).AddMinutes(1);
        for (var day = 0; day < 366 * 8; day++)
        {
            c.Charge(1);
            var date = start.Date.AddDays(day);
            if (!DateMatches(s, date))
            {
                continue;
            }

            for (var minute = day == 0 ? (start.Hour * 60) + start.Minute : 0; minute < 1440; minute++)
            {
                if (s.Hours[minute / 60] && s.Minutes[minute % 60])
                {
                    return date.AddMinutes(minute);
                }
            }
        }

        throw c.Throw("ArgumentError", $"Cron '{s.Expression}' never fires.");
    }

    private static void RegisterCron(Action<string, StdFunction> F)
    {
        static CronState S(NativeContext c, Value v) => c.State<CronState>(v);
        F("Parse/1", (c, a) =>
        {
            var fields = a[0].AsString.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length != 5)
            {
                throw c.Throw("FormatError", "A cron expression needs five fields: minute hour day month weekday.");
            }

            var weekdays = CronField(c, fields[4], 0, 7, ["SUN", "MON", "TUE", "WED", "THU", "FRI", "SAT"]);
            weekdays[0] |= weekdays[7];
            var state = new CronState(a[0].AsString.Trim(), CronField(c, fields[0], 0, 59), CronField(c, fields[1], 0, 23),
                CronField(c, fields[2], 1, 31), CronField(c, fields[3], 1, 12, ["JAN", "FEB", "MAR", "APR", "MAY", "JUN", "JUL", "AUG", "SEP", "OCT", "NOV", "DEC"]),
                weekdays[..7], fields[2] == "*", fields[4] == "*");
            return c.NewInstance(c.Class("Std.Time", "Cron"), state);
        });
        F("get_Expression/0", (c, a) => Value.FromString(S(c, a[0]).Expression));
        F("Matches/1", (c, a) => Value.FromBool(CronMatches(S(c, a[0]), D(c, a[1]).Value)));
        F("Next/1", (c, a) => Date(c, CronNext(c, S(c, a[0]), D(c, a[1]).Value).Ticks));
        F("NextN/2", (c, a) =>
        {
            var times = new List<Value>();
            var t = D(c, a[1]).Value;
            for (var i = 0; i < Int32(c, a[2].Bits, "Count", 0, 10_000); i++)
            {
                t = CronNext(c, S(c, a[0]), t);
                times.Add(Date(c, t.Ticks));
            }

            return c.NewArray([.. times]);
        });
        F("ToString/0", (c, a) => Value.FromString($"Cron({S(c, a[0]).Expression})"));
    }

    #endregion

    #region Path

    /// <summary>The kernel's VfsPath rules, reproduced for scripts (the CScript assembly does not depend on the kernel).</summary>
    internal static string Normalize(NativeContext c, string path)
    {
        if (path.Length == 0 || path[0] != '/')
        {
            throw c.Throw("InvalidPath", $"'{path}': paths must be absolute (start with '/').");
        }

        var stack = new List<string>();
        foreach (var segment in path.Split('/'))
        {
            switch (segment)
            {
                case "" or ".":
                    continue;
                case "..":
                    if (stack.Count == 0)
                    {
                        throw c.Throw("InvalidPath", $"'{path}': '..' escapes the filesystem root.");
                    }

                    stack.RemoveAt(stack.Count - 1);
                    continue;
                default:
                    if (segment.Length > 255 || segment.Any(ch => ch == '\\' || char.IsControl(ch)))
                    {
                        throw c.Throw("InvalidPath", $"'{path}': segment '{segment}' is not a valid name.");
                    }

                    stack.Add(segment);
                    continue;
            }
        }

        return "/" + string.Join('/', stack);
    }

    private static string Join(NativeContext c, string a, string b) => Normalize(c, b.StartsWith('/') ? b : a.TrimEnd('/') + "/" + b);

    private static void RegisterPath(Action<string, StdFunction> G)
    {
        static string Name(string normalized) => normalized == "/" ? string.Empty : normalized[(normalized.LastIndexOf('/') + 1)..];
        static string Ext(string name)
        {
            var dot = name.LastIndexOf('.');
            return dot <= 0 ? string.Empty : name[dot..].ToLowerInvariant();
        }

        static string[] Segments(string normalized) => normalized == "/" ? [] : normalized[1..].Split('/');
        G("Normalize/1", (c, a) => Value.FromString(Normalize(c, a[0].AsString)));
        G("IsValid/1", (c, a) =>
        {
            try
            {
                Normalize(c, a[0].AsString);
                return Value.True;
            }
            catch (ScriptErrorRequest)
            {
                return Value.False;
            }
        });
        G("Join/2", (c, a) => Value.FromString(Join(c, a[0].AsString, a[1].AsString)));
        G("Join/1", (c, a) => Value.FromString(Items(a[0]).Select(v => v.AsString).Aggregate("/", (acc, part) => Join(c, acc, part))));
        G("Resolve/2", (c, a) => Value.FromString(Join(c, a[0].AsString, a[1].AsString)));
        G("Parent/1", (c, a) =>
        {
            var p = Normalize(c, a[0].AsString);
            var i = p.LastIndexOf('/');
            return Value.FromString(i <= 0 ? "/" : p[..i]);
        });
        G("Name/1", (c, a) => Value.FromString(Name(Normalize(c, a[0].AsString))));
        G("Extension/1", (c, a) => Value.FromString(Ext(Name(Normalize(c, a[0].AsString)))));
        G("Stem/1", (c, a) =>
        {
            var name = Name(Normalize(c, a[0].AsString));
            var ext = Ext(name);
            return Value.FromString(name[..(name.Length - ext.Length)]);
        });
        G("ChangeExtension/2", (c, a) =>
        {
            var p = Normalize(c, a[0].AsString);
            var ext = Ext(Name(p));
            var replacement = a[1].AsString.Length == 0 || a[1].AsString.StartsWith('.') ? a[1].AsString : "." + a[1].AsString;
            return Value.FromString(p[..(p.Length - ext.Length)] + replacement);
        });
        G("IsHidden/1", (c, a) => Value.FromBool(Segments(Normalize(c, a[0].AsString)).Any(s => s.StartsWith('.'))));
        G("Segments/1", (c, a) => c.NewArray([.. Segments(Normalize(c, a[0].AsString)).Select(Value.FromString)]));
        G("IsAncestor/2", (c, a) =>
        {
            var (ancestor, path) = (Normalize(c, a[0].AsString), Normalize(c, a[1].AsString));
            return Value.FromBool(ancestor != path && (ancestor == "/" || path.StartsWith(ancestor + "/", StringComparison.Ordinal)));
        });
        G("Relative/2", (c, a) =>
        {
            var (from, to) = (Segments(Normalize(c, a[0].AsString)), Segments(Normalize(c, a[1].AsString)));
            var common = 0;
            while (common < from.Length && common < to.Length && from[common] == to[common])
            {
                common++;
            }

            var parts = Enumerable.Repeat("..", from.Length - common).Concat(to.Skip(common)).ToArray();
            return Value.FromString(parts.Length == 0 ? "." : string.Join('/', parts));
        });
        G("Glob/2", (c, a) => Value.FromBool(TextNatives.Compile(c, TextNatives.GlobToRegex(a[0].AsString, paths: true)).IsMatch(a[1].AsString)));
    }

    #endregion

    #region IP, CIDR, ports, hostnames, MAC

    private static Value Ip(NativeContext c, uint address) => c.NewInstance(c.Class("Std.Net", "IpAddress"), new IpState(address));

    private static uint IpOf(NativeContext c, Value v) => c.State<IpState>(v).Address;

    internal static uint? ParseIp(string text)
    {
        var parts = text.Trim().Split('.');
        if (parts.Length != 4)
        {
            return null;
        }

        uint value = 0;
        foreach (var part in parts)
        {
            if (part.Length is 0 or > 3 || !part.All(char.IsAsciiDigit) || (part.Length > 1 && part[0] == '0') || !byte.TryParse(part, Invariant, out var b))
            {
                return null;
            }

            value = (value << 8) | b;
        }

        return value;
    }

    private static readonly (int Port, string Name)[] Services =
    [
        (20, "ftp-data"), (21, "ftp"), (22, "ssh"), (23, "telnet"), (25, "smtp"), (53, "dns"), (67, "dhcp"), (69, "tftp"), (80, "http"),
        (110, "pop3"), (123, "ntp"), (143, "imap"), (161, "snmp"), (389, "ldap"), (443, "https"), (445, "smb"), (993, "imaps"), (995, "pop3s"),
        (1433, "mssql"), (1883, "mqtt"), (2121, "ftp-alt"), (3306, "mysql"), (3389, "rdp"), (5432, "postgres"), (5900, "vnc"), (6379, "redis"),
        (8080, "http-alt"), (8443, "mgmt-tls"),
    ];

    private static void RegisterIp(NativeRegistry r)
    {
        var F = r.Scope("Std.Net.IpAddress");
        F("Parse/1", (c, a) => ParseIp(a[0].AsString) is { } ip ? Ip(c, ip) : throw c.Throw("FormatError", $"'{a[0].AsString}' is not an IPv4 address."));
        F("TryParse/1", (c, a) => ParseIp(a[0].AsString) is { } ip ? Ip(c, ip) : Value.Null);
        F("FromInt/1", (c, a) => a[0].Bits is >= 0 and <= uint.MaxValue ? Ip(c, (uint)a[0].Bits) : throw c.Throw("ArgumentError", "An IPv4 address is a 32-bit value."));
        F("get_Any/0", (c, _) => Ip(c, 0));
        F("get_Loopback/0", (c, _) => Ip(c, 0x7F000001));
        F("get_Broadcast/0", (c, _) => Ip(c, uint.MaxValue));
        F("ToInt/0", (c, a) => Value.FromInt(IpOf(c, a[0])));
        F("get_Octets/0", (c, a) => c.NewArray([.. BitConverter.GetBytes(IpOf(c, a[0])).Reverse().Select(b => Value.FromInt(b))]));
        F("get_IsPrivate/0", (c, a) =>
        {
            var ip = IpOf(c, a[0]);
            return Value.FromBool((ip >> 24) == 10 || (ip >> 20) == 0xAC1 || (ip >> 16) == 0xC0A8);
        });
        F("get_IsLoopback/0", (c, a) => Value.FromBool((IpOf(c, a[0]) >> 24) == 127));
        F("get_IsMulticast/0", (c, a) => Value.FromBool((IpOf(c, a[0]) >> 28) == 0xE));
        F("Add/1", (c, a) => Ip(c, unchecked(IpOf(c, a[0]) + (uint)a[1].Bits)));
        F("CompareTo/1", (c, a) => Value.FromInt(IpOf(c, a[0]).CompareTo(IpOf(c, a[1]))));
        F("Equals/1", (c, a) => Value.FromBool(IpOf(c, a[0]) == IpOf(c, a[1])));
        F("HashCode/0", (c, a) => Value.FromInt(c.State<IpState>(a[0]).StructuralHash()));
        F("ToString/0", (c, a) => Value.FromString(c.State<IpState>(a[0]).ToString()));

        var C = r.Scope("Std.Net.Cidr");
        static CidrState S(NativeContext c, Value v) => c.State<CidrState>(v);
        C("Parse/1", (c, a) =>
        {
            var parts = a[0].AsString.Trim().Split('/');
            return parts.Length == 2 && ParseIp(parts[0]) is { } ip && int.TryParse(parts[1], Invariant, out var prefix) && prefix is >= 0 and <= 32
                ? c.NewInstance(c.Class("Std.Net", "Cidr"), new CidrState(ip, prefix))
                : throw c.Throw("FormatError", $"'{a[0].AsString}' is not CIDR notation (a.b.c.d/n).");
        });
        C("get_Network/0", (c, a) => Ip(c, S(c, a[0]).Network));
        C("get_PrefixLength/0", (c, a) => Value.FromInt(S(c, a[0]).Prefix));
        C("get_Mask/0", (c, a) => Ip(c, S(c, a[0]).Mask));
        C("get_BroadcastAddress/0", (c, a) => Ip(c, S(c, a[0]).Broadcast));
        C("get_Size/0", (c, a) => Value.FromInt(1L << (32 - S(c, a[0]).Prefix)));
        C("Contains/1", (c, a) => Value.FromBool(S(c, a[0]).Contains(IpOf(c, a[1]))));
        C("Overlaps/1", (c, a) => Value.FromBool(S(c, a[0]).Contains(S(c, a[1]).Network) || S(c, a[1]).Contains(S(c, a[0]).Network)));
        C("Hosts/0", (c, a) =>
        {
            var s = S(c, a[0]);
            var (first, last) = s.Prefix >= 31 ? (s.Network, s.Broadcast) : (s.Network + 1, s.Broadcast - 1);
            return SeqNatives.Create(c, ctx => HostRange(ctx, first, last));
        });
        C("Subnets/1", (c, a) =>
        {
            var s = S(c, a[0]);
            var prefix = Int32(c, a[1].Bits, "Prefix", s.Prefix, Math.Min(32, s.Prefix + 16));
            var count = 1L << (prefix - s.Prefix);
            var size = 1L << (32 - prefix);
            c.Charge(count);
            return c.NewArray([.. Enumerable.Range(0, (int)count).Select(i => c.NewInstance(c.Class("Std.Net", "Cidr"), new CidrState((uint)(s.Network + (i * size)), prefix)))]);
        });
        C("Equals/1", (c, a) => Value.FromBool(S(c, a[0]).StructurallyEquals(S(c, a[1]))));
        C("HashCode/0", (c, a) => Value.FromInt(S(c, a[0]).StructuralHash()));
        C("ToString/0", (c, a) => Value.FromString(S(c, a[0]).ToString()));

        var P = r.Scope("Std.Net.Port");
        P("IsValid/1", (c, a) => Value.FromBool(a[0].Bits is >= 1 and <= 65535));
        P("ServiceName/1", (c, a) => Services.FirstOrDefault(s => s.Port == a[0].Bits).Name is { } name ? Value.FromString(name) : Value.Null);
        P("ForService/1", (c, a) => Value.FromInt(Services.FirstOrDefault(s => s.Name == a[0].AsString.ToLowerInvariant()) is { Name: not null } s ? s.Port : -1));
        P("IsPrivileged/1", (c, a) => Value.FromBool(a[0].Bits is >= 1 and < 1024));

        var H = r.Scope("Std.Net.Hostname");
        static bool ValidHost(string name) => name.Length is > 0 and <= 253
            && name.TrimEnd('.').Split('.').All(l => l.Length is > 0 and <= 63 && l.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '-') && l[0] != '-' && l[^1] != '-');
        H("IsValid/1", (c, a) => Value.FromBool(ValidHost(a[0].AsString)));
        H("Labels/1", (c, a) => c.NewArray([.. a[0].AsString.TrimEnd('.').Split('.').Select(Value.FromString)]));
        H("TopLevel/1", (c, a) => Value.FromString(a[0].AsString.TrimEnd('.').Split('.')[^1]));

        var M = r.Scope("Std.Net.MacAddress");
        static MacState Mac(NativeContext c, Value v) => c.State<MacState>(v);
        M("Parse/1", (c, a) =>
        {
            var parts = a[0].AsString.Trim().Split(':', '-');
            return parts.Length == 6 && parts.All(p => p.Length == 2 && byte.TryParse(p, NumberStyles.AllowHexSpecifier, Invariant, out _))
                ? c.NewInstance(c.Class("Std.Net", "MacAddress"), new MacState([.. parts.Select(p => byte.Parse(p, NumberStyles.AllowHexSpecifier, Invariant))]))
                : throw c.Throw("FormatError", $"'{a[0].AsString}' is not a MAC address.");
        });
        M("get_Bytes/0", (c, a) => Value.FromObject(new BytesObject((byte[])Mac(c, a[0]).Bytes.Clone())));
        M("get_Oui/0", (c, a) => Value.FromString(string.Join(":", Mac(c, a[0]).Bytes.Take(3).Select(b => b.ToString("x2", Invariant)))));
        M("get_IsBroadcast/0", (c, a) => Value.FromBool(Mac(c, a[0]).Bytes.All(b => b == 0xFF)));
        M("get_IsMulticast/0", (c, a) => Value.FromBool((Mac(c, a[0]).Bytes[0] & 1) == 1));
        M("Equals/1", (c, a) => Value.FromBool(Mac(c, a[0]).StructurallyEquals(Mac(c, a[1]))));
        M("HashCode/0", (c, a) => Value.FromInt(Mac(c, a[0]).StructuralHash()));
        M("ToString/0", (c, a) => Value.FromString(Mac(c, a[0]).ToString()));
    }

    private static IEnumerable<Value> HostRange(NativeContext c, uint first, uint last)
    {
        for (var ip = (ulong)first; ip <= last; ip++)
        {
            yield return Ip(c, (uint)ip);
        }
    }

    #endregion

    #region URL and headers

    internal static UrlState? ParseUrl(string text)
    {
        var s = text.Trim();
        var schemeEnd = s.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd <= 0 || !s[..schemeEnd].All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '+' or '-' or '.'))
        {
            return null;
        }

        var scheme = s[..schemeEnd].ToLowerInvariant();
        var rest = s[(schemeEnd + 3)..];
        var fragment = string.Empty;
        var hash = rest.IndexOf('#', StringComparison.Ordinal);
        if (hash >= 0)
        {
            (rest, fragment) = (rest[..hash], rest[(hash + 1)..]);
        }

        var query = string.Empty;
        var q = rest.IndexOf('?', StringComparison.Ordinal);
        if (q >= 0)
        {
            (rest, query) = (rest[..q], rest[(q + 1)..]);
        }

        var slash = rest.IndexOf('/', StringComparison.Ordinal);
        var (authority, path) = slash >= 0 ? (rest[..slash], rest[slash..]) : (rest, "/");
        var user = string.Empty;
        var at = authority.LastIndexOf('@');
        if (at >= 0)
        {
            (user, authority) = (authority[..at], authority[(at + 1)..]);
        }

        var port = -1;
        var colon = authority.LastIndexOf(':');
        if (colon >= 0)
        {
            if (!int.TryParse(authority[(colon + 1)..], Invariant, out port) || port is < 0 or > 65535)
            {
                return null;
            }

            authority = authority[..colon];
        }

        return authority.Length == 0 ? null : new UrlState(scheme, user, authority.ToLowerInvariant(), port, path, query, fragment);
    }

    private static Value Url(NativeContext c, UrlState state) => c.NewInstance(c.Class("Std.Net", "Url"), state);

    private static Value Headers(NativeContext c, HeadersState state) => c.NewInstance(c.Class("Std.Net", "HttpHeaders"), state);

    private static void RegisterNetText(NativeRegistry r)
    {
        var U = r.Scope("Std.Net.Url");
        static UrlState S(NativeContext c, Value v) => c.State<UrlState>(v);
        U("Parse/1", (c, a) => ParseUrl(a[0].AsString) is { } u ? Url(c, u) : throw c.Throw("FormatError", $"'{a[0].AsString}' is not an absolute URL."));
        U("TryParse/1", (c, a) => ParseUrl(a[0].AsString) is { } u ? Url(c, u) : Value.Null);
        U("get_Scheme/0", (c, a) => Value.FromString(S(c, a[0]).Scheme));
        U("get_User/0", (c, a) => Value.FromString(S(c, a[0]).User));
        U("get_Host/0", (c, a) => Value.FromString(S(c, a[0]).Host));
        U("get_Port/0", (c, a) => Value.FromInt(S(c, a[0]).Port));
        U("get_Path/0", (c, a) => Value.FromString(S(c, a[0]).Path));
        U("get_Query/0", (c, a) => Value.FromString(S(c, a[0]).Query));
        U("get_Fragment/0", (c, a) => Value.FromString(S(c, a[0]).Fragment));
        U("Param/1", (c, a) => S(c, a[0]).Params().FirstOrDefault(p => p.Name == a[1].AsString) is { Name: not null } p ? Value.FromString(p.Value) : Value.Null);
        U("get_Params/0", (c, a) =>
        {
            var map = new MapState(c.Comparer);
            foreach (var (name, value) in S(c, a[0]).Params())
            {
                map.Table.TryAdd(Value.FromString(name), Value.FromString(value));
            }

            return CollectionNatives.NewMap(c, map);
        });
        U("WithPath/1", (c, a) => Url(c, S(c, a[0]) with { Path = a[1].AsString.StartsWith('/') ? a[1].AsString : "/" + a[1].AsString }));
        U("WithParam/2", (c, a) =>
        {
            var u = S(c, a[0]);
            var pair = Uri.EscapeDataString(a[1].AsString) + "=" + Uri.EscapeDataString(a[2].AsString);
            var kept = u.Query.Split('&', StringSplitOptions.RemoveEmptyEntries).Where(p => Uri.UnescapeDataString(p.Split('=')[0]) != a[1].AsString);
            return Url(c, u with { Query = string.Join("&", kept.Append(pair)) });
        });
        U("WithHost/1", (c, a) => Url(c, S(c, a[0]) with { Host = a[1].AsString.ToLowerInvariant() }));
        U("Equals/1", (c, a) => Value.FromBool(S(c, a[0]).StructurallyEquals(S(c, a[1]))));
        U("ToString/0", (c, a) => Value.FromString(S(c, a[0]).ToString()));

        var H = r.Scope("Std.Net.HttpHeaders");
        static HeadersState Hs(NativeContext c, Value v) => c.State<HeadersState>(v);
        static string? Get(HeadersState h, string name) =>
            h.Fields.Where(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)).Select(f => f.Value).FirstOrDefault();
        H("get_Empty/0", (c, _) => Headers(c, new HeadersState([])));
        H("Parse/1", (c, a) =>
        {
            var fields = new List<(string, string)>();
            foreach (var line in a[0].AsString.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
            {
                if (line.Trim().Length == 0)
                {
                    continue;
                }

                var colon = line.IndexOf(':', StringComparison.Ordinal);
                if (colon <= 0)
                {
                    throw c.Throw("ProtocolError", $"Malformed header line '{line}'.");
                }

                fields.Add((line[..colon].Trim(), line[(colon + 1)..].Trim()));
            }

            return Headers(c, new HeadersState([.. fields]));
        });
        H("Get/1", (c, a) => Get(Hs(c, a[0]), a[1].AsString) is { } v ? Value.FromString(v) : Value.Null);
        H("Has/1", (c, a) => Value.FromBool(Get(Hs(c, a[0]), a[1].AsString) is not null));
        H("get_Names/0", (c, a) => c.NewArray([.. Hs(c, a[0]).Fields.Select(f => Value.FromString(f.Name))]));
        H("With/2", (c, a) =>
        {
            var fields = Hs(c, a[0]).Fields.Where(f => !string.Equals(f.Name, a[1].AsString, StringComparison.OrdinalIgnoreCase)).Append((a[1].AsString, a[2].AsString));
            return Headers(c, new HeadersState([.. fields]));
        });
        H("Without/1", (c, a) => Headers(c, new HeadersState([.. Hs(c, a[0]).Fields.Where(f => !string.Equals(f.Name, a[1].AsString, StringComparison.OrdinalIgnoreCase))])));
        H("ToString/0", (c, a) => Value.FromString(string.Concat(Hs(c, a[0]).Fields.Select(f => $"{f.Name}: {f.Value}\r\n"))));
    }

    #endregion

    #region packets

    private static ushort InternetChecksum(IEnumerable<byte> data)
    {
        ulong sum = 0;
        var bytes = data.ToArray();
        for (var i = 0; i < bytes.Length; i += 2)
        {
            sum += (ulong)((bytes[i] << 8) | (i + 1 < bytes.Length ? bytes[i + 1] : 0));
        }

        while (sum >> 16 != 0)
        {
            sum = (sum & 0xFFFF) + (sum >> 16);
        }

        return (ushort)~sum;
    }

    private static byte[] U16(int v) => [(byte)(v >> 8), (byte)v];

    private static byte[] U32(long v) => [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];

    private static byte[] Pseudo(uint source, uint destination, int protocol, int length) =>
        [.. U32(source), .. U32(destination), 0, (byte)protocol, .. U16(length)];

    private static void Port(NativeContext c, long port)
    {
        if (port is < 0 or > 65535)
        {
            throw c.Throw("ArgumentError", $"Port {port} is outside 0–65535.");
        }
    }

    private static void RegisterPackets(NativeRegistry r)
    {
        var F = r.Scope("Std.Net.Packet");
        static byte[] Data(Value v) => v.As<BytesObject>().Data;
        static Value Out(NativeContext c, byte[] data)
        {
            c.Charge(1 + (data.Length / 16));
            return Value.FromObject(new BytesObject(data));
        }

        F("Ipv4/5", (c, a) =>
        {
            var payload = Data(a[4]);
            if (payload.Length > 65515)
            {
                throw c.Throw("ArgumentError", "The payload does not fit in one IPv4 packet.");
            }

            byte[] header = [0x45, 0, .. U16(20 + payload.Length), 0, 0, 0x40, 0, (byte)Math.Clamp(a[3].Bits, 0, 255), (byte)a[2].Bits, 0, 0, .. U32(IpOf(c, a[0])), .. U32(IpOf(c, a[1]))];
            var checksum = InternetChecksum(header);
            (header[10], header[11]) = ((byte)(checksum >> 8), (byte)checksum);
            return Out(c, [.. header, .. payload]);
        });
        F("TcpSegment/8", (c, a) =>
        {
            Port(c, a[2].Bits);
            Port(c, a[3].Bits);
            var payload = Data(a[7]);
            byte[] header = [.. U16((int)a[2].Bits), .. U16((int)a[3].Bits), .. U32(a[4].Bits), .. U32(a[5].Bits), 0x50, (byte)(a[6].Bits & 0x3F), 0xFF, 0xFF, 0, 0, 0, 0];
            var checksum = InternetChecksum([.. Pseudo(IpOf(c, a[0]), IpOf(c, a[1]), 6, header.Length + payload.Length), .. header, .. payload]);
            (header[16], header[17]) = ((byte)(checksum >> 8), (byte)checksum);
            return Out(c, [.. header, .. payload]);
        });
        F("UdpDatagram/5", (c, a) =>
        {
            Port(c, a[2].Bits);
            Port(c, a[3].Bits);
            var payload = Data(a[4]);
            var length = 8 + payload.Length;
            byte[] header = [.. U16((int)a[2].Bits), .. U16((int)a[3].Bits), .. U16(length), 0, 0];
            var checksum = InternetChecksum([.. Pseudo(IpOf(c, a[0]), IpOf(c, a[1]), 17, length), .. header, .. payload]);
            checksum = checksum == 0 ? (ushort)0xFFFF : checksum;
            (header[6], header[7]) = ((byte)(checksum >> 8), (byte)checksum);
            return Out(c, [.. header, .. payload]);
        });
        F("IcmpMessage/5", (c, a) =>
        {
            byte[] message = [(byte)a[0].Bits, (byte)a[1].Bits, 0, 0, .. U16((int)a[2].Bits), .. U16((int)a[3].Bits), .. Data(a[4])];
            var checksum = InternetChecksum(message);
            (message[2], message[3]) = ((byte)(checksum >> 8), (byte)checksum);
            return Out(c, message);
        });
        F("ParseIpv4/1", (c, a) =>
        {
            var d = Data(a[0]);
            if (d.Length < 20 || (d[0] >> 4) != 4 || (d[0] & 0xF) < 5 || (d[0] & 0xF) * 4 > d.Length)
            {
                throw c.Throw("ProtocolError", "Not an IPv4 packet (or truncated).");
            }

            var headerLength = (d[0] & 0xF) * 4;
            var total = Math.Min(d.Length, (d[2] << 8) | d[3]);
            var state = new Ipv4State(
                (uint)((d[12] << 24) | (d[13] << 16) | (d[14] << 8) | d[15]), (uint)((d[16] << 24) | (d[17] << 16) | (d[18] << 8) | d[19]),
                d[9], d[8], (d[4] << 8) | d[5], d[headerLength..Math.Max(headerLength, total)], InternetChecksum(d.Take(headerLength)) == 0);
            return c.NewInstance(c.Class("Std.Net", "Ipv4Packet"), state);
        });
        F("FlagNames/1", (c, a) =>
        {
            string[] names = ["FIN", "SYN", "RST", "PSH", "ACK", "URG"];
            return Value.FromString(string.Join("|", names.Where((_, i) => (a[0].Bits & (1 << i)) != 0)));
        });

        var P = r.Scope("Std.Net.Ipv4Packet");
        static Ipv4State S(NativeContext c, Value v) => c.State<Ipv4State>(v);
        P("get_Source/0", (c, a) => Ip(c, S(c, a[0]).Source));
        P("get_Destination/0", (c, a) => Ip(c, S(c, a[0]).Destination));
        P("get_Protocol/0", (c, a) => Value.FromInt(S(c, a[0]).Protocol));
        P("get_Ttl/0", (c, a) => Value.FromInt(S(c, a[0]).Ttl));
        P("get_Identification/0", (c, a) => Value.FromInt(S(c, a[0]).Identification));
        P("get_Payload/0", (c, a) => Value.FromObject(new BytesObject(S(c, a[0]).Payload)));
        P("get_ChecksumValid/0", (c, a) => Value.FromBool(S(c, a[0]).ChecksumValid));
        P("ToString/0", (c, a) => Value.FromString($"IPv4 {new IpState(S(c, a[0]).Source)} → {new IpState(S(c, a[0]).Destination)} proto {S(c, a[0]).Protocol} ttl {S(c, a[0]).Ttl} ({S(c, a[0]).Payload.Length} bytes)"));
    }

    #endregion
}
