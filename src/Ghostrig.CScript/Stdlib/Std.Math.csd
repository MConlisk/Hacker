/// Mathematics (Document 7 §13.5). Floating-point functions are deterministic software implementations (D7): the same
/// inputs give bit-identical results on every machine. Integer arithmetic wraps (D6); the Checked* functions raise
/// ArithmeticError instead. Value types here (BigInt, Rational, Complex, Fixed, vectors, matrices, shapes) are immutable
/// and compare by value with ==, Equals and as collection keys.
module Std.Math;

const float Pi = 3.141592653589793;
const float Tau = 6.283185307179586;
const float E = 2.718281828459045;

// ---------------------------------------------------------------- basic functions

@Native("Std.Math.AbsInt") int Abs(int x);
@Native("Std.Math.AbsFloat") float Abs(float x);
@Native("Std.Math.MinInt") int Min(int a, int b);
@Native("Std.Math.MinFloat") float Min(float a, float b);
@Native("Std.Math.MaxInt") int Max(int a, int b);
@Native("Std.Math.MaxFloat") float Max(float a, float b);
@Native("Std.Math.ClampInt") int Clamp(int value, int min, int max);
@Native("Std.Math.ClampFloat") float Clamp(float value, float min, float max);
@Native("Std.Math.SignInt") int Sign(int x);
@Native("Std.Math.SignFloat") int Sign(float x);
float Pow(float x, float y);
float Sqrt(float x);
float Cbrt(float x);
float Exp(float x);
float Log(float x);
/// Logarithm in an arbitrary base.
float Log(float x, float newBase);
float Log2(float x);
float Log10(float x);
float Floor(float x);
float Ceil(float x);
/// Rounds half away from zero.
float Round(float x);
/// Rounds to a number of decimal digits (half away from zero).
float Round(float x, int digits);
float Trunc(float x);
/// Floor as an integer (raises ArithmeticError outside the int range).
int FloorToInt(float x);
int RoundToInt(float x);
float Sin(float x);
float Cos(float x);
float Tan(float x);
float Asin(float x);
float Acos(float x);
float Atan(float x);
float Atan2(float y, float x);
float Sinh(float x);
float Cosh(float x);
float Tanh(float x);
float Hypot(float x, float y);
float ToRadians(float degrees);
float ToDegrees(float radians);
float Lerp(float a, float b, float t);
float InverseLerp(float a, float b, float value);
/// Maps value from [fromMin, fromMax] to [toMin, toMax].
float Remap(float value, float fromMin, float fromMax, float toMin, float toMax);
/// True when |a − b| ≤ tolerance.
bool ApproxEqual(float a, float b, float tolerance);

// ---------------------------------------------------------------- integers

int Gcd(int a, int b);
int Lcm(int a, int b);
/// base^exponent mod modulus without overflow (128-bit intermediate).
int ModPow(int value, int exponent, int modulus);
/// The inverse of value modulo modulus; raises ArithmeticError when none exists.
int ModInverse(int value, int modulus);
/// Deterministic Miller–Rabin, exact for every 64-bit integer.
bool IsPrime(int n);
int NextPrime(int n);
/// Prime factors in ascending order, with multiplicity.
int[] Factorize(int n);
/// Integer square root (floor).
int ISqrt(int n);
/// Floor division and the matching non-negative modulus (Python semantics).
int FloorDiv(int a, int b);
int FloorMod(int a, int b);
int PopCount(int x);
int LeadingZeros(int x);
int TrailingZeros(int x);
int RotateLeft(int x, int count);
int RotateRight(int x, int count);
bool IsPowerOfTwo(int x);
int NextPowerOfTwo(int x);
/// Overflow-checked arithmetic (ArithmeticError on overflow).
int CheckedAdd(int a, int b);
int CheckedSub(int a, int b);
int CheckedMul(int a, int b);
int CheckedNeg(int a);
int Factorial(int n);
int Binomial(int n, int k);

// ---------------------------------------------------------------- number types

/// Arbitrary-precision integer (RSA-style puzzles). Costs grow with digit count.
class BigInt : Comparable<BigInt>, Equatable<BigInt>, Hashable {
    BigInt(int value);
    static BigInt Zero { get; }
    static BigInt One { get; }
    /// Decimal, or hexadecimal with a 0x prefix.
    static BigInt Parse(string text);
    /// Unsigned big-endian bytes.
    static BigInt FromBytes(bytes data);
    int Sign { get; }
    bool IsZero { get; }
    bool IsEven { get; }
    int BitLength { get; }
    BigInt Add(BigInt other);
    BigInt Subtract(BigInt other);
    BigInt Multiply(BigInt other);
    /// Truncating division; raises ArithmeticError on division by zero.
    BigInt Divide(BigInt other);
    /// Remainder with the sign of the dividend.
    BigInt Remainder(BigInt other);
    /// Non-negative modulus.
    BigInt Mod(BigInt modulus);
    BigInt Negate();
    BigInt Abs();
    BigInt Pow(int exponent);
    BigInt ModPow(BigInt exponent, BigInt modulus);
    BigInt ModInverse(BigInt modulus);
    BigInt Gcd(BigInt other);
    BigInt Sqrt();
    BigInt ShiftLeft(int bits);
    BigInt ShiftRight(int bits);
    bool IsProbablePrime();
    /// Raises ArithmeticError when the value does not fit in an int.
    int ToInt();
    float ToFloat();
    /// Unsigned big-endian bytes of a non-negative value.
    bytes ToBytes();
    string ToHex();
    int CompareTo(BigInt other);
    bool Equals(BigInt other);
    int HashCode();
    string ToString();
}

/// An exact fraction in lowest terms with a positive denominator.
class Rational : Comparable<Rational>, Equatable<Rational>, Hashable {
    Rational(int numerator, int denominator);
    /// "3/4", "-2" or "0.125".
    static Rational Parse(string text);
    static Rational FromBig(BigInt numerator, BigInt denominator);
    BigInt Numerator { get; }
    BigInt Denominator { get; }
    Rational Add(Rational other);
    Rational Subtract(Rational other);
    Rational Multiply(Rational other);
    Rational Divide(Rational other);
    Rational Negate();
    Rational Reciprocal();
    Rational Pow(int exponent);
    BigInt Floor();
    float ToFloat();
    int CompareTo(Rational other);
    bool Equals(Rational other);
    int HashCode();
    string ToString();
}

/// A complex number of two floats.
class Complex : Equatable<Complex>, Hashable {
    Complex(float real, float imaginary);
    static Complex FromPolar(float magnitude, float phase);
    float Real { get; }
    float Imaginary { get; }
    float Magnitude { get; }
    float Phase { get; }
    Complex Add(Complex other);
    Complex Subtract(Complex other);
    Complex Multiply(Complex other);
    Complex Divide(Complex other);
    Complex Conjugate();
    Complex Exp();
    Complex Pow(int exponent);
    bool Equals(Complex other);
    int HashCode();
    string ToString();
}

/// Q32.32 fixed-point number: exact, wrapping arithmetic with 32 fractional bits.
class Fixed : Comparable<Fixed>, Equatable<Fixed>, Hashable {
    Fixed(int whole);
    static Fixed FromFloat(float value);
    static Fixed FromRaw(int raw);
    /// "12.5" style decimal text.
    static Fixed Parse(string text);
    int Raw { get; }
    Fixed Add(Fixed other);
    Fixed Subtract(Fixed other);
    Fixed Multiply(Fixed other);
    Fixed Divide(Fixed other);
    Fixed Negate();
    Fixed Abs();
    Fixed Floor();
    Fixed Sqrt();
    int ToInt();
    float ToFloat();
    int CompareTo(Fixed other);
    bool Equals(Fixed other);
    int HashCode();
    string ToString();
}

// ---------------------------------------------------------------- geometry

class Vector2 : Equatable<Vector2>, Hashable {
    Vector2(float x, float y);
    static Vector2 Zero { get; }
    static Vector2 One { get; }
    static Vector2 FromAngle(float radians);
    float X { get; }
    float Y { get; }
    float Length { get; }
    float LengthSquared { get; }
    /// Angle from the +X axis in radians.
    float Angle { get; }
    Vector2 Add(Vector2 other);
    Vector2 Subtract(Vector2 other);
    Vector2 Scale(float factor);
    Vector2 Negate();
    float Dot(Vector2 other);
    /// The z component of the 3-D cross product.
    float Cross(Vector2 other);
    float Distance(Vector2 other);
    /// Unit vector (Zero stays Zero).
    Vector2 Normalized();
    Vector2 Rotate(float radians);
    Vector2 Perpendicular();
    Vector2 Lerp(Vector2 target, float t);
    Vector2 WithX(float x);
    Vector2 WithY(float y);
    bool Equals(Vector2 other);
    int HashCode();
    string ToString();
}

class Vector3 : Equatable<Vector3>, Hashable {
    Vector3(float x, float y, float z);
    static Vector3 Zero { get; }
    static Vector3 One { get; }
    float X { get; }
    float Y { get; }
    float Z { get; }
    float Length { get; }
    float LengthSquared { get; }
    Vector3 Add(Vector3 other);
    Vector3 Subtract(Vector3 other);
    Vector3 Scale(float factor);
    Vector3 Negate();
    float Dot(Vector3 other);
    Vector3 Cross(Vector3 other);
    float Distance(Vector3 other);
    Vector3 Normalized();
    Vector3 Lerp(Vector3 target, float t);
    bool Equals(Vector3 other);
    int HashCode();
    string ToString();
}

/// A 2-D affine transform [m11 m12; m21 m22; dx dy] applied to row vectors.
class Matrix3x2 : Equatable<Matrix3x2> {
    Matrix3x2(float m11, float m12, float m21, float m22, float dx, float dy);
    static Matrix3x2 Identity { get; }
    static Matrix3x2 Translation(float x, float y);
    static Matrix3x2 Rotation(float radians);
    static Matrix3x2 Scaling(float x, float y);
    float[] Elements { get; }
    /// this, then other.
    Matrix3x2 Multiply(Matrix3x2 other);
    Vector2 Transform(Vector2 point);
    float Determinant();
    /// Raises ArithmeticError for a singular matrix.
    Matrix3x2 Invert();
    bool Equals(Matrix3x2 other);
    string ToString();
}

/// A 4×4 matrix for 3-D transforms (row vectors, row-major).
class Matrix4x4 : Equatable<Matrix4x4> {
    /// Sixteen elements, row-major.
    Matrix4x4(float[] elements);
    static Matrix4x4 Identity { get; }
    static Matrix4x4 Translation(float x, float y, float z);
    static Matrix4x4 Scaling(float x, float y, float z);
    static Matrix4x4 RotationX(float radians);
    static Matrix4x4 RotationY(float radians);
    static Matrix4x4 RotationZ(float radians);
    /// Right-handed perspective projection.
    static Matrix4x4 Perspective(float fieldOfView, float aspect, float near, float far);
    float Get(int row, int column);
    float[] Elements { get; }
    Matrix4x4 Multiply(Matrix4x4 other);
    Vector3 Transform(Vector3 point);
    Matrix4x4 Transpose();
    float Determinant();
    Matrix4x4 Invert();
    bool Equals(Matrix4x4 other);
    string ToString();
}

/// Axis-aligned rectangle; contains points with Left ≤ x < Right and Top ≤ y < Bottom.
class Rect : Equatable<Rect>, Hashable {
    Rect(float x, float y, float width, float height);
    static Rect FromCorners(Vector2 a, Vector2 b);
    float X { get; }
    float Y { get; }
    float Width { get; }
    float Height { get; }
    float Left { get; }
    float Top { get; }
    float Right { get; }
    float Bottom { get; }
    Vector2 Center { get; }
    float Area { get; }
    bool Contains(Vector2 point);
    bool ContainsRect(Rect other);
    bool Intersects(Rect other);
    /// The overlap, or null when the rectangles do not intersect.
    Rect? Intersection(Rect other);
    Rect Union(Rect other);
    Rect Inflate(float amount);
    Rect Offset(Vector2 delta);
    bool Equals(Rect other);
    int HashCode();
    string ToString();
}

class Circle : Equatable<Circle> {
    Circle(Vector2 center, float radius);
    Vector2 Center { get; }
    float Radius { get; }
    float Area { get; }
    bool Contains(Vector2 point);
    bool Intersects(Circle other);
    bool IntersectsRect(Rect rect);
    Rect Bounds { get; }
    bool Equals(Circle other);
    string ToString();
}

/// A simple polygon (vertices in order).
class Polygon {
    Polygon(Vector2[] vertices);
    Vector2[] Vertices { get; }
    int Count { get; }
    /// Even–odd rule.
    bool Contains(Vector2 point);
    /// Signed area: positive for counter-clockwise vertices (y up).
    float SignedArea { get; }
    float Area { get; }
    float Perimeter { get; }
    Vector2 Centroid { get; }
    Rect Bounds { get; }
    bool IsConvex { get; }
    /// Separating-axis test (convex polygons).
    bool Intersects(Polygon other);
    Polygon Transform(Matrix3x2 matrix);
    string ToString();
}

/// Intersection point of segments a1–a2 and b1–b2, or null.
Vector2? SegmentIntersection(Vector2 a1, Vector2 a2, Vector2 b1, Vector2 b2);
/// Distance from point to the segment a–b.
float DistanceToSegment(Vector2 point, Vector2 a, Vector2 b);
/// Convex hull (Andrew's monotone chain), counter-clockwise.
Vector2[] ConvexHull(Vector2[] points);
