using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using SoundCalcs.Domain;
using SoundCalcs.UI.ViewModels;
using SoundCalcs.Visualization;

namespace SoundCalcs.Tests
{
    /// <summary>
    /// Tests for <see cref="HeatmapMath"/>, the colour mapping and grid geometry shared by
    /// the in-panel viewer and the Revit FilledRegion renderer, and for
    /// <see cref="JobInputBuilder"/>, the Revit-free steps of building a job input.
    /// </summary>
    public class HeatmapMathTests
    {
        private static List<ReceiverResult> Grid(int cols, int rows, double spacing,
            Func<int, int, double> spl, double x0 = 0, double y0 = 0)
        {
            var list = new List<ReceiverResult>();
            int idx = 0;
            for (int c = 0; c < cols; c++)
                for (int r = 0; r < rows; r++)
                {
                    double v = spl(c, r);
                    list.Add(new ReceiverResult
                    {
                        ReceiverIndex = idx++,
                        Position = new Vec3(x0 + c * spacing, y0 + r * spacing, 1.2),
                        SplDb = v,
                        SplDbA = v - 3,
                        Sti = Math.Max(0, Math.Min(1, v / 100)),
                        C80Db = v / 10 - 5,
                        SplDbByBand = Enumerable.Repeat(v - 8.45, 7).ToArray()
                    });
                }
            return list;
        }

        // ---------------------------------------------------------------------------
        // Viewer gradient
        // ---------------------------------------------------------------------------

        [Fact]
        public void SampleGradient_EndsAreRedAndGreen_AndClamped()
        {
            Rgba lo = HeatmapMath.SampleGradient(0), hi = HeatmapMath.SampleGradient(1);
            Assert.Equal((0xCC, 0x00, 0x00), (lo.R, lo.G, lo.B));
            Assert.Equal((0x00, 0xAA, 0x00), (hi.R, hi.G, hi.B));
            Assert.Equal(lo.ToString(), HeatmapMath.SampleGradient(-5).ToString());
            Assert.Equal(hi.ToString(), HeatmapMath.SampleGradient(7).ToString());
            Assert.Equal(HeatmapMath.ViewerHeatAlpha, HeatmapMath.SampleGradient(0.3).A);
        }

        [Fact]
        public void SampleGradient_RedFallsAndGreenRisesMonotonically()
        {
            // Red channel never increases past the yellow midpoint; green never decreases before it.
            Rgba prev = HeatmapMath.SampleGradient(0);
            for (int i = 1; i <= 100; i++)
            {
                double t = i / 100.0;
                Rgba c = HeatmapMath.SampleGradient(t);
                if (t <= 0.5) Assert.True(c.G >= prev.G, $"green dropped at t={t}");
                else Assert.True(c.R <= prev.R, $"red rose at t={t}");
                prev = c;
            }
        }

        [Fact]
        public void ViewerLegendColors_AreEightOpaqueSamplesLowToHigh()
        {
            Rgba[] colors = HeatmapMath.ViewerLegendColors();
            Assert.Equal(8, colors.Length);
            Assert.All(colors, c => Assert.Equal(255, c.A));
            Assert.Equal(0xCC, colors[0].R);
            Assert.Equal(0xAA, colors[7].G);
        }

        // ---------------------------------------------------------------------------
        // Viewer range and grid
        // ---------------------------------------------------------------------------

        [Fact]
        public void ComputeViewerRange_UsesPercentiles_IgnoringOutliers()
        {
            var vals = Enumerable.Range(0, 100).Select(i => (double)i).ToArray();
            vals[0] = -1000; vals[99] = 1000;
            var (min, max) = HeatmapMath.ComputeViewerRange(vals, VisualizationMode.SPL);
            Assert.Equal(2, min);
            Assert.Equal(98, max);
        }

        [Theory]
        [InlineData(VisualizationMode.SPL, 70.0, 55.0, 85.0)]
        [InlineData(VisualizationMode.STI, 0.5, 0.25, 0.75)]
        [InlineData(VisualizationMode.C80, 2.0, -3.0, 7.0)]
        public void ComputeViewerRange_WidensFlatFields(VisualizationMode mode, double v, double lo, double hi)
        {
            var (min, max) = HeatmapMath.ComputeViewerRange(Enumerable.Repeat(v, 20).ToArray(), mode);
            Assert.Equal(lo, min, 6);
            Assert.Equal(hi, max, 6);
        }

        [Fact]
        public void BuildViewerGrid_OnePixelPerReceiver_RowZeroIsLowestY()
        {
            var results = Grid(6, 4, 0.5, (c, r) => 60 + r * 10, x0: -1.0, y0: 2.0);
            double[] vals = results.Select(r => r.SplDb).ToArray();
            HeatmapGrid g = HeatmapMath.BuildViewerGrid(results, vals, 60, 30, 0.5);

            Assert.Equal(6, g.Cols);
            Assert.Equal(4, g.Rows);
            Assert.Equal(0, g.Collisions);
            Assert.Equal(0, g.OutOfBounds);
            Assert.Equal(results.Count, g.Filled.Count(f => f));
            Assert.Equal(0xCC, g.Pixels[0].R);                         // row 0 = y 2.0 = quietest
            Assert.Equal(0xAA, g.Pixels[3 * g.Cols].G);                // row 3 = loudest
            Assert.Equal(-1.25, g.WorldLeft, 9);
            Assert.Equal(1.75, g.WorldBottom, 9);
            Assert.Equal(1.75, g.WorldRight, 9);
            Assert.Equal(3.75, g.WorldTop, 9);
        }

        [Fact]
        public void BuildViewerGrid_WithCoarserSpacingThanResults_ReportsCollisions()
        {
            var results = Grid(10, 10, 0.5, (c, r) => 60 + c);
            double[] vals = results.Select(r => r.SplDb).ToArray();
            HeatmapGrid g = HeatmapMath.BuildViewerGrid(results, vals, 60, 10, 1.0);
            Assert.True(g.Collisions > 0);
        }

        // ---------------------------------------------------------------------------
        // Revit filled-region plan
        // ---------------------------------------------------------------------------

        [Fact]
        public void PlanFilledRegions_StripsTileEveryCellOnceInTheRightBand()
        {
            var results = Grid(12, 9, 0.5, (c, r) => 50 + 3 * c + (r % 3));
            FilledRegionPlan plan = HeatmapMath.PlanFilledRegions(results, VisualizationMode.SPL, null);

            Assert.NotNull(plan);
            Assert.Equal(0.5, plan.GridSpacingM, 9);
            Assert.Equal(results.Min(r => r.SplDb), plan.MinVal);
            Assert.Equal(results.Max(r => r.SplDb), plan.MaxVal);

            var strips = plan.Strips.SelectMany(kv => kv.Value.Select(s => (Band: kv.Key, S: s))).ToList();
            double area = strips.Sum(x => (x.S.x1 - x.S.x0) * (x.S.y1 - x.S.y0));
            Assert.Equal(results.Count * 0.25, area, 9);
            Assert.True(strips.Count < results.Count, "strips should merge cells");

            for (int i = 0; i < plan.Results.Count; i++)
            {
                var p = plan.Results[i].Position;
                var hits = strips.Where(x => p.X > x.S.x0 && p.X < x.S.x1 && p.Y > x.S.y0 && p.Y < x.S.y1).ToList();
                Assert.Single(hits);
                Assert.Equal(plan.BandIndex[i], hits[0].Band);
            }
        }

        [Fact]
        public void PlanFilledRegions_SplThresholdDropsQuietReceiversAndSetsMin()
        {
            var results = Grid(10, 1, 1.0, (c, r) => 50 + c * 5);
            FilledRegionPlan plan = HeatmapMath.PlanFilledRegions(results, VisualizationMode.SPL, 65);
            Assert.Equal(7, plan.Results.Count);
            Assert.Equal(65, plan.MinVal);
            Assert.Null(HeatmapMath.PlanFilledRegions(results, VisualizationMode.SPL, 1000));
        }

        [Fact]
        public void PlanFilledRegions_PerBandModeUsesThatBand()
        {
            var results = Grid(4, 4, 1.0, (c, r) => 60);
            for (int i = 0; i < results.Count; i++) results[i].SplDbByBand[5] = i;
            FilledRegionPlan plan = HeatmapMath.PlanFilledRegions(results, VisualizationMode.SPL_4k, null);
            Assert.Equal(5, plan.OctaveBandIndex);
            Assert.Equal(0, plan.MinVal);
            Assert.Equal(results.Count - 1, plan.MaxVal);
        }

        [Theory]
        [InlineData(0.0, 0)]
        [InlineData(12.4, 0)]
        [InlineData(12.5, 1)]
        [InlineData(99.9, 7)]
        [InlineData(100.0, 7)]
        [InlineData(150.0, 7)]
        [InlineData(-5.0, 0)]
        public void ValueToBand_EightEqualBinsClamped(double v, int band)
        {
            Assert.Equal(band, HeatmapMath.ValueToBand(v, 0, 100, 8));
        }

        [Fact]
        public void LegendBands_HighestFirst_ContiguousAndSpanRange()
        {
            var bands = HeatmapMath.GetLegendBands(40, 80);
            Assert.Equal(8, bands.Count);
            Assert.Equal(7, bands[0].Band);
            Assert.Equal("75.0 – 80.0 dB", bands[0].Label);
            Assert.Equal("40.0 – 45.0 dB", bands[7].Label);
            Assert.Equal("#D20000", bands[7].ColorHex);

            var sti = HeatmapMath.GetStiLegendBands(0.2, 0.6);
            Assert.EndsWith("(Excellent)", sti[0].Label);
            Assert.EndsWith("(Bad)", sti[7].Label);
        }

        [Fact]
        public void GetValue_AndOctaveBandIndex_CoverEveryMode()
        {
            var r = Grid(1, 1, 1, (c, rr) => 70)[0];
            r.SplDbByBand = new[] { 1.0, 2, 3, 4, 5, 6, 7 };
            foreach (VisualizationMode mode in Enum.GetValues(typeof(VisualizationMode)))
            {
                int band = HeatmapMath.GetOctaveBandIndex(mode);
                double v = HeatmapMath.GetValue(r, mode);
                if (band >= 0) Assert.Equal(band + 1, v);
            }
            Assert.Equal(r.Sti, HeatmapMath.GetValue(r, VisualizationMode.STI));
            Assert.Equal(r.SplDbA, HeatmapMath.GetValue(r, VisualizationMode.SPL_A));
            Assert.Equal(r.C80Db, HeatmapMath.GetValue(r, VisualizationMode.C80));
        }

        // ---------------------------------------------------------------------------
        // JobInputBuilder
        // ---------------------------------------------------------------------------

        [Fact]
        public void ResolveFacing_WallMountedIsHorizontal_OthersPointDown()
        {
            Vec3 wall = JobInputBuilder.ResolveFacing(ProfileSourceType.WallMounted, new Vec3(0, 3, -1));
            Assert.Equal(new Vec3(0, 1, 0), wall);
            Assert.Equal(new Vec3(1, 0, 0), JobInputBuilder.ResolveFacing(ProfileSourceType.WallMounted, new Vec3(0, 0, -1)));
            Assert.Equal(new Vec3(0, 0, -1), JobInputBuilder.ResolveFacing(ProfileSourceType.SimpleConical, new Vec3(1, 0, 0)));
            Assert.Equal(new Vec3(0, 0, -1), JobInputBuilder.ResolveFacing(ProfileSourceType.SimpleOmni, new Vec3(1, 0, 0)));
        }

        [Fact]
        public void ToComputeWall_ExtendsBothEnds_AndSetsHalfThickness()
        {
            var seg = new WallSegment2D { Start = new Vec2(0, 0), End = new Vec2(4, 0), ThicknessM = 0.3 };
            ComputeWall w = JobInputBuilder.ToComputeWall(seg, 52);
            Assert.Equal(new Vec2(-0.1, 0), w.Start);
            Assert.Equal(new Vec2(4.1, 0), w.End);
            Assert.Equal(52, w.StcRating);
            Assert.Equal(0.15, w.HalfThicknessM, 9);
            Assert.Equal(0.05, JobInputBuilder.ToComputeWall(
                new WallSegment2D { Start = new Vec2(0, 0), End = new Vec2(0, 1), ThicknessM = 0.02 }, 40).HalfThicknessM, 9);
        }

        [Fact]
        public void ApplyCeilingHeights_UsesTallestSpeakerInRoom()
        {
            var room = new RoomPolygon
            {
                Vertices = { new Vec2(0, 0), new Vec2(5, 0), new Vec2(5, 5), new Vec2(0, 5) }
            };
            var speakers = new[]
            {
                new SpeakerInstance { Position = new Vec3(1, 1, 2.7), ElevationFromLevelM = 2.7 },
                new SpeakerInstance { Position = new Vec3(2, 2, 3.1), ElevationFromLevelM = 3.1 },
                new SpeakerInstance { Position = new Vec3(9, 9, 6.0), ElevationFromLevelM = 6.0 },  // outside
            };
            JobInputBuilder.ApplyCeilingHeights(new[] { room }, speakers);
            Assert.Equal(3.1, room.CeilingHeightM, 9);
        }

        [Fact]
        public void ConvexHull_DropsInteriorAndDuplicatePoints()
        {
            var pts = new List<Vec2>
            {
                new Vec2(0, 0), new Vec2(4, 0), new Vec2(4, 3), new Vec2(0, 3),
                new Vec2(2, 1), new Vec2(4, 0), new Vec2(1, 2)
            };
            var hull = JobInputBuilder.ConvexHull(pts);
            Assert.Equal(4, hull.Count);
            Assert.Equal(12, new RoomPolygon { Vertices = hull }.Area, 9);
        }
    }
}
