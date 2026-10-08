using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace SoundCalcs.Domain
{
    /// <summary>
    /// Detects closed room polygons from a set of 2D wall segments, as the faces of the planar graph they form:
    ///   1. Merge duplicated (overlapping collinear) walls: shared walls drawn once per room; bridge joints up to
    ///      <see cref="SlotGapM"/> between collinear walls (panel joints)
    ///   2. Close corners and T junctions: a wall end that stops short of (or overshoots) a crossing wall by up
    ///      to <see cref="CornerReachM"/> is moved onto it. Never toward a parallel wall, so door gaps stay open
    ///   3. Split the walls where they cross, merge coincident nodes (exact coordinates are kept)
    ///   4. Drop dangling ends (free-standing wall parts that enclose nothing)
    ///   5. Walk every half-edge once, turning to the next edge clockwise: inner faces come out counter-clockwise
    ///      (rooms), the outer boundary of each connected group of walls clockwise (dropped, or a hole when it
    ///      lies inside a room of another group: a free-standing closet)
    /// </summary>
    public class RoomDetector
    {
        /// <summary>How far a wall end may be moved onto a crossing wall to close a corner or T junction.</summary>
        public const double CornerReachM = 0.3;

        /// <summary>Walls closer than this are the same wall (duplicates); nodes closer than this are one node.</summary>
        private const double MergeToleranceM = 0.005;

        /// <summary>Gaps up to this wide between collinear walls are joints, not openings, and are closed.</summary>
        public const double SlotGapM = 0.1;

        private const double MinRoomAreaM2 = 1.0;     // Ignore tiny slivers

        /// <summary>Faces narrower than this (2·area/perimeter) are the gap inside a double wall, not a room.</summary>
        private const double MinRoomWidthM = 0.4;

        /// <summary>
        /// Detect room polygons from wall segments.
        /// </summary>
        public static List<RoomPolygon> DetectRooms(
            List<WallSegment2D> walls,
            double floorElevationM)
        {
            if (walls == null || walls.Count < 3)
            {
                Debug.WriteLine("[SoundCalcs] Not enough walls for room detection.");
                return new List<RoomPolygon>();
            }
            Faces(walls, out List<List<Vec2>> inner, out List<List<Vec2>> outer, out int nodeCount);

            var rooms = new List<RoomPolygon>();
            foreach (List<Vec2> ring in inner)
            {
                double area = SignedArea(ring);
                double perimeter = 0;
                for (int i = 0; i < ring.Count; i++) perimeter += Vec2.Distance(ring[i], ring[(i + 1) % ring.Count]);
                if (area < MinRoomAreaM2 || 2 * area / perimeter < MinRoomWidthM) continue;
                rooms.Add(new RoomPolygon { Vertices = ring, FloorElevationM = floorElevationM });
            }

            // A group of walls standing free inside a room (its outer boundary inside that room's outline) is a
            // hole in it: the smallest room around it
            foreach (List<Vec2> boundary in outer)
            {
                Vec2 probe = boundary[0];
                RoomPolygon host = rooms
                    .Where(r => !r.Vertices.Contains(probe) && r.ContainsPoint(probe))
                    .OrderBy(r => Math.Abs(r.SignedArea))
                    .FirstOrDefault();
                if (host != null && Math.Abs(SignedArea(boundary)) >= MinRoomAreaM2 * 0.25)
                    host.Holes.Add(boundary.AsEnumerable().Reverse().ToList());
            }

            // Smallest first, so a point is found in the innermost room; numbered in that order
            rooms = rooms.OrderBy(r => r.Area).ToList();
            for (int i = 0; i < rooms.Count; i++) rooms[i].Name = $"Room {i + 1}";
            Debug.WriteLine($"[SoundCalcs] Detected {rooms.Count} rooms from {walls.Count} walls ({nodeCount} nodes).");
            return rooms;
        }

        /// <summary>
        /// The outline of the walls when they close around all of themselves (the outer boundary of the closed
        /// wall cycles, counter-clockwise; concave for an L-shaped building), or null when some wall lies outside
        /// every closed cycle (an open layout: the caller's convex hull fits better).
        /// </summary>
        public static List<Vec2> OuterOutline(List<WallSegment2D> walls, double toleranceM = CornerReachM)
        {
            if (walls == null || walls.Count < 3) return null;
            Faces(walls, out _, out List<List<Vec2>> outer, out _);
            List<Vec2> outline = outer.OrderByDescending(o => Math.Abs(SignedArea(o))).FirstOrDefault();
            if (outline == null || Math.Abs(SignedArea(outline)) < MinRoomAreaM2) return null;
            outline = outline.AsEnumerable().Reverse().ToList();   // counter-clockwise
            var poly = new RoomPolygon { Vertices = outline };
            foreach (WallSegment2D w in walls)
                foreach (Vec2 p in new[] { w.Start, w.End })
                {
                    if (poly.ContainsPoint(p)) continue;
                    double d = double.MaxValue;
                    for (int i = 0; i < outline.Count; i++)
                        d = Math.Min(d, PointSegmentDistance(p, outline[i], outline[(i + 1) % outline.Count]));
                    if (d > toleranceM) return null;
                }
            return outline;
        }

        /// <summary>
        /// The faces of the wall graph: inner faces counter-clockwise (rooms), outer boundaries of each connected
        /// group of walls clockwise.
        /// </summary>
        private static void Faces(List<WallSegment2D> walls, out List<List<Vec2>> inner, out List<List<Vec2>> outer, out int nodeCount)
        {
            var segs = walls.Where(w => w != null && Vec2.Distance(w.Start, w.End) > 0.01)
                .Select(w => new Seg { A = w.Start, B = w.End }).ToList();
            MergeDuplicates(segs);
            CloseJunctions(segs);

            // Planar graph: split at crossings, merge coincident nodes
            var nodes = new List<Vec2>();
            var nodeGrid = new Dictionary<(long, long), List<int>>();
            int Node(Vec2 p)
            {
                long cx = (long)Math.Floor(p.X / MergeToleranceM), cy = (long)Math.Floor(p.Y / MergeToleranceM);
                for (long dx = -1; dx <= 1; dx++)
                    for (long dy = -1; dy <= 1; dy++)
                        if (nodeGrid.TryGetValue((cx + dx, cy + dy), out var list))
                            foreach (int i in list)
                                if (Vec2.Distance(nodes[i], p) < MergeToleranceM) return i;
                nodes.Add(p);
                if (!nodeGrid.TryGetValue((cx, cy), out var cell)) nodeGrid[(cx, cy)] = cell = new List<int>();
                cell.Add(nodes.Count - 1);
                return nodes.Count - 1;
            }

            var adjacency = new Dictionary<int, HashSet<int>>();
            void Edge(int a, int b)
            {
                if (a == b) return;
                if (!adjacency.TryGetValue(a, out var la)) adjacency[a] = la = new HashSet<int>();
                if (!adjacency.TryGetValue(b, out var lb)) adjacency[b] = lb = new HashSet<int>();
                la.Add(b);
                lb.Add(a);
            }

            foreach (List<Vec2> piece in SplitAtCrossings(segs))
                for (int i = 0; i < piece.Count - 1; i++)
                    Edge(Node(piece[i]), Node(piece[i + 1]));

            // Dangling ends enclose nothing
            var queue = new Queue<int>(adjacency.Where(kv => kv.Value.Count <= 1).Select(kv => kv.Key));
            while (queue.Count > 0)
            {
                int n = queue.Dequeue();
                if (!adjacency.TryGetValue(n, out var nb) || nb.Count > 1) continue;
                foreach (int m in nb)
                {
                    adjacency[m].Remove(n);
                    if (adjacency[m].Count <= 1) queue.Enqueue(m);
                }
                adjacency.Remove(n);
            }

            // Neighbours of each node sorted counter-clockwise by angle
            var sorted = new Dictionary<int, List<int>>();
            foreach (var kv in adjacency)
            {
                Vec2 c = nodes[kv.Key];
                sorted[kv.Key] = kv.Value.OrderBy(m => Math.Atan2(nodes[m].Y - c.Y, nodes[m].X - c.X)).ToList();
            }

            // Faces: each half-edge u→v continues v→w with w the neighbour of v just clockwise of u
            var used = new HashSet<(int, int)>();
            inner = new List<List<Vec2>>();
            outer = new List<List<Vec2>>();
            foreach (var kv in sorted)
                foreach (int to in kv.Value)
                {
                    if (used.Contains((kv.Key, to))) continue;
                    var ring = new List<Vec2>();
                    int u = kv.Key, v = to;
                    for (int guard = 0; guard <= 4 * nodes.Count + 4 && used.Add((u, v)); guard++)
                    {
                        ring.Add(nodes[u]);
                        List<int> around = sorted[v];
                        int at = around.IndexOf(u);
                        int w = around[(at - 1 + around.Count) % around.Count];
                        u = v;
                        v = w;
                    }
                    if (ring.Count < 3) continue;
                    double area = SignedArea(ring);
                    if (area > 0) inner.Add(ring); else outer.Add(ring);
                }
            nodeCount = nodes.Count;
        }

        private class Seg
        {
            public Vec2 A, B;
            public Vec2 Dir => (B - A).Normalized();
            public double Length => Vec2.Distance(A, B);
        }

        private static double SignedArea(List<Vec2> ring)
        {
            double a = 0;
            for (int i = 0; i < ring.Count; i++) a += Vec2.Cross(ring[i], ring[(i + 1) % ring.Count]);
            return a / 2;
        }

        /// <summary>Overlapping collinear walls (a shared wall drawn once for each room) become one.</summary>
        private static void MergeDuplicates(List<Seg> segs)
        {
            double cosTol = Math.Cos(0.5 * Math.PI / 180);
            bool merged = true;
            while (merged)
            {
                merged = false;
                for (int i = 0; i < segs.Count && !merged; i++)
                    for (int j = i + 1; j < segs.Count && !merged; j++)
                    {
                        Seg a = segs[i], b = segs[j];
                        Vec2 d = a.Dir;
                        if (Math.Abs(Vec2.Dot(d, b.Dir)) < cosTol) continue;
                        if (Math.Abs(Vec2.Cross(d, b.A - a.A)) > MergeToleranceM || Math.Abs(Vec2.Cross(d, b.B - a.A)) > MergeToleranceM) continue;
                        double b0 = Vec2.Dot(b.A - a.A, d), b1 = Vec2.Dot(b.B - a.A, d);
                        double lo = Math.Min(b0, b1), hi = Math.Max(b0, b1);
                        // Touching end to end is fine; a joint a few cm wide between collinear pieces (glass panels,
                        // a wall drawn in parts) is bridged: it would join the rooms either side. Door gaps are wider.
                        double gap = Math.Max(lo - a.Length, -hi);
                        if (gap > SlotGapM || (gap > -MergeToleranceM && gap <= MergeToleranceM)) continue;
                        double s0 = Math.Min(0, lo), s1 = Math.Max(a.Length, hi);
                        segs[i] = new Seg { A = a.A + d * s0, B = a.A + d * s1 };
                        segs.RemoveAt(j);
                        merged = true;
                    }
            }
        }

        /// <summary>
        /// Moves wall ends onto crossing walls they stop short of or overshoot by up to <see cref="CornerReachM"/>
        /// (detail lines and IFC centrelines rarely meet exactly). Only toward walls at 20° or more: ends of
        /// collinear walls facing each other across a door gap are left alone.
        /// </summary>
        private static void CloseJunctions(List<Seg> segs)
        {
            double sinMin = Math.Sin(20 * Math.PI / 180);
            var moves = new List<(Seg S, bool AtB, Vec2 To)>();
            foreach (Seg s in segs)
            {
                double len = s.Length;
                Vec2 d = s.Dir;
                foreach (bool atB in new[] { false, true })
                {
                    Vec2 end = atB ? s.B : s.A;
                    // Already on another wall
                    if (segs.Any(o => !ReferenceEquals(o, s) && PointSegmentDistance(end, o.A, o.B) < MergeToleranceM)) continue;
                    double best = double.MaxValue;
                    Vec2 bestPt = end;
                    foreach (Seg t in segs)
                    {
                        if (ReferenceEquals(s, t)) continue;
                        Vec2 dt = t.Dir;
                        double cross = Vec2.Cross(d, dt);
                        if (Math.Abs(cross) < sinMin) continue;
                        Vec2 w = t.A - s.A;
                        double a = Vec2.Cross(w, dt) / cross;          // along s from A
                        double b = Vec2.Cross(w, d) / cross;           // along t from its A
                        double ext = atB ? a - len : -a;               // + beyond the end, − overshoot
                        if (ext < -Math.Min(CornerReachM, len / 2) || ext > CornerReachM) continue;
                        if (b < -CornerReachM || b > t.Length + CornerReachM) continue;
                        if (Math.Abs(ext) < Math.Abs(best)) { best = ext; bestPt = s.A + d * a; }
                    }
                    if (best < double.MaxValue && Math.Abs(best) > 1e-9) moves.Add((s, atB, bestPt));
                }
            }
            foreach (var m in moves)
            {
                if (m.AtB) m.S.B = m.To; else m.S.A = m.To;
            }
        }

        /// <summary>Each wall as a polyline through the points where other walls cross or touch it.</summary>
        private static List<List<Vec2>> SplitAtCrossings(List<Seg> segs)
        {
            var cuts = segs.Select(s => new List<double> { 0, 1 }).ToList();
            for (int i = 0; i < segs.Count; i++)
                for (int j = i + 1; j < segs.Count; j++)
                {
                    Seg a = segs[i], b = segs[j];
                    Vec2 da = a.B - a.A, db = b.B - b.A;
                    double cross = Vec2.Cross(da, db);
                    if (Math.Abs(cross) < 1e-12)
                    {
                        // Collinear walls touching: an end of one on the other
                        AddCutIfOn(cuts[i], a, b.A); AddCutIfOn(cuts[i], a, b.B);
                        AddCutIfOn(cuts[j], b, a.A); AddCutIfOn(cuts[j], b, a.B);
                        continue;
                    }
                    Vec2 w = b.A - a.A;
                    double t = Vec2.Cross(w, db) / cross, u = Vec2.Cross(w, da) / cross;
                    double ea = MergeToleranceM / Math.Max(da.Length, 1e-9), eb = MergeToleranceM / Math.Max(db.Length, 1e-9);
                    if (t < -ea || t > 1 + ea || u < -eb || u > 1 + eb) continue;
                    cuts[i].Add(Math.Max(0, Math.Min(1, t)));
                    cuts[j].Add(Math.Max(0, Math.Min(1, u)));
                }
            var result = new List<List<Vec2>>();
            for (int i = 0; i < segs.Count; i++)
            {
                Seg s = segs[i];
                result.Add(cuts[i].Distinct().OrderBy(t => t).Select(t => s.A + (s.B - s.A) * t).ToList());
            }
            return result;
        }

        private static double PointSegmentDistance(Vec2 p, Vec2 a, Vec2 b)
        {
            Vec2 ab = b - a;
            double len2 = ab.LengthSquared;
            double t = len2 > 1e-18 ? Math.Max(0, Math.Min(1, Vec2.Dot(p - a, ab) / len2)) : 0;
            return Vec2.Distance(p, a + ab * t);
        }

        private static void AddCutIfOn(List<double> cuts, Seg s, Vec2 p)
        {
            Vec2 d = s.B - s.A;
            double len2 = d.LengthSquared;
            if (len2 < 1e-18) return;
            double t = Vec2.Dot(p - s.A, d) / len2;
            if (t > 0 && t < 1 && PointSegmentDistance(p, s.A, s.B) < MergeToleranceM) cuts.Add(t);
        }

        // ========================= Speaker Matching =========================

        /// <summary>
        /// Mark rooms that contain at least one speaker position.
        /// </summary>
        public static void MarkRoomsWithSpeakers(
            List<RoomPolygon> rooms,
            List<Vec3> speakerPositions)
        {
            foreach (RoomPolygon room in rooms)
            {
                room.ContainsSpeaker = false;
                foreach (Vec3 pos in speakerPositions)
                {
                    if (room.ContainsSpeakerPosition(pos))
                    {
                        room.ContainsSpeaker = true;
                        break;
                    }
                }
            }

            int count = rooms.Count(r => r.ContainsSpeaker);
            Debug.WriteLine($"[SoundCalcs] {count} of {rooms.Count} rooms contain speakers.");
        }

        // ========================= Enclosure Ratio =========================

        /// <summary>
        /// For each room polygon, compute the fraction of its perimeter that is
        /// backed by actual wall segments. Sets <see cref="RoomPolygon.EnclosureRatio"/>
        /// and <see cref="RoomPolygon.WallCoverageM"/>.
        /// </summary>
        /// <param name="rooms">Detected room polygons.</param>
        /// <param name="walls">Original wall segments (not extended compute walls).</param>
        public static void ComputeEnclosureRatios(
            List<RoomPolygon> rooms,
            List<WallSegment2D> walls)
        {
            if (rooms == null || walls == null) return;

            const double perpTolerance = 0.30;  // Max perpendicular distance to count as "on edge"
            const double overlapTolerance = 0.15; // Tolerance for endpoint overlap

            double cosParallel = Math.Cos(10 * Math.PI / 180);
            foreach (var room in rooms)
            {
                if (room.Vertices.Count < 3) { room.EnclosureRatio = 0; continue; }

                double totalPerimeter = 0;
                double coveredLength = 0;

                foreach (List<Vec2> ring in room.Rings())
                for (int i = 0; i < ring.Count; i++)
                {
                    Vec2 edgeStart = ring[i];
                    Vec2 edgeEnd = ring[(i + 1) % ring.Count];
                    double edgeLen = Vec2.Distance(edgeStart, edgeEnd);
                    totalPerimeter += edgeLen;

                    if (edgeLen < 1e-6) continue;

                    Vec2 edgeDir = (edgeEnd - edgeStart).Normalized();

                    // For this edge, find all wall segments that are collinear and overlapping.
                    // Track covered intervals along the edge parameter [0, edgeLen].
                    var intervals = new List<(double lo, double hi)>();

                    foreach (var wall in walls)
                    {
                        // Check perpendicular distance of both wall endpoints to the edge line
                        Vec2 edgeNormal = new Vec2(-edgeDir.Y, edgeDir.X);
                        double perpStart = Math.Abs(Vec2.Dot(wall.Start - edgeStart, edgeNormal));
                        double perpEnd = Math.Abs(Vec2.Dot(wall.End - edgeStart, edgeNormal));

                        // The wall must run along the edge: both ends near its line, and roughly parallel
                        // (a wall crossing the edge, or touching it with one end, doesn't close it)
                        if (perpStart > perpTolerance || perpEnd > perpTolerance) continue;
                        Vec2 wd = wall.End - wall.Start;
                        if (wd.Length < 1e-9 || Math.Abs(Vec2.Dot(wd.Normalized(), edgeDir)) < cosParallel) continue;

                        // Project wall endpoints onto the edge direction
                        double tStart = Vec2.Dot(wall.Start - edgeStart, edgeDir);
                        double tEnd = Vec2.Dot(wall.End - edgeStart, edgeDir);

                        double lo = Math.Min(tStart, tEnd);
                        double hi = Math.Max(tStart, tEnd);

                        // Wall must have meaningful overlap with edge
                        double overlapLo = Math.Max(lo, -overlapTolerance);
                        double overlapHi = Math.Min(hi, edgeLen + overlapTolerance);

                        if (overlapHi - overlapLo > overlapTolerance)
                        {
                            intervals.Add((Math.Max(overlapLo, 0), Math.Min(overlapHi, edgeLen)));
                        }
                    }

                    // Merge overlapping intervals and sum covered length
                    coveredLength += MergeAndSumIntervals(intervals);
                }

                room.WallCoverageM = coveredLength;
                room.EnclosureRatio = totalPerimeter > 0
                    ? Math.Min(coveredLength / totalPerimeter, 1.0)
                    : 0;

                // Update room name to reflect enclosure status
                string baseName = room.Name;
                // Strip any previous enclosure suffix
                int parenIdx = baseName.IndexOf(" (");
                if (parenIdx > 0) baseName = baseName.Substring(0, parenIdx);

                if (room.EnclosureRatio >= 0.85)
                    room.Name = $"{baseName} (enclosed)";
                else if (room.EnclosureRatio >= 0.40)
                    room.Name = $"{baseName} (partial {room.EnclosureRatio:P0})";
                else
                    room.Name = $"{baseName} (open)";

                Debug.WriteLine($"[SoundCalcs] {room.Name}: perimeter={totalPerimeter:F1}m, " +
                    $"wallCoverage={coveredLength:F1}m, enclosureRatio={room.EnclosureRatio:P0}");
            }
        }

        /// <summary>
        /// Merge a list of (lo, hi) intervals and return the total covered length.
        /// </summary>
        private static double MergeAndSumIntervals(List<(double lo, double hi)> intervals)
        {
            if (intervals.Count == 0) return 0;

            intervals.Sort((a, b) => a.lo.CompareTo(b.lo));

            double total = 0;
            double curLo = intervals[0].lo;
            double curHi = intervals[0].hi;

            for (int i = 1; i < intervals.Count; i++)
            {
                if (intervals[i].lo <= curHi)
                {
                    curHi = Math.Max(curHi, intervals[i].hi);
                }
                else
                {
                    total += curHi - curLo;
                    curLo = intervals[i].lo;
                    curHi = intervals[i].hi;
                }
            }
            total += curHi - curLo;
            return total;
        }
    }
}
