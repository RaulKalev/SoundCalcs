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

        /// <summary>Row-major values of the receivers on each cell; NaN where <see cref="Filled"/> is false.</summary>
        public double[] Values { get; set; }

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

        /// <summary>
        /// Label for one viewer legend swatch. Uses one decimal when a band is narrower
        /// than 2 units, so adjacent swatches never show identical ranges.
        /// </summary>
        public static string ViewerLegendLabel(double lo, double hi, VisualizationMode mode)
        {
            if (mode == VisualizationMode.STI)
                return $"{lo:F2} \u2013 {hi:F2}";
            string unit = mode == VisualizationMode.SPL_A ? " dBA" : " dB";
            string fmt = hi - lo < 2.0 ? "F1" : "F0";
            return $"{lo.ToString(fmt)} \u2013 {hi.ToString(fmt)}{unit}";
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

        /// <summary>
        /// IEC 60268-16 intelligibility category of an STI value:
        /// Bad &lt; 0.30 ≤ Poor &lt; 0.45 ≤ Fair &lt; 0.60 ≤ Good &lt; 0.75 ≤ Excellent.
        /// </summary>
        public static string StiQuality(double sti)
        {
            if (sti < 0.30) return "Bad";
            if (sti < 0.45) return "Poor";
            if (sti < 0.60) return "Fair";
            if (sti < 0.75) return "Good";
            return "Excellent";
        }

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
        /// Grid spacing the viewer uses for the bitmap: derived from the receiver positions
        /// so it always matches the spacing the results were computed with.
        /// </summary>
        public static double ViewerGridSpacing(List<ReceiverResult> results) => EstimateGridSpacing(results);

        // ── Plan viewer: wall picking ──────────────────────────────────────

        /// <summary>Distance from (px, py) to the segment (ax, ay)–(bx, by); a zero-length segment is a point.</summary>
        public static double DistanceToSegment(double px, double py, double ax, double ay, double bx, double by)
        {
            double dx = bx - ax, dy = by - ay;
            double len2 = dx * dx + dy * dy;
            double t = len2 > 1e-18 ? ((px - ax) * dx + (py - ay) * dy) / len2 : 0.0;
            t = Math.Max(0.0, Math.Min(1.0, t));
            double cx = ax + t * dx - px, cy = ay + t * dy - py;
            return Math.Sqrt(cx * cx + cy * cy);
        }

        /// <summary>
        /// Index of the wall segment nearest to the world point (wx, wy) that lies within
        /// <paramref name="maxDist"/> (world units), or -1 when none does. Ties keep the first segment.
        /// </summary>
        public static int NearestWallSegment(IList<WallSegment2D> segments, double wx, double wy, double maxDist)
        {
            if (segments == null) return -1;
            int best = -1;
            double bestD = maxDist;
            for (int i = 0; i < segments.Count; i++)
            {
                var s = segments[i];
                if (s == null) continue;
                double d = DistanceToSegment(wx, wy, s.Start.X, s.Start.Y, s.End.X, s.End.Y);
                if (d <= bestD && (best < 0 || d < bestD)) { bestD = d; best = i; }
            }
            return best;
        }

        /// <summary>Readable name of a wall surface material ("AcousticPanel" → "Acoustic panel").</summary>
        public static string WallSurfaceLabel(WallAbsorptionPreset preset)
        {
            string name = preset.ToString();
            var sb = new System.Text.StringBuilder(name.Length + 4);
            for (int i = 0; i < name.Length; i++)
            {
                char ch = name[i];
                if (i > 0 && char.IsUpper(ch)) { sb.Append(' '); sb.Append(char.ToLowerInvariant(ch)); }
                else sb.Append(ch);
            }
            return sb.ToString();
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
                Filled = new bool[cols * rows],
                Values = new double[cols * rows]
            };
            for (int i = 0; i < grid.Values.Length; i++) grid.Values[i] = double.NaN;

            for (int i = 0; i < results.Count; i++)
            {
                int col = (int)Math.Round((results[i].Position.X - xMin) / spacing);
                int row = (int)Math.Round((results[i].Position.Y - yMin) / spacing);
                if ((uint)col < (uint)cols && (uint)row < (uint)rows)
                {
                    int idx = row * cols + col;
                    if (grid.Filled[idx]) grid.Collisions++;
                    grid.Filled[idx] = true;
                    grid.Values[idx] = vals[i];
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
            // (a level in dB SPL: in dBA mode it hides the same receivers but doesn't set the dBA range)
            if ((mode == VisualizationMode.SPL || mode == VisualizationMode.SPL_A) && minSplThreshold.HasValue)
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
                minVal = results.Min(r => r.SplDbA);
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
            if (!(maxVal > minVal)) maxVal = minVal + 1e-6;   // one value everywhere (a single receiver)

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
                // Category of the band's midpoint value, not of its position in the colour ramp
                string quality = StiQuality((lo + hi) / 2);
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
        // Report figures: smooth bitmap, contour lines
        // -------------------------------------------------------------------

        /// <summary>
        /// The heatmap bitmap scaled to <paramref name="width"/> × <paramref name="height"/> pixels over the grid's world
        /// rectangle with bilinear filtering (what the viewer's SKFilterQuality.Medium does on screen), interpolating
        /// premultiplied colours so cells next to empty ones fade out instead of darkening. Row 0 is the top (largest Y).
        /// Colours are returned unpremultiplied.
        /// </summary>
        public static Rgba[] ResampleBilinear(HeatmapGrid grid, int width, int height)
        {
            var px = new Rgba[width * height];
            if (grid == null || width <= 0 || height <= 0) return px;
            for (int y = 0; y < height; y++)
            {
                // Texel coordinates: cell centres at integer positions, row 0 = lowest Y
                double v = (height - y - 0.5) / height * grid.Rows - 0.5;
                for (int x = 0; x < width; x++)
                {
                    double u = (x + 0.5) / width * grid.Cols - 0.5;
                    int c0 = (int)Math.Floor(u), r0 = (int)Math.Floor(v);
                    double fu = u - c0, fv = v - r0;
                    double a = 0, r = 0, g = 0, b = 0;
                    for (int k = 0; k < 4; k++)
                    {
                        int c = c0 + (k & 1), rr = r0 + (k >> 1);
                        double w = ((k & 1) == 1 ? fu : 1 - fu) * ((k >> 1) == 1 ? fv : 1 - fv);
                        if (w <= 0) continue;
                        // Clamp to the edge like the GPU sampler does
                        c = c < 0 ? 0 : c >= grid.Cols ? grid.Cols - 1 : c;
                        rr = rr < 0 ? 0 : rr >= grid.Rows ? grid.Rows - 1 : rr;
                        int idx = rr * grid.Cols + c;
                        if (!grid.Filled[idx]) continue;
                        Rgba p = grid.Pixels[idx];
                        double pa = p.A / 255.0 * w;
                        a += pa; r += p.R * pa; g += p.G * pa; b += p.B * pa;
                    }
                    if (a <= 1e-6) continue;
                    px[y * width + x] = new Rgba(
                        (byte)Math.Min(255, Math.Round(r / a)), (byte)Math.Min(255, Math.Round(g / a)),
                        (byte)Math.Min(255, Math.Round(b / a)), (byte)Math.Min(255, Math.Round(a * 255)));
                }
            }
            return px;
        }

        /// <summary>
        /// A copy of <paramref name="grid"/> that keeps only the cells for which <paramref name="keep"/> (row-major cell
        /// index) is true; the others are empty. Used to draw and contour each room on its own, so colours and contour
        /// lines do not blend across the walls between rooms.
        /// </summary>
        public static HeatmapGrid MaskGrid(HeatmapGrid grid, Func<int, bool> keep)
        {
            var m = new HeatmapGrid
            {
                Cols = grid.Cols, Rows = grid.Rows, XMin = grid.XMin, YMin = grid.YMin, XMax = grid.XMax, YMax = grid.YMax,
                Spacing = grid.Spacing,
                Pixels = new Rgba[grid.Pixels.Length],
                Filled = new bool[grid.Filled.Length],
                Values = new double[grid.Filled.Length]
            };
            for (int i = 0; i < m.Filled.Length; i++)
            {
                bool k = grid.Filled[i] && keep(i);
                m.Filled[i] = k;
                m.Pixels[i] = k ? grid.Pixels[i] : default(Rgba);
                m.Values[i] = k && grid.Values != null ? grid.Values[i] : double.NaN;
            }
            return m;
        }

        /// <summary>The grid with <paramref name="cells"/> empty cells added on every side (room to grow into).</summary>
        public static HeatmapGrid Pad(HeatmapGrid grid, int cells)
        {
            int cols = grid.Cols + 2 * cells, rows = grid.Rows + 2 * cells;
            var g = new HeatmapGrid
            {
                Cols = cols, Rows = rows, Spacing = grid.Spacing,
                XMin = grid.XMin - cells * grid.Spacing, YMin = grid.YMin - cells * grid.Spacing,
                XMax = grid.XMax + cells * grid.Spacing, YMax = grid.YMax + cells * grid.Spacing,
                Pixels = new Rgba[cols * rows], Filled = new bool[cols * rows], Values = new double[cols * rows],
                Collisions = grid.Collisions, OutOfBounds = grid.OutOfBounds
            };
            for (int i = 0; i < g.Values.Length; i++) g.Values[i] = double.NaN;
            for (int r = 0; r < grid.Rows; r++)
                for (int c = 0; c < grid.Cols; c++)
                {
                    int from = r * grid.Cols + c, to = (r + cells) * cols + c + cells;
                    g.Pixels[to] = grid.Pixels[from];
                    g.Filled[to] = grid.Filled[from];
                    g.Values[to] = grid.Values != null ? grid.Values[from] : double.NaN;
                }
            return g;
        }

        /// <summary>
        /// Grows the filled cells outward by <paramref name="steps"/> cells: each empty cell next to filled ones takes
        /// their average colour. Clipped to a room outline afterwards, the colours then reach the walls instead of
        /// fading out in the half cell between the last receiver and the wall. Values are not extended.
        /// </summary>
        public static HeatmapGrid Dilate(HeatmapGrid grid, int steps)
        {
            var g = MaskGrid(grid, i => true);
            for (int step = 0; step < steps; step++)
            {
                var filled = (bool[])g.Filled.Clone();
                var pixels = (Rgba[])g.Pixels.Clone();
                for (int r = 0; r < g.Rows; r++)
                    for (int c = 0; c < g.Cols; c++)
                    {
                        int idx = r * g.Cols + c;
                        if (g.Filled[idx]) continue;
                        int n = 0, sr = 0, sg = 0, sb = 0, sa = 0;
                        for (int dr = -1; dr <= 1; dr++)
                            for (int dc = -1; dc <= 1; dc++)
                            {
                                int rr = r + dr, cc = c + dc;
                                if ((dr == 0 && dc == 0) || rr < 0 || cc < 0 || rr >= g.Rows || cc >= g.Cols) continue;
                                int j = rr * g.Cols + cc;
                                if (!g.Filled[j]) continue;
                                Rgba p = g.Pixels[j];
                                n++; sr += p.R; sg += p.G; sb += p.B; sa = Math.Max(sa, p.A);
                            }
                        if (n == 0) continue;
                        filled[idx] = true;
                        pixels[idx] = new Rgba((byte)(sr / n), (byte)(sg / n), (byte)(sb / n), (byte)sa);
                    }
                g.Filled = filled;
                g.Pixels = pixels;
            }
            return g;
        }

        /// <summary>
        /// Contour levels for a value range: the IEC 60268-16 category limits (0.30, 0.45, 0.60, 0.75) inside the range
        /// for STI, otherwise round steps (1, 2, 3, 5, 10 ... units) giving three to seven lines.
        /// </summary>
        public static List<double> ContourLevels(double min, double max, VisualizationMode mode)
        {
            var levels = new List<double>();
            if (!(max > min)) return levels;
            if (mode == VisualizationMode.STI)
            {
                foreach (double l in new[] { 0.30, 0.45, 0.60, 0.75 })
                    if (l > min && l < max) levels.Add(l);
                return levels;
            }
            double[] steps = { 0.5, 1, 2, 3, 5, 10, 20, 50 };
            double step = steps[steps.Length - 1];
            foreach (double st in steps)
            {
                int n = (int)Math.Floor(max / st) - (int)Math.Ceiling(min / st) + 1;
                if (n <= 7) { step = st; break; }
            }
            for (double l = Math.Ceiling(min / step) * step; l < max - 1e-9; l += step)
                if (l > min + 1e-9) levels.Add(Math.Round(l, 6));
            return levels;
        }

        /// <summary>
        /// Contour lines of <see cref="HeatmapGrid.Values"/> at <paramref name="level"/> by marching squares over the
        /// cells whose four corner receivers exist (no line crosses a gap in the grid). Segments are joined into
        /// polylines in world coordinates (metres); closed contours repeat their first point at the end.
        /// </summary>
        public static List<List<Vec2>> Contour(HeatmapGrid grid, double level)
        {
            var segments = new List<(Vec2 A, Vec2 B)>();
            if (grid?.Values == null) return new List<List<Vec2>>();
            int cols = grid.Cols, rows = grid.Rows;
            double sp = grid.Spacing;
            Vec2 P(int c, int r) => new Vec2(grid.XMin + c * sp, grid.YMin + r * sp);
            Vec2 Lerp(int c0, int r0, double v0, int c1, int r1, double v1)
            {
                double t = Math.Abs(v1 - v0) < 1e-12 ? 0.5 : (level - v0) / (v1 - v0);
                t = t < 0 ? 0 : t > 1 ? 1 : t;
                Vec2 a = P(c0, r0), b = P(c1, r1);
                return new Vec2(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
            }

            for (int r = 0; r + 1 < rows; r++)
            {
                for (int c = 0; c + 1 < cols; c++)
                {
                    double v00 = grid.Values[r * cols + c], v10 = grid.Values[r * cols + c + 1];
                    double v11 = grid.Values[(r + 1) * cols + c + 1], v01 = grid.Values[(r + 1) * cols + c];
                    if (double.IsNaN(v00) || double.IsNaN(v10) || double.IsNaN(v11) || double.IsNaN(v01)) continue;
                    int code = (v00 >= level ? 1 : 0) | (v10 >= level ? 2 : 0) | (v11 >= level ? 4 : 0) | (v01 >= level ? 8 : 0);
                    if (code == 0 || code == 15) continue;
                    // Edge crossings: bottom (00-10), right (10-11), top (01-11), left (00-01)
                    Vec2 bottom = Lerp(c, r, v00, c + 1, r, v10);
                    Vec2 right = Lerp(c + 1, r, v10, c + 1, r + 1, v11);
                    Vec2 top = Lerp(c, r + 1, v01, c + 1, r + 1, v11);
                    Vec2 left = Lerp(c, r, v00, c, r + 1, v01);
                    bool centreHigh = (v00 + v10 + v11 + v01) * 0.25 >= level;
                    switch (code)
                    {
                        case 1: case 14: segments.Add((left, bottom)); break;
                        case 2: case 13: segments.Add((bottom, right)); break;
                        case 3: case 12: segments.Add((left, right)); break;
                        case 4: case 11: segments.Add((right, top)); break;
                        case 6: case 9: segments.Add((bottom, top)); break;
                        case 7: case 8: segments.Add((left, top)); break;
                        case 5:   // 00 and 11 high
                            if (centreHigh) { segments.Add((left, top)); segments.Add((bottom, right)); }
                            else { segments.Add((left, bottom)); segments.Add((right, top)); }
                            break;
                        case 10:  // 10 and 01 high
                            if (centreHigh) { segments.Add((left, bottom)); segments.Add((right, top)); }
                            else { segments.Add((left, top)); segments.Add((bottom, right)); }
                            break;
                    }
                }
            }
            return JoinSegments(segments, sp * 1e-6);
        }

        /// <summary>Joins segments that share end points (within <paramref name="tol"/>) into polylines.</summary>
        public static List<List<Vec2>> JoinSegments(List<(Vec2 A, Vec2 B)> segments, double tol)
        {
            double q = tol > 0 ? 1.0 / tol : 1e6;
            (long, long) Key(Vec2 p) => ((long)Math.Round(p.X * q), (long)Math.Round(p.Y * q));
            var ends = new Dictionary<(long, long), List<int>>();
            for (int i = 0; i < segments.Count; i++)
            {
                foreach (Vec2 p in new[] { segments[i].A, segments[i].B })
                {
                    var k = Key(p);
                    if (!ends.TryGetValue(k, out List<int> l)) ends[k] = l = new List<int>();
                    l.Add(i);
                }
            }
            var used = new bool[segments.Count];
            var lines = new List<List<Vec2>>();
            for (int i = 0; i < segments.Count; i++)
            {
                if (used[i]) continue;
                used[i] = true;
                var line = new LinkedList<Vec2>();
                line.AddLast(segments[i].A);
                line.AddLast(segments[i].B);
                // Grow at the end, then at the start
                for (int pass = 0; pass < 2; pass++)
                {
                    while (true)
                    {
                        Vec2 tip = pass == 0 ? line.Last.Value : line.First.Value;
                        int next = -1;
                        foreach (int j in ends[Key(tip)])
                            if (!used[j]) { next = j; break; }
                        if (next < 0) break;
                        used[next] = true;
                        Vec2 other = Key(segments[next].A).Equals(Key(tip)) ? segments[next].B : segments[next].A;
                        if (pass == 0) line.AddLast(other); else line.AddFirst(other);
                    }
                }
                lines.Add(line.ToList());
            }
            return lines;
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
