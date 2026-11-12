using System;

namespace RoboDust;

/// Points, y down, origin at the top-left of the work area. Same conventions as the Mac version.
public struct V2
{
    public float X, Y;
    public V2(float x, float y) { X = x; Y = y; }
    public static readonly V2 Zero = new(0, 0);
    public float Len => MathF.Sqrt(X * X + Y * Y);
    public V2 Unit { get { var l = Len; return l > 1e-5f ? new V2(X / l, Y / l) : Zero; } }
    public V2 Perp => new(-Y, X);
    public float Dot(V2 o) => X * o.X + Y * o.Y;
    public V2 Rotated(float a) { float c = MathF.Cos(a), s = MathF.Sin(a); return new V2(c * X - s * Y, s * X + c * Y); }
    public static V2 operator +(V2 a, V2 b) => new(a.X + b.X, a.Y + b.Y);
    public static V2 operator -(V2 a, V2 b) => new(a.X - b.X, a.Y - b.Y);
    public static V2 operator -(V2 a) => new(-a.X, -a.Y);
    public static V2 operator *(V2 a, float k) => new(a.X * k, a.Y * k);
    public static V2 operator *(float k, V2 a) => new(a.X * k, a.Y * k);
    public static V2 operator /(V2 a, float k) => new(a.X / k, a.Y / k);
    public override string ToString() => $"({X:0.0}, {Y:0.0})";
}

public struct V3
{
    public float X, Y, Z;
    public V3(float x, float y, float z) { X = x; Y = y; Z = z; }
}

public static class M
{
    public const float Pi = MathF.PI;
    public static float Clamp(float v, float a, float b) => MathF.Min(MathF.Max(v, a), b);
    public static float Smooth(float a, float b, float x) { var t = Clamp((x - a) / (b - a), 0, 1); return t * t * (3 - 2 * t); }
    public static float Wrap(float a) { var x = a; while (x > Pi) x -= 2 * Pi; while (x < -Pi) x += 2 * Pi; return x; }
}

/// xorshift, seeded, so the sample mess is the same every time.
public sealed class SeededRng
{
    ulong s;
    public SeededRng(ulong seed) { s = unchecked(seed * 0x9E3779B97F4A7C15UL) | 1; }
    public ulong Next() { s ^= s << 13; s ^= s >> 7; s ^= s << 17; return s; }
    /// Uniform in 0...1.
    public float NextFloat() => (float)((Next() >> 11) * (1.0 / 9007199254740992.0));
    public float Range(float a, float b) => a + (b - a) * NextFloat();
    public int Int(int lo, int hiInclusive) => lo + (int)(Next() % (ulong)(hiInclusive - lo + 1));
}
