using System;
using System.Collections.Generic;
using System.Linq;
using SoundCalcs.Domain;
using SoundCalcs.UI.ViewModels;

namespace SoundCalcs.Visualization
{
    /// <summary>Plain RGBA colour, independent of SkiaSharp / Revit colour types.</summary>
    public struct Rgba
    {
        public readonly byte R, G, B, A;

        public Rgba(byte r, byte g, byte b, byte a = 255)
        {
            R = r; G = g; B = b; A = a;
        }

        public override string ToString() => $"#{R:X2}{G:X2}{B:X2}{A:X2}";
    }

    /// <summary>
    /// Receiver-to-pixel mapping produced for the in-panel viewer heatmap.
    /// One pixel per grid cell; <see cref="Filled"/> marks cells that hold a receiver.
    /// </summary>
    public class HeatmapGrid
    {
        public int Cols { get; set; }
        public int Rows { get; set; }
        public double XMin { get; set; }
        public double YMin { get; set; }
        public double XMax { get; set; }
        public double YMax { get; set; }
        public double Spacing { get; set; }

        /// <summary>Row-major pixel colours (index = row * Cols + col). Row 0 = lowest Y.</summary>
        public Rgba[] Pixels { get; set; }
        public bool[] Filled { get; set; }

        /// <summary>Receivers that landed on a pixel already taken by another receiver.</summary>
        public int Collisions { get; set; }

        /// <summary>Receivers that fell outside the bitmap bounds.</summary>
        public int OutOfBounds { get; set; }

        /// <summary>World-space rectangle covered by the bitmap (cell edges, not centres).</summary>
        public double WorldLeft => XMin - Spacing * 0.5;
        public double WorldBottom => YMin - Spacing * 0.5;
        public double WorldRight => XMax + Spacing * 0.5;
        public double WorldTop => YMax + Spacing * 0.5;
    }

    /// <summary>
    /// Everything the FilledRegion renderer decides before touching the Revit API:
    /// value range, band per receiver and the merged rectangles per band.
    /// </summary>
    public class FilledRegionPlan
    {
        public List<ReceiverResult> Results { get; set; }
        public int[] BandIndex { get; set; }
        public double MinVal { get; set; }
        public double MaxVal { get; set; }
        public double GridSpacingM { get; set; }
        public int NumBands { get; set; }
        public int OctaveBandIndex { get; set; }
        public Dictionary<int, List<(double x0, double y0, double x1, double y1, double z)>> Strips { get; set; }
    }

    /// <summary>
    /// Pure colour-mapping and grid geometry shared by the WPF viewer
    /// (<c>AcousticViewerControl</c>), the Revit <c>FilledRegionRenderer</c>,
    /// the headless harness and the unit tests. No Revit, WPF or SkiaSharp types.
    /// </summary>
    public static class HeatmapMath
    {
        // -------------------------------------------------------------------
        // Viewer palette: smooth gradient red (low) → green (high)
        // -------------------------------------------------------------------

        /// <summary>Stops are evenly spaced 0..1. <see cref="SampleGradient"/> lerps between them.</summary>
        public static readonly Rgba[] ViewerGradientStops =
        {
            new Rgba(0xCC, 0x00, 0x00),  // 0.00 – red        (quietest)
            new Rgba(0xFF, 0x44, 0x00),  // 0.17 – red-orange
            new Rgba(0xFF, 0xAA, 0x00),  // 0.33 – amber
            new Rgba(0xFF, 0xFF, 0x00),  // 0.50 – yellow
            new Rgba(0xAA, 0xFF, 0x00),  // 0.67 – yellow-green
            new Rgba(0x44, 0xDD, 0x00),  // 0.83 – lime
            new Rgba(0x00, 0xAA, 0x00),  // 1.00 – green      (loudest)
        };

        /// <summary>Alpha used for heatmap pixels in the viewer.</summary>
        public const byte ViewerHeatAlpha = 210;

        /// <summary>Number of swatches in the viewer legend.</summary>
        public const int ViewerLegendBands = 8;

        public static Rgba SampleGradient(double t)
        {
            t = t < 0.0 ? 0.0 : t > 1.0 ? 1.0 : t;
            double scaled = t * (ViewerGradientStops.Length - 1);
            int lo = (int)scaled;
            int hi = lo + 1 < ViewerGradientStops.Length ? lo + 1 : lo;
            double frac = scaled - lo;
            Rgba a = ViewerGradientStops[lo], b = ViewerGradientStops[hi];
            return new Rgba(
                (byte)(a.R + (b.R - a.R) * frac),
                (byte)(a.G + (b.G - a.G) * frac),
                (byte)(a.B + (b.B - a.B) * frac),
                ViewerHeatAlpha);
        }

        /// <summary>Opaque legend swatch colours, low → high.</summary>
        public static Rgba[] ViewerLegendColors()
        {
            var colors = new Rgba[ViewerLegendBands];
            for (int i = 0; i < ViewerLegendBands; i++)
            {
                Rgba c = SampleGradient(i / (double)(ViewerLegendBands - 1));
                colors[i] = new Rgba(c.R, c.G, c.B);
            }
            return colors;
        }

        // -------------------------------------------------------------------
        // Revit FilledRegion palette: 8 discrete bands quiet (red) → loud (green)
        // -------------------------------------------------------------------

        public static readonly (string Name, byte R, byte G, byte B)[] RevitBandColors =
        {
            ("SC_SPL_0", 210,   0,   0),   // red         (quiet / low)
            ("SC_SPL_1", 255,  60,   0),   // orange-red
            ("SC_SPL_2", 255, 150,   0),   // amber
            ("SC_SPL_3", 255, 210,   0),   // yellow
            ("SC_SPL_4", 200, 220,   0),   // yellow-green
            ("SC_SPL_5", 140, 220,  30),   // lime
            ("SC_SPL_6",  60, 200,  30),   // green
            ("SC_SPL_7",   0, 160,   0),   // dark green  (loud / strong)
        };

        /// <summary>STI intelligibility labels for the 8 colour bands.</summary>
        public static readonly string[] StiLabels =
        {
            "Bad", "Bad", "Poor", "Poor", "Fair", "Good", "Good", "Excellent"
        };

        // -------------------------------------------------------------------
        // Mode helpers
        // -------------------------------------------------------------------

        /// <summary>
        /// Maps per-band VisualizationMode values to their OctaveBands index (0-6).
        /// Returns -1 for non-band modes (SPL, SPL_A, STI, C80).
        /// </summary>
        public static int GetOctaveBandIndex(VisualizationMode mode)
        {
            switch (mode)
            {
                case VisualizationMode.SPL_125: return 0;
                case VisualizationMode.SPL_250: return 1;
                case VisualizationMode.SPL_500: return 2;
                case VisualizationMode.SPL_1k:  return 3;
                case VisualizationMode.SPL_2k:  return 4;
                case VisualizationMode.SPL_4k:  return 5;
                case VisualizationMode.SPL_8k:  return 6;
                default: return -1;
            }
        }

        /// <summary>The scalar a receiver contributes to the heatmap in the given mode.</summary>
        public static double GetValue(ReceiverResult r, VisualizationMode mode)
        {
            switch (mode)
            {
                case VisualizationMode.STI:   return r.Sti;
                case VisualizationMode.SPL_A: return r.SplDbA;
                case VisualizationMode.C80:   return r.C80Db;
                case VisualizationMode.SPL:   return r.SplDb;
                default:
                    return r.SplDbByBand[GetOctaveBandIndex(mode)];
            }
        }

        // -------------------------------------------------------------------
        // Viewer (SkiaSharp panel) pipeline
        // -------------------------------------------------------------------

        /// <summary>
        /// Colour range used by the viewer: 2nd–98th percentile of the values,
        /// widened around the midpoint when the spread is too small to be meaningful.
        /// </summary>
        public static (double Min, double Max) ComputeViewerRange(double[] vals, VisualizationMode mode)
        {
            bool isSti = mode == VisualizationMode.STI;
            bool isC80 = mode == VisualizationMode.C80;

            var sorted = (double[])vals.Clone();
            Array.Sort(sorted);
            double min = sorted[Math.Max(0, (int)(sorted.Length * 0.02))];
            double max = sorted[Math.Min(sorted.Length - 1, (int)(sorted.Length * 0.98))];
            double minRange = isSti ? 0.05 : (isC80 ? 1.0 : 3.0);
            double halfRange = isSti ? 0.25 : (isC80 ? 5.0 : 15.0);
            if (max - min < minRange)
            {
                double mid = (min + max) * 0.5;
                min = mid - halfRange;
                max = mid + halfRange;
            }
            return (min, max);
        }

        /// <summary>
        /// Place each receiver on a one-pixel-per-cell grid and colour it.
        /// Returns null when there are no results.
        /// </summary>
        public static HeatmapGrid BuildViewerGrid(
            List<ReceiverResult> results, double[] vals,
            double minVal, double range, double spacing)
        {
            if (results.Count == 0) return null;
            if (spacing <= 0) spacing = 1.0;

            double xMin = double.MaxValue, xMax = double.MinValue;
            double yMin = double.MaxValue, yMax = double.MinValue;
            foreach (var r in results)
            {
                if (r.Position.X < xMin) xMin = r.Position.X;
                if (r.Position.X > xMax) xMax = r.Position.X;
                if (r.Position.Y < yMin) yMin = r.Position.Y;
                if (r.Position.Y > yMax) yMax = r.Position.Y;
            }

            int cols = Math.Max(1, (int)Math.Round((xMax - xMin) / spacing) + 1);
            int rows = Math.Max(1, (int)Math.Round((yMax - yMin) / spacing) + 1);

            var grid = new HeatmapGrid
            {
                Cols = cols,
                Rows = rows,
                XMin = xMin,
                YMin = yMin,
                XMax = xMax,
                YMax = yMax,
                Spacing = spacing,
                Pixels = new Rgba[cols * rows],
                Filled = new bool[cols * rows]
            };

            for (int i = 0; i < results.Count; i++)
            {
                int col = (int)Math.Round((results[i].Position.X - xMin) / spacing);
                int row = (int)Math.Round((results[i].Position.Y - yMin) / spacing);
                if ((uint)col < (uint)cols && (uint)row < (uint)rows)
                {
                    int idx = row * cols + col;
                    if (grid.Filled[idx]) grid.Collisions++;
                    grid.Filled[idx] = true;
                    grid.Pixels[idx] = SampleGradient((vals[i] - minVal) / range);
                }
                else
                {
                    grid.OutOfBounds++;
                }
            }

            return grid;
        }

        // -------------------------------------------------------------------
        // Revit FilledRegion pipeline
        // -------------------------------------------------------------------

        /// <summary>
        /// Decide range, per-receiver bands and merged rectangles for the Revit heatmap.
        /// Returns null when nothing is left to draw after filtering.
        /// </summary>
        public static FilledRegionPlan PlanFilledRegions(
            List<ReceiverResult> allResults,
            VisualizationMode mode,
            double? minSplThreshold)
        {
            var results = allResults;
            int octaveBandIdx = GetOctaveBandIndex(mode);
            bool isPerBand = octaveBandIdx >= 0;

            // Filter out points below the minimum SPL threshold
            if (mode == VisualizationMode.SPL && minSplThreshold.HasValue)
                results = results.Where(r => r.SplDb >= minSplThreshold.Value).ToList();

            // For per-band modes, filter out results without band data
            if (isPerBand)
                results = results.Where(r =>
                    r.SplDbByBand != null && r.SplDbByBand.Length > octaveBandIdx).ToList();

            if (results.Count == 0)
                return null;

            double gridSpacingM = EstimateGridSpacing(results);
            double halfM = gridSpacingM * 0.5;

            // Determine value range for banding
            double minVal, maxVal;
            if (mode == VisualizationMode.STI)
            {
                minVal = results.Min(r => r.Sti);
                maxVal = results.Max(r => r.Sti);
            }
            else if (mode == VisualizationMode.SPL_A)
            {
                minVal = minSplThreshold ?? results.Min(r => r.SplDbA);
                maxVal = results.Max(r => r.SplDbA);
            }
            else if (mode == VisualizationMode.C80)
            {
                minVal = results.Min(r => r.C80Db);
                maxVal = results.Max(r => r.C80Db);
            }
            else if (isPerBand)
            {
                minVal = results.Min(r => r.SplDbByBand[octaveBandIdx]);
                maxVal = results.Max(r => r.SplDbByBand[octaveBandIdx]);
            }
            else
            {
                minVal = minSplThreshold ?? results.Min(r => r.SplDb);
                maxVal = results.Max(r => r.SplDb);
            }
            int numBands = RevitBandColors.Length;

            // --- Assign each receiver a band index ---
            int[] bandIndex = new int[results.Count];
            for (int i = 0; i < results.Count; i++)
                bandIndex[i] = ValueToBand(GetValue(results[i], mode), minVal, maxVal, numBands);

            // Stable grid origin for consistent quantisation across all bands
            double originX = results.Min(r => r.Position.X);
            double originY = results.Min(r => r.Position.Y);

            // Build row-strip rectangles then merge vertically to minimise
            // element count while keeping every region a simple rectangle
            // (100% reliable in Revit, unlike complex boundary extraction).
            var strips = BuildRowStrips(results, bandIndex, originX, originY,
                gridSpacingM, halfM, numBands);
            MergeStripsVertically(strips, numBands);

            return new FilledRegionPlan
            {
                Results = results,
                BandIndex = bandIndex,
                MinVal = minVal,
                MaxVal = maxVal,
                GridSpacingM = gridSpacingM,
                NumBands = numBands,
                OctaveBandIndex = octaveBandIdx,
                Strips = strips
            };
        }

        /// <summary>
        /// Get the band info for building a UI legend.
        /// Returns (bandIndex, colorHex, labelText) for each band, highest band first.
        /// </summary>
        public static List<(int Band, string ColorHex, string Label)> GetLegendBands(
            double minSpl, double maxSpl, string suffix = " dB")
        {
            int n = RevitBandColors.Length;
            double range = maxSpl - minSpl;
            double step = n > 0 && range > 0 ? range / n : 0;

            var items = new List<(int, string, string)>(n);
            for (int i = 0; i < n; i++)
            {
                (string _, byte r, byte g, byte b) = RevitBandColors[i];
                string hex = $"#{r:X2}{g:X2}{b:X2}";
                double lo = minSpl + i * step;
                // Last band always extends to maxSpl exactly
                double hi = (i == n - 1) ? maxSpl : minSpl + (i + 1) * step;
                string label = $"{lo:F1} – {hi:F1}{suffix}";
                items.Add((i, hex, label));
            }
            items.Reverse(); // highest band first
            return items;
        }

        /// <summary>
        /// Get the band info for building a STI legend.
        /// Returns (bandIndex, colorHex, labelText) for each band, highest band first.
        /// </summary>
        public static List<(int Band, string ColorHex, string Label)> GetStiLegendBands(
            double minSti, double maxSti)
        {
            int n = RevitBandColors.Length;
            double range = maxSti - minSti;
            double step = n > 0 && range > 0 ? range / n : 0;

            var items = new List<(int, string, string)>(n);
            for (int i = 0; i < n; i++)
            {
                (string _, byte r, byte g, byte b) = RevitBandColors[i];
                string hex = $"#{r:X2}{g:X2}{b:X2}";
                double lo = minSti + i * step;
                // Last band always extends to maxSti exactly
                double hi = (i == n - 1) ? maxSti : minSti + (i + 1) * step;
                string quality = i < StiLabels.Length ? StiLabels[i] : "";
                string label = $"{lo:F2} – {hi:F2} ({quality})";
                items.Add((i, hex, label));
            }
            items.Reverse(); // highest band first
            return items;
        }

        /// <summary>
        /// Build row-strip rectangles: for each grid row, merge consecutive
        /// same-band cells into a single rectangle. Returns strips grouped by band.
        /// Each strip is (x0, y0, x1, y1, z) in metres — ready for CurveLoop.
        /// </summary>
        public static Dictionary<int, List<(double x0, double y0, double x1, double y1, double z)>>
            BuildRowStrips(
                List<ReceiverResult> results,
                int[] bandIndex,
                double originX, double originY,
                double gridSpacingM, double halfM,
                int numBands)
        {
            // Map each receiver to (col, row, band, z)
            var cellMap = new Dictionary<(int col, int row), (int band, double z)>(results.Count);

            for (int i = 0; i < results.Count; i++)
            {
                ReceiverResult r = results[i];
                int col = Quantise(r.Position.X, originX, gridSpacingM);
                int row = Quantise(r.Position.Y, originY, gridSpacingM);
                cellMap[(col, row)] = (bandIndex[i], r.Position.Z);
            }

            // Group cells by row
            var byRow = new SortedDictionary<int, SortedDictionary<int, (int band, double z)>>();
            foreach (var kv in cellMap)
            {
                int row = kv.Key.row;
                int col = kv.Key.col;
                if (!byRow.TryGetValue(row, out var rowCells))
                {
                    rowCells = new SortedDictionary<int, (int band, double z)>();
                    byRow[row] = rowCells;
                }
                rowCells[col] = kv.Value;
            }

            // Scan each row: merge consecutive same-band columns into strips
            var strips = new Dictionary<int, List<(double x0, double y0, double x1, double y1, double z)>>();
            for (int b = 0; b < numBands; b++)
                strips[b] = new List<(double, double, double, double, double)>();

            foreach (var rowKv in byRow)
            {
                int row = rowKv.Key;
                double yCenter = originY + row * gridSpacingM;
                double y0 = yCenter - halfM;
                double y1 = yCenter + halfM;

                var cols = rowKv.Value;
                int runBand = -1;
                int runStartCol = 0;
                int runEndCol = 0;
                double runZ = 0;
                bool inRun = false;

                foreach (var colKv in cols)
                {
                    int col = colKv.Key;
                    int band = colKv.Value.band;
                    double z = colKv.Value.z;

                    if (inRun && band == runBand && col == runEndCol + 1)
                    {
                        // Extend current run
                        runEndCol = col;
                    }
                    else
                    {
                        // Flush previous run
                        if (inRun)
                        {
                            double x0 = originX + runStartCol * gridSpacingM - halfM;
                            double x1 = originX + runEndCol * gridSpacingM + halfM;
                            strips[runBand].Add((x0, y0, x1, y1, runZ));
                        }

                        // Start new run
                        runBand = band;
                        runStartCol = col;
                        runEndCol = col;
                        runZ = z;
                        inRun = true;
                    }
                }

                // Flush final run
                if (inRun)
                {
                    double x0 = originX + runStartCol * gridSpacingM - halfM;
                    double x1 = originX + runEndCol * gridSpacingM + halfM;
                    strips[runBand].Add((x0, y0, x1, y1, runZ));
                }
            }

            return strips;
        }

        /// <summary>
        /// Second pass over row-strips: merge vertically adjacent strips that share
        /// the same x0 and x1 into taller rectangles. Reduces element count
        /// roughly 5–10× compared to row-strips alone while keeping all regions
        /// as simple rectangles (no complex boundary extraction).
        /// </summary>
        public static void MergeStripsVertically(
            Dictionary<int, List<(double x0, double y0, double x1, double y1, double z)>> strips,
            int numBands)
        {
            const double eps = 1e-6;

            for (int b = 0; b < numBands; b++)
            {
                if (!strips.ContainsKey(b) || strips[b].Count <= 1) continue;

                // Sort by x0, x1, then y0 so vertically stackable strips are adjacent
                strips[b].Sort((a, c) =>
                {
                    int cmpX0 = a.x0.CompareTo(c.x0);
                    if (cmpX0 != 0) return cmpX0;
                    int cmpX1 = a.x1.CompareTo(c.x1);
                    if (cmpX1 != 0) return cmpX1;
                    return a.y0.CompareTo(c.y0);
                });

                var merged = new List<(double x0, double y0, double x1, double y1, double z)>();
                var list = strips[b];

                var cur = list[0];
                for (int i = 1; i < list.Count; i++)
                {
                    var next = list[i];

                    // Same column span and vertically touching?
                    if (Math.Abs(cur.x0 - next.x0) < eps &&
                        Math.Abs(cur.x1 - next.x1) < eps &&
                        Math.Abs(cur.y1 - next.y0) < eps)
                    {
                        // Extend current strip downward (merge)
                        cur = (cur.x0, cur.y0, cur.x1, next.y1, cur.z);
                    }
                    else
                    {
                        merged.Add(cur);
                        cur = next;
                    }
                }
                merged.Add(cur);

                strips[b] = merged;
            }
        }

        // -------------------------------------------------------------------
        // Misc utilities
        // -------------------------------------------------------------------

        public static int ValueToBand(double value, double minVal, double maxVal, int numBands)
        {
            if (maxVal <= minVal) return 0;
            double t = Math.Max(0.0, Math.Min(1.0, (value - minVal) / (maxVal - minVal)));
            int band = (int)(t * numBands);
            return Math.Min(band, numBands - 1);
        }

        public static int Quantise(double value, double origin, double spacing)
            => (int)Math.Round((value - origin) / spacing);

        public static double EstimateGridSpacing(List<ReceiverResult> results)
        {
            if (results.Count < 2) return 1.0;

            var sorted = results
                .OrderBy(r => r.Position.X)
                .ThenBy(r => r.Position.Y)
                .ToList();

            double minDelta = double.MaxValue;
            int limit = Math.Min(sorted.Count, 500);

            for (int i = 1; i < limit; i++)
            {
                double dx = Math.Abs(sorted[i].Position.X - sorted[i - 1].Position.X);
                double dy = Math.Abs(sorted[i].Position.Y - sorted[i - 1].Position.Y);
                double d = Math.Max(dx, dy);
                if (d > 1e-4 && d < minDelta)
                    minDelta = d;
            }

            return minDelta < double.MaxValue ? minDelta : 1.0;
        }
    }
}
