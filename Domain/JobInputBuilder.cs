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
            if (IsAimAdjustable(profileSource))
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
        /// Whether the user's horizontal aim (drag in the viewer, stored in Revit) affects
        /// the calculation. Only wall-mounted speakers aim horizontally; omni and ceiling
        /// cone speakers always point straight down, so rotating them has no effect.
        /// </summary>
        public static bool IsAimAdjustable(ProfileSourceType profileSource) =>
            profileSource == ProfileSourceType.WallMounted;

        /// <summary>
        /// True for the "Open (No Wall)" type: it neither blocks, reflects nor encloses.
        /// </summary>
        public static bool IsOpening(WallTypeInfo wallType) =>
            wallType != null && wallType.Surface == WallAbsorptionPreset.Open;

        /// <summary>
        /// Convert a detail-line wall segment into a compute wall with the STC and surface
        /// material of its assigned wall type (null = no type: STC 0, default surface).
        /// </summary>
        public static ComputeWall ToComputeWall(WallSegment2D seg, WallTypeInfo wallType, double heightM = 0)
        {
            ComputeWall w = ToComputeWall(seg, wallType?.StcRating ?? 0);
            if (wallType != null)
                w.AbsorptionByBand = (double[])wallType.AbsorptionByBand.Clone();
            w.HeightM = Math.Max(0, heightM);
            return w;
        }

        /// <summary>
        /// The walls with every stretch covered twice on the same line removed from the later wall: collinear pieces
        /// (a wall traced in several lines, an arc's segments) overlap at their joints because each end is extended
        /// by <see cref="WallEndExtensionM"/>, and duplicated lines overlap entirely. A ray through such a stretch
        /// would pay the wall's loss twice. Returns new wall objects; the input is not changed.
        /// </summary>
        public static List<ComputeWall> RemoveCollinearOverlaps(List<ComputeWall> walls)
        {
            const double lineTol = 0.01;
            double cosTol = Math.Cos(0.5 * Math.PI / 180);
            var result = new List<ComputeWall>();
            foreach (ComputeWall w in walls)
            {
                // Pieces of w still uncovered, as intervals along w
                Vec2 d = w.End - w.Start;
                double len = d.Length;
                if (len < 1e-9) { result.Add(w); continue; }
                Vec2 u = d * (1.0 / len);
                var free = new List<(double Lo, double Hi)> { (0, len) };
                foreach (ComputeWall a in result)
                {
                    Vec2 ad = a.End - a.Start;
                    double alen = ad.Length;
                    if (alen < 1e-9 || Math.Abs(Vec2.Dot(ad * (1.0 / alen), u)) < cosTol) continue;
                    if (Math.Abs(Vec2.Cross(u, a.Start - w.Start)) > lineTol || Math.Abs(Vec2.Cross(u, a.End - w.Start)) > lineTol) continue;
                    double a0 = Vec2.Dot(a.Start - w.Start, u), a1 = Vec2.Dot(a.End - w.Start, u);
                    double lo = Math.Min(a0, a1), hi = Math.Max(a0, a1);
                    var next = new List<(double, double)>();
                    foreach (var (fl, fh) in free)
                    {
                        if (hi <= fl || lo >= fh) { next.Add((fl, fh)); continue; }
                        if (lo > fl) next.Add((fl, lo));
                        if (hi < fh) next.Add((hi, fh));
                    }
                    free = next;
                }
                if (free.Count == 1 && free[0].Lo == 0 && free[0].Hi == len) { result.Add(w); continue; }
                foreach (var (fl, fh) in free)
                {
                    if (fh - fl < 0.01) continue;
                    result.Add(new ComputeWall
                    {
                        Start = w.Start + u * fl, End = w.Start + u * fh,
                        StcRating = w.StcRating, HalfThicknessM = w.HalfThicknessM, HeightM = w.HeightM,
                        BaseElevationM = w.BaseElevationM, AbsorptionByBand = w.AbsorptionByBand
                    });
                }
            }
            return result;
        }

        /// <summary>Partial walls at least this tall count as enclosing the room.</summary>
        public const double EnclosingHeightM = 2.4;

        /// <summary>
        /// Whether a wall line group closes off the room (for the enclosure ratio / reverberant
        /// field): not an opening, and full height or at least <see cref="EnclosingHeightM"/>.
        /// </summary>
        public static bool IsEnclosing(WallTypeInfo wallType, double heightM) =>
            !IsOpening(wallType) && (heightM <= 0 || heightM >= EnclosingHeightM);

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
                HalfThicknessM = Math.Max(seg.ThicknessM * 0.5, 0.05),
                BaseElevationM = seg.BaseElevationM
            };
        }

        /// <summary>
        /// Derive ceiling height per room from the tallest ceiling-mounted speaker in it.
        /// Pass only ceiling speakers (omni / conical / GLL): a wall-mounted speaker at
        /// 2.2 m says nothing about the ceiling. Rooms with none keep the default.
        /// </summary>
        public static void ApplyCeilingHeights(
            IEnumerable<RoomPolygon> rooms, IEnumerable<SpeakerInstance> ceilingSpeakers)
        {
            var speakerList = ceilingSpeakers.ToList();
            foreach (RoomPolygon room in rooms)
            {
                double maxElevation = 0;
                foreach (SpeakerInstance inst in speakerList)
                {
                    if (!room.ContainsSpeakerPosition(inst.Position)) continue;
                    // Only speakers on this room's level: one on the floor above stands over it in plan too
                    if (!string.IsNullOrEmpty(inst.LevelName) && Math.Abs(inst.LevelElevationM - room.FloorElevationM) > 0.5)
                        continue;
                    double h = inst.Position.Z - room.FloorElevationM;
                    if (h > maxElevation) maxElevation = h;
                }
                if (maxElevation > 0.5)
                    room.CeilingHeightM = maxElevation;
            }
        }

        /// <summary>
        /// Split each analysis boundary into the rooms its enclosing walls form, plus an
        /// "Open area" for whatever the rooms don't cover, and generate receivers over the whole
        /// boundary with each receiver assigned to the room it stands in. Rooms come before
        /// the open area so point-in-room lookups find the enclosed room first. A boundary that
        /// is one room (or has none) stays as it is.
        /// </summary>
        public static (List<RoomPolygon> Rooms, List<ReceiverPoint> Receivers) BuildRoomsAndReceivers(
            IList<RoomPolygon> boundaries, List<WallSegment2D> enclosingSegments, AnalysisSettings settings)
        {
            var rooms = new List<RoomPolygon>();
            var receivers = new List<ReceiverPoint>();
            int nextIndex = 0;

            foreach (RoomPolygon boundary in boundaries)
            {
                var detected = (enclosingSegments != null && enclosingSegments.Count >= 3
                        ? RoomDetector.DetectRooms(enclosingSegments, boundary.FloorElevationM)
                        : new List<RoomPolygon>())
                    .Where(r => r.Area >= 1.0 && boundary.ContainsPoint(VertexCentroid(r)))
                    .ToList();

                var group = new List<RoomPolygon>();
                bool single = detected.Count == 0 || (detected.Count == 1 && detected[0].Area > 0.9 * boundary.Area);
                if (single)
                {
                    group.Add(boundary);
                }
                else
                {
                    for (int i = 0; i < detected.Count; i++)
                        detected[i].Name = $"{boundary.Name} – Room {i + 1}";
                    group.AddRange(detected);
                    double remainder = boundary.Area - detected.Sum(r => r.Area);
                    if (remainder > 1.0)
                    {
                        // The boundary with the rooms cut out: its points, perimeter and ceiling speakers are the
                        // open area's own, not the rooms'
                        group.Add(new RoomPolygon
                        {
                            Vertices = boundary.Vertices.ToList(),
                            Holes = detected.Select(r => r.Vertices.ToList()).ToList(),
                            FloorElevationM = boundary.FloorElevationM,
                            Name = $"{boundary.Name} – Open area",
                            AreaOverrideM2 = remainder
                        });
                    }
                }

                int first = rooms.Count;
                rooms.AddRange(group);

                List<ReceiverPoint> pts = ReceiverGrid.GenerateForPolygon(boundary, settings, nextIndex, first);
                nextIndex += pts.Count;
                foreach (ReceiverPoint pt in pts)
                    pt.RoomIndex = first + RoomOf(new Vec2(pt.Position.X, pt.Position.Y), group);
                receivers.AddRange(pts);
            }

            return (rooms, receivers);
        }

        /// <summary>Index in <paramref name="group"/> of the room containing p, else the nearest one.</summary>
        private static int RoomOf(Vec2 p, List<RoomPolygon> group)
        {
            for (int i = 0; i < group.Count; i++)
                if (group[i].ContainsPoint(p)) return i;

            int best = 0; double bestD = double.MaxValue;
            for (int i = 0; i < group.Count; i++)
            {
                var v = group[i].Vertices;
                for (int j = 0; j < v.Count; j++)
                {
                    double d = PointSegmentDistance(p, v[j], v[(j + 1) % v.Count]);
                    if (d < bestD) { bestD = d; best = i; }
                }
            }
            return best;
        }

        private static Vec2 VertexCentroid(RoomPolygon r) =>
            new Vec2(r.Vertices.Average(v => v.X), r.Vertices.Average(v => v.Y));

        private static double PointSegmentDistance(Vec2 p, Vec2 a, Vec2 b)
        {
            Vec2 ab = b - a;
            double len2 = ab.LengthSquared;
            double t = len2 > 1e-18 ? Math.Max(0, Math.Min(1, Vec2.Dot(p - a, ab) / len2)) : 0;
            return Vec2.Distance(p, a + ab * t);
        }

        /// <summary>A group of wall lines for per-room RT60: its segments and surface absorption.</summary>
        public class WallLines
        {
            public List<WallSegment2D> Segments { get; set; } = new List<WallSegment2D>();
            public double[] Absorption { get; set; }
            public bool Enclosing { get; set; } = true;
        }

        /// <summary>
        /// Estimate each room's RT60 from its own geometry (Eyring, see
        /// RoomAcoustics.EstimateRt60FromGeometry): its floor area and ceiling height, the wall
        /// lines along its perimeter with their materials, the open part of its perimeter, and
        /// its share of the occupants (by floor area). Sets <see cref="RoomPolygon.RT60ByBand"/>.
        /// </summary>
        public static void EstimateRoomRt60s(List<RoomPolygon> rooms, List<WallLines> wallLines,
            double[] floorAbsorption, double[] ceilingAbsorption, int occupants,
            double temperatureC, double relativeHumidityPct, double defaultCeilingHeightM = 3.0)
        {
            double totalArea = rooms.Sum(r => r.EffectiveAreaM2);
            foreach (RoomPolygon room in rooms)
            {
                var lines = new List<(double LengthM, double[] Absorption)>();
                double covered = 0;
                foreach (WallLines g in wallLines.Where(g => g.Enclosing && g.Absorption != null))
                {
                    var probe = new RoomPolygon { Vertices = room.Vertices.ToList() };
                    RoomDetector.ComputeEnclosureRatios(new List<RoomPolygon> { probe }, g.Segments);
                    if (probe.WallCoverageM <= 0) continue;
                    lines.Add((probe.WallCoverageM, g.Absorption));
                    covered += probe.WallCoverageM;
                }

                double h = room.CeilingHeightM > 0.5 ? room.CeilingHeightM : defaultCeilingHeightM;
                int people = totalArea > 0 ? (int)Math.Round(occupants * room.EffectiveAreaM2 / totalArea) : 0;
                room.RT60ByBand = Compute.RoomAcoustics.EstimateRt60FromGeometry(
                    room.EffectiveAreaM2, room.Perimeter, h, lines, Math.Min(covered, room.Perimeter),
                    floorAbsorption, ceilingAbsorption, people, temperatureC, relativeHumidityPct);
            }
        }

        /// <summary>
        /// Whether an A/B line parameter value means <paramref name="line"/> ("A" or "B"): "A", "a", " A ",
        /// "A-line", "A line", "Line A" all mean A; "AB" or "Bar" mean neither.
        /// </summary>
        public static bool IsOnLine(string abLine, string line)
        {
            if (string.IsNullOrWhiteSpace(abLine) || string.IsNullOrEmpty(line)) return false;
            string v = abLine.Trim();
            if (v.StartsWith("line", StringComparison.OrdinalIgnoreCase)) v = v.Substring(4).Trim(' ', '-', '_', ':');
            if (v.Length == 0 || !string.Equals(v.Substring(0, 1), line, StringComparison.OrdinalIgnoreCase)) return false;
            return v.Length == 1 || !char.IsLetterOrDigit(v[1]);
        }

        /// <summary>
        /// The analysis boundary for a set of wall lines: their own outline when they close around all of
        /// themselves (so the notch of an L-shaped building stays outside), else the convex hull of their ends
        /// (open layouts, a few lines marking an area).
        /// </summary>
        public static List<Vec2> BoundaryFromWalls(List<WallSegment2D> walls)
        {
            List<Vec2> outline = RoomDetector.OuterOutline(walls);
            if (outline != null) return outline;
            var pts = new List<Vec2>();
            foreach (WallSegment2D w in walls) { pts.Add(w.Start); pts.Add(w.End); }
            return ConvexHull(pts);
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
