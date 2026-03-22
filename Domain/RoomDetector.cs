using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace SoundCalcs.Domain
{
    /// <summary>
    /// Detects closed room polygons from a set of 2D wall segments.
    /// Uses planar face detection via the "leftmost turn" (minimum angle) algorithm:
    ///   1. Snap wall endpoints within tolerance
    ///   2. Build a planar adjacency graph
    ///   3. Trace minimal faces by always turning left (CCW)
    ///   4. Filter to valid interior rooms
    /// </summary>
    public class RoomDetector
    {
        private const double SnapToleranceM = 0.15;  // 150mm — generous for IFC geometry
        private const double MinRoomAreaM2 = 1.0;     // Ignore tiny slivers

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

            // Step 0: Preprocess — extend wall centerlines and split at intersections
            // This is critical for IFC geometry where walls overlap at corners
            var processed = SplitWallsAtIntersections(walls);
            Debug.WriteLine($"[SoundCalcs] Preprocessing: {walls.Count} walls → {processed.Count} segments after splitting.");

            // Step 1: Extract and snap endpoints
            var edges = new List<(int from, int to)>();
            var nodeList = new List<Vec2>();
            var nodeMap = new Dictionary<long, int>(); // hash -> index

            foreach (WallSegment2D wall in processed)
            {
                int startIdx = GetOrAddNode(wall.Start, nodeList, nodeMap);
                int endIdx = GetOrAddNode(wall.End, nodeList, nodeMap);

                if (startIdx == endIdx) continue; // degenerate wall

                edges.Add((startIdx, endIdx));
                edges.Add((endIdx, startIdx)); // bidirectional
            }

            int nodeCount = nodeList.Count;
            Debug.WriteLine($"[SoundCalcs] Room detection: {nodeCount} nodes, {edges.Count / 2} edges from {processed.Count} segments.");

            if (nodeCount < 3)
                return new List<RoomPolygon>();

            // Step 2: Build adjacency list sorted by angle
            var adjacency = new Dictionary<int, List<int>>();
            for (int i = 0; i < nodeCount; i++)
                adjacency[i] = new List<int>();

            foreach (var (from, to) in edges)
            {
                if (!adjacency[from].Contains(to))
                    adjacency[from].Add(to);
            }

            // Sort each adjacency list by the angle of the edge direction
            foreach (int node in adjacency.Keys.ToList())
            {
                Vec2 nodePos = nodeList[node];
                adjacency[node].Sort((a, b) =>
                {
                    double angleA = Math.Atan2(nodeList[a].Y - nodePos.Y, nodeList[a].X - nodePos.X);
                    double angleB = Math.Atan2(nodeList[b].Y - nodePos.Y, nodeList[b].X - nodePos.X);
                    return angleA.CompareTo(angleB);
                });
            }

            // Step 3: Trace minimal faces using "next CCW edge" algorithm
            var usedEdges = new HashSet<long>();
            var rooms = new List<RoomPolygon>();
            int roomNumber = 1;

            foreach (var (from, to) in edges)
            {
                long edgeKey = EdgeKey(from, to);
                if (usedEdges.Contains(edgeKey)) continue;

                // Trace a face starting from this directed edge
                var face = TraceFace(from, to, nodeList, adjacency, usedEdges);
                if (face == null || face.Count < 3) continue;

                // Build vertices
                var vertices = new List<Vec2>();
                foreach (int idx in face)
                    vertices.Add(nodeList[idx]);

                var room = new RoomPolygon
                {
                    Vertices = vertices,
                    FloorElevationM = floorElevationM,
                    Name = $"Room {roomNumber}"
                };

                // Filter: must have reasonable area
                // Accept both CW and CCW winding — IFC geometry may have unpredictable winding
                if (room.Area >= MinRoomAreaM2)
                {
                    rooms.Add(room);
                    roomNumber++;
                }
            }

            Debug.WriteLine($"[SoundCalcs] Detected {rooms.Count} rooms.");
            return rooms;
        }

        // ========================= Wall Intersection Preprocessing =========================

        /// <summary>
        /// Extend wall centerlines slightly and split them at intersection points.
        /// IFC wall centerlines typically overlap or have gaps at corners — this creates
        /// proper shared corner nodes for the face-tracing algorithm.
        /// </summary>
        private static List<WallSegment2D> SplitWallsAtIntersections(List<WallSegment2D> walls)
        {
            // Extend each wall slightly in both directions
            const double extendM = 0.3; // Extend by 300mm to find intersections

            var extended = new List<WallSegment2D>();
            foreach (var w in walls)
            {
                Vec2 dir = (w.End - w.Start).Normalized();
                extended.Add(new WallSegment2D
                {
                    Start = w.Start - dir * extendM,
                    End = w.End + dir * extendM,
                    BaseElevationM = w.BaseElevationM,
                    HeightM = w.HeightM,
                    ThicknessM = w.ThicknessM
                });
            }

            // For each wall, find all intersection points with other walls
            var splitPoints = new List<List<Vec2>>();
            for (int i = 0; i < extended.Count; i++)
                splitPoints.Add(new List<Vec2>());

            for (int i = 0; i < extended.Count; i++)
            {
                for (int j = i + 1; j < extended.Count; j++)
                {
                    Vec2? pt = LineLineIntersect(
                        extended[i].Start, extended[i].End,
                        extended[j].Start, extended[j].End);

                    if (pt.HasValue)
                    {
                        splitPoints[i].Add(pt.Value);
                        splitPoints[j].Add(pt.Value);
                    }
                }
            }

            // Split each wall at its intersection points
            var result = new List<WallSegment2D>();
            for (int i = 0; i < extended.Count; i++)
            {
                var w = extended[i];
                var pts = splitPoints[i];

                if (pts.Count == 0)
                {
                    // No intersections — use original (non-extended) wall
                    result.Add(walls[i]);
                    continue;
                }

                // Sort intersection points along the wall direction
                Vec2 dir = w.End - w.Start;
                double wallLen = dir.Length;
                Vec2 normDir = dir.Normalized();

                // Project each point onto the wall and get its parameter t
                var projections = new List<(double t, Vec2 point)>();
                projections.Add((0, w.Start));
                projections.Add((wallLen, w.End));

                foreach (Vec2 p in pts)
                {
                    double t = Vec2.Dot(p - w.Start, normDir);
                    projections.Add((t, p));
                }

                projections.Sort((a, b) => a.t.CompareTo(b.t));

                // Remove duplicates (points closer than snap tolerance)
                var unique = new List<(double t, Vec2 point)> { projections[0] };
                for (int k = 1; k < projections.Count; k++)
                {
                    if (projections[k].t - unique[unique.Count - 1].t > SnapToleranceM)
                        unique.Add(projections[k]);
                }

                // Create sub-segments between consecutive points
                for (int k = 0; k < unique.Count - 1; k++)
                {
                    double segLen = unique[k + 1].t - unique[k].t;
                    if (segLen < SnapToleranceM) continue; // Skip tiny segments

                    result.Add(new WallSegment2D
                    {
                        Start = unique[k].point,
                        End = unique[k + 1].point,
                        BaseElevationM = walls[i].BaseElevationM,
                        HeightM = walls[i].HeightM,
                        ThicknessM = walls[i].ThicknessM
                    });
                }
            }

            return result;
        }

        /// <summary>
        /// Compute intersection point of two finite line segments.
        /// Returns null if segments are parallel or intersection is too far from both segments.
        /// </summary>
        private static Vec2? LineLineIntersect(Vec2 a1, Vec2 a2, Vec2 b1, Vec2 b2)
        {
            Vec2 d1 = a2 - a1;
            Vec2 d2 = b2 - b1;

            double cross = d1.X * d2.Y - d1.Y * d2.X;
            if (Math.Abs(cross) < 1e-10) return null; // Parallel or coincident

            Vec2 diff = b1 - a1;
            double t = (diff.X * d2.Y - diff.Y * d2.X) / cross;
            double u = (diff.X * d1.Y - diff.Y * d1.X) / cross;

            // Allow slight extension beyond segment endpoints for walls that almost meet
            const double margin = 0.3; // In parametric terms based on segment length
            double lenA = d1.Length;
            double lenB = d2.Length;

            double tMarginA = lenA > 0 ? margin / lenA : margin;
            double tMarginB = lenB > 0 ? margin / lenB : margin;

            if (t < -tMarginA || t > 1 + tMarginA) return null;
            if (u < -tMarginB || u > 1 + tMarginB) return null;

            return a1 + d1 * t;
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

        /// <summary>
        /// Trace a minimal face starting from directed edge (startFrom → startTo).
        /// Returns list of node indices forming the face, or null if degenerate.
        /// </summary>
        private static List<int> TraceFace(
            int startFrom, int startTo,
            List<Vec2> nodes,
            Dictionary<int, List<int>> adjacency,
            HashSet<long> usedEdges)
        {
            var face = new List<int>();
            int current = startFrom;
            int next = startTo;
            int maxSteps = nodes.Count + 2; // Safety limit

            for (int step = 0; step < maxSteps; step++)
            {
                long edgeKey = EdgeKey(current, next);
                if (usedEdges.Contains(edgeKey))
                {
                    // We've come back to a used edge — face is complete if we're at start
                    if (current == startFrom && next == startTo && face.Count >= 3)
                        return face;
                    return null; // Degenerate
                }

                usedEdges.Add(edgeKey);
                face.Add(current);

                // Find the next edge: the one that makes the smallest CCW turn from (current→next)
                int prev = current;
                current = next;
                next = GetNextCCW(prev, current, nodes, adjacency);

                if (next < 0) return null; // Dead end

                // Check if we've completed the loop
                if (current == startFrom && next == startTo)
                {
                    usedEdges.Add(EdgeKey(current, next));
                    face.Add(current);
                    return face;
                }
            }

            return null; // Exceeded max steps
        }

        /// <summary>
        /// Given we arrived at 'current' from 'prev', find the next node
        /// by taking the most clockwise turn (= next edge CW from incoming direction).
        /// This traces the left boundary of each face.
        /// </summary>
        private static int GetNextCCW(int prev, int current, List<Vec2> nodes, Dictionary<int, List<int>> adjacency)
        {
            List<int> neighbors = adjacency[current];
            if (neighbors.Count == 0) return -1;
            if (neighbors.Count == 1) return neighbors[0]; // Only one way to go

            // Incoming direction angle
            Vec2 incoming = nodes[current] - nodes[prev];
            double inAngle = Math.Atan2(incoming.Y, incoming.X);

            // Find the neighbor with the smallest CW angle from the incoming direction
            // This means we want the next edge sorted CW after the reverse of incoming
            int bestIdx = -1;
            double bestAngle = double.MaxValue;

            foreach (int neighbor in neighbors)
            {
                if (neighbor == prev && neighbors.Count > 1) continue; // Don't go back (unless dead end)

                Vec2 outgoing = nodes[neighbor] - nodes[current];
                double outAngle = Math.Atan2(outgoing.Y, outgoing.X);

                // Signed angle difference: how much we turn CW from incoming direction
                double diff = inAngle - outAngle;
                // Normalize to (0, 2π] — we want the smallest positive CW turn
                while (diff <= 0) diff += 2 * Math.PI;
                while (diff > 2 * Math.PI) diff -= 2 * Math.PI;

                if (diff < bestAngle)
                {
                    bestAngle = diff;
                    bestIdx = neighbor;
                }
            }

            return bestIdx;
        }

        private static int GetOrAddNode(Vec2 point, List<Vec2> nodeList, Dictionary<long, int> nodeMap)
        {
            // Snap to grid: round to nearest 10mm
            double snapX = Math.Round(point.X, 2);
            double snapY = Math.Round(point.Y, 2);
            Vec2 snapped = new Vec2(snapX, snapY);

            long hash = snapped.GetHashCode();

            // Check for exact match first
            if (nodeMap.TryGetValue(hash, out int idx))
            {
                // Verify it's actually close (hash collision check)
                if (Vec2.Distance(nodeList[idx], point) < SnapToleranceM)
                    return idx;
            }

            // Linear scan for nearby nodes (handles hash collisions)
            for (int i = 0; i < nodeList.Count; i++)
            {
                if (Vec2.Distance(nodeList[i], point) < SnapToleranceM)
                    return i;
            }

            // New node
            int newIdx = nodeList.Count;
            nodeList.Add(snapped);
            nodeMap[hash] = newIdx;
            return newIdx;
        }

        private static long EdgeKey(int from, int to)
        {
            return ((long)from << 32) | (uint)to;
        }
    }
}
