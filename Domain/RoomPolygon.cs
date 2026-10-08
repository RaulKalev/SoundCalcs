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

        /// <summary>
        /// Areas inside the outline that are not part of the room (a free-standing closet or shaft whose own walls
        /// form another room). Excluded from <see cref="Area"/> and <see cref="ContainsPoint"/>.
        /// </summary>
        public List<List<Vec2>> Holes { get; set; } = new List<List<Vec2>>();

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

        /// <summary>Area in square meters, holes excluded.</summary>
        public double Area
        {
            get
            {
                double a = Math.Abs(SignedArea);
                if (Holes != null)
                    foreach (List<Vec2> h in Holes) a -= Math.Abs(SignedAreaOf(h));
                return Math.Max(0, a);
            }
        }

        private static double SignedAreaOf(List<Vec2> pts)
        {
            double area = 0;
            for (int i = 0; i < pts.Count; i++) area += Vec2.Cross(pts[i], pts[(i + 1) % pts.Count]);
            return area * 0.5;
        }

        /// <summary>
        /// Floor area to use for the room's volume when it differs from the polygon's area —
        /// the "open area" remainder of a boundary after the enclosed rooms are taken out.
        /// </summary>
        public double? AreaOverrideM2 { get; set; }

        /// <summary>Floor area used for volume and reverberation.</summary>
        public double EffectiveAreaM2 => AreaOverrideM2 ?? Area;

        /// <summary>
        /// Per-band RT60 for this room (7 values). Null = use the job's global RT60.
        /// </summary>
        public double[] RT60ByBand { get; set; }

        /// <summary>Total perimeter length in meters.</summary>
        public double Perimeter
        {
            get
            {
                double p = 0;
                foreach (List<Vec2> ring in Rings())
                    for (int i = 0; i < ring.Count; i++)
                        p += Vec2.Distance(ring[i], ring[(i + 1) % ring.Count]);
                return p;
            }
        }

        /// <summary>
        /// Fraction of the polygon perimeter backed by actual wall segments (0.0–1.0).
        /// Set by <see cref="RoomDetector.ComputeEnclosureRatios"/>.
        /// </summary>
        public double EnclosureRatio { get; set; } = 1.0;

        /// <summary>
        /// Total length of perimeter edges matched to wall segments (meters).
        /// Set by <see cref="RoomDetector.ComputeEnclosureRatios"/>.
        /// </summary>
        public double WallCoverageM { get; set; }

        /// <summary>
        /// Test whether a 2D point lies inside this polygon using ray casting.
        /// </summary>
        public bool ContainsPoint(Vec2 point)
        {
            if (!InRing(point, Vertices)) return false;
            if (Holes != null)
                foreach (List<Vec2> h in Holes)
                    if (InRing(point, h)) return false;
            return true;
        }

        /// <summary>The outline followed by the holes' outlines.</summary>
        public IEnumerable<List<Vec2>> Rings()
        {
            yield return Vertices;
            if (Holes != null)
                foreach (List<Vec2> h in Holes) yield return h;
        }

        private static bool InRing(Vec2 point, List<Vec2> ring)
        {
            int n = ring.Count;
            if (n < 3) return false;

            bool inside = false;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                Vec2 vi = ring[i];
                Vec2 vj = ring[j];

                if ((vi.Y > point.Y) != (vj.Y > point.Y) &&
                    point.X < (vj.X - vi.X) * (point.Y - vi.Y) / (vj.Y - vi.Y) + vi.X)
                {
                    inside = !inside;
                }
            }
            return inside;
        }

        public override string ToString() => $"{Name} ({Vertices.Count} vertices, {Area:F1} m²)";

        /// <summary>
        /// Whether this room contains at least one speaker.
        /// Set by <see cref="RoomDetector.MarkRoomsWithSpeakers"/>.
        /// </summary>
        public bool ContainsSpeaker { get; set; }

        /// <summary>
        /// Ceiling height in meters. Derived from the tallest speaker’s
        /// <c>ElevationFromLevelM</c> in this room (speakers are ceiling-mounted).
        /// Falls back to 0 (use default) if no speakers are present.
        /// </summary>
        public double CeilingHeightM { get; set; }

        /// <summary>
        /// Test whether a 3D speaker position (projected to XY) lies inside this room.
        /// </summary>
        public bool ContainsSpeakerPosition(Vec3 pos)
        {
            return ContainsPoint(new Vec2(pos.X, pos.Y));
        }
    }
}
