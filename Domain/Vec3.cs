using System;

namespace SoundCalcs.Domain
{
    /// <summary>
    /// Immutable 3D vector in meters. Used throughout the compute layer
    /// so that no Revit types leak outside Revit/.
    /// </summary>
    public struct Vec3 : IEquatable<Vec3>
    {
        public readonly double X;
        public readonly double Y;
        public readonly double Z;

        public Vec3(double x, double y, double z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public static Vec3 Zero => new Vec3(0, 0, 0);
        public static Vec3 UnitZ => new Vec3(0, 0, 1);

        public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);
        public double LengthSquared => X * X + Y * Y + Z * Z;

        public Vec3 Normalized()
        {
            double len = Length;
            if (len < 1e-12) return Zero;
            return new Vec3(X / len, Y / len, Z / len);
        }

        public static Vec3 operator +(Vec3 a, Vec3 b) => new Vec3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Vec3 operator -(Vec3 a, Vec3 b) => new Vec3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Vec3 operator *(Vec3 v, double s) => new Vec3(v.X * s, v.Y * s, v.Z * s);
        public static Vec3 operator *(double s, Vec3 v) => v * s;
        public static Vec3 operator /(Vec3 v, double s) => new Vec3(v.X / s, v.Y / s, v.Z / s);
        public static Vec3 operator -(Vec3 v) => new Vec3(-v.X, -v.Y, -v.Z);

        public static double Dot(Vec3 a, Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

        public static Vec3 Cross(Vec3 a, Vec3 b) => new Vec3(
            a.Y * b.Z - a.Z * b.Y,
            a.Z * b.X - a.X * b.Z,
            a.X * b.Y - a.Y * b.X);

        public static double Distance(Vec3 a, Vec3 b) => (a - b).Length;

        public static double DistanceSquared(Vec3 a, Vec3 b) => (a - b).LengthSquared;

        public bool Equals(Vec3 other) =>
            Math.Abs(X - other.X) < 1e-9 &&
            Math.Abs(Y - other.Y) < 1e-9 &&
            Math.Abs(Z - other.Z) < 1e-9;

        public override bool Equals(object obj) => obj is Vec3 v && Equals(v);
        public override int GetHashCode() => X.GetHashCode() ^ (Y.GetHashCode() << 2) ^ (Z.GetHashCode() >> 2);
        public override string ToString() => $"({X:F3}, {Y:F3}, {Z:F3})";

        public static bool operator ==(Vec3 a, Vec3 b) => a.Equals(b);
        public static bool operator !=(Vec3 a, Vec3 b) => !a.Equals(b);
    }
}
