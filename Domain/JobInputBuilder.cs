using System;
using System.Collections.Generic;
using System.Linq;

namespace SoundCalcs.Domain
{
    /// <summary>
    /// Revit-free steps used to turn picked speakers, detail-line walls and the
    /// boundary polygon into an <see cref="AcousticJobInput"/>. Shared by
    /// <c>MainViewModel</c> and the headless harness so both build identical jobs.
    /// </summary>
    public static class JobInputBuilder
    {
        /// <summary>
        /// Detail-line walls are extended by this much at each end to bridge small
        /// gaps at corners and T-junctions where endpoints don't connect perfectly.
        /// </summary>
        public const double WallEndExtensionM = 0.10;

        /// <summary>
        /// Facing direction sent to the compute engine. Wall-mounted speakers aim
        /// horizontally along their per-instance drag line; omni / conical speakers
        /// are ceiling-mounted and always aim straight down.
        /// </summary>
        public static Vec3 ResolveFacing(ProfileSourceType profileSource, Vec3 instanceFacing)
        {
            if (profileSource == ProfileSourceType.WallMounted)
            {
                double hx = instanceFacing.X;
                double hy = instanceFacing.Y;
                double hLen = Math.Sqrt(hx * hx + hy * hy);
                if (hLen < 1e-6) { hx = 1.0; hy = 0.0; hLen = 1.0; }
                return new Vec3(hx / hLen, hy / hLen, 0);
            }

            return new Vec3(0, 0, -1);
        }

        /// <summary>
        /// Convert a detail-line wall segment into a compute wall with the given STC,
        /// extending both ends by <see cref="WallEndExtensionM"/>.
        /// </summary>
        public static ComputeWall ToComputeWall(WallSegment2D seg, int stc)
        {
            Vec2 dir = (seg.End - seg.Start);
            double len = dir.Length;
            Vec2 norm = len > 1e-6 ? dir * (1.0 / len) : Vec2.Zero;
            Vec2 extStart = seg.Start - norm * WallEndExtensionM;
            Vec2 extEnd   = seg.End   + norm * WallEndExtensionM;

            return new ComputeWall
            {
                Start = extStart,
                End = extEnd,
                StcRating = stc,
                HalfThicknessM = Math.Max(seg.ThicknessM * 0.5, 0.05)
            };
        }

        /// <summary>
        /// Derive ceiling height per room from the tallest speaker in each room.
        /// Speakers are typically ceiling-mounted, so their elevation ≈ ceiling.
        /// </summary>
        public static void ApplyCeilingHeights(
            IEnumerable<RoomPolygon> rooms, IEnumerable<SpeakerInstance> speakers)
        {
            var speakerList = speakers.ToList();
            foreach (RoomPolygon room in rooms)
            {
                double maxElevation = 0;
                foreach (SpeakerInstance inst in speakerList)
                {
                    if (room.ContainsSpeakerPosition(inst.Position))
                    {
                        double h = inst.ElevationFromLevelM;
                        if (h > maxElevation) maxElevation = h;
                    }
                }
                if (maxElevation > 0.5)
                    room.CeilingHeightM = maxElevation;
            }
        }

        /// <summary>
        /// Convex hull (Andrew's monotone chain) of the given points, CCW order.
        /// Used to build the analysis boundary from picked detail-line endpoints.
        /// </summary>
        public static List<Vec2> ConvexHull(List<Vec2> points)
        {
            if (points.Count < 3)
                return new List<Vec2>(points);

            // Sort by X, then Y
            var sorted = points.OrderBy(p => p.X).ThenBy(p => p.Y).ToList();

            // Remove duplicates
            var unique = new List<Vec2> { sorted[0] };
            for (int i = 1; i < sorted.Count; i++)
            {
                if (Vec2.Distance(sorted[i], sorted[i - 1]) > 1e-6)
                    unique.Add(sorted[i]);
            }
            if (unique.Count < 3)
                return unique;

            int n = unique.Count;
            var hull = new Vec2[2 * n];
            int k = 0;

            // Lower hull
            for (int i = 0; i < n; i++)
            {
                while (k >= 2 && Vec2.Cross(hull[k - 1] - hull[k - 2], unique[i] - hull[k - 2]) <= 0)
                    k--;
                hull[k++] = unique[i];
            }

            // Upper hull
            int lower = k + 1;
            for (int i = n - 2; i >= 0; i--)
            {
                while (k >= lower && Vec2.Cross(hull[k - 1] - hull[k - 2], unique[i] - hull[k - 2]) <= 0)
                    k--;
                hull[k++] = unique[i];
            }

            var result = new List<Vec2>(k - 1);
            for (int i = 0; i < k - 1; i++)
                result.Add(hull[i]);
            return result;
        }
    }
}
