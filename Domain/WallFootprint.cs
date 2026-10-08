using System;
using System.Collections.Generic;
using System.Linq;

namespace SoundCalcs.Domain
{
    /// <summary>A wall centerline piece with its thickness, in plan (metres).</summary>
    public class WallPiece
    {
        public Vec2 Start { get; set; }
        public Vec2 End { get; set; }
        public double ThicknessM { get; set; }

        public double Length => Vec2.Distance(Start, End);
        public Vec2 Direction => (End - Start).Normalized();

        public override string ToString() => $"{Start} → {End} t={ThicknessM:F3}";
    }

    /// <summary>How a wall's centerline was found.</summary>
    public enum WallFootprintMethod
    {
        /// <summary>Paired the parallel faces of its plan outline.</summary>
        Outline,
        /// <summary>The outline did not pair up: the long axis of its smallest enclosing rectangle.</summary>
        Rectangle,
        /// <summary>Nothing usable.</summary>
        None
    }

    /// <summary>
    /// Turns a wall's plan shape (as Revit rebuilds an IFC wall: geometry only, no location line or width)
    /// back into centerline pieces with thicknesses. Revit-free so the harness and tests exercise it.
    /// </summary>
    public static class WallFootprint
    {
        public const double MinThicknessM = 0.03;
        public const double MaxThicknessM = 1.2;

        /// <summary>Faces this close to antiparallel are treated as the two sides of one wall.</summary>
        public const double ParallelToleranceDeg = 15;

        /// <summary>Outline simplification tolerance: smooths mesh noise and long arc tessellations.</summary>
        public const double SimplifyToleranceM = 0.01;

        /// <summary>
        /// Centerline pieces of a wall whose plan outline (top or bottom face) is <paramref name="outline"/>.
        /// Pairs facing sides that are a wall thickness apart; the midline over their overlap is a piece.
        /// Falls back to the smallest enclosing rectangle when the sides do not pair up.
        /// </summary>
        public static List<WallPiece> FromOutline(IList<Vec2> outline, out WallFootprintMethod method)
        {
            method = WallFootprintMethod.None;
            List<Vec2> pts = Clean(outline);
            if (pts.Count < 3) return new List<WallPiece>();

            var pieces = PairSides(pts);
            double perimeter = 0;
            for (int i = 0; i < pts.Count; i++) perimeter += Vec2.Distance(pts[i], pts[(i + 1) % pts.Count]);
            double covered = pieces.Sum(p => p.Length);
            double avgT = pieces.Count > 0 ? pieces.Sum(p => p.ThicknessM * p.Length) / Math.Max(1e-9, covered) : 0;

            // Most of the outline's length should be explained by the pieces: two sides per piece, plus end caps.
            double expected = Math.Max(0, perimeter / 2 - 2 * avgT);
            if (pieces.Count > 0 && covered >= 0.6 * expected)
            {
                method = WallFootprintMethod.Outline;
                return pieces;
            }

            WallPiece box = FromPoints(pts);
            if (box == null) return pieces;
            method = WallFootprintMethod.Rectangle;
            return new List<WallPiece> { box };
        }

        /// <summary>
        /// Long axis of the smallest rectangle enclosing <paramref name="points"/>; its short side is the thickness.
        /// Null when there are fewer than 3 distinct points.
        /// </summary>
        public static WallPiece FromPoints(IEnumerable<Vec2> points)
        {
            List<Vec2> hull = JobInputBuilder.ConvexHull(points.ToList());
            if (hull.Count < 3) return null;

            double bestArea = double.MaxValue;
            WallPiece best = null;
            for (int i = 0; i < hull.Count; i++)
            {
                Vec2 u = (hull[(i + 1) % hull.Count] - hull[i]).Normalized();
                if (u.LengthSquared < 0.5) continue;
                var v = new Vec2(-u.Y, u.X);
                double minU = double.MaxValue, maxU = double.MinValue, minV = double.MaxValue, maxV = double.MinValue;
                foreach (Vec2 p in hull)
                {
                    double a = Vec2.Dot(p, u), b = Vec2.Dot(p, v);
                    minU = Math.Min(minU, a); maxU = Math.Max(maxU, a);
                    minV = Math.Min(minV, b); maxV = Math.Max(maxV, b);
                }
                double area = (maxU - minU) * (maxV - minV);
                if (area >= bestArea) continue;
                bestArea = area;

                double lenU = maxU - minU, lenV = maxV - minV;
                if (lenV > lenU)
                {
                    // Long axis along v
                    double cu = (minU + maxU) / 2;
                    best = new WallPiece { Start = u * cu + v * minV, End = u * cu + v * maxV, ThicknessM = lenU };
                }
                else
                {
                    double cv = (minV + maxV) / 2;
                    best = new WallPiece { Start = u * minU + v * cv, End = u * maxU + v * cv, ThicknessM = lenV };
                }
            }
            return best;
        }

        /// <summary>
        /// Outer boundary of a set of triangles (a triangulated face, e.g. the top of a mesh wall):
        /// the longest loop of edges used by only one triangle. Empty when there is none.
        /// </summary>
        public static List<Vec2> OutlineFromTriangles(IEnumerable<(Vec2 A, Vec2 B, Vec2 C)> triangles, double weldM = 0.001)
        {
            var keyOf = new Dictionary<(long, long), int>();
            var verts = new List<Vec2>();
            int Key(Vec2 p)
            {
                var k = ((long)Math.Round(p.X / weldM), (long)Math.Round(p.Y / weldM));
                if (!keyOf.TryGetValue(k, out int idx)) { idx = verts.Count; verts.Add(p); keyOf[k] = idx; }
                return idx;
            }

            // Edges used by one triangle only are the boundary; meshes are not reliably wound, so walk them undirected
            var edgeCount = new Dictionary<(int, int), int>();
            foreach (var t in triangles)
            {
                int a = Key(t.A), b = Key(t.B), c = Key(t.C);
                if (a == b || b == c || a == c) continue;
                foreach (var (p, q) in new[] { (a, b), (b, c), (c, a) })
                {
                    var k = p < q ? (p, q) : (q, p);
                    edgeCount.TryGetValue(k, out int n);
                    edgeCount[k] = n + 1;
                }
            }

            var adjacent = new Dictionary<int, List<int>>();
            foreach (var kv in edgeCount)
            {
                if (kv.Value != 1) continue;
                foreach (var (p, q) in new[] { (kv.Key.Item1, kv.Key.Item2), (kv.Key.Item2, kv.Key.Item1) })
                {
                    if (!adjacent.TryGetValue(p, out var list)) adjacent[p] = list = new List<int>();
                    list.Add(q);
                }
            }

            var best = new List<Vec2>();
            double bestLen = 0;
            var used = new HashSet<(int, int)>();
            foreach (int start in adjacent.Keys.ToList())
            {
                foreach (int first in adjacent[start])
                {
                    var key0 = start < first ? (start, first) : (first, start);
                    if (used.Contains(key0)) continue;
                    used.Add(key0);
                    var loop = new List<int> { start };
                    int cur = first;
                    while (cur != start && loop.Count <= adjacent.Count)
                    {
                        loop.Add(cur);
                        int from = cur;
                        int nxt = -1;
                        foreach (int o in adjacent[from])
                        {
                            var k = from < o ? (from, o) : (o, from);
                            if (used.Contains(k)) continue;
                            used.Add(k);
                            nxt = o;
                            break;
                        }
                        if (nxt < 0) break;
                        cur = nxt;
                    }
                    if (cur != start || loop.Count < 3) continue;
                    double len = 0;
                    for (int i = 0; i < loop.Count; i++) len += Vec2.Distance(verts[loop[i]], verts[loop[(i + 1) % loop.Count]]);
                    if (len > bestLen) { bestLen = len; best = loop.Select(i => verts[i]).ToList(); }
                }
            }
            return best;
        }

        /// <summary>
        /// Moves piece ends onto the lines of the pieces they meet: L corners (both pieces end near the crossing)
        /// and T junctions (one piece ends at the face of another). Ends that stop short are extended (else sound
        /// leaks through the corner); ends that run past the other wall's centerline, to its outer face, are trimmed
        /// (else they leave stubs outside the room). <paramref name="extraM"/> is added to the half thicknesses
        /// when deciding how far an end may move.
        /// </summary>
        public static void JoinEnds(IList<WallPiece> pieces, double extraM = 0.05)
        {
            // Only real corners and junctions: the pieces of a tessellated curved wall meet at a few degrees
            // and already touch; stretching them over each other would count the wall twice
            double sinMin = Math.Sin(30 * Math.PI / 180);
            var moves = new List<(WallPiece Piece, bool AtEnd, Vec2 To)>();

            foreach (WallPiece s in pieces)
            {
                double lenS = s.Length;
                if (lenS < 1e-6) continue;
                Vec2 dS = s.Direction;
                foreach (bool atEnd in new[] { false, true })
                {
                    Vec2 endPt = atEnd ? s.End : s.Start;
                    if (pieces.Any(o => !ReferenceEquals(o, s) &&
                                        (Vec2.Distance(endPt, o.Start) < 0.01 || Vec2.Distance(endPt, o.End) < 0.01)))
                        continue;   // already joined end to end
                    double bestExt = double.MaxValue;
                    Vec2 bestPt = default(Vec2);
                    foreach (WallPiece t in pieces)
                    {
                        if (ReferenceEquals(s, t)) continue;
                        double lenT = t.Length;
                        if (lenT < 1e-6) continue;
                        Vec2 dT = t.Direction;
                        double cross = Vec2.Cross(dS, dT);
                        if (Math.Abs(cross) < sinMin) continue;

                        // Intersection of the two lines: s.Start + dS·a = t.Start + dT·b
                        Vec2 w = t.Start - s.Start;
                        double a = Vec2.Cross(w, dT) / cross;
                        double b = Vec2.Cross(w, dS) / cross;
                        double reach = (s.ThicknessM + t.ThicknessM) / 2 + extraM;

                        // How far beyond this end the crossing is: positive = extend, negative = the end overshoots
                        // the other wall's line (IFC walls often run to the outer face of a corner) and is trimmed
                        double ext = atEnd ? a - lenS : -a;
                        if (ext < -Math.Min(reach, lenS / 2) || ext > reach) continue;
                        if (b < -reach || b > lenT + reach) continue;
                        if (Math.Abs(ext) < Math.Abs(bestExt)) { bestExt = ext; bestPt = s.Start + dS * a; }
                    }
                    if (bestExt < double.MaxValue && Math.Abs(bestExt) > 1e-9)
                        moves.Add((s, atEnd, bestPt));
                }
            }

            foreach (var m in moves)
            {
                if (m.AtEnd) m.Piece.End = m.To;
                else m.Piece.Start = m.To;
            }
        }

        /// <summary>
        /// Merges pieces on the same line that overlap or meet end to end (the faces of one wall at several heights:
        /// a stepped top, window sills) into one piece over their union, so the wall is counted once along its length.
        /// </summary>
        public static void MergeCollinear(List<WallPiece> pieces, double gapM = 0.05)
        {
            bool merged = true;
            while (merged)
            {
                merged = false;
                for (int i = 0; i < pieces.Count && !merged; i++)
                    for (int j = i + 1; j < pieces.Count && !merged; j++)
                    {
                        WallPiece a = pieces[i], b = pieces[j];
                        Vec2 d = a.Direction;
                        if (a.Length < 1e-9 || b.Length < 1e-9 || Math.Abs(Vec2.Dot(d, b.Direction)) < Math.Cos(2 * Math.PI / 180)) continue;
                        var n = new Vec2(-d.Y, d.X);
                        double tol = 0.5 * Math.Min(a.ThicknessM, b.ThicknessM) + 0.01;
                        if (Math.Abs(Vec2.Dot(n, b.Start - a.Start)) > tol || Math.Abs(Vec2.Dot(n, b.End - a.Start)) > tol) continue;
                        double b0 = Vec2.Dot(d, b.Start - a.Start), b1 = Vec2.Dot(d, b.End - a.Start);
                        double lo = Math.Min(b0, b1), hi = Math.Max(b0, b1);
                        if (lo > a.Length + gapM || hi < -gapM) continue;   // apart along the line

                        // The longer piece's line and the larger thickness
                        WallPiece keep = a.Length >= b.Length ? a : b;
                        Vec2 kd = keep.Direction;
                        double s0 = Math.Min(Math.Min(Vec2.Dot(kd, a.Start - keep.Start), Vec2.Dot(kd, a.End - keep.Start)),
                                             Math.Min(Vec2.Dot(kd, b.Start - keep.Start), Vec2.Dot(kd, b.End - keep.Start)));
                        double s1 = Math.Max(Math.Max(Vec2.Dot(kd, a.Start - keep.Start), Vec2.Dot(kd, a.End - keep.Start)),
                                             Math.Max(Vec2.Dot(kd, b.Start - keep.Start), Vec2.Dot(kd, b.End - keep.Start)));
                        pieces[i] = new WallPiece { Start = keep.Start + kd * s0, End = keep.Start + kd * s1, ThicknessM = Math.Max(a.ThicknessM, b.ThicknessM) };
                        pieces.RemoveAt(j);
                        merged = true;
                    }
            }
        }

        /// <summary>
        /// Merges parallel pieces that touch side by side (the layers of one wall modelled as separate solids)
        /// into one piece spanning their combined thickness.
        /// </summary>
        public static void MergeTouching(List<WallPiece> pieces, double gapM = 0.01)
        {
            bool merged = true;
            while (merged)
            {
                merged = false;
                for (int i = 0; i < pieces.Count && !merged; i++)
                    for (int j = i + 1; j < pieces.Count && !merged; j++)
                    {
                        WallPiece a = pieces[i], b = pieces[j];
                        Vec2 d = a.Direction;
                        if (Math.Abs(Vec2.Dot(d, b.Direction)) < 0.995) continue;
                        var n = new Vec2(-d.Y, d.X);
                        double offB = Vec2.Dot(n, (b.Start + b.End) * 0.5 - a.Start);
                        if (Math.Abs(offB) > (a.ThicknessM + b.ThicknessM) / 2 + gapM) continue;
                        double b0 = Vec2.Dot(d, b.Start - a.Start), b1 = Vec2.Dot(d, b.End - a.Start);
                        double lo = Math.Max(0, Math.Min(b0, b1)), hi = Math.Min(a.Length, Math.Max(b0, b1));
                        if (hi - lo < 0.5 * Math.Min(a.Length, b.Length)) continue;

                        // Faces across the wall, measured from a's line
                        double near = Math.Min(-a.ThicknessM / 2, offB - b.ThicknessM / 2);
                        double far = Math.Max(a.ThicknessM / 2, offB + b.ThicknessM / 2);
                        double mid = (near + far) / 2;
                        double s0 = Math.Min(0, Math.Min(b0, b1)), s1 = Math.Max(a.Length, Math.Max(b0, b1));
                        pieces[i] = new WallPiece { Start = a.Start + d * s0 + n * mid, End = a.Start + d * s1 + n * mid, ThicknessM = far - near };
                        pieces.RemoveAt(j);
                        merged = true;
                    }
            }
        }

        // ------------------------------------------------------------------

        private struct Side
        {
            public Vec2 A, B, Dir;
            public double Len;
        }

        private static List<WallPiece> PairSides(List<Vec2> pts)
        {
            var sides = new List<Side>();
            for (int i = 0; i < pts.Count; i++)
            {
                Vec2 a = pts[i], b = pts[(i + 1) % pts.Count];
                double len = Vec2.Distance(a, b);
                if (len < 1e-6) continue;
                sides.Add(new Side { A = a, B = b, Dir = (b - a) * (1.0 / len), Len = len });
            }

            double cosTol = Math.Cos(ParallelToleranceDeg * Math.PI / 180);
            var candidates = new List<(int I, int J, double D, double Lo, double Hi)>();
            for (int i = 0; i < sides.Count; i++)
                for (int j = i + 1; j < sides.Count; j++)
                {
                    Side si = sides[i], sj = sides[j];
                    if (Vec2.Dot(si.Dir, sj.Dir) > -cosTol) continue;

                    // Thickness: distance between the two sides, measured both ways
                    double d = (Math.Abs(Vec2.Cross(si.Dir, (sj.A + sj.B) * 0.5 - si.A)) +
                                Math.Abs(Vec2.Cross(sj.Dir, (si.A + si.B) * 0.5 - sj.A))) / 2;
                    if (d < MinThicknessM || d > MaxThicknessM) continue;
                    // End caps of a short wall face each other too: a wall side is longer than the wall is thick
                    if (Math.Max(si.Len, sj.Len) < d) continue;

                    double t1 = Vec2.Dot(sj.A - si.A, si.Dir), t2 = Vec2.Dot(sj.B - si.A, si.Dir);
                    double lo = Math.Max(0, Math.Min(t1, t2)), hi = Math.Min(si.Len, Math.Max(t1, t2));
                    if (hi - lo < Math.Max(0.02, 0.5 * d)) continue;

                    // The sides must face each other: the other side lies on the inside (left of a CCW outline
                    // or right of a CW one); check by the midpoint between them lying inside the outline.
                    Vec2 mid = si.A + si.Dir * ((lo + hi) / 2);
                    Vec2 mid2 = ClosestOnLine(mid, sj.A, sj.Dir);
                    if (!PointInPolygon((mid + mid2) * 0.5, pts)) continue;

                    candidates.Add((i, j, d, lo, hi));
                }

            // Closest pairs first; a side already covered by a closer partner is not paired again there
            var covered = new Dictionary<int, List<(double Lo, double Hi)>>();
            var pieces = new List<WallPiece>();
            foreach (var c in candidates.OrderBy(c => c.D))
            {
                Side si = sides[c.I], sj = sides[c.J];
                double ja = Vec2.Dot(si.A + si.Dir * c.Lo - sj.A, sj.Dir), jb = Vec2.Dot(si.A + si.Dir * c.Hi - sj.A, sj.Dir);
                double jlo = Math.Max(0, Math.Min(ja, jb)), jhi = Math.Min(sj.Len, Math.Max(ja, jb));
                if (CoveredFraction(covered, c.I, c.Lo, c.Hi) > 0.5 || CoveredFraction(covered, c.J, jlo, jhi) > 0.5)
                    continue;
                AddCovered(covered, c.I, c.Lo, c.Hi);
                AddCovered(covered, c.J, jlo, jhi);

                Vec2 p0 = si.A + si.Dir * c.Lo, p1 = si.A + si.Dir * c.Hi;
                Vec2 q0 = ClosestOnLine(p0, sj.A, sj.Dir), q1 = ClosestOnLine(p1, sj.A, sj.Dir);
                pieces.Add(new WallPiece { Start = (p0 + q0) * 0.5, End = (p1 + q1) * 0.5, ThicknessM = c.D });
            }
            return pieces;
        }

        private static double CoveredFraction(Dictionary<int, List<(double Lo, double Hi)>> covered, int side, double lo, double hi)
        {
            if (hi - lo < 1e-9 || !covered.TryGetValue(side, out var list)) return 0;
            double sum = 0;
            foreach (var (a, b) in list) sum += Math.Max(0, Math.Min(hi, b) - Math.Max(lo, a));
            return sum / (hi - lo);
        }

        private static void AddCovered(Dictionary<int, List<(double Lo, double Hi)>> covered, int side, double lo, double hi)
        {
            if (!covered.TryGetValue(side, out var list)) covered[side] = list = new List<(double, double)>();
            list.Add((lo, hi));
        }

        private static Vec2 ClosestOnLine(Vec2 p, Vec2 a, Vec2 dir) => a + dir * Vec2.Dot(p - a, dir);

        private static bool PointInPolygon(Vec2 p, List<Vec2> poly)
        {
            bool inside = false;
            for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            {
                Vec2 a = poly[i], b = poly[j];
                if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X)
                    inside = !inside;
            }
            return inside;
        }

        /// <summary>Drops repeated points and the closing duplicate, then simplifies (Douglas–Peucker, closed).</summary>
        private static List<Vec2> Clean(IList<Vec2> outline)
        {
            var pts = new List<Vec2>();
            foreach (Vec2 p in outline ?? new List<Vec2>())
                if (pts.Count == 0 || Vec2.Distance(pts[pts.Count - 1], p) > 1e-4) pts.Add(p);
            while (pts.Count > 1 && Vec2.Distance(pts[0], pts[pts.Count - 1]) <= 1e-4) pts.RemoveAt(pts.Count - 1);
            if (pts.Count < 4) return pts;

            // Split the closed ring at the point farthest from the first, simplify both halves
            int far = 0;
            double farD = 0;
            for (int i = 1; i < pts.Count; i++)
            {
                double d = Vec2.Distance(pts[0], pts[i]);
                if (d > farD) { farD = d; far = i; }
            }
            var first = Simplify(pts.GetRange(0, far + 1));
            var second = pts.GetRange(far, pts.Count - far);
            second.Add(pts[0]);
            second = Simplify(second);

            var result = new List<Vec2>(first);
            result.AddRange(second.Skip(1).Take(second.Count - 2));
            return result;
        }

        private static List<Vec2> Simplify(List<Vec2> pts)
        {
            if (pts.Count < 3) return pts;
            var keep = new bool[pts.Count];
            keep[0] = keep[pts.Count - 1] = true;
            var stack = new Stack<(int, int)>();
            stack.Push((0, pts.Count - 1));
            while (stack.Count > 0)
            {
                var (s, e) = stack.Pop();
                Vec2 dir = (pts[e] - pts[s]).Normalized();
                double maxD = 0;
                int idx = -1;
                for (int i = s + 1; i < e; i++)
                {
                    double d = dir.LengthSquared < 0.5 ? Vec2.Distance(pts[i], pts[s]) : Math.Abs(Vec2.Cross(dir, pts[i] - pts[s]));
                    if (d > maxD) { maxD = d; idx = i; }
                }
                if (idx >= 0 && maxD > SimplifyToleranceM)
                {
                    keep[idx] = true;
                    stack.Push((s, idx));
                    stack.Push((idx, e));
                }
            }
            return pts.Where((p, i) => keep[i]).ToList();
        }
    }
}
