using System.Globalization;
using System.Text;
using Ghostrig.CScript.Runtime;
using Ghostrig.CScript.Vm;
using static Ghostrig.CScript.Natives.Std.StdSupport;

namespace Ghostrig.CScript.Natives.Std;

#region Game states

internal sealed class LoopState(long step) : NativeState
{
    public long Step { get; } = step;
    public long Accumulator { get; set; }
    public long Total { get; set; }

    protected internal override object CaptureSnapshot() => (Accumulator, Total);

    protected internal override void RestoreSnapshot(object snapshot) => (Accumulator, Total) = ((long, long))snapshot;
}

internal sealed record TweenState(double From, double To, long Duration, string Easing);

internal sealed class ColorState(int r, int g, int b, int a) : ImmutableNativeState, IStructuralState
{
    public int R { get; } = r;
    public int G { get; } = g;
    public int B { get; } = b;
    public int A { get; } = a;
    public bool StructurallyEquals(IStructuralState other) => other is ColorState o && o.R == R && o.G == G && o.B == B && o.A == A;
    public long StructuralHash() => (long)Mix((uint)((R << 24) | (G << 16) | (B << 8) | A));
    public string Hex => A == 255 ? $"#{R:x2}{G:x2}{B:x2}" : $"#{R:x2}{G:x2}{B:x2}{A:x2}";
}

internal sealed class SpatialState(double cellSize, IEqualityComparer<Value> comparer) : NativeState
{
    public double CellSize { get; } = cellSize;
    public IEqualityComparer<Value> Comparer { get; } = comparer;
    public OrderedTable Items { get; } = new(comparer);
    public Dictionary<(long, long), List<Value>> Cells { get; private set; } = [];

    public IEnumerable<(long, long)> CellsOf(RectState r)
    {
        var (x0, y0) = ((long)Math.Floor(r.X / CellSize), (long)Math.Floor(r.Y / CellSize));
        var (x1, y1) = ((long)Math.Floor(r.Right / CellSize), (long)Math.Floor(r.Bottom / CellSize));
        for (var y = y0; y <= y1; y++)
        {
            for (var x = x0; x <= x1; x++)
            {
                yield return (x, y);
            }
        }
    }

    public void Index(Value item, RectState bounds)
    {
        foreach (var cell in CellsOf(bounds))
        {
            if (!Cells.TryGetValue(cell, out var list))
            {
                Cells[cell] = list = [];
            }

            list.Add(item);
        }
    }

    public void Unindex(Value item, RectState bounds)
    {
        foreach (var cell in CellsOf(bounds))
        {
            if (Cells.TryGetValue(cell, out var list))
            {
                list.RemoveAll(v => Comparer.Equals(v, item));
                if (list.Count == 0)
                {
                    Cells.Remove(cell);
                }
            }
        }
    }

    protected internal override object CaptureSnapshot() =>
        (Items.Capture(), Cells.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray()));

    protected internal override void RestoreSnapshot(object snapshot)
    {
        var (items, cells) = ((object, Dictionary<(long, long), Value[]>))snapshot;
        Items.Restore(items);
        Cells = cells.ToDictionary(kv => kv.Key, kv => kv.Value.ToList());
    }
}

internal sealed class BodyState(double x, double y, double mass) : NativeState
{
    public double X { get; set; } = x;
    public double Y { get; set; } = y;
    public double Vx { get; set; }
    public double Vy { get; set; }
    public double Fx { get; set; }
    public double Fy { get; set; }
    public double Mass { get; } = mass;
    public double Damping { get; set; }

    protected internal override object CaptureSnapshot() => new[] { X, Y, Vx, Vy, Fx, Fy, Damping };

    protected internal override void RestoreSnapshot(object snapshot)
    {
        var s = (double[])snapshot;
        (X, Y, Vx, Vy, Fx, Fy, Damping) = (s[0], s[1], s[2], s[3], s[4], s[5], s[6]);
    }
}

internal sealed class MachineState(string initial) : NativeState
{
    public string State { get; set; } = initial;
    public List<(string From, string Trigger, string To)> Transitions { get; private set; } = [];
    public List<FunctionObject> Handlers { get; private set; } = [];
    public List<string> History { get; private set; } = [initial];

    protected internal override object CaptureSnapshot() => (State, Transitions.ToArray(), Handlers.ToArray(), History.ToArray());

    protected internal override void RestoreSnapshot(object snapshot)
    {
        var (state, transitions, handlers, history) = ((string, (string, string, string)[], FunctionObject[], string[]))snapshot;
        (State, Transitions, Handlers, History) = (state, [.. transitions], [.. handlers], [.. history]);
    }
}

internal sealed class CameraState(double width, double height) : NativeState
{
    public double Width { get; } = width;
    public double Height { get; } = height;
    public double X { get; set; }
    public double Y { get; set; }
    public double Zoom { get; set; } = 1;
    public double Rotation { get; set; }

    protected internal override object CaptureSnapshot() => (X, Y, Zoom, Rotation);

    protected internal override void RestoreSnapshot(object snapshot) => (X, Y, Zoom, Rotation) = ((double, double, double, double))snapshot;
}

internal sealed class TileMapState(int width, int height, double tileSize) : NativeState
{
    public int Width { get; } = width;
    public int Height { get; } = height;
    public double TileSize { get; } = tileSize;
    public long[] Tiles { get; private set; } = new long[width * height];

    protected internal override object CaptureSnapshot() => Tiles.Clone();

    protected internal override void RestoreSnapshot(object snapshot) => Tiles = (long[])((long[])snapshot).Clone();
}

internal sealed record ParticleState(double X, double Y, double Vx, double Vy, double Age, double Lifetime, ColorState Color);

internal sealed class ParticlesState(long seed, int max) : NativeState
{
    public RandomState Random { get; } = new(seed);
    public int Max { get; } = max;
    public List<ParticleState> Particles { get; private set; } = [];
    public (double X, double Y) Gravity { get; set; }

    protected internal override object CaptureSnapshot() => (Particles.ToArray(), Gravity, Random.CaptureSnapshot());

    protected internal override void RestoreSnapshot(object snapshot)
    {
        var (particles, gravity, rng) = ((ParticleState[], (double, double), object))snapshot;
        (Particles, Gravity) = ([.. particles], gravity);
        Random.RestoreSnapshot(rng);
    }
}

internal sealed class InputState : NativeState
{
    public List<(string Action, string Input)> Bindings { get; private set; } = [];
    public HashSet<string> Pressed { get; private set; } = new(StringComparer.Ordinal);
    public HashSet<string> Triggered { get; private set; } = new(StringComparer.Ordinal);

    public bool Active(string action) => Bindings.Any(b => b.Action == action && Pressed.Contains(b.Input));

    protected internal override object CaptureSnapshot() => (Bindings.ToArray(), Pressed.ToArray(), Triggered.ToArray());

    protected internal override void RestoreSnapshot(object snapshot)
    {
        var (bindings, pressed, triggered) = (((string, string)[], string[], string[]))snapshot;
        Bindings = [.. bindings];
        Pressed = new HashSet<string>(pressed, StringComparer.Ordinal);
        Triggered = new HashSet<string>(triggered, StringComparer.Ordinal);
    }
}

#endregion

#region Test states

/// <summary>A generator: produce a value from a random stream, and list smaller candidate values for shrinking.</summary>
internal sealed record GenState(Func<NativeContext, RandomState, Value> Generate, Func<NativeContext, Value, IEnumerable<Value>> Shrink);

internal sealed record BenchState(string Name, long Iterations, long TotalSteps);

#endregion

/// <summary>Host implementations of <c>Std.Game</c> and <c>Std.Test</c>.</summary>
internal static class GameTestNatives
{
    private const string Game = "Std.Game";
    private const string Test = "Std.Test";

    public static void Register(NativeRegistry r)
    {
        RegisterLoopAndEasing(r);
        RegisterColor(r);
        RegisterCollision(r);
        RegisterSpatialAndBody(r);
        RegisterMachineAndPathfinding(r);
        RegisterCameraTilesParticles(r);
        RegisterInput(r.Scope($"{Game}.InputMap"));
        RegisterAssertions(r);
        RegisterProperties(r);
        RegisterBenchAndDebug(r);
    }

    private static double D(Value v) => MathNatives.D(v);

    #region loop, easing, tween

    internal static double Ease(string name, double t)
    {
        t = Math.Clamp(t, 0, 1);
        const double c1 = 1.70158, c3 = c1 + 1, c4 = 2 * SoftMath.Pi / 3;
        return name switch
        {
            "linear" => t,
            "inQuad" => t * t,
            "outQuad" => 1 - ((1 - t) * (1 - t)),
            "inOutQuad" => t < 0.5 ? 2 * t * t : 1 - (SoftMath.Pow((-2 * t) + 2, 2) / 2),
            "inCubic" => t * t * t,
            "outCubic" => 1 - SoftMath.Pow(1 - t, 3),
            "inOutCubic" => t < 0.5 ? 4 * t * t * t : 1 - (SoftMath.Pow((-2 * t) + 2, 3) / 2),
            "inSine" => 1 - SoftMath.Cos(t * SoftMath.Pi / 2),
            "outSine" => SoftMath.Sin(t * SoftMath.Pi / 2),
            "inOutSine" => -(SoftMath.Cos(SoftMath.Pi * t) - 1) / 2,
            "outBack" => 1 + (c3 * SoftMath.Pow(t - 1, 3)) + (c1 * SoftMath.Pow(t - 1, 2)),
            "outElastic" => t is 0 or 1 ? t : (SoftMath.Pow(2, -10 * t) * SoftMath.Sin(((t * 10) - 0.75) * c4)) + 1,
            "outBounce" => Bounce(t),
            _ => throw new ScriptErrorRequest("ArgumentError", $"Unknown easing '{name}'."),
        };
    }

    private static double Bounce(double t)
    {
        const double n1 = 7.5625, d1 = 2.75;
        return t switch
        {
            < 1 / d1 => n1 * t * t,
            < 2 / d1 => (n1 * (t -= 1.5 / d1) * t) + 0.75,
            < 2.5 / d1 => (n1 * (t -= 2.25 / d1) * t) + 0.9375,
            _ => (n1 * (t -= 2.625 / d1) * t) + 0.984375,
        };
    }

    private static void RegisterLoopAndEasing(NativeRegistry r)
    {
        var L = r.Scope($"{Game}.GameLoop");
        static LoopState S(NativeContext c, Value v) => c.State<LoopState>(v);
        L("GameLoop/1", (c, a) => a[1].Bits > 0 ? CollectionNatives.Init(a[0], new LoopState(a[1].Bits)) : throw c.Throw("ArgumentError", "The step must be positive."));
        L("get_Step/0", (c, a) => Value.FromDuration(S(c, a[0]).Step));
        L("Advance/2", (c, a) => Mutate<LoopState>(c, a[0], s =>
        {
            s.Accumulator += Math.Max(0, a[1].Bits);
            var due = s.Accumulator / s.Step;
            var max = Math.Max(1, a[2].Bits);
            if (due > max)
            {
                s.Accumulator = 0;
                due = max;
            }
            else
            {
                s.Accumulator -= due * s.Step;
            }

            s.Total += due;
            return Value.FromInt(due);
        }));
        L("get_Alpha/0", (c, a) => Value.FromFloat((double)S(c, a[0]).Accumulator / S(c, a[0]).Step));
        L("get_TotalSteps/0", (c, a) => Value.FromInt(S(c, a[0]).Total));

        var E = r.Scope($"{Game}.Easing");
        foreach (var name in new[] { "Linear", "InQuad", "OutQuad", "InOutQuad", "InCubic", "OutCubic", "InOutCubic", "InSine", "OutSine", "InOutSine", "OutBack", "OutElastic", "OutBounce" })
        {
            var key = char.ToLowerInvariant(name[0]) + name[1..];
            E($"{name}/1", (c, a) => Value.FromFloat(Ease(key, D(a[0]))));
        }

        E("Apply/2", (c, a) => Value.FromFloat(Ease(a[0].AsString, D(a[1]))));

        var T = r.Scope($"{Game}.Tween");
        static TweenState Tw(NativeContext c, Value v) => c.State<TweenState>(v);
        T("Tween/4", (c, a) =>
        {
            Ease(a[4].AsString, 0);
            return a[3].Bits > 0 ? CollectionNatives.Init(a[0], new TweenState(D(a[1]), D(a[2]), a[3].Bits, a[4].AsString)) : throw c.Throw("ArgumentError", "The duration must be positive.");
        });
        T("ValueAt/1", (c, a) =>
        {
            var t = Tw(c, a[0]);
            return Value.FromFloat(t.From + ((t.To - t.From) * Ease(t.Easing, (double)a[1].Bits / t.Duration)));
        });
        T("IsDone/1", (c, a) => Value.FromBool(a[1].Bits >= Tw(c, a[0]).Duration));
    }

    #endregion

    #region colour

    private static Value Color(NativeContext c, int r, int g, int b, int a = 255) =>
        c.NewInstance(c.Class(Game, "Color"), new ColorState(Math.Clamp(r, 0, 255), Math.Clamp(g, 0, 255), Math.Clamp(b, 0, 255), Math.Clamp(a, 0, 255)));

    private static ColorState Col(NativeContext c, Value v) => c.State<ColorState>(v);

    private static Value Palette(NativeContext c, string[] hex) => c.NewArray([.. hex.Select(h => Color(c, Convert.ToInt32(h[..2], 16), Convert.ToInt32(h[2..4], 16), Convert.ToInt32(h[4..6], 16)))]);

    private static void RegisterColor(NativeRegistry r)
    {
        var F = r.Scope($"{Game}.Color");
        static int Channel(NativeContext c, long v) => v is >= 0 and <= 255 ? (int)v : throw c.Throw("ArgumentError", $"Colour channel {v} is outside 0–255.");
        F("Color/3", (c, a) => CollectionNatives.Init(a[0], new ColorState(Channel(c, a[1].Bits), Channel(c, a[2].Bits), Channel(c, a[3].Bits), 255)));
        F("Color/4", (c, a) => CollectionNatives.Init(a[0], new ColorState(Channel(c, a[1].Bits), Channel(c, a[2].Bits), Channel(c, a[3].Bits), Channel(c, a[4].Bits))));
        F("Parse/1", (c, a) =>
        {
            var hex = a[0].AsString.Trim().TrimStart('#');
            if (hex.Length == 3)
            {
                hex = string.Concat(hex.Select(ch => $"{ch}{ch}"));
            }

            if (hex.Length is not (6 or 8) || !hex.All(char.IsAsciiHexDigit))
            {
                throw c.Throw("FormatError", $"'{a[0].AsString}' is not a hex colour.");
            }

            int Part(int i) => Convert.ToInt32(hex.Substring(i, 2), 16);
            return Color(c, Part(0), Part(2), Part(4), hex.Length == 8 ? Part(6) : 255);
        });
        F("FromHsv/3", (c, a) =>
        {
            var (h, s, v) = ((((D(a[0]) % 360) + 360) % 360) / 60, Math.Clamp(D(a[1]), 0, 1), Math.Clamp(D(a[2]), 0, 1));
            var chroma = v * s;
            var x = chroma * (1 - Math.Abs((h % 2) - 1));
            var (r1, g1, b1) = (int)h switch
            {
                0 => (chroma, x, 0.0),
                1 => (x, chroma, 0.0),
                2 => (0.0, chroma, x),
                3 => (0.0, x, chroma),
                4 => (x, 0.0, chroma),
                _ => (chroma, 0.0, x),
            };
            var m = v - chroma;
            int To(double ch) => (int)Math.Round((ch + m) * 255, MidpointRounding.AwayFromZero);
            return Color(c, To(r1), To(g1), To(b1));
        });
        F("get_Black/0", (c, _) => Color(c, 0, 0, 0));
        F("get_White/0", (c, _) => Color(c, 255, 255, 255));
        F("get_Transparent/0", (c, _) => Color(c, 0, 0, 0, 0));
        F("get_R/0", (c, a) => Value.FromInt(Col(c, a[0]).R));
        F("get_G/0", (c, a) => Value.FromInt(Col(c, a[0]).G));
        F("get_B/0", (c, a) => Value.FromInt(Col(c, a[0]).B));
        F("get_A/0", (c, a) => Value.FromInt(Col(c, a[0]).A));
        F("WithAlpha/1", (c, a) => Color(c, Col(c, a[0]).R, Col(c, a[0]).G, Col(c, a[0]).B, Channel(c, a[1].Bits)));
        F("Lerp/2", (c, a) =>
        {
            var (x, y, t) = (Col(c, a[0]), Col(c, a[1]), Math.Clamp(D(a[2]), 0, 1));
            int Mix(int p, int q) => (int)Math.Round(p + ((q - p) * t), MidpointRounding.AwayFromZero);
            return Color(c, Mix(x.R, y.R), Mix(x.G, y.G), Mix(x.B, y.B), Mix(x.A, y.A));
        });
        F("Darken/1", (c, a) =>
        {
            var (x, k) = (Col(c, a[0]), 1 - Math.Clamp(D(a[1]), 0, 1));
            return Color(c, (int)Math.Round(x.R * k), (int)Math.Round(x.G * k), (int)Math.Round(x.B * k), x.A);
        });
        F("Lighten/1", (c, a) =>
        {
            var (x, k) = (Col(c, a[0]), Math.Clamp(D(a[1]), 0, 1));
            int Up(int ch) => (int)Math.Round(ch + ((255 - ch) * k));
            return Color(c, Up(x.R), Up(x.G), Up(x.B), x.A);
        });
        F("get_Luminance/0", (c, a) =>
        {
            var x = Col(c, a[0]);
            static double Linear(int ch) => ch / 255.0 is var s && s <= 0.04045 ? s / 12.92 : SoftMath.Pow((s + 0.055) / 1.055, 2.4);
            return Value.FromFloat((0.2126 * Linear(x.R)) + (0.7152 * Linear(x.G)) + (0.0722 * Linear(x.B)));
        });
        F("ToHex/0", (c, a) => Value.FromString(Col(c, a[0]).Hex));
        F("Equals/1", (c, a) => Value.FromBool(Col(c, a[0]).StructurallyEquals(Col(c, a[1]))));
        F("HashCode/0", (c, a) => Value.FromInt(Col(c, a[0]).StructuralHash()));
        F("ToString/0", (c, a) => Value.FromString(Col(c, a[0]).Hex));

        var P = r.Scope($"{Game}.Palette");
        P("get_Phosphor/0", (c, _) => Palette(c, ["001a00", "003b00", "006b1a", "00a832", "33ff66", "b3ffcc"]));
        P("get_Amber/0", (c, _) => Palette(c, ["1a0f00", "4d2e00", "8f5600", "cc7a00", "ffb000", "ffd780"]));
        P("get_Cga/0", (c, _) => Palette(c, ["000000", "0000aa", "00aa00", "00aaaa", "aa0000", "aa00aa", "aa5500", "aaaaaa", "555555", "5555ff", "55ff55", "55ffff", "ff5555", "ff55ff", "ffff55", "ffffff"]));
        P("get_Accessible/0", (c, _) => Palette(c, ["000000", "e69f00", "56b4e9", "009e73", "f0e442", "0072b2", "d55e00", "cc79a7"]));
        P("Nearest/2", (c, a) =>
        {
            var target = Col(c, a[1]);
            var palette = Items(a[0]);
            if (palette.Length == 0)
            {
                throw Empty(c, "palette");
            }

            return palette.MinBy(p =>
            {
                var x = Col(c, p);
                return ((x.R - target.R) * (x.R - target.R)) + ((x.G - target.G) * (x.G - target.G)) + ((x.B - target.B) * (x.B - target.B));
            });
        });
    }

    #endregion

    #region collision

    private static RectState Rc(NativeContext c, Value v) => c.State<RectState>(v);

    private static CircleState Ci(NativeContext c, Value v) => c.State<CircleState>(v);

    private static void RegisterCollision(NativeRegistry r)
    {
        var F = r.Scope($"{Game}.Collision");
        static bool Overlap(RectState a, RectState b) => a.X < b.Right && b.X < a.Right && a.Y < b.Bottom && b.Y < a.Bottom;
        F("RectRect/2", (c, a) => Value.FromBool(Overlap(Rc(c, a[0]), Rc(c, a[1]))));
        F("CircleCircle/2", (c, a) =>
        {
            var (p, q) = (Ci(c, a[0]), Ci(c, a[1]));
            var (dx, dy, rr) = (p.X - q.X, p.Y - q.Y, p.R + q.R);
            return Value.FromBool((dx * dx) + (dy * dy) <= rr * rr);
        });
        F("CircleRect/2", (c, a) =>
        {
            var (s, rect) = (Ci(c, a[0]), Rc(c, a[1]));
            var (dx, dy) = (s.X - Math.Clamp(s.X, rect.X, rect.Right), s.Y - Math.Clamp(s.Y, rect.Y, rect.Bottom));
            return Value.FromBool((dx * dx) + (dy * dy) <= s.R * s.R);
        });
        F("PolygonPolygon/2", (c, a) =>
        {
            var intersects = c.Image.Natives.TryGet("Std.Math.Polygon.Intersects/1", out var f) ? f : throw new InvalidOperationException("Polygon natives missing.");
            return intersects(c, a);
        });
        F("PointInRect/2", (c, a) =>
        {
            var (p, rect) = (c.State<Vec2State>(a[0]), Rc(c, a[1]));
            return Value.FromBool(p.X >= rect.X && p.X < rect.Right && p.Y >= rect.Y && p.Y < rect.Bottom);
        });
        F("RectPenetration/2", (c, a) =>
        {
            var (p, q) = (Rc(c, a[0]), Rc(c, a[1]));
            if (!Overlap(p, q))
            {
                return Value.Null;
            }

            var left = q.X - p.Right;
            var right = q.Right - p.X;
            var up = q.Y - p.Bottom;
            var down = q.Bottom - p.Y;
            var dx = Math.Abs(left) < Math.Abs(right) ? left : right;
            var dy = Math.Abs(up) < Math.Abs(down) ? up : down;
            return Math.Abs(dx) < Math.Abs(dy) ? GeometryNatives.V2(c, dx, 0) : GeometryNatives.V2(c, 0, dy);
        });
        F("CirclePenetration/2", (c, a) =>
        {
            var (p, q) = (Ci(c, a[0]), Ci(c, a[1]));
            var (dx, dy) = (p.X - q.X, p.Y - q.Y);
            var distance = SoftMath.Hypot(dx, dy);
            var overlap = p.R + q.R - distance;
            if (overlap <= 0)
            {
                return Value.Null;
            }

            return distance == 0 ? GeometryNatives.V2(c, overlap, 0) : GeometryNatives.V2(c, dx / distance * overlap, dy / distance * overlap);
        });
        F("Sweep/3", (c, a) =>
        {
            var (m, v, t) = (Rc(c, a[0]), c.State<Vec2State>(a[1]), Rc(c, a[2]));
            double Entry(double velocity, double minM, double maxM, double minT, double maxT, out double exit)
            {
                if (velocity > 0)
                {
                    exit = (maxT - minM) / velocity;
                    return (minT - maxM) / velocity;
                }

                if (velocity < 0)
                {
                    exit = (minT - maxM) / velocity;
                    return (maxT - minM) / velocity;
                }

                var overlapping = maxM > minT && minM < maxT;
                exit = overlapping ? double.PositiveInfinity : double.NegativeInfinity;
                return overlapping ? double.NegativeInfinity : double.PositiveInfinity;
            }

            var ex = Entry(v.X, m.X, m.Right, t.X, t.Right, out var xExit);
            var ey = Entry(v.Y, m.Y, m.Bottom, t.Y, t.Bottom, out var yExit);
            var entry = Math.Max(ex, ey);
            var exit = Math.Min(xExit, yExit);
            return entry > exit || entry < 0 || entry > 1 ? Value.Null : Value.FromFloat(entry);
        });
    }

    #endregion

    #region spatial hash and bodies

    private static void RegisterSpatialAndBody(NativeRegistry r)
    {
        var H = r.Scope($"{Game}.SpatialHash");
        static SpatialState S(NativeContext c, Value v) => c.State<SpatialState>(v);
        static void Insert(SpatialState s, Value item, RectState bounds)
        {
            if (s.Items.TryGetValue(item, out var old))
            {
                s.Unindex(item, (RectState)old.Object!);
            }

            s.Items.Set(item, Value.FromObject(bounds));
            s.Index(item, bounds);
        }

        static Value Query(NativeContext c, SpatialState s, RectState area, bool point)
        {
            var found = new HashSet<Value>(s.Comparer);
            foreach (var cell in s.CellsOf(area))
            {
                if (s.Cells.TryGetValue(cell, out var list))
                {
                    found.UnionWith(list);
                }
            }

            c.Charge(1 + found.Count);
            return c.NewArray([.. found.Select(item => (Item: item, Slot: s.Items.TryGetSlot(item, out var slot) ? slot : int.MaxValue)).OrderBy(x => x.Slot).Where(x =>
            {
                var b = (RectState)s.Items.ValueAt(x.Slot).Object!;
                return point
                    ? area.X >= b.X && area.X < b.Right && area.Y >= b.Y && area.Y < b.Bottom
                    : b.X < area.Right && area.X < b.Right && b.Y < area.Bottom && area.Y < b.Bottom;
            }).Select(x => x.Item)]);
        }

        H("SpatialHash/1", (c, a) => D(a[1]) > 0 ? CollectionNatives.Init(a[0], new SpatialState(D(a[1]), c.Comparer)) : throw c.Throw("ArgumentError", "The cell size must be positive."));
        H("get_Count/0", (c, a) => Value.FromInt(S(c, a[0]).Items.Count));
        H("Insert/2", (c, a) =>
        {
            var bounds = Rc(c, a[2]);
            return Mutate<SpatialState>(c, a[0], s => Void(() => Insert(s, a[1], bounds)));
        });
        H("Update/2", (c, a) =>
        {
            var bounds = Rc(c, a[2]);
            return Mutate<SpatialState>(c, a[0], s => Void(() => Insert(s, a[1], bounds)));
        });
        H("Remove/1", (c, a) => !S(c, a[0]).Items.ContainsKey(a[1]) ? Value.False : Mutate<SpatialState>(c, a[0], s =>
        {
            s.Items.Remove(a[1], out var old);
            s.Unindex(a[1], (RectState)old.Object!);
            return Value.True;
        }));
        H("Query/1", (c, a) => Query(c, S(c, a[0]), Rc(c, a[1]), point: false));
        H("QueryPoint/1", (c, a) =>
        {
            var p = c.State<Vec2State>(a[1]);
            return Query(c, S(c, a[0]), new RectState(p.X, p.Y, 0, 0), point: true);
        });
        H("Clear/0", (c, a) => Mutate<SpatialState>(c, a[0], s =>
        {
            s.Items.Clear();
            s.Cells.Clear();
            return Value.Null;
        }));

        var B = r.Scope($"{Game}.Body");
        static BodyState Bd(NativeContext c, Value v) => c.State<BodyState>(v);
        B("Body/2", (c, a) => D(a[2]) > 0
            ? CollectionNatives.Init(a[0], new BodyState(c.State<Vec2State>(a[1]).X, c.State<Vec2State>(a[1]).Y, D(a[2])))
            : throw c.Throw("ArgumentError", "Mass must be positive."));
        B("get_Position/0", (c, a) => GeometryNatives.V2(c, Bd(c, a[0]).X, Bd(c, a[0]).Y));
        B("set_Position/1", (c, a) => Mutate<BodyState>(c, a[0], b => Void(() => (b.X, b.Y) = (c.State<Vec2State>(a[1]).X, c.State<Vec2State>(a[1]).Y))));
        B("get_Velocity/0", (c, a) => GeometryNatives.V2(c, Bd(c, a[0]).Vx, Bd(c, a[0]).Vy));
        B("set_Velocity/1", (c, a) => Mutate<BodyState>(c, a[0], b => Void(() => (b.Vx, b.Vy) = (c.State<Vec2State>(a[1]).X, c.State<Vec2State>(a[1]).Y))));
        B("get_Mass/0", (c, a) => Value.FromFloat(Bd(c, a[0]).Mass));
        B("get_Damping/0", (c, a) => Value.FromFloat(Bd(c, a[0]).Damping));
        B("set_Damping/1", (c, a) => Mutate<BodyState>(c, a[0], b => Void(() => b.Damping = Math.Clamp(D(a[1]), 0, 0.999999))));
        B("ApplyForce/1", (c, a) => Mutate<BodyState>(c, a[0], b => Void(() =>
        {
            b.Fx += c.State<Vec2State>(a[1]).X;
            b.Fy += c.State<Vec2State>(a[1]).Y;
        })));
        B("ApplyImpulse/1", (c, a) => Mutate<BodyState>(c, a[0], b => Void(() =>
        {
            b.Vx += c.State<Vec2State>(a[1]).X / b.Mass;
            b.Vy += c.State<Vec2State>(a[1]).Y / b.Mass;
        })));
        B("Step/1", (c, a) =>
        {
            var dt = D(a[1]);
            return dt < 0 ? throw c.Throw("ArgumentError", "dt cannot be negative.") : Mutate<BodyState>(c, a[0], b => Void(() =>
            {
                b.Vx += b.Fx / b.Mass * dt;
                b.Vy += b.Fy / b.Mass * dt;
                var keep = SoftMath.Pow(1 - b.Damping, dt);
                (b.Vx, b.Vy) = (b.Vx * keep, b.Vy * keep);
                b.X += b.Vx * dt;
                b.Y += b.Vy * dt;
                (b.Fx, b.Fy) = (0, 0);
            }));
        });
    }

    #endregion

    #region state machine and pathfinding

    private static void RegisterMachineAndPathfinding(NativeRegistry r)
    {
        var M = r.Scope($"{Game}.StateMachine");
        static MachineState S(NativeContext c, Value v) => c.State<MachineState>(v);
        static string? Target(MachineState s, string trigger) => s.Transitions.Where(t => t.From == s.State && t.Trigger == trigger).Select(t => t.To).FirstOrDefault();
        static Value Fire(NativeContext c, Value receiver, string trigger, bool raise)
        {
            var s = S(c, receiver);
            if (Target(s, trigger) is not { } to)
            {
                return raise ? throw c.Throw("Error", $"'{trigger}' is not allowed in state '{s.State}'.") : Value.False;
            }

            var from = s.State;
            c.Mutating(s);
            s.State = to;
            s.History.Add(to);
            foreach (var handler in s.Handlers.ToArray())
            {
                c.Invoke(handler, Value.FromString(from), Value.FromString(trigger), Value.FromString(to));
            }

            return Value.True;
        }

        M("StateMachine/1", (c, a) => CollectionNatives.Init(a[0], new MachineState(a[1].AsString)));
        M("get_State/0", (c, a) => Value.FromString(S(c, a[0]).State));
        M("Allow/3", (c, a) => Mutate<MachineState>(c, a[0], s =>
        {
            s.Transitions.RemoveAll(t => t.From == a[1].AsString && t.Trigger == a[2].AsString);
            s.Transitions.Add((a[1].AsString, a[2].AsString, a[3].AsString));
            return a[0];
        }));
        M("Can/1", (c, a) => Value.FromBool(Target(S(c, a[0]), a[1].AsString) is not null));
        M("Fire/1", (c, a) =>
        {
            Fire(c, a[0], a[1].AsString, raise: true);
            return Value.Null;
        });
        M("TryFire/1", (c, a) => Fire(c, a[0], a[1].AsString, raise: false));
        M("OnTransition/1", (c, a) => Mutate<MachineState>(c, a[0], s => Void(() => s.Handlers.Add(Fn(a[1])))));
        M("get_History/0", (c, a) => c.NewArray([.. S(c, a[0]).History.Select(Value.FromString)]));

        var P = r.Scope($"{Game}.Pathfinding");
        static (int, int) Cell(Value tuple) => ((int)tuple.As<TupleObject>().Items[0].Bits, (int)tuple.As<TupleObject>().Items[1].Bits);
        static Value Pair(int x, int y) => Tuple(Value.FromInt(x), Value.FromInt(y));
        static bool Walkable(GridState g, int x, int y) => x >= 0 && y >= 0 && x < g.Width && y < g.Height && g.Cells[(y * g.Width) + x].AsBool;
        (int, int)[] Steps(bool diagonal) => diagonal
            ? [(0, -1), (1, 0), (0, 1), (-1, 0), (1, -1), (1, 1), (-1, 1), (-1, -1)]
            : [(0, -1), (1, 0), (0, 1), (-1, 0)];
        P("FindPath/4", (c, a) =>
        {
            var g = c.State<GridState>(a[0]);
            var ((sx, sy), (gx, gy), diagonal) = (Cell(a[1]), Cell(a[2]), a[3].AsBool);
            if (!Walkable(g, sx, sy) || !Walkable(g, gx, gy))
            {
                return Value.Null;
            }

            double H(int x, int y)
            {
                var (dx, dy) = (Math.Abs(x - gx), Math.Abs(y - gy));
                return diagonal ? Math.Max(dx, dy) + ((1.4142135623730951 - 1) * Math.Min(dx, dy)) : dx + dy;
            }

            var cost = new Dictionary<(int, int), double> { [(sx, sy)] = 0 };
            var previous = new Dictionary<(int, int), (int, int)>();
            var queue = new PriorityQueue<(int, int), (double, long)>();
            var sequence = 0L;
            queue.Enqueue((sx, sy), (H(sx, sy), sequence++));
            var closed = new HashSet<(int, int)>();
            while (queue.TryDequeue(out var current, out _))
            {
                if (!closed.Add(current))
                {
                    continue;
                }

                c.Charge(2);
                if (current == (gx, gy))
                {
                    var path = new List<Value>();
                    for (var at = current; ; at = previous[at])
                    {
                        path.Add(Pair(at.Item1, at.Item2));
                        if (at == (sx, sy))
                        {
                            break;
                        }
                    }

                    path.Reverse();
                    return c.NewArray([.. path]);
                }

                foreach (var (dx, dy) in Steps(diagonal))
                {
                    var (nx, ny) = (current.Item1 + dx, current.Item2 + dy);
                    if (!Walkable(g, nx, ny) || (dx != 0 && dy != 0 && (!Walkable(g, current.Item1 + dx, current.Item2) || !Walkable(g, current.Item1, current.Item2 + dy))))
                    {
                        continue;
                    }

                    var candidate = cost[current] + (dx != 0 && dy != 0 ? 1.4142135623730951 : 1);
                    if (!cost.TryGetValue((nx, ny), out var known) || candidate < known)
                    {
                        cost[(nx, ny)] = candidate;
                        previous[(nx, ny)] = current;
                        queue.Enqueue((nx, ny), (candidate + H(nx, ny), sequence++));
                    }
                }
            }

            return Value.Null;
        });
        P("DistanceField/2", (c, a) =>
        {
            var g = c.State<GridState>(a[0]);
            var (sx, sy) = Cell(a[1]);
            var field = new GridState(g.Width, g.Height, Value.FromInt(-1));
            if (Walkable(g, sx, sy))
            {
                field.Cells[(sy * g.Width) + sx] = Value.FromInt(0);
                var queue = new Queue<(int, int)>([(sx, sy)]);
                while (queue.Count > 0)
                {
                    var (x, y) = queue.Dequeue();
                    c.Charge(1);
                    foreach (var (dx, dy) in Steps(false))
                    {
                        var (nx, ny) = (x + dx, y + dy);
                        if (Walkable(g, nx, ny) && field.Cells[(ny * g.Width) + nx].Bits < 0)
                        {
                            field.Cells[(ny * g.Width) + nx] = Value.FromInt(field.Cells[(y * g.Width) + x].Bits + 1);
                            queue.Enqueue((nx, ny));
                        }
                    }
                }
            }

            return c.NewInstance(c.Class("Std.Collections", "Grid2D"), field);
        });
    }

    #endregion

    #region camera, tiles, particles

    private static void RegisterCameraTilesParticles(NativeRegistry r)
    {
        var C = r.Scope($"{Game}.Camera2D");
        static CameraState Cam(NativeContext c, Value v) => c.State<CameraState>(v);
        static (double, double) ToScreen(CameraState s, double x, double y)
        {
            var (dx, dy) = (x - s.X, y - s.Y);
            var (cos, sin) = (SoftMath.Cos(-s.Rotation), SoftMath.Sin(-s.Rotation));
            return ((((dx * cos) - (dy * sin)) * s.Zoom) + (s.Width / 2), (((dx * sin) + (dy * cos)) * s.Zoom) + (s.Height / 2));
        }

        static (double, double) ToWorld(CameraState s, double x, double y)
        {
            var (dx, dy) = ((x - (s.Width / 2)) / s.Zoom, (y - (s.Height / 2)) / s.Zoom);
            var (cos, sin) = (SoftMath.Cos(s.Rotation), SoftMath.Sin(s.Rotation));
            return (((dx * cos) - (dy * sin)) + s.X, ((dx * sin) + (dy * cos)) + s.Y);
        }

        C("Camera2D/2", (c, a) => CollectionNatives.Init(a[0], new CameraState(D(a[1]), D(a[2]))));
        C("get_Position/0", (c, a) => GeometryNatives.V2(c, Cam(c, a[0]).X, Cam(c, a[0]).Y));
        C("set_Position/1", (c, a) => Mutate<CameraState>(c, a[0], s => Void(() => (s.X, s.Y) = (c.State<Vec2State>(a[1]).X, c.State<Vec2State>(a[1]).Y))));
        C("get_Zoom/0", (c, a) => Value.FromFloat(Cam(c, a[0]).Zoom));
        C("set_Zoom/1", (c, a) => D(a[1]) > 0 ? Mutate<CameraState>(c, a[0], s => Void(() => s.Zoom = D(a[1]))) : throw c.Throw("ArgumentError", "Zoom must be positive."));
        C("get_Rotation/0", (c, a) => Value.FromFloat(Cam(c, a[0]).Rotation));
        C("set_Rotation/1", (c, a) => Mutate<CameraState>(c, a[0], s => Void(() => s.Rotation = D(a[1]))));
        C("WorldToScreen/1", (c, a) =>
        {
            var (x, y) = ToScreen(Cam(c, a[0]), c.State<Vec2State>(a[1]).X, c.State<Vec2State>(a[1]).Y);
            return GeometryNatives.V2(c, x, y);
        });
        C("ScreenToWorld/1", (c, a) =>
        {
            var (x, y) = ToWorld(Cam(c, a[0]), c.State<Vec2State>(a[1]).X, c.State<Vec2State>(a[1]).Y);
            return GeometryNatives.V2(c, x, y);
        });
        C("get_VisibleArea/0", (c, a) =>
        {
            var s = Cam(c, a[0]);
            var corners = new[] { ToWorld(s, 0, 0), ToWorld(s, s.Width, 0), ToWorld(s, 0, s.Height), ToWorld(s, s.Width, s.Height) };
            var (x0, y0) = (corners.Min(p => p.Item1), corners.Min(p => p.Item2));
            return c.NewInstance(c.Class("Std.Math", "Rect"), new RectState(x0, y0, corners.Max(p => p.Item1) - x0, corners.Max(p => p.Item2) - y0));
        });
        C("Follow/2", (c, a) =>
        {
            var target = c.State<Vec2State>(a[1]);
            var k = Math.Clamp(D(a[2]), 0, 1);
            return Mutate<CameraState>(c, a[0], s => Void(() => (s.X, s.Y) = (s.X + ((target.X - s.X) * k), s.Y + ((target.Y - s.Y) * k))));
        });

        var T = r.Scope($"{Game}.TileMap");
        static TileMapState Tm(NativeContext c, Value v) => c.State<TileMapState>(v);
        static int At(NativeContext c, TileMapState t, long x, long y) =>
            x >= 0 && y >= 0 && x < t.Width && y < t.Height ? (int)((y * t.Width) + x) : throw c.Throw("IndexError", $"Tile ({x}, {y}) is outside {t.Width}×{t.Height}.");
        static Value Create(NativeContext c, Value receiver, long w, long h, double size) =>
            size > 0 && w >= 0 && h >= 0 && w * h <= 16_000_000
                ? CollectionNatives.Init(receiver, new TileMapState((int)w, (int)h, size))
                : throw c.Throw("ArgumentError", "Invalid tile map dimensions.");
        T("TileMap/3", (c, a) => Create(c, a[0], a[1].Bits, a[2].Bits, D(a[3])));
        T("get_Width/0", (c, a) => Value.FromInt(Tm(c, a[0]).Width));
        T("get_Height/0", (c, a) => Value.FromInt(Tm(c, a[0]).Height));
        T("get_TileSize/0", (c, a) => Value.FromFloat(Tm(c, a[0]).TileSize));
        T("Get/2", (c, a) => Value.FromInt(Tm(c, a[0]).Tiles[At(c, Tm(c, a[0]), a[1].Bits, a[2].Bits)]));
        T("Set/3", (c, a) =>
        {
            var i = At(c, Tm(c, a[0]), a[1].Bits, a[2].Bits);
            return Mutate<TileMapState>(c, a[0], t => Void(() => t.Tiles[i] = a[3].Bits));
        });
        T("Fill/1", (c, a) => Mutate<TileMapState>(c, a[0], t => Void(() => System.Array.Fill(t.Tiles, a[1].Bits))));
        T("WorldToTile/1", (c, a) =>
        {
            var (t, p) = (Tm(c, a[0]), c.State<Vec2State>(a[1]));
            return Tuple(Value.FromInt((long)Math.Floor(p.X / t.TileSize)), Value.FromInt((long)Math.Floor(p.Y / t.TileSize)));
        });
        T("TileBounds/2", (c, a) =>
        {
            var t = Tm(c, a[0]);
            return c.NewInstance(c.Class("Std.Math", "Rect"), new RectState(a[1].Bits * t.TileSize, a[2].Bits * t.TileSize, t.TileSize, t.TileSize));
        });
        T("TilesIn/1", (c, a) =>
        {
            var (t, area) = (Tm(c, a[0]), Rc(c, a[1]));
            var (x0, y0) = (Math.Max(0, (int)Math.Floor(area.X / t.TileSize)), Math.Max(0, (int)Math.Floor(area.Y / t.TileSize)));
            var (x1, y1) = (Math.Min(t.Width - 1, (int)Math.Ceiling(area.Right / t.TileSize) - 1), Math.Min(t.Height - 1, (int)Math.Ceiling(area.Bottom / t.TileSize) - 1));
            var tiles = new List<Value>();
            for (var y = y0; y <= y1; y++)
            {
                for (var x = x0; x <= x1; x++)
                {
                    tiles.Add(Tuple(Value.FromInt(x), Value.FromInt(y)));
                }
            }

            return c.NewArray([.. tiles]);
        });
        T("Parse/2", (c, a) =>
        {
            var lines = a[0].AsString.Replace("\r\n", "\n", StringComparison.Ordinal).Trim('\n').Split('\n');
            var width = lines.Max(l => l.Length);
            var state = new TileMapState(width, lines.Length, D(a[1]) > 0 ? D(a[1]) : throw c.Throw("ArgumentError", "Tile size must be positive."));
            for (var y = 0; y < lines.Length; y++)
            {
                for (var x = 0; x < lines[y].Length; x++)
                {
                    state.Tiles[(y * width) + x] = lines[y][x] switch { '#' => 1, '.' or ' ' => 0, var d when char.IsAsciiDigit(d) => d - '0', var other => other };
                }
            }

            return c.NewInstance(c.Class(Game, "TileMap"), state);
        });

        var Pt = r.Scope($"{Game}.Particle");
        static ParticleState Pa(NativeContext c, Value v) => c.State<ParticleState>(v);
        Pt("get_Position/0", (c, a) => GeometryNatives.V2(c, Pa(c, a[0]).X, Pa(c, a[0]).Y));
        Pt("get_Velocity/0", (c, a) => GeometryNatives.V2(c, Pa(c, a[0]).Vx, Pa(c, a[0]).Vy));
        Pt("get_Age/0", (c, a) => Value.FromFloat(Pa(c, a[0]).Age));
        Pt("get_Lifetime/0", (c, a) => Value.FromFloat(Pa(c, a[0]).Lifetime));
        Pt("get_Color/0", (c, a) => c.NewInstance(c.Class(Game, "Color"), Pa(c, a[0]).Color));

        var S = r.Scope($"{Game}.ParticleSystem");
        static ParticlesState Ps(NativeContext c, Value v) => c.State<ParticlesState>(v);
        S("ParticleSystem/2", (c, a) => CollectionNatives.Init(a[0], new ParticlesState(a[1].Bits, Int32(c, a[2].Bits, "maxParticles", 0, 1_000_000))));
        S("get_Count/0", (c, a) => Value.FromInt(Ps(c, a[0]).Particles.Count));
        S("get_Gravity/0", (c, a) => GeometryNatives.V2(c, Ps(c, a[0]).Gravity.X, Ps(c, a[0]).Gravity.Y));
        S("set_Gravity/1", (c, a) => Mutate<ParticlesState>(c, a[0], s => Void(() => s.Gravity = (c.State<Vec2State>(a[1]).X, c.State<Vec2State>(a[1]).Y))));
        S("Emit/8", (c, a) =>
        {
            var origin = c.State<Vec2State>(a[1]);
            var (count, angle, spread, min, max, life) = (Int32(c, a[2].Bits, "count", 0, 100_000), D(a[3]), D(a[4]), D(a[5]), D(a[6]), D(a[7]));
            var color = c.State<ColorState>(a[8]);
            return Mutate<ParticlesState>(c, a[0], s => Void(() =>
            {
                for (var i = 0; i < count && s.Particles.Count < s.Max; i++)
                {
                    var theta = angle + ((s.Random.NextDouble() - 0.5) * spread);
                    var speed = min + ((max - min) * s.Random.NextDouble());
                    s.Particles.Add(new ParticleState(origin.X, origin.Y, SoftMath.Cos(theta) * speed, SoftMath.Sin(theta) * speed, 0, life, color));
                }

                c.Charge(count);
            }));
        });
        S("Step/1", (c, a) =>
        {
            var dt = D(a[1]);
            return Mutate<ParticlesState>(c, a[0], s => Void(() =>
            {
                c.Charge(s.Particles.Count);
                s.Particles.RemoveAll(p => p.Age + dt >= p.Lifetime);
                for (var i = 0; i < s.Particles.Count; i++)
                {
                    var p = s.Particles[i];
                    var (vx, vy) = (p.Vx + (s.Gravity.X * dt), p.Vy + (s.Gravity.Y * dt));
                    s.Particles[i] = p with { X = p.X + (vx * dt), Y = p.Y + (vy * dt), Vx = vx, Vy = vy, Age = p.Age + dt };
                }
            }));
        });
        S("get_Particles/0", (c, a) => c.NewArray([.. Ps(c, a[0]).Particles.Select(p => c.NewInstance(c.Class(Game, "Particle"), p))]));
        S("Clear/0", (c, a) => Mutate<ParticlesState>(c, a[0], s => Void(s.Particles.Clear)));
    }

    #endregion

    #region input

    private static void RegisterInput(Action<string, StdFunction> F)
    {
        static InputState S(NativeContext c, Value v) => c.State<InputState>(v);
        F("InputMap/0", (c, a) => CollectionNatives.Init(a[0], new InputState()));
        F("Bind/2", (c, a) => Mutate<InputState>(c, a[0], s =>
        {
            if (!s.Bindings.Contains((a[1].AsString, a[2].AsString)))
            {
                s.Bindings.Add((a[1].AsString, a[2].AsString));
            }

            return a[0];
        }));
        F("Unbind/2", (c, a) => Mutate<InputState>(c, a[0], s => Void(() => s.Bindings.Remove((a[1].AsString, a[2].AsString)))));
        F("ActionsFor/1", (c, a) => c.NewArray([.. S(c, a[0]).Bindings.Where(b => b.Input == a[1].AsString).Select(b => Value.FromString(b.Action))]));
        F("InputsFor/1", (c, a) => c.NewArray([.. S(c, a[0]).Bindings.Where(b => b.Action == a[1].AsString).Select(b => Value.FromString(b.Input))]));
        F("Press/1", (c, a) => Mutate<InputState>(c, a[0], s => Void(() =>
        {
            var before = s.Bindings.Where(b => b.Input == a[1].AsString).Select(b => b.Action).Where(act => !s.Active(act)).ToList();
            s.Pressed.Add(a[1].AsString);
            s.Triggered.UnionWith(before);
        })));
        F("Release/1", (c, a) => Mutate<InputState>(c, a[0], s => Void(() => s.Pressed.Remove(a[1].AsString))));
        F("IsActive/1", (c, a) => Value.FromBool(S(c, a[0]).Active(a[1].AsString)));
        F("WasTriggered/1", (c, a) => Value.FromBool(S(c, a[0]).Triggered.Contains(a[1].AsString)));
        F("Update/0", (c, a) => Mutate<InputState>(c, a[0], s => Void(s.Triggered.Clear)));
    }

    #endregion

    #region assertions

    /// <summary>Raises <c>Std.Test.AssertionFailed</c> (code EASSERT).</summary>
    internal static Exception Fail(NativeContext c, string message)
    {
        var instance = c.Class(Test, "AssertionFailed").CreateInstance();
        instance.NativeState = new ErrorState("EASSERT", message);
        return new ScriptException(Value.FromObject(instance), message);
    }

    private static string Show(NativeContext c, Value v) => v.Object switch
    {
        string s => $"\"{s}\"",
        ArrayObject array => Join(c, array.Items.Select(item => Value.FromString(Show(c, item)))),
        TupleObject tuple => "(" + string.Join(", ", tuple.Items.Select(item => Show(c, item))) + ")",
        _ => Display(c, v),
    };

    private static bool IsClass(Value error, string name)
    {
        for (var cls = (error.Object as ObjectInstance)?.Class; cls is not null; cls = cls.Base)
        {
            if (cls.Name == name)
            {
                return true;
            }
        }

        return false;
    }

    private static Value Catch(NativeContext c, FunctionObject action)
    {
        try
        {
            c.Invoke(action);
        }
        catch (ScriptException e)
        {
            if (IsClass(e.Error, "StepBudgetExceeded") || IsClass(e.Error, "KernelPanic"))
            {
                throw;
            }

            return e.Error;
        }
        catch (ScriptErrorRequest e) when (e.ClassName is not ("StepBudgetExceeded" or "KernelPanic"))
        {
            return c.Error(e.ClassName, e.Message);
        }

        throw Fail(c, "Expected an error, but none was raised.");
    }

    private static void RegisterAssertions(NativeRegistry r)
    {
        r.Scope($"{Test}.AssertionFailed")("AssertionFailed/1", (c, a) =>
        {
            a[0].As<ObjectInstance>().NativeState = new ErrorState("EASSERT", a[1].Object as string ?? string.Empty);
            return Value.Null;
        });

        var A = r.Scope($"{Test}.Assert");
        A("True/1", (c, a) => a[0].AsBool ? Value.Null : throw Fail(c, "Expected true, but was false."));
        A("True/2", (c, a) => a[0].AsBool ? Value.Null : throw Fail(c, a[1].AsString));
        A("False/1", (c, a) => !a[0].AsBool ? Value.Null : throw Fail(c, "Expected false, but was true."));
        A("Equal/2", (c, a) => c.Comparer.Equals(a[0], a[1]) ? Value.Null : throw Fail(c, $"Expected: {Show(c, a[0])}\nActual:   {Show(c, a[1])}"));
        A("NotEqual/2", (c, a) => !c.Comparer.Equals(a[0], a[1]) ? Value.Null : throw Fail(c, $"Expected a value other than {Show(c, a[0])}."));
        A("Null/1", (c, a) => a[0].IsNull ? Value.Null : throw Fail(c, $"Expected null, but was {Show(c, a[0])}."));
        A("NotNull/1", (c, a) => !a[0].IsNull ? Value.Null : throw Fail(c, "Expected a value, but was null."));
        A("Near/3", (c, a) => Math.Abs(D(a[0]) - D(a[1])) <= D(a[2])
            ? Value.Null
            : throw Fail(c, $"Expected {ValueFormatting.FormatFloat(D(a[0]))} ± {ValueFormatting.FormatFloat(D(a[2]))}, but was {ValueFormatting.FormatFloat(D(a[1]))}."));
        A("InRange/3", (c, a) => a[0].Bits >= a[1].Bits && a[0].Bits <= a[2].Bits ? Value.Null : throw Fail(c, $"{a[0].Bits} is outside [{a[1].Bits}, {a[2].Bits}]."));
        A("Contains/2", (c, a) => a[0].AsString.Contains(a[1].AsString, StringComparison.Ordinal) ? Value.Null : throw Fail(c, $"\"{a[0].AsString}\" does not contain \"{a[1].AsString}\"."));
        A("SequenceEqual/2", (c, a) =>
        {
            var (x, y) = (Materialize(c, a[0]), Materialize(c, a[1]));
            for (var i = 0; i < Math.Max(x.Count, y.Count); i++)
            {
                if (i >= x.Count || i >= y.Count || !c.Comparer.Equals(x[i], y[i]))
                {
                    throw Fail(c, $"Sequences differ at index {i}: expected {(i < x.Count ? Show(c, x[i]) : "<end>")}, actual {(i < y.Count ? Show(c, y[i]) : "<end>")}.");
                }
            }

            return Value.Null;
        });
        A("Throws/1", (c, a) => Catch(c, Fn(a[0])));
        A("Throws/2", (c, a) =>
        {
            var error = Catch(c, Fn(a[0]));
            return IsClass(error, a[1].AsString) ? error : throw Fail(c, $"Expected {a[1].AsString}, but {VirtualMachine.DescribeError(error)} was raised.");
        });
        A("Fail/1", (c, a) => throw Fail(c, a[0].AsString));
    }

    #endregion

    #region property testing

    private static Value Gen(NativeContext c, GenState state) => c.NewInstance(c.Class(Test, "Gen"), state);

    private static GenState Gs(NativeContext c, Value v) => c.State<GenState>(v);

    private static IEnumerable<long> ShrinkInt(long value, long min, long max)
    {
        var target = Math.Clamp(0, min, max);
        if (value == target)
        {
            yield break;
        }

        yield return target;
        for (var delta = (value - target) / 2; delta != 0; delta /= 2)
        {
            yield return value - delta;
        }
    }

    private static void RegisterProperties(NativeRegistry r)
    {
        var G = r.Scope($"{Test}.Gens");
        G("Int/2", (c, a) =>
        {
            var (min, max) = (a[0].Bits, a[1].Bits);
            return max < min ? throw c.Throw("ArgumentError", "max < min.") : Gen(c, new GenState(
                (_, rng) => Value.FromInt(min + (long)rng.Below((ulong)(max - min) + 1)),
                (_, v) => ShrinkInt(v.Bits, min, max).Select(Value.FromInt)));
        });
        G("Float/2", (c, a) =>
        {
            var (min, max) = (D(a[0]), D(a[1]));
            return Gen(c, new GenState(
                (_, rng) => Value.FromFloat(min + ((max - min) * rng.NextDouble())),
                (_, v) => new[] { Math.Clamp(0, min, max), Math.Round(v.AsFloat), v.AsFloat / 2 }.Where(x => x >= min && x <= max && x != v.AsFloat).Distinct().Select(Value.FromFloat)));
        });
        G("Bool/0", (c, _) => Gen(c, new GenState((_, rng) => Value.FromBool((rng.Next() & 1) == 1), (_, v) => v.AsBool ? [Value.False] : [])));
        G("Text/1", (c, a) =>
        {
            var maxLength = Int32(c, a[0].Bits, "maxLength", 0, 10_000);
            return Gen(c, new GenState(
                (_, rng) => Value.FromString(new string([.. Enumerable.Range(0, (int)rng.Below((ulong)maxLength + 1)).Select(_ => (char)(32 + (int)rng.Below(95)))])),
                (_, v) =>
                {
                    var s = v.AsString;
                    return s.Length == 0 ? [] : new[] { string.Empty, s[..(s.Length / 2)], s[1..], s[..^1] }.Distinct().Where(x => x != s).Select(Value.FromString);
                }));
        });
        G("OneOf/1", (c, a) =>
        {
            var values = (Value[])Items(a[0]).Clone();
            return values.Length == 0 ? throw Empty(c, "array") : Gen(c, new GenState(
                (_, rng) => values[(int)rng.Below((ulong)values.Length)],
                (ctx, v) => values.TakeWhile(x => !ctx.Comparer.Equals(x, v))));
        });
        G("ArrayOf/2", (c, a) =>
        {
            var element = Gs(c, a[0]);
            var maxLength = Int32(c, a[1].Bits, "maxLength", 0, 10_000);
            return Gen(c, new GenState(
                (ctx, rng) => ctx.NewArray([.. Enumerable.Range(0, (int)rng.Below((ulong)maxLength + 1)).Select(_ => element.Generate(ctx, rng))]),
                (ctx, v) => ShrinkArray(ctx, element, Items(v))));
        });
        G("Pair/2", (c, a) =>
        {
            var (first, second) = (Gs(c, a[0]), Gs(c, a[1]));
            return Gen(c, new GenState(
                (ctx, rng) => Tuple(first.Generate(ctx, rng), second.Generate(ctx, rng)),
                (ctx, v) =>
                {
                    var items = v.As<TupleObject>().Items;
                    return first.Shrink(ctx, items[0]).Select(x => Tuple(x, items[1])).Concat(second.Shrink(ctx, items[1]).Select(y => Tuple(items[0], y)));
                }));
        });

        var M = r.Scope($"{Test}.Gen");
        M("Map/1", (c, a) =>
        {
            // Mapped generators shrink by shrinking the source value and mapping again, so they keep their own seed values.
            var (source, f) = (Gs(c, a[0]), Fn(a[1]));
            var origins = new Dictionary<Value, Value>();
            return Gen(c, new GenState(
                (ctx, rng) =>
                {
                    var origin = source.Generate(ctx, rng);
                    var mapped = ctx.Invoke(f, origin);
                    origins[mapped] = origin;
                    return mapped;
                },
                (ctx, v) => origins.TryGetValue(v, out var origin)
                    ? source.Shrink(ctx, origin).Select(o =>
                    {
                        var mapped = ctx.Invoke(f, o);
                        origins[mapped] = o;
                        return mapped;
                    })
                    : []));
        });
        M("Filter/1", (c, a) =>
        {
            var (source, f) = (Gs(c, a[0]), Fn(a[1]));
            return Gen(c, new GenState(
                (ctx, rng) =>
                {
                    for (var attempt = 0; attempt < 1000; attempt++)
                    {
                        var v = source.Generate(ctx, rng);
                        if (ctx.Invoke(f, v).AsBool)
                        {
                            return v;
                        }
                    }

                    throw ctx.Throw("Error", "Gen.Filter rejected 1000 values in a row.");
                },
                (ctx, v) => source.Shrink(ctx, v).Where(x => ctx.Invoke(f, x).AsBool)));
        });
        M("Sample/1", (c, a) => Gs(c, a[0]).Generate(c, new RandomState(a[1].Bits)));

        var P = r.Scope($"{Test}.Property");
        P("ForAll/2", (c, a) => ForAll(c, Gs(c, a[0]), Fn(a[1]), 100, 0));
        P("ForAll/4", (c, a) => ForAll(c, Gs(c, a[0]), Fn(a[1]), Int32(c, a[2].Bits, "runs", 1, 1_000_000), a[3].Bits));
    }

    private static IEnumerable<Value> ShrinkArray(NativeContext c, GenState element, Value[] items)
    {
        if (items.Length == 0)
        {
            yield break;
        }

        yield return c.NewArray([]);
        yield return c.NewArray(items[..(items.Length / 2)]);
        for (var i = 0; i < items.Length; i++)
        {
            yield return c.NewArray([.. items[..i], .. items[(i + 1)..]]);
        }

        for (var i = 0; i < items.Length; i++)
        {
            foreach (var smaller in element.Shrink(c, items[i]).Take(4))
            {
                var copy = (Value[])items.Clone();
                copy[i] = smaller;
                yield return c.NewArray(copy);
            }
        }
    }

    private static bool Holds(NativeContext c, FunctionObject property, Value input)
    {
        try
        {
            return c.Invoke(property, input).AsBool;
        }
        catch (ScriptException e) when (!IsClass(e.Error, "StepBudgetExceeded") && !IsClass(e.Error, "KernelPanic"))
        {
            return false;
        }
        catch (ScriptErrorRequest e) when (e.ClassName is not ("StepBudgetExceeded" or "KernelPanic"))
        {
            return false;
        }
    }

    private static Value ForAll(NativeContext c, GenState gen, FunctionObject property, int runs, long seed)
    {
        var rng = new RandomState(seed);
        for (var run = 1; run <= runs; run++)
        {
            var input = gen.Generate(c, rng);
            if (Holds(c, property, input))
            {
                continue;
            }

            var shrinks = 0;
            for (var improved = true; improved && shrinks < 1000;)
            {
                improved = false;
                foreach (var candidate in gen.Shrink(c, input).Take(200))
                {
                    if (!Holds(c, property, candidate))
                    {
                        (input, improved) = (candidate, true);
                        shrinks++;
                        break;
                    }
                }
            }

            throw Fail(c, $"Falsified after {run} test(s) and {shrinks} shrink(s) (seed {seed}): {Show(c, input)}");
        }

        return Value.Null;
    }

    #endregion

    #region bench and debug

    private static void RegisterBenchAndDebug(NativeRegistry r)
    {
        var B = r.Scope($"{Test}.Bench");
        static long Measure(NativeContext c, FunctionObject action)
        {
            var before = c.StepsExecuted;
            c.Invoke(action);
            return c.StepsExecuted - before;
        }

        B("Run/3", (c, a) =>
        {
            var (action, iterations) = (Fn(a[1]), Int32(c, a[2].Bits, "iterations", 1, 10_000_000));
            var total = 0L;
            for (var i = 0; i < iterations; i++)
            {
                total += Measure(c, action);
            }

            return c.NewInstance(c.Class(Test, "BenchResult"), new BenchState(a[0].AsString, iterations, total));
        });
        B("Steps/1", (c, a) => Value.FromInt(Measure(c, Fn(a[0]))));

        var R = r.Scope($"{Test}.BenchResult");
        static BenchState S(NativeContext c, Value v) => c.State<BenchState>(v);
        R("get_Name/0", (c, a) => Value.FromString(S(c, a[0]).Name));
        R("get_Iterations/0", (c, a) => Value.FromInt(S(c, a[0]).Iterations));
        R("get_TotalSteps/0", (c, a) => Value.FromInt(S(c, a[0]).TotalSteps));
        R("get_StepsPerIteration/0", (c, a) => Value.FromFloat((double)S(c, a[0]).TotalSteps / S(c, a[0]).Iterations));
        R("ToString/0", (c, a) => Value.FromString(
            $"{S(c, a[0]).Name}: {S(c, a[0]).TotalSteps.ToString("N0", CultureInfo.InvariantCulture)} steps / {S(c, a[0]).Iterations} = {((double)S(c, a[0]).TotalSteps / S(c, a[0]).Iterations).ToString("0.##", CultureInfo.InvariantCulture)} steps per iteration"));

        var Dbg = r.Scope($"{Test}.Debug");
        Dbg("Assert/2", (c, a) => a[0].AsBool ? Value.Null : throw Fail(c, a[1].AsString));
        Dbg("Trace/1", (c, a) =>
        {
            c.Trace(a[0].AsString);
            return Value.Null;
        });
        Dbg("Inspect/1", (c, a) => Value.FromString(Inspect(c, a[0], 0, [])));
        Dbg("Breakpoint/0", (c, _) =>
        {
            c.Breakpoint();
            return Value.Null;
        });
        Dbg("StepCount/0", (c, _) => Value.FromInt(c.StepsExecuted));
        Dbg("Profile/1", (c, a) =>
        {
            var (steps, start) = (c.StepsExecuted, c.Now);
            c.Invoke(Fn(a[0]));
            return Value.FromString($"{(c.StepsExecuted - steps).ToString("N0", CultureInfo.InvariantCulture)} steps, {TextNatives.Duration(c.Now - start)} simulated");
        });
    }

    /// <summary>Pretty-prints an object graph: fields of script objects, native collections and cache statistics.</summary>
    private static string Inspect(NativeContext c, Value value, int depth, HashSet<object> seen)
    {
        var indent = new string(' ', depth * 2);
        if (value.Object is ObjectInstance { NativeState: null, Class.IsStruct: var isStruct } o && o.Class.Symbol is { } symbol)
        {
            if (!seen.Add(o) || depth > 6)
            {
                return $"{o.Class.Name} {{…}}";
            }

            var sb = new StringBuilder($"{o.Class.Name} {{\n");
            for (var cls = o.Class; cls?.Symbol is { } type; cls = cls.Base)
            {
                foreach (var member in type.Members)
                {
                    var (name, field) = member switch
                    {
                        Semantics.FieldSymbol { IsStatic: false } f => (f.Name, f),
                        Semantics.PropertySymbol { BackingField: { IsStatic: false } backing } p => (p.Name, backing),
                        _ => (null, null),
                    };
                    if (name is not null && field!.Slot >= 0 && field.Slot < o.Fields.Length)
                    {
                        sb.Append(indent).Append("  ").Append(name).Append(": ").Append(Inspect(c, o.Fields[field.Slot], depth + 1, seen)).Append('\n');
                    }
                }
            }

            _ = isStruct;
            _ = symbol;
            return sb.Append(indent).Append('}').ToString();
        }

        return value.Object switch
        {
            string s => $"\"{s}\"",
            ArrayObject arr => $"[{arr.Length}] " + Join(c, arr.Items.Take(16)),
            ObjectInstance { NativeState: CacheState cache } inst => $"{inst.Class.Name} {{ {cache.Stats} }}",
            _ => Display(c, value),
        };
    }

    #endregion
}
