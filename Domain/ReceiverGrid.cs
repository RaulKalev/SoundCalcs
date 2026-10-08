using System;
using System.Collections.Generic;

namespace SoundCalcs.Domain
{
    /// <summary>
    /// Generates a uniform receiver grid within a rectangular bounding region.
    /// MVP: generates a flat grid at a fixed elevation; future versions will
    /// clip to room boundaries.
    /// </summary>
    public class ReceiverGrid
    {
        /// <summary>
        /// Generate a rectangular grid of receiver points within the given bounds.
        /// </summary>
        /// <param name="minCorner">Minimum XY corner in meters.</param>
        /// <param name="maxCorner">Maximum XY corner in meters.</param>
        /// <param name="elevation">Z elevation for all points (meters).</param>
        /// <param name="spacing">Distance between points (meters).</param>
        /// <param name="boundaryOffset">Inset from edges (meters).</param>
        public static List<ReceiverPoint> Generate(
            Vec3 minCorner,
            Vec3 maxCorner,
            double elevation,
            double spacing,
            double boundaryOffset)
        {
            var points = new List<ReceiverPoint>();

            double xMin = minCorner.X + boundaryOffset;
            double yMin = minCorner.Y + boundaryOffset;
            double xMax = maxCorner.X - boundaryOffset;
            double yMax = maxCorner.Y - boundaryOffset;

            if (xMin >= xMax || yMin >= yMax || spacing <= 0)
                return points;

            int index = 0;
            for (double x = xMin; x <= xMax; x += spacing)
            {
                for (double y = yMin; y <= yMax; y += spacing)
                {
                    points.Add(new ReceiverPoint(new Vec3(x, y, elevation), index));
                    index++;
                }
            }

            return points;
        }

        /// <summary>
        /// Generate a grid from room bounding box with floor elevation + receiver height.
        /// </summary>
        public static List<ReceiverPoint> GenerateForRoom(
            Vec3 roomMin,
            Vec3 roomMax,
            double floorElevationM,
            AnalysisSettings settings)
        {
            double elevation = floorElevationM + settings.ReceiverHeightM;
            return Generate(roomMin, roomMax, elevation, settings.GridSpacingM, settings.BoundaryOffsetM);
        }

        /// <summary>
        /// Generate a grid within a polygon room boundary.
        /// Points are generated on a regular grid within the bounding box,
        /// then filtered to only include points inside the polygon.
        /// </summary>
        public static List<ReceiverPoint> GenerateForPolygon(
            RoomPolygon room,
            AnalysisSettings settings,
            int startIndex = 0,
            int roomIndex = -1)
        {
            var points = new List<ReceiverPoint>();
            double elevation = room.FloorElevationM + settings.ReceiverHeightM;
            double spacing = settings.GridSpacingM;
            double offset = settings.BoundaryOffsetM;

            Vec2 bMin = room.BoundsMin;
            Vec2 bMax = room.BoundsMax;

            double xMin = bMin.X + offset;
            double yMin = bMin.Y + offset;
            double xMax = bMax.X - offset;
            double yMax = bMax.Y - offset;

            if (xMin >= xMax || yMin >= yMax || spacing <= 0)
                return points;

            int index = startIndex;
            for (double x = xMin; x <= xMax; x += spacing)
            {
                for (double y = yMin; y <= yMax; y += spacing)
                {
                    // Only include points inside the room polygon, at least the offset from each of its edges
                    var p = new Vec2(x, y);
                    if (room.ContainsPoint(p) && (offset <= 0 || DistanceToEdges(room, p) >= offset - 1e-9))
                    {
                        var pt = new ReceiverPoint(new Vec3(x, y, elevation), index);
                        pt.RoomIndex = roomIndex;
                        points.Add(pt);
                        index++;
                    }
                }
            }

            return points;
        }

        private static double DistanceToEdges(RoomPolygon room, Vec2 p)
        {
            double best = double.MaxValue;
            foreach (List<Vec2> ring in room.Rings())
                for (int i = 0; i < ring.Count; i++)
                {
                    Vec2 a = ring[i], ab = ring[(i + 1) % ring.Count] - a;
                    double len2 = ab.LengthSquared;
                    double t = len2 > 1e-18 ? Math.Max(0, Math.Min(1, Vec2.Dot(p - a, ab) / len2)) : 0;
                    best = Math.Min(best, Vec2.Distance(p, a + ab * t));
                }
            return best;
        }
    }
}
