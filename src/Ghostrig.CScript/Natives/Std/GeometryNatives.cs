using Ghostrig.CScript.Runtime;
using Ghostrig.CScript.Vm;
using static Ghostrig.CScript.Natives.Std.MathNatives;
using static Ghostrig.CScript.Natives.Std.StdSupport;

namespace Ghostrig.CScript.Natives.Std;

/// <summary>Host implementations of the <c>Std.Math</c> vectors, matrices and shapes (all immutable value types).</summary>
internal static class GeometryNatives
{
    private const string M = "Std.Math";

    public static void Register(NativeRegistry r)
    {
        RegisterVector2(r.Scope($"{M}.Vector2"));
        RegisterVector3(r.Scope($"{M}.Vector3"));
        RegisterMatrix3x2(r.Scope($"{M}.Matrix3x2"));
        RegisterMatrix4x4(r.Scope($"{M}.Matrix4x4"));
        RegisterRect(r.Scope($"{M}.Rect"));
        RegisterCircle(r.Scope($"{M}.Circle"));
        RegisterPolygon(r.Scope($"{M}.Polygon"));
        RegisterFunctions(r.Scope(M));
    }

    private static Value Fl(double d) => Value.FromFloat(d);

    private static string N(double d) => ValueFormatting.FormatFloat(d);

    internal static Value V2(NativeContext c, double x, double y) => c.NewInstance(c.Class(M, "Vector2"), new Vec2State(x, y));

    internal static Vec2State P(NativeContext c, Value v) => c.State<Vec2State>(v);

    private static Value V3(NativeContext c, double x, double y, double z) => c.NewInstance(c.Class(M, "Vector3"), new Vec3State(x, y, z));

    private static Vec3State Q(NativeContext c, Value v) => c.State<Vec3State>(v);

    #region vectors

    private static void RegisterVector2(Action<string, StdFunction> F)
    {
        F("Vector2/2", (c, a) => CollectionNatives.Init(a[0], new Vec2State(D(a[1]), D(a[2]))));
        F("get_Zero/0", (c, _) => V2(c, 0, 0));
        F("get_One/0", (c, _) => V2(c, 1, 1));
        F("FromAngle/1", (c, a) => V2(c, SoftMath.Cos(D(a[0])), SoftMath.Sin(D(a[0]))));
        F("get_X/0", (c, a) => Fl(P(c, a[0]).X));
        F("get_Y/0", (c, a) => Fl(P(c, a[0]).Y));
        F("get_Length/0", (c, a) => Fl(SoftMath.Hypot(P(c, a[0]).X, P(c, a[0]).Y)));
        F("get_LengthSquared/0", (c, a) => Fl((P(c, a[0]).X * P(c, a[0]).X) + (P(c, a[0]).Y * P(c, a[0]).Y)));
        F("get_Angle/0", (c, a) => Fl(SoftMath.Atan2(P(c, a[0]).Y, P(c, a[0]).X)));
        F("Add/1", (c, a) => V2(c, P(c, a[0]).X + P(c, a[1]).X, P(c, a[0]).Y + P(c, a[1]).Y));
        F("Subtract/1", (c, a) => V2(c, P(c, a[0]).X - P(c, a[1]).X, P(c, a[0]).Y - P(c, a[1]).Y));
        F("Scale/1", (c, a) => V2(c, P(c, a[0]).X * D(a[1]), P(c, a[0]).Y * D(a[1])));
        F("Negate/0", (c, a) => V2(c, -P(c, a[0]).X, -P(c, a[0]).Y));
        F("Dot/1", (c, a) => Fl((P(c, a[0]).X * P(c, a[1]).X) + (P(c, a[0]).Y * P(c, a[1]).Y)));
        F("Cross/1", (c, a) => Fl((P(c, a[0]).X * P(c, a[1]).Y) - (P(c, a[0]).Y * P(c, a[1]).X)));
        F("Distance/1", (c, a) => Fl(SoftMath.Hypot(P(c, a[0]).X - P(c, a[1]).X, P(c, a[0]).Y - P(c, a[1]).Y)));
        F("Normalized/0", (c, a) =>
        {
            var v = P(c, a[0]);
            var length = SoftMath.Hypot(v.X, v.Y);
            return length == 0 ? V2(c, 0, 0) : V2(c, v.X / length, v.Y / length);
        });
        F("Rotate/1", (c, a) =>
        {
            var v = P(c, a[0]);
            var (cos, sin) = (SoftMath.Cos(D(a[1])), SoftMath.Sin(D(a[1])));
            return V2(c, (v.X * cos) - (v.Y * sin), (v.X * sin) + (v.Y * cos));
        });
        F("Perpendicular/0", (c, a) => V2(c, -P(c, a[0]).Y, P(c, a[0]).X));
        F("Lerp/2", (c, a) =>
        {
            var (v, w, t) = (P(c, a[0]), P(c, a[1]), D(a[2]));
            return V2(c, v.X + ((w.X - v.X) * t), v.Y + ((w.Y - v.Y) * t));
        });
        F("WithX/1", (c, a) => V2(c, D(a[1]), P(c, a[0]).Y));
        F("WithY/1", (c, a) => V2(c, P(c, a[0]).X, D(a[1])));
        F("Equals/1", (c, a) => Value.FromBool(P(c, a[0]).StructurallyEquals(P(c, a[1]))));
        F("HashCode/0", (c, a) => Value.FromInt(P(c, a[0]).StructuralHash()));
        F("ToString/0", (c, a) => Value.FromString($"({N(P(c, a[0]).X)}, {N(P(c, a[0]).Y)})"));
    }

    private static void RegisterVector3(Action<string, StdFunction> F)
    {
        static double Length(Vec3State v) => Math.Sqrt((v.X * v.X) + (v.Y * v.Y) + (v.Z * v.Z));
        F("Vector3/3", (c, a) => CollectionNatives.Init(a[0], new Vec3State(D(a[1]), D(a[2]), D(a[3]))));
        F("get_Zero/0", (c, _) => V3(c, 0, 0, 0));
        F("get_One/0", (c, _) => V3(c, 1, 1, 1));
        F("get_X/0", (c, a) => Fl(Q(c, a[0]).X));
        F("get_Y/0", (c, a) => Fl(Q(c, a[0]).Y));
        F("get_Z/0", (c, a) => Fl(Q(c, a[0]).Z));
        F("get_Length/0", (c, a) => Fl(Length(Q(c, a[0]))));
        F("get_LengthSquared/0", (c, a) => Fl(Length(Q(c, a[0])) * Length(Q(c, a[0]))));
        F("Add/1", (c, a) => V3(c, Q(c, a[0]).X + Q(c, a[1]).X, Q(c, a[0]).Y + Q(c, a[1]).Y, Q(c, a[0]).Z + Q(c, a[1]).Z));
        F("Subtract/1", (c, a) => V3(c, Q(c, a[0]).X - Q(c, a[1]).X, Q(c, a[0]).Y - Q(c, a[1]).Y, Q(c, a[0]).Z - Q(c, a[1]).Z));
        F("Scale/1", (c, a) => V3(c, Q(c, a[0]).X * D(a[1]), Q(c, a[0]).Y * D(a[1]), Q(c, a[0]).Z * D(a[1])));
        F("Negate/0", (c, a) => V3(c, -Q(c, a[0]).X, -Q(c, a[0]).Y, -Q(c, a[0]).Z));
        F("Dot/1", (c, a) => Fl((Q(c, a[0]).X * Q(c, a[1]).X) + (Q(c, a[0]).Y * Q(c, a[1]).Y) + (Q(c, a[0]).Z * Q(c, a[1]).Z)));
        F("Cross/1", (c, a) =>
        {
            var (u, v) = (Q(c, a[0]), Q(c, a[1]));
            return V3(c, (u.Y * v.Z) - (u.Z * v.Y), (u.Z * v.X) - (u.X * v.Z), (u.X * v.Y) - (u.Y * v.X));
        });
        F("Distance/1", (c, a) => Fl(Length(new Vec3State(Q(c, a[0]).X - Q(c, a[1]).X, Q(c, a[0]).Y - Q(c, a[1]).Y, Q(c, a[0]).Z - Q(c, a[1]).Z))));
        F("Normalized/0", (c, a) =>
        {
            var v = Q(c, a[0]);
            var length = Length(v);
            return length == 0 ? V3(c, 0, 0, 0) : V3(c, v.X / length, v.Y / length, v.Z / length);
        });
        F("Lerp/2", (c, a) =>
        {
            var (v, w, t) = (Q(c, a[0]), Q(c, a[1]), D(a[2]));
            return V3(c, v.X + ((w.X - v.X) * t), v.Y + ((w.Y - v.Y) * t), v.Z + ((w.Z - v.Z) * t));
        });
        F("Equals/1", (c, a) => Value.FromBool(Q(c, a[0]).StructurallyEquals(Q(c, a[1]))));
        F("HashCode/0", (c, a) => Value.FromInt(Q(c, a[0]).StructuralHash()));
        F("ToString/0", (c, a) => Value.FromString($"({N(Q(c, a[0]).X)}, {N(Q(c, a[0]).Y)}, {N(Q(c, a[0]).Z)})"));
    }

    #endregion

    #region matrices

    private static Value Mat(NativeContext c, string type, double[] m) => c.NewInstance(c.Class(M, type), new MatrixState(m));

    private static double[] Mx(NativeContext c, Value v) => c.State<MatrixState>(v).M;

    private static void RegisterMatrix3x2(Action<string, StdFunction> F)
    {
        const string T = "Matrix3x2";
        F("Matrix3x2/6", (c, a) => CollectionNatives.Init(a[0], new MatrixState([.. a[1..].Select(D)])));
        F("get_Identity/0", (c, _) => Mat(c, T, [1, 0, 0, 1, 0, 0]));
        F("Translation/2", (c, a) => Mat(c, T, [1, 0, 0, 1, D(a[0]), D(a[1])]));
        F("Rotation/1", (c, a) =>
        {
            var (cos, sin) = (SoftMath.Cos(D(a[0])), SoftMath.Sin(D(a[0])));
            return Mat(c, T, [cos, sin, -sin, cos, 0, 0]);
        });
        F("Scaling/2", (c, a) => Mat(c, T, [D(a[0]), 0, 0, D(a[1]), 0, 0]));
        F("get_Elements/0", (c, a) => c.NewArray([.. Mx(c, a[0]).Select(Fl)]));
        F("Multiply/1", (c, a) =>
        {
            var (x, y) = (Mx(c, a[0]), Mx(c, a[1]));
            return Mat(c, T,
            [
                (x[0] * y[0]) + (x[1] * y[2]), (x[0] * y[1]) + (x[1] * y[3]),
                (x[2] * y[0]) + (x[3] * y[2]), (x[2] * y[1]) + (x[3] * y[3]),
                (x[4] * y[0]) + (x[5] * y[2]) + y[4], (x[4] * y[1]) + (x[5] * y[3]) + y[5],
            ]);
        });
        F("Transform/1", (c, a) =>
        {
            var (m, p) = (Mx(c, a[0]), P(c, a[1]));
            return V2(c, (p.X * m[0]) + (p.Y * m[2]) + m[4], (p.X * m[1]) + (p.Y * m[3]) + m[5]);
        });
        F("Determinant/0", (c, a) => Fl((Mx(c, a[0])[0] * Mx(c, a[0])[3]) - (Mx(c, a[0])[1] * Mx(c, a[0])[2])));
        F("Invert/0", (c, a) =>
        {
            var m = Mx(c, a[0]);
            var det = (m[0] * m[3]) - (m[1] * m[2]);
            if (det == 0)
            {
                throw c.Throw("ArithmeticError", "The matrix is singular.");
            }

            var inv = 1 / det;
            return Mat(c, T,
            [
                m[3] * inv, -m[1] * inv, -m[2] * inv, m[0] * inv,
                ((m[2] * m[5]) - (m[4] * m[3])) * inv, ((m[4] * m[1]) - (m[0] * m[5])) * inv,
            ]);
        });
        F("Equals/1", (c, a) => Value.FromBool(c.State<MatrixState>(a[0]).StructurallyEquals(c.State<MatrixState>(a[1]))));
        F("ToString/0", (c, a) => Value.FromString($"[{string.Join(", ", Mx(c, a[0]).Select(N))}]"));
    }

    private static double[] Multiply4(double[] x, double[] y)
    {
        var r = new double[16];
        for (var i = 0; i < 4; i++)
        {
            for (var j = 0; j < 4; j++)
            {
                r[(i * 4) + j] = (x[i * 4] * y[j]) + (x[(i * 4) + 1] * y[4 + j]) + (x[(i * 4) + 2] * y[8 + j]) + (x[(i * 4) + 3] * y[12 + j]);
            }
        }

        return r;
    }

    /// <summary>Gauss–Jordan with partial pivoting; returns the determinant and (if non-singular) the inverse.</summary>
    private static (double Determinant, double[]? Inverse) Solve4(double[] m)
    {
        var a = (double[])m.Clone();
        var inv = new double[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };
        var det = 1.0;
        for (var col = 0; col < 4; col++)
        {
            var pivot = col;
            for (var row = col + 1; row < 4; row++)
            {
                if (Math.Abs(a[(row * 4) + col]) > Math.Abs(a[(pivot * 4) + col]))
                {
                    pivot = row;
                }
            }

            if (a[(pivot * 4) + col] == 0)
            {
                return (0, null);
            }

            if (pivot != col)
            {
                det = -det;
                for (var k = 0; k < 4; k++)
                {
                    (a[(col * 4) + k], a[(pivot * 4) + k]) = (a[(pivot * 4) + k], a[(col * 4) + k]);
                    (inv[(col * 4) + k], inv[(pivot * 4) + k]) = (inv[(pivot * 4) + k], inv[(col * 4) + k]);
                }
            }

            var p = a[(col * 4) + col];
            det *= p;
            for (var k = 0; k < 4; k++)
            {
                a[(col * 4) + k] /= p;
                inv[(col * 4) + k] /= p;
            }

            for (var row = 0; row < 4; row++)
            {
                if (row == col)
                {
                    continue;
                }

                var factor = a[(row * 4) + col];
                for (var k = 0; k < 4; k++)
                {
                    a[(row * 4) + k] -= factor * a[(col * 4) + k];
                    inv[(row * 4) + k] -= factor * inv[(col * 4) + k];
                }
            }
        }

        return (det, inv);
    }

    private static void RegisterMatrix4x4(Action<string, StdFunction> F)
    {
        const string T = "Matrix4x4";
        double[] Identity() => [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1];
        F("Matrix4x4/1", (c, a) =>
        {
            var items = Items(a[1]);
            return items.Length == 16
                ? CollectionNatives.Init(a[0], new MatrixState([.. items.Select(D)]))
                : throw c.Throw("ArgumentError", "Matrix4x4 needs 16 elements.");
        });
        F("get_Identity/0", (c, _) => Mat(c, T, Identity()));
        F("Translation/3", (c, a) =>
        {
            var m = Identity();
            (m[12], m[13], m[14]) = (D(a[0]), D(a[1]), D(a[2]));
            return Mat(c, T, m);
        });
        F("Scaling/3", (c, a) =>
        {
            var m = Identity();
            (m[0], m[5], m[10]) = (D(a[0]), D(a[1]), D(a[2]));
            return Mat(c, T, m);
        });
        F("RotationX/1", (c, a) =>
        {
            var (cos, sin) = (SoftMath.Cos(D(a[0])), SoftMath.Sin(D(a[0])));
            return Mat(c, T, [1, 0, 0, 0, 0, cos, sin, 0, 0, -sin, cos, 0, 0, 0, 0, 1]);
        });
        F("RotationY/1", (c, a) =>
        {
            var (cos, sin) = (SoftMath.Cos(D(a[0])), SoftMath.Sin(D(a[0])));
            return Mat(c, T, [cos, 0, -sin, 0, 0, 1, 0, 0, sin, 0, cos, 0, 0, 0, 0, 1]);
        });
        F("RotationZ/1", (c, a) =>
        {
            var (cos, sin) = (SoftMath.Cos(D(a[0])), SoftMath.Sin(D(a[0])));
            return Mat(c, T, [cos, sin, 0, 0, -sin, cos, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1]);
        });
        F("Perspective/4", (c, a) =>
        {
            var (fov, aspect, near, far) = (D(a[0]), D(a[1]), D(a[2]), D(a[3]));
            if (!(fov > 0 && fov < SoftMath.Pi) || !(near > 0) || !(far > near) || !(aspect > 0))
            {
                throw c.Throw("ArgumentError", "Perspective needs 0 < fov < π, aspect > 0 and 0 < near < far.");
            }

            var yScale = 1 / SoftMath.Tan(fov / 2);
            return Mat(c, T, [yScale / aspect, 0, 0, 0, 0, yScale, 0, 0, 0, 0, far / (near - far), -1, 0, 0, near * far / (near - far), 0]);
        });
        F("Get/2", (c, a) => Fl(Mx(c, a[0])[(Index(c, a[1].Bits, 4) * 4) + Index(c, a[2].Bits, 4)]));
        F("get_Elements/0", (c, a) => c.NewArray([.. Mx(c, a[0]).Select(Fl)]));
        F("Multiply/1", (c, a) =>
        {
            c.Charge(16);
            return Mat(c, T, Multiply4(Mx(c, a[0]), Mx(c, a[1])));
        });
        F("Transform/1", (c, a) =>
        {
            var (m, p) = (Mx(c, a[0]), Q(c, a[1]));
            var x = (p.X * m[0]) + (p.Y * m[4]) + (p.Z * m[8]) + m[12];
            var y = (p.X * m[1]) + (p.Y * m[5]) + (p.Z * m[9]) + m[13];
            var z = (p.X * m[2]) + (p.Y * m[6]) + (p.Z * m[10]) + m[14];
            var w = (p.X * m[3]) + (p.Y * m[7]) + (p.Z * m[11]) + m[15];
            return w is 1 or 0 ? V3(c, x, y, z) : V3(c, x / w, y / w, z / w);
        });
        F("Transpose/0", (c, a) =>
        {
            var m = Mx(c, a[0]);
            return Mat(c, T, [.. Enumerable.Range(0, 16).Select(i => m[((i % 4) * 4) + (i / 4)])]);
        });
        F("Determinant/0", (c, a) =>
        {
            c.Charge(16);
            return Fl(Solve4(Mx(c, a[0])).Determinant);
        });
        F("Invert/0", (c, a) =>
        {
            c.Charge(32);
            return Solve4(Mx(c, a[0])).Inverse is { } inverse ? Mat(c, T, inverse) : throw c.Throw("ArithmeticError", "The matrix is singular.");
        });
        F("Equals/1", (c, a) => Value.FromBool(c.State<MatrixState>(a[0]).StructurallyEquals(c.State<MatrixState>(a[1]))));
        F("ToString/0", (c, a) =>
        {
            var m = Mx(c, a[0]);
            return Value.FromString(string.Join("\n", Enumerable.Range(0, 4).Select(row => "[" + string.Join(", ", m.Skip(row * 4).Take(4).Select(N)) + "]")));
        });
    }

    #endregion

    #region shapes

    private static Value Rect(NativeContext c, double x, double y, double w, double h) => c.NewInstance(c.Class(M, "Rect"), new RectState(x, y, w, h));

    private static RectState Rc(NativeContext c, Value v) => c.State<RectState>(v);

    private static bool Overlap(RectState a, RectState b) => a.X < b.Right && b.X < a.Right && a.Y < b.Bottom && b.Y < a.Bottom;

    private static void RegisterRect(Action<string, StdFunction> F)
    {
        F("Rect/4", (c, a) => D(a[3]) >= 0 && D(a[4]) >= 0
            ? CollectionNatives.Init(a[0], new RectState(D(a[1]), D(a[2]), D(a[3]), D(a[4])))
            : throw c.Throw("ArgumentError", "A rectangle cannot have a negative size."));
        F("FromCorners/2", (c, a) =>
        {
            var (p, q) = (P(c, a[0]), P(c, a[1]));
            return Rect(c, Math.Min(p.X, q.X), Math.Min(p.Y, q.Y), Math.Abs(p.X - q.X), Math.Abs(p.Y - q.Y));
        });
        F("get_X/0", (c, a) => Fl(Rc(c, a[0]).X));
        F("get_Y/0", (c, a) => Fl(Rc(c, a[0]).Y));
        F("get_Width/0", (c, a) => Fl(Rc(c, a[0]).W));
        F("get_Height/0", (c, a) => Fl(Rc(c, a[0]).H));
        F("get_Left/0", (c, a) => Fl(Rc(c, a[0]).X));
        F("get_Top/0", (c, a) => Fl(Rc(c, a[0]).Y));
        F("get_Right/0", (c, a) => Fl(Rc(c, a[0]).Right));
        F("get_Bottom/0", (c, a) => Fl(Rc(c, a[0]).Bottom));
        F("get_Center/0", (c, a) => V2(c, Rc(c, a[0]).X + (Rc(c, a[0]).W / 2), Rc(c, a[0]).Y + (Rc(c, a[0]).H / 2)));
        F("get_Area/0", (c, a) => Fl(Rc(c, a[0]).W * Rc(c, a[0]).H));
        F("Contains/1", (c, a) =>
        {
            var (r, p) = (Rc(c, a[0]), P(c, a[1]));
            return Value.FromBool(p.X >= r.X && p.X < r.Right && p.Y >= r.Y && p.Y < r.Bottom);
        });
        F("ContainsRect/1", (c, a) =>
        {
            var (r, o) = (Rc(c, a[0]), Rc(c, a[1]));
            return Value.FromBool(o.X >= r.X && o.Right <= r.Right && o.Y >= r.Y && o.Bottom <= r.Bottom);
        });
        F("Intersects/1", (c, a) => Value.FromBool(Overlap(Rc(c, a[0]), Rc(c, a[1]))));
        F("Intersection/1", (c, a) =>
        {
            var (r, o) = (Rc(c, a[0]), Rc(c, a[1]));
            if (!Overlap(r, o))
            {
                return Value.Null;
            }

            var (x, y) = (Math.Max(r.X, o.X), Math.Max(r.Y, o.Y));
            return Rect(c, x, y, Math.Min(r.Right, o.Right) - x, Math.Min(r.Bottom, o.Bottom) - y);
        });
        F("Union/1", (c, a) =>
        {
            var (r, o) = (Rc(c, a[0]), Rc(c, a[1]));
            var (x, y) = (Math.Min(r.X, o.X), Math.Min(r.Y, o.Y));
            return Rect(c, x, y, Math.Max(r.Right, o.Right) - x, Math.Max(r.Bottom, o.Bottom) - y);
        });
        F("Inflate/1", (c, a) =>
        {
            var (r, d) = (Rc(c, a[0]), D(a[1]));
            return Rect(c, r.X - d, r.Y - d, Math.Max(0, r.W + (2 * d)), Math.Max(0, r.H + (2 * d)));
        });
        F("Offset/1", (c, a) => Rect(c, Rc(c, a[0]).X + P(c, a[1]).X, Rc(c, a[0]).Y + P(c, a[1]).Y, Rc(c, a[0]).W, Rc(c, a[0]).H));
        F("Equals/1", (c, a) => Value.FromBool(Rc(c, a[0]).StructurallyEquals(Rc(c, a[1]))));
        F("HashCode/0", (c, a) => Value.FromInt(Rc(c, a[0]).StructuralHash()));
        F("ToString/0", (c, a) => Value.FromString($"Rect({N(Rc(c, a[0]).X)}, {N(Rc(c, a[0]).Y)}, {N(Rc(c, a[0]).W)}×{N(Rc(c, a[0]).H)})"));
    }

    private static void RegisterCircle(Action<string, StdFunction> F)
    {
        static CircleState C(NativeContext c, Value v) => c.State<CircleState>(v);
        F("Circle/2", (c, a) => D(a[2]) >= 0
            ? CollectionNatives.Init(a[0], new CircleState(P(c, a[1]).X, P(c, a[1]).Y, D(a[2])))
            : throw c.Throw("ArgumentError", "A circle cannot have a negative radius."));
        F("get_Center/0", (c, a) => V2(c, C(c, a[0]).X, C(c, a[0]).Y));
        F("get_Radius/0", (c, a) => Fl(C(c, a[0]).R));
        F("get_Area/0", (c, a) => Fl(SoftMath.Pi * C(c, a[0]).R * C(c, a[0]).R));
        F("Contains/1", (c, a) =>
        {
            var (s, p) = (C(c, a[0]), P(c, a[1]));
            var (dx, dy) = (p.X - s.X, p.Y - s.Y);
            return Value.FromBool((dx * dx) + (dy * dy) <= s.R * s.R);
        });
        F("Intersects/1", (c, a) =>
        {
            var (s, o) = (C(c, a[0]), C(c, a[1]));
            var (dx, dy, r) = (s.X - o.X, s.Y - o.Y, s.R + o.R);
            return Value.FromBool((dx * dx) + (dy * dy) <= r * r);
        });
        F("IntersectsRect/1", (c, a) =>
        {
            var (s, r) = (C(c, a[0]), Rc(c, a[1]));
            var (nx, ny) = (Math.Clamp(s.X, r.X, r.Right), Math.Clamp(s.Y, r.Y, r.Bottom));
            var (dx, dy) = (s.X - nx, s.Y - ny);
            return Value.FromBool((dx * dx) + (dy * dy) <= s.R * s.R);
        });
        F("get_Bounds/0", (c, a) => Rect(c, C(c, a[0]).X - C(c, a[0]).R, C(c, a[0]).Y - C(c, a[0]).R, 2 * C(c, a[0]).R, 2 * C(c, a[0]).R));
        F("Equals/1", (c, a) => Value.FromBool(C(c, a[0]).StructurallyEquals(C(c, a[1]))));
        F("ToString/0", (c, a) => Value.FromString($"Circle(({N(C(c, a[0]).X)}, {N(C(c, a[0]).Y)}), r {N(C(c, a[0]).R)})"));
    }

    private static PolygonState Pg(NativeContext c, Value v) => c.State<PolygonState>(v);

    private static bool Inside((double X, double Y)[] points, double x, double y)
    {
        var inside = false;
        for (int i = 0, j = points.Length - 1; i < points.Length; j = i++)
        {
            if ((points[i].Y > y) != (points[j].Y > y)
                && x < ((points[j].X - points[i].X) * (y - points[i].Y) / (points[j].Y - points[i].Y)) + points[i].X)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    private static bool Separated((double X, double Y)[] a, (double X, double Y)[] b)
    {
        for (var i = 0; i < a.Length; i++)
        {
            var (p, q) = (a[i], a[(i + 1) % a.Length]);
            var (nx, ny) = (q.Y - p.Y, p.X - q.X);
            var (minA, maxA) = (double.PositiveInfinity, double.NegativeInfinity);
            var (minB, maxB) = (double.PositiveInfinity, double.NegativeInfinity);
            foreach (var v in a)
            {
                var d = (v.X * nx) + (v.Y * ny);
                (minA, maxA) = (Math.Min(minA, d), Math.Max(maxA, d));
            }

            foreach (var v in b)
            {
                var d = (v.X * nx) + (v.Y * ny);
                (minB, maxB) = (Math.Min(minB, d), Math.Max(maxB, d));
            }

            if (maxA < minB || maxB < minA)
            {
                return true;
            }
        }

        return false;
    }

    private static void RegisterPolygon(Action<string, StdFunction> F)
    {
        F("Polygon/1", (c, a) =>
        {
            var points = Items(a[1]).Select(v => (P(c, v).X, P(c, v).Y)).ToArray();
            return points.Length >= 3
                ? CollectionNatives.Init(a[0], new PolygonState(points))
                : throw c.Throw("ArgumentError", "A polygon needs at least three vertices.");
        });
        F("get_Vertices/0", (c, a) => c.NewArray([.. Pg(c, a[0]).Points.Select(p => V2(c, p.X, p.Y))]));
        F("get_Count/0", (c, a) => Value.FromInt(Pg(c, a[0]).Points.Length));
        F("Contains/1", (c, a) =>
        {
            var points = Pg(c, a[0]).Points;
            c.Charge(points.Length);
            return Value.FromBool(Inside(points, P(c, a[1]).X, P(c, a[1]).Y));
        });
        F("get_SignedArea/0", (c, a) => Fl(Pg(c, a[0]).SignedArea));
        F("get_Area/0", (c, a) => Fl(Math.Abs(Pg(c, a[0]).SignedArea)));
        F("get_Perimeter/0", (c, a) =>
        {
            var p = Pg(c, a[0]).Points;
            return Fl(p.Select((v, i) => SoftMath.Hypot(p[(i + 1) % p.Length].X - v.X, p[(i + 1) % p.Length].Y - v.Y)).Sum());
        });
        F("get_Centroid/0", (c, a) =>
        {
            var polygon = Pg(c, a[0]);
            var p = polygon.Points;
            var area = polygon.SignedArea;
            if (area == 0)
            {
                return V2(c, p.Average(v => v.X), p.Average(v => v.Y));
            }

            double cx = 0, cy = 0;
            for (var i = 0; i < p.Length; i++)
            {
                var (u, v) = (p[i], p[(i + 1) % p.Length]);
                var cross = (u.X * v.Y) - (v.X * u.Y);
                cx += (u.X + v.X) * cross;
                cy += (u.Y + v.Y) * cross;
            }

            return V2(c, cx / (6 * area), cy / (6 * area));
        });
        F("get_Bounds/0", (c, a) =>
        {
            var p = Pg(c, a[0]).Points;
            var (x, y) = (p.Min(v => v.X), p.Min(v => v.Y));
            return Rect(c, x, y, p.Max(v => v.X) - x, p.Max(v => v.Y) - y);
        });
        F("get_IsConvex/0", (c, a) =>
        {
            var p = Pg(c, a[0]).Points;
            var sign = 0;
            for (var i = 0; i < p.Length; i++)
            {
                var (u, v, w) = (p[i], p[(i + 1) % p.Length], p[(i + 2) % p.Length]);
                var cross = ((v.X - u.X) * (w.Y - v.Y)) - ((v.Y - u.Y) * (w.X - v.X));
                if (cross != 0)
                {
                    var s = Math.Sign(cross);
                    if (sign != 0 && s != sign)
                    {
                        return Value.False;
                    }

                    sign = s;
                }
            }

            return Value.True;
        });
        F("Intersects/1", (c, a) =>
        {
            var (p, q) = (Pg(c, a[0]).Points, Pg(c, a[1]).Points);
            c.Charge((long)p.Length * q.Length / 4);
            return Value.FromBool(!Separated(p, q) && !Separated(q, p));
        });
        F("Transform/1", (c, a) =>
        {
            var m = Mx(c, a[1]);
            var points = Pg(c, a[0]).Points.Select(p => ((p.X * m[0]) + (p.Y * m[2]) + m[4], (p.X * m[1]) + (p.Y * m[3]) + m[5])).ToArray();
            return c.NewInstance(c.Class(M, "Polygon"), new PolygonState(points));
        });
        F("ToString/0", (c, a) => Value.FromString($"Polygon({string.Join(", ", Pg(c, a[0]).Points.Select(p => $"({N(p.X)}, {N(p.Y)})"))})"));
    }

    private static void RegisterFunctions(Action<string, StdFunction> G)
    {
        G("SegmentIntersection/4", (c, a) =>
        {
            var (p, p2, q, q2) = (P(c, a[0]), P(c, a[1]), P(c, a[2]), P(c, a[3]));
            var (rx, ry, sx, sy) = (p2.X - p.X, p2.Y - p.Y, q2.X - q.X, q2.Y - q.Y);
            var denominator = (rx * sy) - (ry * sx);
            if (denominator == 0)
            {
                return Value.Null;
            }

            var t = (((q.X - p.X) * sy) - ((q.Y - p.Y) * sx)) / denominator;
            var u = (((q.X - p.X) * ry) - ((q.Y - p.Y) * rx)) / denominator;
            return t is >= 0 and <= 1 && u is >= 0 and <= 1 ? V2(c, p.X + (t * rx), p.Y + (t * ry)) : Value.Null;
        });
        G("DistanceToSegment/3", (c, a) =>
        {
            var (p, s, e) = (P(c, a[0]), P(c, a[1]), P(c, a[2]));
            var (dx, dy) = (e.X - s.X, e.Y - s.Y);
            var lengthSquared = (dx * dx) + (dy * dy);
            var t = lengthSquared == 0 ? 0 : Math.Clamp((((p.X - s.X) * dx) + ((p.Y - s.Y) * dy)) / lengthSquared, 0, 1);
            return Fl(SoftMath.Hypot(p.X - (s.X + (t * dx)), p.Y - (s.Y + (t * dy))));
        });
        G("ConvexHull/1", (c, a) =>
        {
            var points = Items(a[0]).Select(v => (P(c, v).X, P(c, v).Y)).Distinct().OrderBy(p => p.X).ThenBy(p => p.Y).ToArray();
            c.Charge(points.Length * (1 + (long)Math.Log2(points.Length + 1)));
            if (points.Length < 3)
            {
                return c.NewArray([.. points.Select(p => V2(c, p.X, p.Y))]);
            }

            static double Cross((double X, double Y) o, (double X, double Y) a, (double X, double Y) b) => ((a.X - o.X) * (b.Y - o.Y)) - ((a.Y - o.Y) * (b.X - o.X));
            var hull = new List<(double X, double Y)>();
            foreach (var pass in new[] { points, points.Reverse().ToArray() })
            {
                var start = hull.Count;
                foreach (var p in pass)
                {
                    while (hull.Count >= start + 2 && Cross(hull[^2], hull[^1], p) <= 0)
                    {
                        hull.RemoveAt(hull.Count - 1);
                    }

                    hull.Add(p);
                }

                hull.RemoveAt(hull.Count - 1);
            }

            return c.NewArray([.. hull.Select(p => V2(c, p.X, p.Y))]);
        });
    }

    #endregion
}
