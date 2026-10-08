using System;
using System.Collections.Generic;
using System.Linq;
using SoundCalcs.Domain.Ifc;

namespace SoundCalcs.Domain
{
    /// <summary>What became of a column: see <see cref="IfcWallBuilder.PlaceColumn"/>.</summary>
    public enum ColumnPlacement { Placed, InsideWall, TooSmall, Unusable }

    /// <summary>What an <see cref="IfcLinkWall"/> is: a wall, a door / window standing in or for one, or a column.</summary>
    public enum IfcElementKind { Wall, Door, Window, Column }

    /// <summary>
    /// One wall of a linked IFC as Revit rebuilt it: shapes only (DirectShape), plus the IFC data Revit kept as
    /// parameters. Coordinates in metres, in the host model. Doors and windows (curtain wall panels among them)
    /// come the same way with their <see cref="Kind"/>.
    /// </summary>
    public class IfcLinkWall
    {
        public IfcElementKind Kind { get; set; } = IfcElementKind.Wall;
        public int ElementId { get; set; }
        /// <summary>IfcGUID parameter: matches the wall in the original file.</summary>
        public string IfcGuid { get; set; } = "";
        /// <summary>Row name for the walls table: IFC type / object type.</summary>
        public string GroupName { get; set; } = "";
        /// <summary>Plan outlines of its top faces (one per solid); see <see cref="WallFootprint.FromOutline"/>.</summary>
        public List<List<Vec2>> Outlines { get; set; } = new List<List<Vec2>>();
        /// <summary>All its vertices in plan: the fallback when no outline could be read.</summary>
        public List<Vec2> Points { get; set; } = new List<Vec2>();
        public double BaseZ { get; set; }
        public double TopZ { get; set; }
        public string AcousticRating { get; set; }
        public List<string> Materials { get; set; } = new List<string>();
    }

    public class IfcWallBuildOptions
    {
        /// <summary>
        /// Floor level of the analysis (metres). Walls that do not stand at <see cref="ProbeHeightM"/> above it
        /// (other storeys) are left out. Null keeps every wall.
        /// </summary>
        public double? LevelElevationM { get; set; }
        public double ProbeHeightM { get; set; } = 1.2;

        /// <summary>The original IFC file, read; null to use the shapes only.</summary>
        public IfcFileData File { get; set; }

        /// <summary>
        /// Largest accepted median distance between a wall placed from the file and its shape (metres).
        /// Larger means the file does not line up with the link (another version or file): it is not used.
        /// </summary>
        public double MaxFitResidualM { get; set; } = 0.25;
    }

    /// <summary>2D rotation then translation, file coordinates → host model.</summary>
    public class PlanTransform
    {
        public double AngleRad { get; set; }
        public Vec2 Offset { get; set; }

        public Vec2 Apply(Vec2 p)
        {
            double c = Math.Cos(AngleRad), s = Math.Sin(AngleRad);
            return new Vec2(c * p.X - s * p.Y + Offset.X, s * p.X + c * p.Y + Offset.Y);
        }

        public override string ToString() => $"rotate {AngleRad * 180 / Math.PI:F2}°, move ({Offset.X:F3}, {Offset.Y:F3}) m";
    }

    public class IfcWallBuildResult
    {
        public List<WallLineGroup> Groups { get; set; } = new List<WallLineGroup>();
        public int WallCount { get; set; }
        /// <summary>Walls placed from the file's axis and layer thickness.</summary>
        public int FromFile { get; set; }
        /// <summary>Walls whose sides were paired from their shape.</summary>
        public int FromOutline { get; set; }
        /// <summary>Walls that fell back to their enclosing rectangle.</summary>
        public int FromRectangle { get; set; }
        public int OtherLevel { get; set; }
        public int Unusable { get; set; }
        /// <summary>Doors and windows set into a wall (the wall cut around them), and standing on their own.</summary>
        public int OpeningsInWalls { get; set; }
        public int OpeningsFree { get; set; }
        /// <summary>Columns standing free, and those inside a wall (left to the wall).</summary>
        public int Columns { get; set; }
        public int ColumnsInWalls { get; set; }
        public int ColumnsSmall { get; set; }
        /// <summary>How the file was used, for the status line and the log.</summary>
        public string FileStatus { get; set; } = "";
        public PlanTransform FileToModel { get; set; }
        public double FitResidualM { get; set; }

        public string Summary()
        {
            var parts = new List<string> { $"{WallCount} IFC wall(s)" };
            if (FromFile > 0) parts.Add($"{FromFile} from the IFC file");
            if (FromOutline > 0) parts.Add($"{FromOutline} from shapes");
            if (FromRectangle > 0) parts.Add($"{FromRectangle} approximated");
            if (OpeningsInWalls + OpeningsFree > 0)
                parts.Add($"{OpeningsInWalls + OpeningsFree} door(s)/window(s), {OpeningsFree} standing free (glazing, curtain panels)");
            if (Columns + ColumnsInWalls + ColumnsSmall > 0)
                parts.Add($"{Columns} column(s)" + (ColumnsInWalls > 0 ? $" ({ColumnsInWalls} more inside walls)" : "") +
                    (ColumnsSmall > 0 ? $", {ColumnsSmall} thinner than {IfcWallBuilder.MinColumnSizeM * 1000:F0} mm left out" : ""));
            if (OtherLevel > 0) parts.Add($"{OtherLevel} on other levels skipped");
            if (Unusable > 0) parts.Add($"{Unusable} unreadable");
            return string.Join(", ", parts);
        }
    }

    /// <summary>
    /// Rebuilds wall lines for the acoustic model from a linked IFC. Each wall's shape gives its centerline and
    /// thickness (<see cref="WallFootprint"/>); when the original file is available its walls are matched by
    /// GUID and their exact axis and layer thickness used, placed by fitting the file onto the link.
    /// </summary>
    public static class IfcWallBuilder
    {
        public const string GroupSuffix = " (IFC)";

        public static IfcWallBuildResult Build(IEnumerable<IfcLinkWall> linkWalls, IfcWallBuildOptions options = null)
        {
            options = options ?? new IfcWallBuildOptions();
            var result = new IfcWallBuildResult();

            var walls = new List<IfcLinkWall>();
            foreach (IfcLinkWall w in linkWalls ?? Enumerable.Empty<IfcLinkWall>())
            {
                if (options.LevelElevationM is double level)
                {
                    double probe = level + options.ProbeHeightM;
                    if (w.TopZ < probe || w.BaseZ > probe) { result.OtherLevel++; continue; }
                }
                walls.Add(w);
            }
            var openings = walls.Where(w => w.Kind == IfcElementKind.Door || w.Kind == IfcElementKind.Window).ToList();
            var columns = walls.Where(w => w.Kind == IfcElementKind.Column).ToList();
            walls = walls.Where(w => w.Kind == IfcElementKind.Wall).ToList();

            // Doors and windows: the long axis of their plan footprint (a leaf, a glass pane, a frame)
            var openingPieces = new Dictionary<IfcLinkWall, WallPiece>();
            foreach (IfcLinkWall o in openings)
            {
                WallPiece p = o.Points.Count >= 3 ? WallFootprint.FromPoints(o.Points) : null;
                if (p == null || p.Length < 0.1 || p.ThicknessM > WallFootprint.MaxThicknessM) { result.Unusable++; continue; }
                p.ThicknessM = Math.Max(p.ThicknessM, 0.02);
                openingPieces[o] = p;
            }

            // Centerlines from the shapes
            var fromShape = new Dictionary<IfcLinkWall, (List<WallPiece> Pieces, WallFootprintMethod Method)>();
            foreach (IfcLinkWall w in walls)
            {
                var pieces = new List<WallPiece>();
                var method = WallFootprintMethod.None;
                foreach (List<Vec2> outline in w.Outlines)
                {
                    List<WallPiece> p = WallFootprint.FromOutline(outline, out WallFootprintMethod m);
                    pieces.AddRange(p);
                    if (m == WallFootprintMethod.Outline || method == WallFootprintMethod.None) method = m;
                }
                if (pieces.Count == 0 && w.Points.Count >= 3)
                {
                    WallPiece box = WallFootprint.FromPoints(w.Points);
                    if (box != null) { pieces.Add(box); method = WallFootprintMethod.Rectangle; }
                }
                pieces.RemoveAll(p => p.Length < 0.05 || p.ThicknessM > WallFootprint.MaxThicknessM);
                // Faces at several heights (stepped top, sills) are one wall along its length; a wall modelled as
                // one solid per layer: the touching layers are one wall, not several
                if (w.Outlines.Count > 1)
                {
                    WallFootprint.MergeCollinear(pieces);
                    WallFootprint.MergeTouching(pieces);
                }
                fromShape[w] = (pieces, pieces.Count > 0 ? method : WallFootprintMethod.None);
            }

            // Centerlines from the file, fitted onto the shapes
            var fromFile = new Dictionary<IfcLinkWall, (List<WallPiece> Pieces, IfcFileWall Wall)>();
            if (options.File != null)
                FitFile(walls, fromShape, options, result, fromFile);

            var piecesOf = new Dictionary<IfcLinkWall, List<WallPiece>>();
            foreach (IfcLinkWall w in walls)
            {
                if (fromFile.TryGetValue(w, out var f)) { piecesOf[w] = f.Pieces; result.FromFile++; continue; }
                var s = fromShape[w];
                if (s.Method == WallFootprintMethod.None) { result.Unusable++; continue; }
                piecesOf[w] = s.Pieces;
                if (s.Method == WallFootprintMethod.Outline) result.FromOutline++;
                else result.FromRectangle++;
            }
            result.WallCount = walls.Count;

            // A door or window in a wall replaces that stretch of it; the others (curtain panels, glazed
            // partitions) stand on their own
            foreach (WallPiece o in openingPieces.Values)
            {
                if (SetIntoWall(o, piecesOf.Values.ToList())) result.OpeningsInWalls++;
                else result.OpeningsFree++;
            }

            // Close the corners and T junctions between all walls
            WallFootprint.JoinEnds(piecesOf.Values.SelectMany(p => p).Concat(openingPieces.Values).ToList());

            // Columns: their plan outline, unless they stand inside a wall (the wall already blocks there)
            var allWallPieces = piecesOf.Values.SelectMany(p => p).Concat(openingPieces.Values).ToList();
            var columnPieces = new Dictionary<IfcLinkWall, List<WallPiece>>();
            foreach (IfcLinkWall c in columns)
            {
                List<WallPiece> pieces = PlaceColumn(c.Points, allWallPieces, out ColumnPlacement how);
                if (how == ColumnPlacement.Placed) { columnPieces[c] = pieces; result.Columns++; }
                else if (how == ColumnPlacement.InsideWall) result.ColumnsInWalls++;
                else if (how == ColumnPlacement.TooSmall) result.ColumnsSmall++;
                else result.Unusable++;
            }

            // One row per IFC type
            var groups = new Dictionary<string, GroupAcc>();
            void Add(IfcLinkWall w, IfcFileWall fw, string name, IEnumerable<WallPiece> pieces)
            {
                if (!groups.TryGetValue(name, out GroupAcc g))
                    groups[name] = g = new GroupAcc { Kind = w.Kind, Group = new WallLineGroup { LineStyleName = name, UserEdited = false } };
                string rating = w.AcousticRating ?? fw?.AcousticRating;
                if (g.Rating == null && rating != null) g.Rating = rating;
                foreach (string m in w.Materials.Concat(fw?.Materials ?? new List<string>()))
                    if (!g.Materials.Contains(m)) g.Materials.Add(m);

                double height = Math.Max(0, w.TopZ - w.BaseZ);
                g.Height.Add(height);
                foreach (WallPiece p in pieces)
                {
                    var seg = new WallSegment2D
                    {
                        Start = p.Start,
                        End = p.End,
                        BaseElevationM = w.BaseZ,
                        HeightM = height > 0 ? height : 3.0,
                        ThicknessM = p.ThicknessM
                    };
                    g.Group.Segments.Add(seg);
                    g.Group.SegmentCount++;
                    g.Group.TotalLengthM += seg.Length;
                    g.Thickness.Add(p.ThicknessM);
                }
            }
            foreach (var kv in piecesOf)
            {
                IfcLinkWall w = kv.Key;
                IfcFileWall fw = fromFile.TryGetValue(w, out var f) ? f.Wall : options.File?.Find(w.IfcGuid);
                string name = (string.IsNullOrWhiteSpace(w.GroupName) ? (fw?.TypeName ?? fw?.ObjectType ?? "Wall") : w.GroupName) + GroupSuffix;
                Add(w, fw, name, kv.Value);
            }
            foreach (var kv in openingPieces)
            {
                string name = (string.IsNullOrWhiteSpace(kv.Key.GroupName) ? OpeningGroupName(kv.Key.Kind, "") : kv.Key.GroupName) + GroupSuffix;
                Add(kv.Key, null, name, new[] { kv.Value });
            }
            foreach (var kv in columnPieces)
                Add(kv.Key, null, "Column " + (string.IsNullOrWhiteSpace(kv.Key.GroupName) ? "" : kv.Key.GroupName).Trim() + GroupSuffix, kv.Value);

            foreach (GroupAcc g in groups.Values.OrderBy(v => v.Group.LineStyleName, StringComparer.Ordinal))
            {
                double thickness = Median(g.Thickness);
                // A wall measured implausibly thin (a layer of it, an unreadable shape): the type name's thickness
                if (g.Kind == IfcElementKind.Wall && thickness < 0.08 &&
                    WallTypeEstimator.NominalThicknessM(g.Group.LineStyleName) is double nominal)
                    thickness = nominal;
                // Insulation layers say nothing about what the wall is ("Insulation - Fiberglass" is no glass wall)
                string names = g.Group.LineStyleName + " " + string.Join(" ", g.Materials.Where(m => !WallTypeEstimator.IsInsulation(m)));
                if (g.Kind == IfcElementKind.Column)
                {
                    // Solid (concrete, steel): rated as a thick wall of its material
                    g.Group.WallType = WallTypeEstimator.Estimate(names, 0.3, g.Rating);
                    g.Group.HeightM = 0;
                    g.Group.IsObstacle = true;
                }
                else if (g.Kind == IfcElementKind.Wall)
                {
                    g.Group.WallType = WallTypeEstimator.Estimate(names, thickness, g.Rating);
                    // Low walls (screens, parapets) as tall as modelled; others full height
                    double h = Median(g.Height);
                    g.Group.HeightM = h > 0.1 && h < JobInputBuilder.EnclosingHeightM ? Math.Round(h, 2) : 0;
                }
                else
                {
                    g.Group.WallType = WallTypeEstimator.EstimateOpening(names, g.Kind == IfcElementKind.Door, g.Rating);
                    // The wall above a door or window closes the rest: full height, encloses the room
                    g.Group.HeightM = 0;
                }
                result.Groups.Add(g.Group);
            }
            return result;
        }

        private class GroupAcc
        {
            public IfcElementKind Kind;
            public WallLineGroup Group;
            public readonly List<double> Thickness = new List<double>();
            public readonly List<double> Height = new List<double>();
            public string Rating;
            public readonly List<string> Materials = new List<string>();
        }

        /// <summary>
        /// Columns thinner than this (steel tubes, posts) are left out: sound bends round them at speech
        /// wavelengths, and their faces would only slow the run.
        /// </summary>
        public const double MinColumnSizeM = 0.15;

        /// <summary>
        /// Wall pieces for a column with plan vertices <paramref name="points"/>: its outline (<see cref="ColumnOutline"/>)
        /// plus ties to the walls near it (<see cref="TiesToWalls"/>). Null, with the reason, when it stands inside
        /// one of <paramref name="walls"/>, is too small or unreadable.
        /// </summary>
        public static List<WallPiece> PlaceColumn(IList<Vec2> points, IList<WallPiece> walls, out ColumnPlacement how)
        {
            List<Vec2> outline = ColumnOutline(points);
            if (outline == null) { how = ColumnPlacement.Unusable; return null; }
            if (MinSide(outline) < MinColumnSizeM) { how = ColumnPlacement.TooSmall; return null; }
            Vec2 centre = new Vec2(outline.Average(v => v.X), outline.Average(v => v.Y));
            if (walls.Any(p => DistanceToSegment(centre, p.Start, p.End) <= p.ThicknessM / 2 + 0.01)) { how = ColumnPlacement.InsideWall; return null; }
            var pieces = new List<WallPiece>();
            for (int i = 0; i < outline.Count; i++)
                pieces.Add(new WallPiece { Start = outline[i], End = outline[(i + 1) % outline.Count], ThicknessM = 0.02 });
            pieces.AddRange(TiesToWalls(outline, walls));
            how = ColumnPlacement.Placed;
            return pieces;
        }

        // Width of a convex outline across its narrowest direction (a rotated column's true size)
        private static double MinSide(List<Vec2> hull)
        {
            double best = double.MaxValue;
            for (int i = 0; i < hull.Count; i++)
            {
                Vec2 a = hull[i], b = hull[(i + 1) % hull.Count];
                Vec2 d = b - a;
                double len = d.Length;
                if (len < 1e-9) continue;
                double far = hull.Max(p => Math.Abs(Vec2.Cross(d, p - a)) / len);
                best = Math.Min(best, far);
            }
            return best == double.MaxValue ? 0 : best;
        }

        /// <summary>Gaps up to this wide between a column and a wall are closed by a tie piece.</summary>
        public const double ColumnTieM = 0.3;

        /// <summary>
        /// Short pieces joining a column to each wall that passes within <see cref="ColumnTieM"/> of it without
        /// touching: a facade or partition fixed to the column, so the room it closes is closed.
        /// </summary>
        public static List<WallPiece> TiesToWalls(List<Vec2> outline, IEnumerable<WallPiece> walls)
        {
            var ties = new List<WallPiece>();
            foreach (WallPiece w in walls)
            {
                if (w.Length < 1e-6) continue;
                // Closest pair: a column vertex to the wall's centerline, or a wall end to a column edge
                double best = double.MaxValue;
                Vec2 a = default(Vec2), b = default(Vec2);
                foreach (Vec2 v in outline)
                {
                    Vec2 q = ClosestOnSegment(v, w.Start, w.End);
                    double d = Vec2.Distance(v, q);
                    if (d < best) { best = d; a = v; b = q; }
                }
                for (int i = 0; i < outline.Count; i++)
                    foreach (Vec2 e in new[] { w.Start, w.End })
                    {
                        Vec2 q = ClosestOnSegment(e, outline[i], outline[(i + 1) % outline.Count]);
                        double d = Vec2.Distance(e, q);
                        if (d < best) { best = d; a = q; b = e; }
                    }
                if (best > 0.005 && best <= ColumnTieM)
                    ties.Add(new WallPiece { Start = a, End = b, ThicknessM = 0.02 });
            }
            return ties;
        }

        private static Vec2 ClosestOnSegment(Vec2 p, Vec2 a, Vec2 b)
        {
            Vec2 ab = b - a;
            double len2 = ab.LengthSquared;
            double t = len2 < 1e-12 ? 0 : Math.Max(0, Math.Min(1, Vec2.Dot(p - a, ab) / len2));
            return a + ab * t;
        }

        /// <summary>
        /// Plan outline of a column from its vertices: their convex hull (columns are rectangles, circles,
        /// polygons), a round one reduced to 8 sides. Null when it is too small or not column-like.
        /// </summary>
        public static List<Vec2> ColumnOutline(IList<Vec2> points)
        {
            if (points == null || points.Count < 3) return null;
            List<Vec2> hull = JobInputBuilder.ConvexHull(points.ToList());
            if (hull.Count < 3) return null;
            if (hull.Count > 8)
            {
                var reduced = new List<Vec2>();
                for (int i = 0; i < 8; i++) reduced.Add(hull[(int)Math.Round(i * hull.Count / 8.0) % hull.Count]);
                hull = reduced;
            }
            double minX = hull.Min(p => p.X), maxX = hull.Max(p => p.X), minY = hull.Min(p => p.Y), maxY = hull.Max(p => p.Y);
            double size = Math.Max(maxX - minX, maxY - minY);
            if (size < 0.05 || size > 3.0) return null;
            return hull;
        }

        /// <summary>
        /// Walls-table row for a door or window: its kind and the letters of its mark ("KFA-04" → "Window KFA",
        /// "VU-01-3" → "Door VU"), so a facade's panels or a door family share a row instead of one per size.
        /// </summary>
        public static string OpeningGroupName(IfcElementKind kind, string mark)
        {
            string label = kind == IfcElementKind.Door ? "Door" : "Window";
            string m = (mark ?? "").Trim();
            int n = 0;
            while (n < m.Length && char.IsLetter(m[n])) n++;
            return n > 0 && n <= 8 ? $"{label} {m.Substring(0, n).ToUpperInvariant()}" : label + "s";
        }

        /// <summary>
        /// Sets an opening into the wall piece it stands in (parallel, within both half thicknesses of its
        /// centerline, along at least half of the opening): that stretch of the wall is cut out and the opening
        /// snapped onto the wall's centerline, so a ray through the door pays the door only. False (opening left
        /// as is) when no wall holds it. <paramref name="walls"/> are the pieces per wall; split pieces replace
        /// the original in its list.
        /// </summary>
        public static bool SetIntoWall(WallPiece opening, IList<List<WallPiece>> walls)
        {
            double lenO = opening.Length;
            if (lenO < 1e-6) return false;
            Vec2 dO = opening.Direction;
            Vec2 mid = (opening.Start + opening.End) * 0.5;
            double sinTol = Math.Sin(10 * Math.PI / 180);

            List<WallPiece> owner = null;
            WallPiece best = null;
            double bestLateral = double.MaxValue, bestT0 = 0, bestT1 = 0;
            foreach (List<WallPiece> list in walls)
                foreach (WallPiece p in list)
                {
                    double lenP = p.Length;
                    if (lenP < 1e-6) continue;
                    Vec2 dP = p.Direction;
                    if (Math.Abs(Vec2.Cross(dP, dO)) > sinTol) continue;
                    double lateral = Math.Abs(Vec2.Cross(dP, mid - p.Start));
                    if (lateral > (p.ThicknessM + opening.ThicknessM) / 2 + 0.05 || lateral >= bestLateral) continue;
                    double a = Vec2.Dot(opening.Start - p.Start, dP), b = Vec2.Dot(opening.End - p.Start, dP);
                    double t0 = Math.Max(0, Math.Min(a, b)), t1 = Math.Min(lenP, Math.Max(a, b));
                    if (t1 - t0 < 0.5 * lenO) continue;
                    owner = list; best = p; bestLateral = lateral; bestT0 = t0; bestT1 = t1;
                }
            if (best == null) return false;

            Vec2 d = best.Direction;
            double len = best.Length;
            opening.Start = best.Start + d * bestT0;
            opening.End = best.Start + d * bestT1;
            int at = owner.IndexOf(best);
            owner.RemoveAt(at);
            if (len - bestT1 > 0.02) owner.Insert(at, new WallPiece { Start = opening.End, End = best.End, ThicknessM = best.ThicknessM });
            if (bestT0 > 0.02) owner.Insert(at, new WallPiece { Start = best.Start, End = opening.Start, ThicknessM = best.ThicknessM });
            return true;
        }

        /// <summary>
        /// Matches file walls to link walls by GUID, fits the file onto the link (rotation + offset, from the
        /// straight walls), and keeps a wall's file centerline when it lands on its shape.
        /// </summary>
        private static void FitFile(List<IfcLinkWall> walls,
            Dictionary<IfcLinkWall, (List<WallPiece> Pieces, WallFootprintMethod Method)> fromShape,
            IfcWallBuildOptions options, IfcWallBuildResult result,
            Dictionary<IfcLinkWall, (List<WallPiece>, IfcFileWall)> fromFile)
        {
            var matches = new List<(IfcLinkWall Link, IfcFileWall File)>();
            foreach (IfcLinkWall w in walls)
            {
                IfcFileWall fw = options.File.Find(w.IfcGuid);
                if (fw != null && fw.Centerline.Count >= 2) matches.Add((w, fw));
            }
            if (matches.Count == 0)
            {
                result.FileStatus = options.File.Walls.Count == 0
                    ? "the IFC file has no walls"
                    : "no wall in the IFC file matches the link (IfcGUID), or none has an axis";
                return;
            }

            // Straight walls on both sides give the fit: shape centroid vs file centerline midpoint, and direction
            var pairs = new List<(Vec2 ModelMid, Vec2 FileMid, double ModelAngle, double FileAngle, double Length)>();
            foreach (var (link, file) in matches)
            {
                var shape = fromShape[link];
                if (shape.Pieces.Count != 1 || file.Centerline.Count != 2) continue;
                WallPiece p = shape.Pieces[0];
                Vec2 fd = file.Centerline[1] - file.Centerline[0];
                if (p.Length < 0.5 || fd.Length < 0.5) continue;
                pairs.Add(((p.Start + p.End) * 0.5, (file.Centerline[0] + file.Centerline[1]) * 0.5,
                    p.Direction.Angle(), fd.Angle(), p.Length));
            }
            if (pairs.Count == 0)
            {
                result.FileStatus = "no straight wall to line the IFC file up with the link";
                return;
            }

            // Rotation mod 180° (a shape has no direction): mean of doubled angles, length weighted
            double sx = 0, sy = 0;
            foreach (var pr in pairs)
            {
                double d = 2 * (pr.ModelAngle - pr.FileAngle);
                sx += Math.Cos(d) * pr.Length;
                sy += Math.Sin(d) * pr.Length;
            }
            double baseAngle = Math.Atan2(sy, sx) / 2;

            PlanTransform best = null;
            double bestResidual = double.MaxValue;
            foreach (double angle in new[] { baseAngle, baseAngle + Math.PI })
            {
                var rot = new PlanTransform { AngleRad = angle };
                var offsets = pairs.Select(pr => pr.ModelMid - rot.Apply(pr.FileMid)).ToList();
                var offset = new Vec2(Median(offsets.Select(o => o.X)), Median(offsets.Select(o => o.Y)));
                double residual = Median(offsets.Select(o => Vec2.Distance(o, offset)));
                // Equal residuals (one wall): prefer the smaller rotation
                if (residual < bestResidual - 1e-6 ||
                    (Math.Abs(residual - bestResidual) <= 1e-6 && Math.Abs(NormalizeRad(angle)) < Math.Abs(NormalizeRad(best.AngleRad))))
                {
                    bestResidual = residual;
                    best = new PlanTransform { AngleRad = NormalizeRad(angle), Offset = offset };
                }
            }

            // Along a wall the two midpoints can disagree (a corner shortens the shape's midline); across it they
            // must not. Refine the offset from the across-wall distances (least squares), when walls run in at
            // least two directions, and measure the fit by them.
            var normals = new List<(Vec2 N, Vec2 Model, Vec2 File)>();
            foreach (var pr in pairs)
            {
                var n = new Vec2(-Math.Sin(pr.ModelAngle), Math.Cos(pr.ModelAngle));
                Vec2 rf = new PlanTransform { AngleRad = best.AngleRad }.Apply(pr.FileMid);
                normals.Add((n, pr.ModelMid, rf));
            }
            Vec2? Solve(List<(Vec2 N, Vec2 Model, Vec2 File)> use)
            {
                double s11 = 0, s12 = 0, s22 = 0, r1 = 0, r2 = 0;
                foreach (var x in use)
                {
                    double rhs = Vec2.Dot(x.N, x.Model - x.File);
                    s11 += x.N.X * x.N.X; s12 += x.N.X * x.N.Y; s22 += x.N.Y * x.N.Y;
                    r1 += x.N.X * rhs; r2 += x.N.Y * rhs;
                }
                double dt = s11 * s22 - s12 * s12;
                if (dt <= 0.05 * use.Count * use.Count * 0.25) return null;   // walls in one direction only
                return new Vec2((s22 * r1 - s12 * r2) / dt, (s11 * r2 - s12 * r1) / dt);
            }
            double Across((Vec2 N, Vec2 Model, Vec2 File) x, Vec2 off) => Math.Abs(Vec2.Dot(x.N, x.File + off - x.Model));
            Vec2? solved = Solve(normals);
            if (solved.HasValue)
            {
                // A wall that is far off (moved in one model but not the other, unreadable placement) must not drag
                // the others: solve again without the outliers
                double med = Median(normals.Select(x => Across(x, solved.Value)));
                var inliers = normals.Where(x => Across(x, solved.Value) <= Math.Max(0.1, 3 * med)).ToList();
                Vec2? again = inliers.Count >= 2 && inliers.Count < normals.Count ? Solve(inliers) : null;
                best.Offset = again ?? solved.Value;
                bestResidual = Median(normals.Select(x => Across(x, best.Offset)));
            }

            result.FitResidualM = bestResidual;
            if (bestResidual > options.MaxFitResidualM)
            {
                result.FileStatus = $"the IFC file does not line up with the link (walls {bestResidual:F2} m apart); using the shapes";
                return;
            }
            result.FileToModel = best;

            // Keep each wall's file centerline only when it runs inside that wall's shape
            int rejected = 0;
            foreach (var (link, file) in matches)
            {
                List<Vec2> line = file.Centerline.Select(best.Apply).ToList();
                var shape = fromShape[link];
                double thickness = file.ThicknessM ?? (shape.Pieces.Count > 0 ? Median(shape.Pieces.Select(p => p.ThicknessM)) : 0);
                if (thickness <= 0 || thickness > WallFootprint.MaxThicknessM) { rejected++; continue; }

                if (shape.Pieces.Count > 0)
                {
                    // The inside of the axis must run along the shape; its ends may reach into joined walls
                    double tol = Math.Max(0.15, thickness) + shape.Pieces.Max(sp => sp.ThicknessM) / 2;
                    bool onShape = true;
                    for (int i = 0; i < line.Count - 1 && onShape; i++)
                        foreach (double t in new[] { 0.25, 0.5, 0.75 })
                        {
                            Vec2 p = line[i] + (line[i + 1] - line[i]) * t;
                            if (shape.Pieces.Min(sp => DistanceToSegment(p, sp.Start, sp.End)) > tol) { onShape = false; break; }
                        }
                    if (!onShape) { rejected++; continue; }
                }

                var pieces = new List<WallPiece>();
                for (int i = 0; i < line.Count - 1; i++)
                    if (Vec2.Distance(line[i], line[i + 1]) > 1e-4)
                        pieces.Add(new WallPiece { Start = line[i], End = line[i + 1], ThicknessM = thickness });
                if (pieces.Count > 0) fromFile[link] = (pieces, file);
            }

            result.FileStatus = $"{fromFile.Count} of {walls.Count} wall(s) placed from the IFC file ({best})" +
                (rejected > 0 ? $", {rejected} kept from shapes" : "");
        }

        private static double NormalizeRad(double a)
        {
            while (a <= -Math.PI) a += 2 * Math.PI;
            while (a > Math.PI) a -= 2 * Math.PI;
            return a;
        }

        private static double DistanceToSegment(Vec2 p, Vec2 a, Vec2 b)
        {
            Vec2 ab = b - a;
            double len2 = ab.LengthSquared;
            double t = len2 < 1e-12 ? 0 : Math.Max(0, Math.Min(1, Vec2.Dot(p - a, ab) / len2));
            return Vec2.Distance(p, a + ab * t);
        }

        private static double Median(IEnumerable<double> values)
        {
            var v = values.OrderBy(x => x).ToList();
            if (v.Count == 0) return 0;
            return v.Count % 2 == 1 ? v[v.Count / 2] : (v[v.Count / 2 - 1] + v[v.Count / 2]) / 2;
        }
    }
}
