using System;
using System.Collections.Generic;

namespace SoundCalcs.Domain
{
    /// <summary>
    /// A closed 2D polygon representing a detected room boundary.
    /// Vertices are ordered (CW or CCW) and in meters.
    /// </summary>
    public class RoomPolygon
    {
        /// <summary>Ordered vertices of the room boundary (2D, meters).</summary>
        public List<Vec2> Vertices { get; set; } = new List<Vec2>();

        /// <summary>Floor elevation in meters.</summary>
        public double FloorElevationM { get; set; }

        /// <summary>Auto-generated name (e.g. "Room 1").</summary>
        public string Name { get; set; } = "Virtual Room";

        /// <summary>
        /// Axis-aligned bounding box minimum corner.
        /// </summary>
        public Vec2 BoundsMin
        {
            get
            {
                if (Vertices.Count == 0) return Vec2.Zero;
                double minX = double.MaxValue, minY = double.MaxValue;
                foreach (Vec2 v in Vertices)
                {
                    if (v.X < minX) minX = v.X;
                    if (v.Y < minY) minY = v.Y;
                }
                return new Vec2(minX, minY);
            }
        }

        /// <summary>
        /// Axis-aligned bounding box maximum corner.
        /// </summary>
        public Vec2 BoundsMax
        {
            get
            {
                if (Vertices.Count == 0) return Vec2.Zero;
                double maxX = double.MinValue, maxY = double.MinValue;
                foreach (Vec2 v in Vertices)
                {
                    if (v.X > maxX) maxX = v.X;
                    if (v.Y > maxY) maxY = v.Y;
                }
                return new Vec2(maxX, maxY);
            }
        }

        /// <summary>
        /// Signed area of the polygon. Positive = CCW, Negative = CW.
        /// </summary>
        public double SignedArea
        {
            get
            {
                double area = 0;
                int n = Vertices.Count;
                for (int i = 0; i < n; i++)
                {
                    Vec2 current = Vertices[i];
                    Vec2 next = Vertices[(i + 1) % n];
                    area += Vec2.Cross(current, next);
                }
                return area * 0.5;
            }
        }

        /// <summary>Absolute area in square meters.</summary>
        public double Area => Math.Abs(SignedArea);

        /// <summary>
        /// Test whether a 2D point lies inside this polygon using ray casting.
        /// </summary>
        public bool ContainsPoint(Vec2 point)
        {
            int n = Vertices.Count;
            if (n < 3) return false;

            bool inside = false;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                Vec2 vi = Vertices[i];
                Vec2 vj = Vertices[j];

                if ((vi.Y > point.Y) != (vj.Y > point.Y) &&
                    point.X < (vj.X - vi.X) * (point.Y - vi.Y) / (vj.Y - vi.Y) + vi.X)
                {
                    inside = !inside;
                }
            }
            return inside;
        }

        public override string ToString() => $"{Name} ({Vertices.Count} vertices, {Area:F1} m²)";
    }
}
