using System;
using System.Collections.Generic;
using System.Linq;
using SoundCalcs.Domain.Ifc;

namespace SoundCalcs.Domain
{
    /// <summary>
    /// One wall of a linked IFC as Revit rebuilt it: shapes only (DirectShape), plus the IFC data Revit kept as
    /// parameters. Coordinates in metres, in the host model.
    /// </summary>
    public class IfcLinkWall
    {
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
                // A wall modelled as one solid per layer: the touching layers are one wall, not several
                if (w.Outlines.Count > 1) WallFootprint.MergeTouching(pieces);
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

            // Close the corners and T junctions between all walls
            WallFootprint.JoinEnds(piecesOf.Values.SelectMany(p => p).ToList());

            // One row per IFC type
            var groups = new Dictionary<string, (WallLineGroup Group, List<double> Thickness, List<double> Height, string Rating, List<string> Materials)>();
            foreach (var kv in piecesOf)
            {
                IfcLinkWall w = kv.Key;
                IfcFileWall fw = fromFile.TryGetValue(w, out var f) ? f.Wall : options.File?.Find(w.IfcGuid);
                string name = (string.IsNullOrWhiteSpace(w.GroupName) ? (fw?.TypeName ?? fw?.ObjectType ?? "Wall") : w.GroupName) + GroupSuffix;
                if (!groups.TryGetValue(name, out var g))
                {
                    g = (new WallLineGroup { LineStyleName = name }, new List<double>(), new List<double>(), null, new List<string>());
                    groups[name] = g;
                }
                string rating = w.AcousticRating ?? fw?.AcousticRating;
                if (g.Rating == null && rating != null) { g.Rating = rating; groups[name] = g; }
                foreach (string m in w.Materials.Concat(fw?.Materials ?? new List<string>()))
                    if (!g.Materials.Contains(m)) g.Materials.Add(m);

                double height = Math.Max(0, w.TopZ - w.BaseZ);
                g.Height.Add(height);
                foreach (WallPiece p in kv.Value)
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

            foreach (var g in groups.Values.OrderBy(v => v.Group.LineStyleName, StringComparer.Ordinal))
            {
                double thickness = Median(g.Thickness);
                string names = g.Group.LineStyleName + " " + string.Join(" ", g.Materials);
                g.Group.WallType = WallTypeEstimator.Estimate(names, thickness, g.Rating);
                // Low walls (screens, parapets) as tall as modelled; others full height
                double h = Median(g.Height);
                g.Group.HeightM = h > 0.1 && h < JobInputBuilder.EnclosingHeightM ? Math.Round(h, 2) : 0;
                result.Groups.Add(g.Group);
            }
            return result;
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
            double a11 = 0, a12 = 0, a22 = 0, b1 = 0, b2 = 0;
            var normals = new List<(Vec2 N, Vec2 Model, Vec2 File)>();
            foreach (var pr in pairs)
            {
                var n = new Vec2(-Math.Sin(pr.ModelAngle), Math.Cos(pr.ModelAngle));
                Vec2 rf = new PlanTransform { AngleRad = best.AngleRad }.Apply(pr.FileMid);
                double rhs = Vec2.Dot(n, pr.ModelMid - rf);
                a11 += n.X * n.X; a12 += n.X * n.Y; a22 += n.Y * n.Y;
                b1 += n.X * rhs; b2 += n.Y * rhs;
                normals.Add((n, pr.ModelMid, rf));
            }
            double det = a11 * a22 - a12 * a12;
            if (det > 0.05 * pairs.Count * pairs.Count * 0.25)
            {
                best.Offset = new Vec2((a22 * b1 - a12 * b2) / det, (a11 * b2 - a12 * b1) / det);
                bestResidual = Median(normals.Select(x => Math.Abs(Vec2.Dot(x.N, x.File + best.Offset - x.Model))));
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
