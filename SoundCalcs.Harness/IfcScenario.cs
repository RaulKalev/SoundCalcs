using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using SoundCalcs.Domain;
using SoundCalcs.Domain.Ifc;
using SoundCalcs.Compute;
using SoundCalcs.UI.ViewModels;
using SoundCalcs.Visualization;

namespace SoundCalcs.Harness
{
    /// <summary>
    /// Walls from a linked IFC: the plan of <c>two_rooms</c> modelled as IFC walls (shapes only, as Revit rebuilds
    /// them), turned back into wall lines by <see cref="IfcWallBuilder"/> and run through the normal pipeline. The
    /// same walls are also written as an IFC file in a rotated, moved frame to check the file path.
    /// </summary>
    public static class IfcScenarios
    {
        // Reference centerlines (host coordinates), thickness, and how the IFC wall's shape is modelled
        class RefWall
        {
            public string Guid, Type;
            public Vec2 A, B;
            public double T;
            public List<Vec2> Outline;
        }

        static List<RefWall> Plan()
        {
            // 16 × 8 m, 0.2 m concrete outside, 0.1 m partition at x = 10. South and west walls run past the
            // corner to the outer face; the east + north walls are one L-shaped element; the partition stops
            // at the faces of the south and north walls (as IFC exporters cut them).
            return new List<RefWall>
            {
                new RefWall { Guid = "south", Type = "Concrete 200", A = new Vec2(0, 0), B = new Vec2(16, 0), T = 0.2,
                    Outline = Rect(-0.1, -0.1, 16.1, 0.1) },
                new RefWall { Guid = "west", Type = "Concrete 200", A = new Vec2(0, 8), B = new Vec2(0, 0), T = 0.2,
                    Outline = Rect(-0.1, 0.1, 0.1, 8.1) },
                new RefWall { Guid = "east+north", Type = "Concrete 200", A = new Vec2(16, 0), B = new Vec2(16, 8), T = 0.2,
                    Outline = new List<Vec2> { new Vec2(15.9, 0.1), new Vec2(16.1, 0.1), new Vec2(16.1, 8.1), new Vec2(0.1, 8.1),
                        new Vec2(0.1, 7.9), new Vec2(15.9, 7.9) } },
                new RefWall { Guid = "partition", Type = "Gypsum partition 100", A = new Vec2(10, 0), B = new Vec2(10, 8), T = 0.1,
                    Outline = Rect(9.95, 0.1, 10.05, 7.9) },
            };
        }

        // The second leg of the L-shaped element, for checking its centerline
        static readonly (Vec2 A, Vec2 B) NorthLeg = (new Vec2(16, 8), new Vec2(0, 8));

        static List<Vec2> Rect(double x0, double y0, double x1, double y1) =>
            new List<Vec2> { new Vec2(x0, y0), new Vec2(x1, y0), new Vec2(x1, y1), new Vec2(x0, y1) };

        static List<IfcLinkWall> LinkWalls() => Plan().Select((w, i) => new IfcLinkWall
        {
            ElementId = i + 1, IfcGuid = w.Guid, GroupName = w.Type, BaseZ = 0, TopZ = 3,
            Outlines = { w.Outline }
        }).ToList();

        static List<WallSpec> ToWallSpecs(IfcWallBuildResult built) =>
            built.Groups.SelectMany(g => g.Segments.Select(s => new WallSpec
            {
                X1 = s.Start.X, Y1 = s.Start.Y, X2 = s.End.X, Y2 = s.End.Y,
                WallType = g.WallType.Key, HeightM = g.HeightM, ThicknessM = s.ThicknessM
            })).ToList();

        public static Scenario IfcWalls()
        {
            IfcWallBuildResult built = IfcWallBuilder.Build(LinkWalls(), new IfcWallBuildOptions { LevelElevationM = 0 });
            var spec = new ScenarioSpec
            {
                Name = "ifc_walls",
                Description = "The two_rooms plan as IFC walls, shapes only (as Revit rebuilds a linked IFC): butt-joined " +
                              "outer walls, an L-shaped wall element, a partition cut at the outer walls' faces. Wall lines " +
                              "are rebuilt from the shapes and must land on the true centerlines with closed corners, so " +
                              "the boundary splits into the same two rooms; the same walls written as an IFC file in a " +
                              "rotated, moved frame must be fitted back onto the link.",
                Walls = ToWallSpecs(built),
                Quality = CalculationQuality.Full,
                Environment = new EnvironmentSettings { RT60ByBand = IecReference.Fill(0.8) },
                Speakers = { new SpeakerSpec { X = 5, Y = 4, HeightM = 3.0, Profile = ScenarioSpec.Cone(90, 60, -12) } }
            };

            return new Scenario
            {
                Spec = spec,
                ViewerModes = new[] { VisualizationMode.SPL },
                Checks = (run, ctx) =>
                {
                    ctx.Assert("every IFC wall rebuilt by pairing its shape's sides",
                        built.FromOutline == 4 && built.FromRectangle == 0 && built.Unusable == 0, built.Summary());

                    var segs = built.Groups.SelectMany(g => g.Segments).ToList();
                    var refs = Plan().Where(w => w.Guid != "east+north").Select(w => (w.A, w.B, w.T, w.Guid)).ToList();
                    refs.Add((new Vec2(16, 0), new Vec2(16, 8), 0.2, "east leg"));
                    refs.Add((NorthLeg.A, NorthLeg.B, 0.2, "north leg"));
                    foreach (var r in refs)
                    {
                        Vec2 d = (r.B - r.A).Normalized();
                        var along = segs.Where(s => Math.Abs(Vec2.Cross(d, (s.End - s.Start).Normalized())) < 1e-6 &&
                                                    Math.Abs(Vec2.Cross(d, s.Start - r.A)) < 0.05).ToList();
                        double off = along.Count == 0 ? double.NaN : along.Max(s => Math.Max(Math.Abs(Vec2.Cross(d, s.Start - r.A)), Math.Abs(Vec2.Cross(d, s.End - r.A))));
                        double len = along.Sum(s => s.Length);
                        ctx.Assert($"{r.Guid}: on its centerline, full length, thickness {r.T} m",
                            along.Count > 0 && off < 1e-6 && Math.Abs(len - Vec2.Distance(r.A, r.B)) < 1e-6 && along.All(s => Math.Abs(s.ThicknessM - r.T) < 1e-9),
                            $"{along.Count} segment(s), off by {CheckContext.F(off)} m, length {CheckContext.F(len)} m");
                    }

                    // Closed corners and T junctions: every end lies on another wall line
                    int open = 0;
                    foreach (var s in segs)
                        foreach (Vec2 p in new[] { s.Start, s.End })
                            if (!segs.Any(o => !ReferenceEquals(o, s) && DistanceToSegment(p, o.Start, o.End) < 1e-6)) open++;
                    ctx.Assert("every wall end meets another wall (no gaps at corners or junctions)", open == 0, $"{open} open end(s)");

                    var rooms = run.Input.Rooms;
                    var areas = rooms.Select(r => r.EffectiveAreaM2).OrderByDescending(a => a).ToList();
                    ctx.Assert("boundary split into the two walled rooms (80 m² and 48 m²)",
                        rooms.Count == 2 && Math.Abs(areas[0] - 80) < 1 && Math.Abs(areas[1] - 48) < 1,
                        string.Join(", ", rooms.Select(r => $"{r.Name}: {CheckContext.F(r.EffectiveAreaM2)} m²")));
                    double gap = run.Output.Results.Where(r => r.Position.X < 9.5).Average(r => r.SplDb) -
                                 run.Output.Results.Where(r => r.Position.X > 10.5).Average(r => r.SplDb);
                    ctx.Assert("room behind the partition is far quieter (no leak at the junctions)", gap > 15,
                        $"{CheckContext.F(gap)} dB");

                    var concrete = built.Groups.Single(g => g.LineStyleName.StartsWith("Concrete"));
                    ctx.Assert("concrete walls typed as concrete from their name", concrete.WallType.Surface == WallAbsorptionPreset.Concrete,
                        concrete.WallType.DisplayName);

                    // ---- The original file: same walls, storey rotated 25° and moved 300 m / −120 m ----
                    string ifc = WriteIfc(Plan(), 25, 300, -120);
                    IfcFileData file = IfcWallReader.Read(new StringReader(ifc));
                    IfcWallBuildResult fromFile = IfcWallBuilder.Build(LinkWalls(), new IfcWallBuildOptions { LevelElevationM = 0, File = file });
                    ctx.Assert("IFC file: every wall placed from its axis and layers", fromFile.FromFile == 4, fromFile.FileStatus);
                    ctx.Near("IFC file: rotation back onto the link", fromFile.FileToModel?.AngleRad * 180 / Math.PI ?? double.NaN, -25, 1e-6, "°");
                    var fileSegs = fromFile.Groups.SelectMany(g => g.Segments).ToList();
                    double worst = fileSegs.Max(s => Math.Max(segs.Min(o => DistanceToLine(s.Start, o)), segs.Min(o => DistanceToLine(s.End, o))));
                    ctx.Assert("IFC file: walls land on the lines rebuilt from the shapes", worst < 1e-6, $"worst {CheckContext.F(worst)} m");
                    var concreteFile = fromFile.Groups.Single(g => g.LineStyleName.StartsWith("Concrete"));
                    ctx.Assert("IFC file: AcousticRating (Rw 55) sets the rating, the name the material",
                        concreteFile.WallType.StcRating == 55 && concreteFile.WallType.Surface == WallAbsorptionPreset.Concrete,
                        concreteFile.WallType.DisplayName);

                    IfcFileData shifted = IfcWallReader.Read(new StringReader(WriteIfc(Plan(), 25, 300, -120, scrambleEast: 3.0)));
                    var partial = IfcWallBuilder.Build(LinkWalls(), new IfcWallBuildOptions { LevelElevationM = 0, File = shifted });
                    ctx.Assert("IFC file: a wall whose axis is not on its shape keeps the shape's line",
                        partial.FromFile == 3 && partial.FromOutline == 1, partial.FileStatus);
                }
            };
        }

        static double DistanceToSegment(Vec2 p, Vec2 a, Vec2 b)
        {
            Vec2 ab = b - a;
            double t = Math.Max(0, Math.Min(1, Vec2.Dot(p - a, ab) / Math.Max(1e-12, ab.LengthSquared)));
            return Vec2.Distance(p, a + ab * t);
        }

        static double DistanceToLine(Vec2 p, WallSegment2D s)
        {
            Vec2 d = (s.End - s.Start).Normalized();
            double t = Vec2.Dot(p - s.Start, d);
            if (t < -0.2 || t > s.Length + 0.2) return double.MaxValue;
            return Math.Abs(Vec2.Cross(d, p - s.Start));
        }

        /// <summary>
        /// The plan as an IFC4 file in millimetres: walls in a storey placed at <paramref name="angleDeg"/> and
        /// (<paramref name="dx"/>, <paramref name="dy"/>) m, each with an Axis polyline and a layer set usage whose
        /// reference line is the wall's face (offset 0, layers to the left), AcousticRating on the concrete type.
        /// </summary>
        static string WriteIfc(List<RefWall> plan, double angleDeg, double dx, double dy, double scrambleEast = 0)
        {
            var sb = new StringBuilder();
            int id = 100;
            string F(double v) => v.ToString("0.0###########", CultureInfo.InvariantCulture);
            int Add(string entity) { sb.Append('#').Append(++id).Append('=').Append(entity).Append(";\n"); return id; }

            sb.Append("ISO-10303-21;\nHEADER;FILE_SCHEMA(('IFC4'));ENDSEC;\nDATA;\n");
            int mm = Add("IFCSIUNIT(*,.LENGTHUNIT.,.MILLI.,.METRE.)");
            Add($"IFCUNITASSIGNMENT((#{mm}))");
            double a = angleDeg * Math.PI / 180;
            int stoLoc = Add($"IFCCARTESIANPOINT(({F(dx * 1000)},{F(dy * 1000)},0.))");
            int stoZ = Add("IFCDIRECTION((0.,0.,1.))");
            int stoX = Add($"IFCDIRECTION(({F(Math.Cos(a))},{F(Math.Sin(a))},0.))");
            int stoAxes = Add($"IFCAXIS2PLACEMENT3D(#{stoLoc},#{stoZ},#{stoX})");
            int sto = Add($"IFCLOCALPLACEMENT($,#{stoAxes})");

            var types = new Dictionary<string, (int Type, int LayerSet)>();
            foreach (var w in plan)
            {
                if (!types.ContainsKey(w.Type))
                {
                    int mat = Add($"IFCMATERIAL('{(w.Type.StartsWith("Concrete") ? "Concrete" : "Gypsum board")}',$,$)");
                    int layer = Add($"IFCMATERIALLAYER(#{mat},{F(w.T * 1000)},$,$,$,$,$)");
                    int set = Add($"IFCMATERIALLAYERSET((#{layer}),'{w.Type}',$)");
                    var props = new List<int>();
                    if (w.Type.StartsWith("Concrete"))
                        props.Add(Add("IFCPROPERTYSINGLEVALUE('AcousticRating',$,IFCLABEL('Rw 55 dB'),$)"));
                    int pset = Add($"IFCPROPERTYSET('{w.Type}-pset',$,'Pset_WallCommon',$,({string.Join(",", props.Select(p => "#" + p))}))");
                    int type = Add($"IFCWALLTYPE('{w.Type}-type',$,'{w.Type}',$,$,(#{pset}),$,$,$,.STANDARD.)");
                    types[w.Type] = (type, set);
                }

                // Axis polylines: the L element's two legs as one polyline; the reference line is the wall's
                // right face (layers POSITIVE to the left, offset 0), so the reader must shift it by T/2
                var axis = w.Guid == "east+north" ? new List<Vec2> { w.A, w.B, NorthLeg.B } : new List<Vec2> { w.A, w.B };
                if (w.Guid == "east+north" && scrambleEast > 0) axis = axis.Select(p => new Vec2(p.X - scrambleEast, p.Y - scrambleEast)).ToList();
                List<Vec2> face = IfcWallReader.OffsetLeft(axis, -w.T / 2);
                var pts = face.Select(p => Add($"IFCCARTESIANPOINT(({F(p.X * 1000)},{F(p.Y * 1000)}))")).ToList();
                int poly = Add($"IFCPOLYLINE(({string.Join(",", pts.Select(p => "#" + p))}))");
                int rep = Add($"IFCSHAPEREPRESENTATION($,'Axis','Curve2D',(#{poly}))");
                int shape = Add($"IFCPRODUCTDEFINITIONSHAPE($,$,(#{rep}))");
                int origin = Add("IFCCARTESIANPOINT((0.,0.,0.))");
                int place = Add($"IFCAXIS2PLACEMENT3D(#{origin},$,$)");
                int local = Add($"IFCLOCALPLACEMENT(#{sto},#{place})");
                int wall = Add($"IFCWALL('{w.Guid}',$,'{w.Guid}',$,$,#{local},#{shape},$,$)");
                int usage = Add($"IFCMATERIALLAYERSETUSAGE(#{types[w.Type].LayerSet},.AXIS2.,.POSITIVE.,0.,$)");
                Add($"IFCRELASSOCIATESMATERIAL('{w.Guid}-m',$,$,$,(#{wall}),#{usage})");
                Add($"IFCRELDEFINESBYTYPE('{w.Guid}-t',$,$,$,(#{wall}),#{types[w.Type].Type})");
            }
            sb.Append("ENDSEC;\nEND-ISO-10303-21;\n");
            return sb.ToString();
        }
    }
}
