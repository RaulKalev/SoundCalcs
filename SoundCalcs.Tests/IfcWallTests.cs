using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SoundCalcs.Domain;
using SoundCalcs.Domain.Ifc;
using Xunit;

namespace SoundCalcs.Tests
{
    public class StepFileTests
    {
        [Fact]
        public void Parses_Values_Strings_Comments_AndTypedValues()
        {
            const string text = @"ISO-10303-21;
HEADER;
FILE_SCHEMA(('IFC2X3'));
ENDSEC;
DATA;
/* a comment; with a semicolon */
#1=IFCLABELLED('it''s; fine','\X2\00E4\X0\ and \X\E4',.T.,$,*,(1,2.5,-3.,1.E-3),#22,IFCLABEL('x'));
#2 = IFCOTHER ( ( #1 , #3 ) , .POSITIVE. ) ;
ENDSEC;
END-ISO-10303-21;";
            StepFile f = StepFile.Parse(new StringReader(text));

            Assert.Equal("IFC2X3", f.Schema);
            StepEntity e = f.Get(1);
            Assert.Equal("IFCLABELLED", e.Type);
            Assert.Equal("it's; fine", e.Arg(0));
            Assert.Equal("ä and ä", e.Arg(1));
            Assert.Equal("T", ((StepEnum)e.Arg(2)).Name);
            Assert.Null(e.Arg(3));
            Assert.Null(e.Arg(4));
            var nums = (List<object>)e.Arg(5);
            Assert.Equal(new object[] { 1L, 2.5, -3.0, 0.001 }, nums);
            Assert.Equal(22, ((StepRef)e.Arg(6)).Id);
            Assert.Equal("x", ((StepTyped)e.Arg(7)).Value);

            StepEntity e2 = f.Get(2);
            Assert.Equal(new[] { 1, 3 }, ((List<object>)e2.Arg(0)).Cast<StepRef>().Select(r => r.Id));
            Assert.Equal("POSITIVE", ((StepEnum)e2.Arg(1)).Name);
        }

        [Fact]
        public void KeepType_SkipsOtherEntities()
        {
            StepFile f = StepFile.Parse(new StringReader("DATA;#1=IFCA(1);#2=IFCB(2);ENDSEC;"), t => t == "IFCB");
            Assert.Null(f.Get(1));
            Assert.NotNull(f.Get(2));
        }
    }

    public class IfcWallReaderTests
    {
        // Millimetres; storey rotated 90° and moved; one straight wall with a NEGATIVE layer set offset,
        // one curved wall (trimmed circle, degrees), AcousticRating on the wall type.
        const string Ifc = @"ISO-10303-21;
HEADER;FILE_SCHEMA(('IFC2X3'));ENDSEC;
DATA;
#1=IFCSIUNIT(*,.LENGTHUNIT.,.MILLI.,.METRE.);
#2=IFCSIUNIT(*,.PLANEANGLEUNIT.,$,.RADIAN.);
#3=IFCMEASUREWITHUNIT(IFCPLANEANGLEMEASURE(0.0174532925199433),#2);
#4=IFCCONVERSIONBASEDUNIT(#5,.PLANEANGLEUNIT.,'DEGREE',#3);
#5=IFCDIMENSIONALEXPONENTS(0,0,0,0,0,0,0);
#6=IFCUNITASSIGNMENT((#1,#4));
#10=IFCCARTESIANPOINT((1000.,2000.,0.));
#11=IFCDIRECTION((0.,0.,1.));
#12=IFCDIRECTION((0.,1.,0.));
#13=IFCAXIS2PLACEMENT3D(#10,#11,#12);
#14=IFCLOCALPLACEMENT($,#13);
#20=IFCCARTESIANPOINT((0.,0.,0.));
#21=IFCAXIS2PLACEMENT3D(#20,$,$);
#22=IFCLOCALPLACEMENT(#14,#21);
#30=IFCCARTESIANPOINT((0.,0.));
#31=IFCCARTESIANPOINT((4000.,0.));
#32=IFCPOLYLINE((#30,#31));
#33=IFCSHAPEREPRESENTATION(#99,'Axis','Curve2D',(#32));
#34=IFCPRODUCTDEFINITIONSHAPE($,$,(#33));
#40=IFCWALLSTANDARDCASE('wall-1',$,'W1',$,'Basic Wall:Concrete 200',#22,#34,$);
#50=IFCMATERIAL('Concrete');
#51=IFCMATERIALLAYER(#50,200.,$);
#52=IFCMATERIALLAYERSET((#51),'Concrete 200');
#53=IFCMATERIALLAYERSETUSAGE(#52,.AXIS2.,.NEGATIVE.,100.,$);
#54=IFCRELASSOCIATESMATERIAL('r1',$,$,$,(#40),#53);
#60=IFCWALLTYPE('t1',$,'Concrete 200',$,$,(#62),$,$,$,.STANDARD.);
#61=IFCPROPERTYSINGLEVALUE('AcousticRating',$,IFCLABEL('Rw 52 dB'),$);
#62=IFCPROPERTYSET('p1',$,'Pset_WallCommon',$,(#61,#63));
#63=IFCPROPERTYSINGLEVALUE('IsExternal',$,IFCBOOLEAN(.F.),$);
#64=IFCRELDEFINESBYTYPE('r2',$,$,$,(#40),#60);
#70=IFCAXIS2PLACEMENT2D(#30,$);
#71=IFCCIRCLE(#70,5000.);
#72=IFCTRIMMEDCURVE(#71,(IFCPARAMETERVALUE(0.)),(IFCPARAMETERVALUE(90.)),.T.,.PARAMETER.);
#73=IFCSHAPEREPRESENTATION(#99,'Axis','Curve2D',(#72));
#74=IFCPRODUCTDEFINITIONSHAPE($,$,(#73));
#75=IFCWALL('wall-2',$,'Curved',$,$,#14,#74,$);
ENDSEC;
END-ISO-10303-21;";

        static IfcFileData Read() => IfcWallReader.Read(new StringReader(Ifc));

        [Fact]
        public void StraightWall_PlacedThroughChain_CenteredOnLayers()
        {
            IfcFileData d = Read();
            Assert.Equal(0.001, d.LengthUnitM);
            IfcFileWall w = d.Find("wall-1");

            // Storey at (1, 2) m, X axis along world +Y: local (x, y) → world (1 − y, 2 + x).
            // Layers NEGATIVE from an offset of +100 mm: they span local y 100 … −100, centred on the axis.
            Assert.Equal(2, w.Centerline.Count);
            Assert.Equal(1.0, w.Centerline[0].X, 6);
            Assert.Equal(2.0, w.Centerline[0].Y, 6);
            Assert.Equal(1.0, w.Centerline[1].X, 6);
            Assert.Equal(6.0, w.Centerline[1].Y, 6);
            Assert.Equal(0.2, w.ThicknessM.Value, 6);
            Assert.Equal(new[] { "Concrete" }, w.Materials);
            Assert.Equal("Concrete 200", w.TypeName);
            Assert.Equal("Basic Wall:Concrete 200", w.ObjectType);
            Assert.Equal("Rw 52 dB", w.AcousticRating);
            Assert.False(w.IsExternal);
        }

        [Fact]
        public void CurvedWall_TrimmedCircleInDegrees_IsTessellatedOnTheArc()
        {
            IfcFileWall w = Read().Find("wall-2");
            Assert.True(w.Centerline.Count > 10);
            var centre = new Vec2(1, 2);
            Assert.All(w.Centerline, p => Assert.Equal(5.0, Vec2.Distance(p, centre), 6));
            // Quarter circle from local +X (world +Y) counter-clockwise to local +Y (world −X)
            Assert.Equal(1.0, w.Centerline[0].X, 6);
            Assert.Equal(7.0, w.Centerline[0].Y, 6);
            Assert.Equal(-4.0, w.Centerline.Last().X, 6);
            Assert.Equal(2.0, w.Centerline.Last().Y, 6);
            Assert.Null(w.ThicknessM);
        }

        [Fact]
        public void OffsetLeft_KeepsSidesParallel()
        {
            var line = new List<Vec2> { new Vec2(0, 0), new Vec2(4, 0), new Vec2(4, 3) };
            List<Vec2> o = IfcWallReader.OffsetLeft(line, 0.5);
            Assert.Equal(new Vec2(0, 0.5), o[0]);
            Assert.Equal(3.5, o[1].X, 9);
            Assert.Equal(0.5, o[1].Y, 9);
            Assert.Equal(new Vec2(3.5, 3), o[2]);
        }
    }

    public class WallFootprintTests
    {
        static List<Vec2> Poly(params double[] xy) =>
            Enumerable.Range(0, xy.Length / 2).Select(i => new Vec2(xy[2 * i], xy[2 * i + 1])).ToList();

        static double OffLine(WallPiece p, Vec2 a, Vec2 b)
        {
            Vec2 d = (b - a).Normalized();
            return Math.Max(Math.Abs(Vec2.Cross(d, p.Start - a)), Math.Abs(Vec2.Cross(d, p.End - a)));
        }

        [Fact]
        public void StraightWall_GivesItsCenterlineAndThickness()
        {
            var pieces = WallFootprint.FromOutline(Poly(0, -0.1, 5, -0.1, 5, 0.1, 0, 0.1), out var method);
            Assert.Equal(WallFootprintMethod.Outline, method);
            WallPiece p = Assert.Single(pieces);
            Assert.Equal(0.2, p.ThicknessM, 9);
            Assert.Equal(5, p.Length, 9);
            Assert.True(OffLine(p, new Vec2(0, 0), new Vec2(1, 0)) < 1e-9);
        }

        [Fact]
        public void ShortStub_EndCapsDoNotPair()
        {
            var pieces = WallFootprint.FromOutline(Poly(0, 0, 0.3, 0, 0.3, 0.2, 0, 0.2), out _);
            WallPiece p = Assert.Single(pieces);
            Assert.Equal(0.2, p.ThicknessM, 9);
            Assert.Equal(0.3, p.Length, 9);
        }

        [Fact]
        public void LShapedWall_TwoLegs_JoinAtTheCorner()
        {
            // Legs along x (y 0..0.2) and along y (x 0..0.2), outer corner at the origin
            var pieces = WallFootprint.FromOutline(Poly(0, 0, 4, 0, 4, 0.2, 0.2, 0.2, 0.2, 3, 0, 3), out var method);
            Assert.Equal(WallFootprintMethod.Outline, method);
            Assert.Equal(2, pieces.Count);
            WallFootprint.JoinEnds(pieces);
            var corner = new Vec2(0.1, 0.1);
            Assert.All(pieces, p => Assert.True(
                Math.Min(Vec2.Distance(p.Start, corner), Vec2.Distance(p.End, corner)) < 1e-9, p.ToString()));
        }

        [Fact]
        public void TJunction_ExtendsTheStemToTheOtherCenterline()
        {
            var pieces = new List<WallPiece>
            {
                new WallPiece { Start = new Vec2(0, 0), End = new Vec2(10, 0), ThicknessM = 0.3 },
                new WallPiece { Start = new Vec2(5, 0.15), End = new Vec2(5, 4), ThicknessM = 0.1 }
            };
            WallFootprint.JoinEnds(pieces);
            Assert.Equal(new Vec2(5, 0), pieces[1].Start);
            Assert.Equal(new Vec2(0, 0), pieces[0].Start);   // the through wall is untouched
            Assert.Equal(new Vec2(10, 0), pieces[0].End);
        }

        [Fact]
        public void CornerOvershoot_IsTrimmedToTheCorner()
        {
            // South wall modelled to the outer face (−0.1 … 10.1); west wall between the faces
            var pieces = new List<WallPiece>
            {
                new WallPiece { Start = new Vec2(-0.1, 0), End = new Vec2(10.1, 0), ThicknessM = 0.2 },
                new WallPiece { Start = new Vec2(0, 0.1), End = new Vec2(0, 6), ThicknessM = 0.2 }
            };
            WallFootprint.JoinEnds(pieces);
            Assert.Equal(new Vec2(0, 0), pieces[0].Start);
            Assert.Equal(new Vec2(10.1, 0), pieces[0].End);   // nothing to meet at the far end
            Assert.Equal(new Vec2(0, 0), pieces[1].Start);
        }

        [Fact]
        public void FarApartWalls_AreNotJoined()
        {
            var pieces = new List<WallPiece>
            {
                new WallPiece { Start = new Vec2(0, 0), End = new Vec2(10, 0), ThicknessM = 0.2 },
                new WallPiece { Start = new Vec2(5, 1.5), End = new Vec2(5, 4), ThicknessM = 0.2 }
            };
            WallFootprint.JoinEnds(pieces);
            Assert.Equal(new Vec2(5, 1.5), pieces[1].Start);
        }

        [Fact]
        public void CurvedWall_PiecesFollowTheMiddleRadius()
        {
            // Quarter annulus, radii 4.9 and 5.1, finely tessellated as Revit does
            var outline = new List<Vec2>();
            for (int i = 0; i <= 45; i++) { double a = i * Math.PI / 2 / 45; outline.Add(new Vec2(5.1 * Math.Cos(a), 5.1 * Math.Sin(a))); }
            for (int i = 30; i >= 0; i--) { double a = i * Math.PI / 2 / 30; outline.Add(new Vec2(4.9 * Math.Cos(a), 4.9 * Math.Sin(a))); }

            var pieces = WallFootprint.FromOutline(outline, out var method);
            Assert.Equal(WallFootprintMethod.Outline, method);
            Assert.All(pieces, p =>
            {
                Assert.InRange(p.ThicknessM, 0.17, 0.23);
                Assert.InRange(((p.Start + p.End) * 0.5).Length, 4.93, 5.07);
            });
            Assert.InRange(pieces.Sum(p => p.Length), 0.85 * 5 * Math.PI / 2, 1.05 * 5 * Math.PI / 2);
        }

        [Fact]
        public void UnpairableShape_FallsBackToEnclosingRectangle()
        {
            // A squat triangle has no sides facing each other
            var pieces = WallFootprint.FromOutline(Poly(0, 0, 1, 0, 0.5, 0.8), out var method);
            Assert.Equal(WallFootprintMethod.Rectangle, method);
            Assert.Single(pieces);
        }

        [Fact]
        public void FromPoints_FindsTheRotatedRectangle()
        {
            double a = 30 * Math.PI / 180;
            Vec2 R(double x, double y) => new Vec2(x * Math.Cos(a) - y * Math.Sin(a), x * Math.Sin(a) + y * Math.Cos(a));
            WallPiece p = WallFootprint.FromPoints(new[] { R(0, 0), R(6, 0), R(6, 0.25), R(0, 0.25), R(3, 0.1) });
            Assert.Equal(0.25, p.ThicknessM, 6);
            Assert.Equal(6, p.Length, 6);
            Assert.Equal(30, Math.Abs(p.Direction.Angle() * 180 / Math.PI) % 180, 6);
        }

        [Fact]
        public void OutlineFromTriangles_IsTheBoundaryOfTheMesh()
        {
            // 4 m × 0.2 m top face as four triangles (two quads), either winding
            var a = new Vec2(0, 0); var b = new Vec2(2, 0); var c = new Vec2(4, 0);
            var d = new Vec2(0, 0.2); var e = new Vec2(2, 0.2); var f = new Vec2(4, 0.2);
            var loop = WallFootprint.OutlineFromTriangles(new[] { (a, b, e), (a, e, d), (b, f, c), (b, e, f) });
            Assert.Equal(6, loop.Count);
            var pieces = WallFootprint.FromOutline(loop, out _);
            WallPiece p = Assert.Single(pieces);
            Assert.Equal(4, p.Length, 9);
            Assert.Equal(0.2, p.ThicknessM, 9);
        }

        [Fact]
        public void CurvedWall_PiecesAreNotStretchedOverEachOther()
        {
            // Quarter circle R = 5 m in 5° pieces, 0.2 m thick: joining pieces 10° apart used to stretch them over
            // their neighbours (7.85 m of wall became 11.7 m, half of it counted twice)
            var pieces = new List<WallPiece>();
            for (int i = 0; i < 18; i++)
            {
                double a0 = i * 5 * Math.PI / 180, a1 = (i + 1) * 5 * Math.PI / 180;
                pieces.Add(new WallPiece { Start = new Vec2(5 * Math.Cos(a0), 5 * Math.Sin(a0)), End = new Vec2(5 * Math.Cos(a1), 5 * Math.Sin(a1)), ThicknessM = 0.2 });
            }
            double before = pieces.Sum(p => p.Length);
            WallFootprint.JoinEnds(pieces);
            Assert.Equal(before, pieces.Sum(p => p.Length), 9);
        }

        [Fact]
        public void SteppedTopAndSill_GiveOneWallOverTheWholeLength()
        {
            // 10 m wall: 6 m at full height, 4 m lower (two top faces), and a window sill face inside the first part
            var builder = new IfcLinkWall
            {
                ElementId = 1, GroupName = "W", BaseZ = 0, TopZ = 3,
                Outlines =
                {
                    Poly(0, -0.1, 6, -0.1, 6, 0.1, 0, 0.1),
                    Poly(6, -0.1, 10, -0.1, 10, 0.1, 6, 0.1),
                    Poly(2, -0.1, 3.5, -0.1, 3.5, 0.1, 2, 0.1)
                }
            };
            var r = IfcWallBuilder.Build(new[] { builder });
            var seg = Assert.Single(r.Groups.Single().Segments);
            Assert.Equal(10, seg.Length, 6);
            Assert.Equal(0.2, seg.ThicknessM, 6);
        }

        [Fact]
        public void MergeTouching_JoinsLayerSolids()
        {
            var pieces = new List<WallPiece>
            {
                new WallPiece { Start = new Vec2(0, 0.1), End = new Vec2(5, 0.1), ThicknessM = 0.2 },
                new WallPiece { Start = new Vec2(5, 0.25), End = new Vec2(0, 0.25), ThicknessM = 0.1 }
            };
            WallFootprint.MergeTouching(pieces);
            WallPiece p = Assert.Single(pieces);
            Assert.Equal(0.3, p.ThicknessM, 9);
            Assert.Equal(0.15, p.Start.Y, 9);
            Assert.Equal(0.15, p.End.Y, 9);
        }
    }

    public class WallTypeEstimatorTests
    {
        [Theory]
        [InlineData("STC 50", 50)]
        [InlineData("Rw 52 dB", 52)]
        [InlineData("Rw (C;Ctr) = 48 (-1;-4) dB", 48)]
        [InlineData("45", 45)]
        [InlineData("44,5 dB", 45)]
        [InlineData("STC-50", 50)]
        public void Rating_IsRead(string value, int expected)
        {
            Assert.True(WallTypeEstimator.TryParseRating(value, out int r));
            Assert.Equal(expected, r);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("good")]
        [InlineData("5")]
        [InlineData("EI 60")]
        [InlineData("-45")]
        public void Rating_Unreadable(string value) => Assert.False(WallTypeEstimator.TryParseRating(value, out _));

        [Theory]
        [InlineData("Wall", true)]
        [InlineData("Partition Wall 100", true)]
        [InlineData("Precast wall panel", true)]
        [InlineData("Walls:Basic Wall:Generic - 200mm", true)]
        [InlineData("Wall cabinet 600", false)]
        [InlineData("Wall-mounted TV", false)]
        [InlineData("Drywall ceiling", false)]
        [InlineData("Wall light", false)]
        [InlineData("Door", false)]
        public void ObjectTypeNamesAWall(string objectType, bool isWall) =>
            Assert.Equal(isWall, WallTypeEstimator.NamesAWall(objectType));

        [Fact]
        public void Rating_SetsStc_NameKeepsTheMaterial()
        {
            WallTypeInfo t = WallTypeEstimator.Estimate("Ext Concrete 300", 0.3, "Rw 50 dB");
            Assert.Equal(WallAbsorptionPreset.Concrete, t.Surface);
            Assert.Equal(50, t.StcRating);
        }

        [Fact]
        public void NamedMaterial_OnlyWhenItHasATypeNearTheEstimate()
        {
            // A 90 mm brick wall estimates STC 45; the nearest brick type is 50: the rating wins (old behaviour)
            Assert.Equal(45, WallTypeEstimator.Estimate("Generic - 90mm Brick", 0.09).StcRating);
            // 300 mm concrete estimates 57: concrete 55 is within 3 dB
            Assert.Equal("concrete_200", WallTypeEstimator.Estimate("Concrete 300", 0.3).Key);
        }

        [Fact]
        public void ThicknessNoise_DoesNotDropAClass()
        {
            Assert.Equal(WallTypeEstimator.Estimate("Gypsum partition", 0.1).Key,
                WallTypeEstimator.Estimate("Gypsum partition", 0.0999999).Key);
        }
    }

    public class IfcWallBuilderTests
    {
        // A 10 × 6 m room of 0.2 m walls as shapes (outer faces butt-joined), in host coordinates
        static IfcLinkWall Box(int id, string guid, double x0, double y0, double x1, double y1, string group = "Wall 200",
            double baseZ = 0, double topZ = 3)
            => new IfcLinkWall
            {
                ElementId = id, IfcGuid = guid, GroupName = group, BaseZ = baseZ, TopZ = topZ,
                Outlines = { new List<Vec2> { new Vec2(x0, y0), new Vec2(x1, y0), new Vec2(x1, y1), new Vec2(x0, y1) } }
            };

        static List<IfcLinkWall> Room() => new List<IfcLinkWall>
        {
            Box(1, "s", -0.1, -0.1, 10.1, 0.1),
            Box(2, "e", 9.9, 0.1, 10.1, 5.9),
            Box(3, "n", -0.1, 5.9, 10.1, 6.1),
            Box(4, "w", -0.1, 0.1, 0.1, 5.9),
        };

        [Fact]
        public void Shapes_GiveAClosedRoomOfCenterlines()
        {
            IfcWallBuildResult r = IfcWallBuilder.Build(Room());
            Assert.Equal(4, r.FromOutline);
            WallLineGroup g = Assert.Single(r.Groups);
            Assert.Equal("Wall 200" + IfcWallBuilder.GroupSuffix, g.LineStyleName);
            Assert.All(g.Segments, s => Assert.Equal(0.2, s.ThicknessM, 9));

            // Every end meets another wall's end at a room corner
            var corners = new[] { new Vec2(0, 0), new Vec2(10, 0), new Vec2(10, 6), new Vec2(0, 6) };
            foreach (var s in g.Segments)
                foreach (Vec2 p in new[] { s.Start, s.End })
                    Assert.True(corners.Min(c => Vec2.Distance(c, p)) < 0.11, $"{p} is not at a corner");
            // East/west walls (between the others' faces) are extended to the corners exactly
            Assert.Contains(g.Segments, s => Vec2.Distance(s.Start, new Vec2(10, 0)) < 1e-9 || Vec2.Distance(s.End, new Vec2(10, 0)) < 1e-9);
        }

        [Fact]
        public void File_IsFittedOntoTheLink_AndUsedPerWall()
        {
            // The file's coordinates: the room rotated by −20° and moved; centerlines from the file are exact
            double a = -20 * Math.PI / 180;
            Vec2 ToFile(Vec2 p) => new Vec2(Math.Cos(a) * p.X - Math.Sin(a) * p.Y + 50, Math.Sin(a) * p.X + Math.Cos(a) * p.Y - 30);
            IfcFileWall F(string guid, Vec2 p0, Vec2 p1) => new IfcFileWall
            {
                GlobalId = guid, Centerline = { ToFile(p0), ToFile(p1) }, ThicknessM = 0.2, AcousticRating = "Rw 55 dB",
                Materials = { "Concrete" }
            };
            var file = new IfcFileData
            {
                Walls =
                {
                    F("s", new Vec2(0, 0), new Vec2(10, 0)), F("e", new Vec2(10, 0), new Vec2(10, 6)),
                    F("n", new Vec2(10, 6), new Vec2(0, 6)), F("w", new Vec2(0, 6), new Vec2(0, 0))
                }
            };

            IfcWallBuildResult r = IfcWallBuilder.Build(Room(), new IfcWallBuildOptions { File = file });

            Assert.Equal(4, r.FromFile);
            Assert.Equal(20, r.FileToModel.AngleRad * 180 / Math.PI, 6);
            Assert.True(r.FitResidualM < 1e-6);
            WallLineGroup g = Assert.Single(r.Groups);
            Assert.Contains(g.Segments, s => Vec2.Distance(s.Start, new Vec2(0, 0)) < 1e-6 && Vec2.Distance(s.End, new Vec2(10, 0)) < 1e-6);
            Assert.Equal(WallAbsorptionPreset.Concrete, g.WallType.Surface);
            Assert.Equal(55, g.WallType.StcRating);
        }

        [Fact]
        public void File_OneMisplacedWall_DoesNotDragTheOthers()
        {
            // 24 straight walls in two directions; one wall's file axis is 4 m off (e.g. on a grid placement)
            var link = new List<IfcLinkWall>();
            var walls = new List<IfcFileWall>();
            for (int i = 0; i < 12; i++)
            {
                link.Add(Box(i + 1, "h" + i, 0, 3 * i - 0.15, 8, 3 * i + 0.15));
                walls.Add(new IfcFileWall { GlobalId = "h" + i, Centerline = { new Vec2(100, 50 + 3 * i + (i == 5 ? 4 : 0)), new Vec2(108, 50 + 3 * i + (i == 5 ? 4 : 0)) }, ThicknessM = 0.3 });
                link.Add(Box(i + 101, "v" + i, 20 + 3 * i - 0.15, 0, 20 + 3 * i + 0.15, 8));
                walls.Add(new IfcFileWall { GlobalId = "v" + i, Centerline = { new Vec2(120 + 3 * i, 50), new Vec2(120 + 3 * i, 58) }, ThicknessM = 0.3 });
            }
            IfcWallBuildResult r = IfcWallBuilder.Build(link, new IfcWallBuildOptions { File = new IfcFileData { Walls = walls } });

            Assert.Equal(-100, r.FileToModel.Offset.X, 6);
            Assert.Equal(-50, r.FileToModel.Offset.Y, 6);
            Assert.Equal(23, r.FromFile);
            Assert.Equal(1, r.FromOutline);
        }

        [Fact]
        public void File_ThatDoesNotLineUp_IsNotUsed()
        {
            var file = new IfcFileData
            {
                Walls =
                {
                    new IfcFileWall { GlobalId = "s", Centerline = { new Vec2(0, 0), new Vec2(10, 0) }, ThicknessM = 0.2 },
                    new IfcFileWall { GlobalId = "e", Centerline = { new Vec2(10, 3), new Vec2(10, 9) }, ThicknessM = 0.2 },
                    new IfcFileWall { GlobalId = "n", Centerline = { new Vec2(10, 6), new Vec2(0, 6) }, ThicknessM = 0.2 },
                    new IfcFileWall { GlobalId = "w", Centerline = { new Vec2(4, 6), new Vec2(4, 0) }, ThicknessM = 0.2 },
                }
            };
            IfcWallBuildResult r = IfcWallBuilder.Build(Room(), new IfcWallBuildOptions { File = file });
            Assert.Equal(0, r.FromFile);
            Assert.Equal(4, r.FromOutline);
            Assert.Contains("does not line up", r.FileStatus);
        }

        [Fact]
        public void OtherStoreys_AreLeftOut_LowWallsKeepTheirHeight()
        {
            var walls = Room();
            walls.Add(Box(5, "upper", 2, 2, 8, 2.2, baseZ: 3.5, topZ: 6.5));
            walls.Add(Box(6, "screen", 2, 3, 6, 3.1, group: "Screen", topZ: 1.5));

            IfcWallBuildResult r = IfcWallBuilder.Build(walls, new IfcWallBuildOptions { LevelElevationM = 0 });

            Assert.Equal(1, r.OtherLevel);
            Assert.Equal(5, r.WallCount);
            Assert.Equal(1.5, r.Groups.Single(g => g.LineStyleName.StartsWith("Screen")).HeightM);
            Assert.Equal(0, r.Groups.Single(g => g.LineStyleName.StartsWith("Wall 200")).HeightM);
        }
    }
}
