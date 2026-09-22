using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using SoundCalcs.Compute;
using SoundCalcs.Domain;

namespace SoundCalcs.Tests
{
    /// <summary>
    /// The shoebox image-source lattice used for box-shaped rooms: calibration to the room's
    /// RT60, agreement with Sabine in proportionate rooms, flush-mounting, openings, fitting.
    /// </summary>
    public class ShoeboxLatticeTests
    {
        private static double[] A(WallAbsorptionPreset p) => OctaveBands.AbsorptionPresets[p];
        private static readonly double[] Air = OctaveBands.ComputeAirAbsorption(20, 50);
        private const double C = 343.4;

        private static (ShoeboxLattice Box, RoomPolygon Room) Box(double lx, double ly, double h, double t60,
            WallAbsorptionPreset wall, WallAbsorptionPreset floor, WallAbsorptionPreset ceiling, bool openSides = false)
        {
            var room = new RoomPolygon
            {
                Vertices = { new Vec2(0, 0), new Vec2(lx, 0), new Vec2(lx, ly), new Vec2(0, ly) },
                CeilingHeightM = h
            };
            var walls = openSides ? new List<ComputeWall>() : new List<ComputeWall>
            {
                new ComputeWall { Start = new Vec2(0, 0), End = new Vec2(lx, 0), StcRating = 50, AbsorptionByBand = A(wall) },
                new ComputeWall { Start = new Vec2(lx, 0), End = new Vec2(lx, ly), StcRating = 50, AbsorptionByBand = A(wall) },
                new ComputeWall { Start = new Vec2(lx, ly), End = new Vec2(0, ly), StcRating = 50, AbsorptionByBand = A(wall) },
                new ComputeWall { Start = new Vec2(0, ly), End = new Vec2(0, 0), StcRating = 50, AbsorptionByBand = A(wall) },
            };
            var box = ShoeboxLattice.TryCreate(room, walls, A(floor), A(ceiling), Enumerable.Repeat(t60, 7).ToArray(), Air, 3.0);
            return (box, room);
        }

        private static (double[][] Bins, double[] Tail) Run(ShoeboxLattice box, Vec3 s, Vec3 r, Func<Vec3, int, double> g2 = null)
        {
            int n = (int)Math.Ceiling(ShoeboxLattice.CutoffTimeS / ShoeboxLattice.BinWidthS);
            var bins = Enumerable.Range(0, 7).Select(_ => new double[n]).ToArray();
            var tail = new double[7];
            box.Accumulate(s, r, Enumerable.Repeat(1e9 / 7, 7).ToArray(), g2 ?? ((d, k) => 1.0), Air, C, bins, tail);
            return (bins, tail);
        }

        /// <summary>T60 from the decay slope of the binned energy (10 ms groups, 40 … 150 ms).</summary>
        private static double FittedT60(double[] bins)
        {
            int group = (int)Math.Round(0.010 / ShoeboxLattice.BinWidthS);
            var pts = new List<(double T, double Db)>();
            for (int i = 0; i + group <= bins.Length; i += group)
            {
                double t = (i + group / 2.0) * ShoeboxLattice.BinWidthS;
                double e = bins.Skip(i).Take(group).Sum();
                if (t >= 0.040 && e > 0) pts.Add((t, 10 * Math.Log10(e)));
            }
            double mt = pts.Average(p => p.T), md = pts.Average(p => p.Db);
            double slope = pts.Sum(p => (p.T - mt) * (p.Db - md)) / pts.Sum(p => (p.T - mt) * (p.T - mt));
            return -60 / slope;
        }

        [Theory]
        [InlineData(10, 8, 3, 0.8, WallAbsorptionPreset.Drywall, WallAbsorptionPreset.Drywall, WallAbsorptionPreset.Drywall)]
        [InlineData(10, 8, 3, 0.8, WallAbsorptionPreset.Drywall, WallAbsorptionPreset.Carpet, WallAbsorptionPreset.Drywall)]
        [InlineData(10, 8, 3, 0.8, WallAbsorptionPreset.Concrete, WallAbsorptionPreset.Concrete, WallAbsorptionPreset.AcousticTile)]
        [InlineData(12, 9, 4, 1.5, WallAbsorptionPreset.Brick, WallAbsorptionPreset.Wood, WallAbsorptionPreset.Drywall)]
        public void Lattice_DecaysAtTheCalibratedRt60_AndMatchesSabineInProportionateRooms(
            double lx, double ly, double h, double t60, WallAbsorptionPreset wall, WallAbsorptionPreset floor, WallAbsorptionPreset ceiling)
        {
            var (box, _) = Box(lx, ly, h, t60, wall, floor, ceiling);
            Assert.NotNull(box);
            var (bins, tails) = Run(box, new Vec3(lx * 0.3, ly * 0.45, 1.5), new Vec3(lx * 0.7, ly * 0.6, 1.2));
            double sabine = 1e9 / 7 * 16 * Math.PI / (0.161 * lx * ly * h / t60);
            foreach (int k in new[] { 1, 3, 5 })
            {
                double tail = tails[k];
                Assert.InRange(FittedT60(bins[k]) / t60, 0.80, 1.25);
                Assert.InRange(10 * Math.Log10((bins[k].Sum() + tail) / sabine), -1.5, 1.5);
            }
        }

        [Fact]
        public void FlushCeilingSource_EmitsOnlyDownward_WithoutDuplicateImages()
        {
            // A flush omni speaker on the ceiling must behave like a downward-only speaker just
            // below it; duplicated mirror images would make it ≈ 3 dB louder.
            var (box, _) = Box(10, 8, 3, 0.8, WallAbsorptionPreset.Drywall, WallAbsorptionPreset.Concrete, WallAbsorptionPreset.Drywall);
            var recv = new Vec3(6.5, 4.5, 1.2);
            double flush = Run(box, new Vec3(4, 4, 3.0), recv).Bins[3].Sum();
            double downOnly = Run(box, new Vec3(4, 4, 2.88), recv, (d, k) => d.Z < 0 ? 1.0 : 0.0).Bins[3].Sum();
            Assert.InRange(10 * Math.Log10(flush / downOnly), -1.0, 1.0);
        }

        [Fact]
        public void OpenSides_ReflectNothing_AndOpeningLinesCountAsOpen()
        {
            var (open, room) = Box(10, 8, 3, 0.8, WallAbsorptionPreset.Drywall, WallAbsorptionPreset.Concrete, WallAbsorptionPreset.Drywall, openSides: true);
            for (int i = 0; i < 4; i++) Assert.All(open.Reflection[i], r => Assert.Equal(0.0, r));

            var openingLines = new List<ComputeWall>
            {
                new ComputeWall { Start = new Vec2(0, 0), End = new Vec2(10, 0), AbsorptionByBand = A(WallAbsorptionPreset.Open) },
            };
            var box = ShoeboxLattice.TryCreate(room, openingLines, A(WallAbsorptionPreset.Concrete), A(WallAbsorptionPreset.Drywall),
                Enumerable.Repeat(0.8, 7).ToArray(), Air, 3.0);
            Assert.All(box.Reflection[2], r => Assert.Equal(0.0, r));   // the y = 0 side
        }

        [Fact]
        public void TryCreate_FitsRotatedRectangles_AndRejectsLShapes()
        {
            double a = 0.5; Vec2 ux = new Vec2(Math.Cos(a), Math.Sin(a)), uy = new Vec2(-Math.Sin(a), Math.Cos(a));
            var rotated = new RoomPolygon { Vertices = { Vec2.Zero, ux * 12, ux * 12 + uy * 5, uy * 5 }, CeilingHeightM = 3 };
            var box = ShoeboxLattice.TryCreate(rotated, new List<ComputeWall>(), A(WallAbsorptionPreset.Concrete),
                A(WallAbsorptionPreset.Drywall), Enumerable.Repeat(0.8, 7).ToArray(), Air, 3.0);
            Assert.NotNull(box);
            Assert.Equal(60, box.LengthX * box.LengthY, 6);

            var lShape = new RoomPolygon
            {
                Vertices = { new Vec2(0, 0), new Vec2(10, 0), new Vec2(10, 4), new Vec2(4, 4), new Vec2(4, 10), new Vec2(0, 10) }
            };
            Assert.Null(ShoeboxLattice.TryCreate(lShape, new List<ComputeWall>(), A(WallAbsorptionPreset.Concrete),
                A(WallAbsorptionPreset.Drywall), Enumerable.Repeat(0.8, 7).ToArray(), Air, 3.0));
            Assert.Null(ShoeboxLattice.TryCreate(new RoomPolygon { Vertices = rotated.Vertices, AreaOverrideM2 = 40 },
                new List<ComputeWall>(), A(WallAbsorptionPreset.Concrete), A(WallAbsorptionPreset.Drywall),
                Enumerable.Repeat(0.8, 7).ToArray(), Air, 3.0));
        }

        [Fact]
        public void RadiatedPower_IsFullSphereInTheRoom_AndHalfSphereFlushInTheCeiling()
        {
            var (box, _) = Box(10, 8, 3, 0.8, WallAbsorptionPreset.Drywall, WallAbsorptionPreset.Drywall, WallAbsorptionPreset.Drywall);
            double inRoom = box.RadiatedPower(new Vec3(5, 4, 1.5), (d, k) => 1.0)[3];
            double flush = box.RadiatedPower(new Vec3(5, 4, 3.0), (d, k) => 1.0)[3];
            Assert.InRange(inRoom / (4 * Math.PI), 0.995, 1.005);
            Assert.InRange(flush / (2 * Math.PI), 0.995, 1.005);
        }

        [Fact]
        public void DiffusePart_FallsWithDistance_AndNeverArrivesBeforeTheDirectSound()
        {
            // Image paths are always longer than the direct path, so energy before the direct
            // sound could only come from the diffuse part (which starts at the direct sound).
            var (box, _) = Box(30, 20, 3, 0.8, WallAbsorptionPreset.Drywall, WallAbsorptionPreset.Drywall, WallAbsorptionPreset.Drywall);
            var src = new Vec3(3, 10, 1.5);
            var near = Run(box, src, new Vec3(6, 10, 1.2));
            var far = Run(box, src, new Vec3(27, 10, 1.2));
            double Total((double[][] Bins, double[] Tail) x) => x.Bins[3].Sum() + x.Tail[3];
            Assert.True(Total(far) < Total(near));
            int firstFar = Array.FindIndex(far.Bins[3], e => e > 0);
            Assert.True(firstFar * ShoeboxLattice.BinWidthS >= 24.0 / C - ShoeboxLattice.BinWidthS);
        }
    }
}
