using System;
using System.Collections.Generic;
using System.Linq;
using SoundCalcs.Domain;
using SoundCalcs.UI.ViewModels;
using SoundCalcs.Visualization;
using Xunit;

namespace SoundCalcs.Tests
{
    public class HeatmapContourTests
    {
        static HeatmapGrid Grid(int cols, int rows, Func<double, double, double> f, double spacing = 1.0)
        {
            var results = new List<ReceiverResult>();
            var vals = new List<double>();
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                {
                    double x = c * spacing, y = r * spacing, v = f(x, y);
                    if (double.IsNaN(v)) continue;
                    results.Add(new ReceiverResult { Position = new Vec3(x, y, 1.2), SplDb = v });
                    vals.Add(v);
                }
            return HeatmapMath.BuildViewerGrid(results, vals.ToArray(), 0, 10, spacing);
        }

        [Fact]
        public void LinearField_GivesOneStraightLine()
        {
            var g = Grid(6, 5, (x, y) => x);
            var lines = HeatmapMath.Contour(g, 2.5);
            Assert.Single(lines);
            Assert.Equal(5, lines[0].Count);               // one point per row
            Assert.All(lines[0], p => Assert.Equal(2.5, p.X, 9));
            Assert.Equal(0, lines[0].Min(p => p.Y), 9);
            Assert.Equal(4, lines[0].Max(p => p.Y), 9);
        }

        [Fact]
        public void RadialField_GivesClosedRingAroundTheCentre()
        {
            var g = Grid(11, 11, (x, y) => Math.Sqrt((x - 5) * (x - 5) + (y - 5) * (y - 5)));
            var lines = HeatmapMath.Contour(g, 3.0);
            Assert.Single(lines);
            List<Vec2> ring = lines[0];
            Assert.True(Vec2.Distance(ring[0], ring[ring.Count - 1]) < 1e-6, "closed");
            Assert.All(ring, p => Assert.InRange(Math.Sqrt((p.X - 5) * (p.X - 5) + (p.Y - 5) * (p.Y - 5)), 2.85, 3.05));
        }

        [Fact]
        public void LinesStopAtGapsInTheGrid()
        {
            // Column 3 missing: no cell spans it, so the x = 3.5 level makes no line
            var g = Grid(7, 4, (x, y) => Math.Abs(x - 3) < 0.1 ? double.NaN : x);
            Assert.Empty(HeatmapMath.Contour(g, 3.5));
            Assert.Single(HeatmapMath.Contour(g, 4.5));
        }

        [Fact]
        public void ContourLevels_StiUsesCategoryLimits_DbUsesRoundSteps()
        {
            Assert.Equal(new[] { 0.45, 0.60 }, HeatmapMath.ContourLevels(0.40, 0.70, VisualizationMode.STI));
            var db = HeatmapMath.ContourLevels(61.3, 82.9, VisualizationMode.SPL);
            Assert.InRange(db.Count, 3, 7);
            double step = db[1] - db[0];
            Assert.Contains(step, new[] { 1.0, 2, 3, 5, 10 });
            Assert.All(db, l => Assert.Equal(0, Math.IEEERemainder(l, step), 9));
            Assert.Empty(HeatmapMath.ContourLevels(5, 5, VisualizationMode.SPL));
        }

        [Fact]
        public void Resample_UniformGridKeepsColour_EmptyCellsFadeOut()
        {
            // Columns 0–1 and 5 filled, 2–4 empty; 10 px per column
            var g = Grid(6, 4, (x, y) => x >= 2 && x <= 4 ? double.NaN : 5.0);
            Assert.Equal(6, g.Cols);
            Rgba[] px = HeatmapMath.ResampleBilinear(g, 60, 40);
            Rgba filled = g.Pixels[0];
            Rgba inside = px[20 * 60 + 5];        // centre of column 0
            Assert.Equal(filled.R, inside.R);
            Assert.Equal(filled.G, inside.G);
            Assert.Equal(filled.A, inside.A);
            Assert.Equal(0, px[20 * 60 + 35].A);  // centre of empty column 3
            Rgba edge = px[20 * 60 + 20];         // between columns 1 and 2: about half alpha, same colour
            Assert.InRange(edge.A, filled.A / 2 - 30, filled.A / 2 + 30);
            Assert.Equal(filled.R, edge.R);
        }
    }
}
