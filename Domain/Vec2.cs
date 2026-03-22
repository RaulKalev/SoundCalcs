using System;

namespace SoundCalcs.Domain
{
    /// <summary>
    /// Immutable 2D vector for XY-plane polygon math.
    /// Avoids Z-axis confusion when working with plan-view room detection.
    /// </summary>
    public struct Vec2 : IEquatable<Vec2>
    {
        public readonly double X;
        public readonly double Y;

        public Vec2(double x, double y)
        {
            X = x;
            Y = y;
        }

        public static Vec2 Zero => new Vec2(0, 0);

        public double Length => Math.Sqrt(X * X + Y * Y);
        public double LengthSquared => X * X + Y * Y;
        public Vec2 Normalized() { double l = Length; return l > 1e-12 ? new Vec2(X / l, Y / l) : Zero; }

        public static Vec2 operator +(Vec2 a, Vec2 b) => new Vec2(a.X + b.X, a.Y + b.Y);
        public static Vec2 operator -(Vec2 a, Vec2 b) => new Vec2(a.X - b.X, a.Y - b.Y);
        public static Vec2 operator *(Vec2 v, double s) => new Vec2(v.X * s, v.Y * s);
        public static Vec2 operator *(double s, Vec2 v) => v * s;

        public static double Dot(Vec2 a, Vec2 b) => a.X * b.X + a.Y * b.Y;

        /// <summary>
        /// 2D cross product (z-component of 3D cross). Positive = CCW turn.
        /// </summary>
        public static double Cross(Vec2 a, Vec2 b) => a.X * b.Y - a.Y * b.X;

        public static double Distance(Vec2 a, Vec2 b) => (a - b).Length;

        /// <summary>
        /// Angle in radians from positive X-axis, range [-π, π].
        /// </summary>
        public double Angle() => Math.Atan2(Y, X);

        public bool Equals(Vec2 other) =>
            Math.Abs(X - other.X) < 1e-6 &&
            Math.Abs(Y - other.Y) < 1e-6;

        public override bool Equals(object obj) => obj is Vec2 v && Equals(v);
        public override int GetHashCode()
        {
            // Round to tolerance for hashing
            long hx = (long)(Math.Round(X * 1000));
            long hy = (long)(Math.Round(Y * 1000));
            return hx.GetHashCode() ^ (hy.GetHashCode() << 16);
        }
        public override string ToString() => $"({X:F3}, {Y:F3})";

        public static bool operator ==(Vec2 a, Vec2 b) => a.Equals(b);
        public static bool operator !=(Vec2 a, Vec2 b) => !a.Equals(b);
    }
}
