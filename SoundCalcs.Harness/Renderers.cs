using System;
using System.Collections.Generic;
using System.Linq;
using SoundCalcs.Domain;
using SoundCalcs.UI.ViewModels;
using SoundCalcs.Visualization;

namespace SoundCalcs.Harness
{
    /// <summary>World (metres, Y up) → screen (pixels, Y down), same convention as the viewer.</summary>
    public class ViewTransform
    {
        public double Zoom, PanX, PanY;

        public (double X, double Y) ToScreen(double x, double y) => (x * Zoom + PanX, -y * Zoom + PanY);

        /// <summary>Port of AcousticViewerControl.FitView (64 px padding).</summary>
        public static ViewTransform Fit(IEnumerable<(double x, double y)> points,
            double left, double top, double width, double height)
        {
            var pts = points.ToList();
            var v = new ViewTransform();
            if (pts.Count == 0)
            {
                v.Zoom = 50; v.PanX = left + width / 2; v.PanY = top + height / 2;
                return v;
            }
            double xMin = pts.Min(p => p.x), xMax = pts.Max(p => p.x);
            double yMin = pts.Min(p => p.y), yMax = pts.Max(p => p.y);
            double bw = Math.Max(xMax - xMin, 0.1), bh = Math.Max(yMax - yMin, 0.1);
            const double pad = 64;
            v.Zoom = Math.Max(1, Math.Min((width - 2 * pad) / bw, (height - 2 * pad) / bh));
            double cx = (xMin + xMax) * 0.5, cy = (yMin + yMax) * 0.5;
            v.PanX = left + width * 0.5 - cx * v.Zoom;
            v.PanY = top + height * 0.5 + cy * v.Zoom;
            return v;
        }
    }

    /// <summary>
    /// Draws what the in-panel viewer (AcousticViewerControl) shows, using the same
    /// HeatmapMath range / grid / gradient code and the viewer's colours and layout.
    /// </summary>
    public static class ViewerImage
    {
        static readonly Rgba BgColor     = new Rgba(0x1F, 0x1F, 0x1F);
        static readonly Rgba WallColor   = new Rgba(0xAA, 0xBB, 0xCC);
        static readonly Rgba SpeakerFill = new Rgba(0x40, 0x90, 0xFF, 200);
        static readonly Rgba SpeakerRing = new Rgba(0x80, 0xB8, 0xFF);
        static readonly Rgba DirColor    = new Rgba(0xFF, 0xFF, 0x60, 200);
        static readonly Rgba LegendBg    = new Rgba(0x18, 0x18, 0x18, 0xCC);
        static readonly Rgba TextBright  = new Rgba(0xEE, 0xEE, 0xEE);
        static readonly Rgba TextDim     = new Rgba(0xAA, 0xAA, 0xAA);

        public const int Width = 1000, Height = 720;

        public static void Render(AcousticJobInput input, AcousticJobOutput output,
            VisualizationMode mode, double gridSpacing, string title, string path)
        {
            var results = output.Results;
            var vals = results.Select(r => HeatmapMath.GetValue(r, mode)).ToArray();
            var (minVal, maxVal) = HeatmapMath.ComputeViewerRange(vals, mode);
            HeatmapGrid grid = HeatmapMath.BuildViewerGrid(results, vals, minVal, maxVal - minVal, gridSpacing);

            var img = new Raster(Width, Height, BgColor);
            var view = ViewTransform.Fit(ContentPoints(input, output), 0, 0, Width, Height);

            // Heatmap: bilinear sampling of the one-pixel-per-cell bitmap, like
            // SKFilterQuality.Medium does when the viewer scales it into worldRect.
            if (grid != null)
            {
                var (sx0, sy1) = view.ToScreen(grid.WorldLeft, grid.WorldBottom);
                var (sx1, sy0) = view.ToScreen(grid.WorldRight, grid.WorldTop);
                double texW = (grid.WorldRight - grid.WorldLeft) / grid.Cols;
                double texH = (grid.WorldTop - grid.WorldBottom) / grid.Rows;
                for (int py = Math.Max(0, (int)sy0); py < Math.Min(Height, (int)Math.Ceiling(sy1)); py++)
                {
                    for (int px = Math.Max(0, (int)sx0); px < Math.Min(Width, (int)Math.Ceiling(sx1)); px++)
                    {
                        double wx = (px + 0.5 - view.PanX) / view.Zoom;
                        double wy = -(py + 0.5 - view.PanY) / view.Zoom;
                        double u = (wx - grid.WorldLeft) / texW - 0.5;
                        double v = (wy - grid.WorldBottom) / texH - 0.5;
                        if (u < -0.5 || v < -0.5 || u > grid.Cols - 0.5 || v > grid.Rows - 0.5) continue;
                        img.Blend(px, py, SampleBilinear(grid, u, v));
                    }
                }
            }

            // Walls: 2 px strokes
            foreach (var w in input.Walls)
            {
                var (ax, ay) = view.ToScreen(w.Start.X, w.Start.Y);
                var (bx, by) = view.ToScreen(w.End.X, w.End.Y);
                img.Line(ax, ay, bx, by, WallColor, 2);
            }

            // Speakers: circle + aim indicator for horizontal facing
            double radiusPx = Math.Max(0.25 * view.Zoom, 8);
            foreach (var s in input.Sources)
            {
                var (cx, cy) = view.ToScreen(s.Position.X, s.Position.Y);
                double dx = s.FacingDirection.X, dy = s.FacingDirection.Y;
                double hLen = Math.Sqrt(dx * dx + dy * dy);
                if (hLen > 0.15)
                {
                    double k = radiusPx * 2.5 / hLen;
                    img.Line(cx, cy, cx + dx * k, cy - dy * k, DirColor, 1.5);
                }
                img.FillCircle(cx, cy, radiusPx, SpeakerFill);
                img.StrokeCircle(cx, cy, radiusPx, SpeakerRing, 1.5);
            }

            DrawLegend(img, mode, minVal, maxVal);
            DrawScaleBar(img, view);

            img.Text(10, 10, title, TextBright, 2);
            img.Text(10, 30, $"VIEWER  MODE {mode}  RANGE {Fmt(minVal, mode)} TO {Fmt(maxVal, mode)}  " +
                             $"(P2-P98)  {results.Count} RECEIVERS", TextDim);
            img.SavePng(path);
        }

        static Rgba SampleBilinear(HeatmapGrid g, double u, double v)
        {
            int x0 = (int)Math.Floor(u), y0 = (int)Math.Floor(v);
            double fx = u - x0, fy = v - y0;
            double r = 0, gg = 0, b = 0, a = 0;
            void Acc(int x, int y, double w)
            {
                x = Math.Max(0, Math.Min(g.Cols - 1, x));
                y = Math.Max(0, Math.Min(g.Rows - 1, y));
                int i = y * g.Cols + x;
                if (!g.Filled[i]) return;               // transparent texel
                Rgba c = g.Pixels[i];
                double aa = c.A / 255.0 * w;             // premultiplied accumulate
                r += c.R * aa; gg += c.G * aa; b += c.B * aa; a += aa;
            }
            Acc(x0, y0, (1 - fx) * (1 - fy));
            Acc(x0 + 1, y0, fx * (1 - fy));
            Acc(x0, y0 + 1, (1 - fx) * fy);
            Acc(x0 + 1, y0 + 1, fx * fy);
            if (a <= 1e-9) return new Rgba(0, 0, 0, 0);
            return new Rgba((byte)Math.Round(r / a), (byte)Math.Round(gg / a), (byte)Math.Round(b / a),
                            (byte)Math.Round(Math.Min(1, a) * 255));
        }

        /// <summary>Port of AcousticViewerControl.DrawLegend (8 swatches, max at top).</summary>
        static void DrawLegend(Raster img, VisualizationMode mode, double minVal, double maxVal)
        {
            Rgba[] colors = HeatmapMath.ViewerLegendColors();
            bool isSti = mode == VisualizationMode.STI;
            int bandIdx = HeatmapMath.GetOctaveBandIndex(mode);
            string modeLabel = isSti ? "STI"
                : mode == VisualizationMode.SPL_A ? "DBA"
                : mode == VisualizationMode.C80 ? "C80"
                : bandIdx >= 0 ? $"SPL {OctaveBands.Labels[bandIdx]} HZ"
                : "SPL";

            const int swW = 16, swH = 18, gap = 2, pad = 8, panelW = 130;
            int ox = Width - panelW - 6 - pad, oy = 54;
            int totalH = (swH + gap) * colors.Length + pad + 14;
            img.FillRect(ox - pad, oy - pad, ox + panelW + pad, oy + totalH, LegendBg);
            img.Text(ox, oy - 2, modeLabel, TextDim);

            int rowStart = oy + 12;
            for (int i = colors.Length - 1; i >= 0; i--)
            {
                int row = colors.Length - 1 - i;
                int sy = rowStart + row * (swH + gap);
                img.FillRect(ox, sy, ox + swW, sy + swH, colors[i]);
                double lo = minVal + (maxVal - minVal) * i / colors.Length;
                double hi = minVal + (maxVal - minVal) * (i + 1) / colors.Length;
                string label = HeatmapMath.ViewerLegendLabel(lo, hi, mode);
                img.Text(ox + swW + 6, sy + 5, label, TextBright);
            }
        }

        static void DrawScaleBar(Raster img, ViewTransform view)
        {
            double[] nice = { 0.5, 1, 2, 5, 10, 20, 50, 100 };
            double metres = nice.FirstOrDefault(m => m * view.Zoom >= 80);
            if (metres == 0) metres = nice.Last();
            double px = metres * view.Zoom;
            int x0 = 20, y0 = Height - 24;
            img.Line(x0, y0, x0 + px, y0, TextDim, 2);
            img.Line(x0, y0 - 5, x0, y0 + 5, TextDim, 2);
            img.Line(x0 + px, y0 - 5, x0 + px, y0 + 5, TextDim, 2);
            img.Text(x0, y0 - 16, $"{metres:0.#} M", TextDim);
        }

        internal static IEnumerable<(double x, double y)> ContentPoints(AcousticJobInput input, AcousticJobOutput output)
        {
            foreach (var w in input.Walls) { yield return (w.Start.X, w.Start.Y); yield return (w.End.X, w.End.Y); }
            foreach (var s in input.Sources) yield return (s.Position.X, s.Position.Y);
            foreach (var r in output.Results) yield return (r.Position.X, r.Position.Y);
        }

        internal static string Fmt(double v, VisualizationMode mode) =>
            mode == VisualizationMode.STI ? v.ToString("F2") : v.ToString("F1");
    }

    /// <summary>
    /// Draws what FilledRegionRenderer puts into the Revit plan view: one solid
    /// rectangle per merged strip, coloured by band, plus the panel legend.
    /// </summary>
    public static class RevitImage
    {
        static readonly Rgba Paper = new Rgba(0xFF, 0xFF, 0xFF);
        static readonly Rgba Ink   = new Rgba(0x20, 0x20, 0x20);
        static readonly Rgba Dim   = new Rgba(0x60, 0x60, 0x60);

        public const int Width = 1000, Height = 720;

        public static void Render(AcousticJobInput input, AcousticJobOutput output,
            VisualizationMode mode, double? minSplThreshold, string title, string path)
        {
            // The legend lives in the plugin panel, not in the Revit view, so keep it off the plot.
            const int legendW = 230;
            var img = new Raster(Width, Height, Paper);
            var view = ViewTransform.Fit(ViewerImage.ContentPoints(input, output), 0, 0, Width - legendW, Height);
            FilledRegionPlan plan = HeatmapMath.PlanFilledRegions(output.Results, mode, minSplThreshold);

            int regions = 0;
            if (plan != null)
            {
                foreach (var kv in plan.Strips)
                {
                    var (_, r, g, b) = HeatmapMath.RevitBandColors[kv.Key];
                    foreach (var (x0, y0, x1, y1, _) in kv.Value)
                    {
                        var (ax, ay) = view.ToScreen(x0, y1);
                        var (bx, by) = view.ToScreen(x1, y0);
                        img.FillRect(ax, ay, bx, by, new Rgba(r, g, b));
                        regions++;
                    }
                }
            }

            foreach (var w in input.Walls)
            {
                var (ax, ay) = view.ToScreen(w.Start.X, w.Start.Y);
                var (bx, by) = view.ToScreen(w.End.X, w.End.Y);
                img.Line(ax, ay, bx, by, Ink, 2);
            }
            foreach (var s in input.Sources)
            {
                var (cx, cy) = view.ToScreen(s.Position.X, s.Position.Y);
                img.StrokeCircle(cx, cy, 6, Ink, 2);
                img.Line(cx - 4, cy, cx + 4, cy, Ink, 1);
                img.Line(cx, cy - 4, cx, cy + 4, Ink, 1);
            }

            if (plan != null)
            {
                var legend = mode == VisualizationMode.STI
                    ? HeatmapMath.GetStiLegendBands(plan.MinVal, plan.MaxVal)
                    : HeatmapMath.GetLegendBands(plan.MinVal, plan.MaxVal,
                        mode == VisualizationMode.SPL_A ? " dBA" : " dB");
                int ox = Width - legendW + 20, oy = 60;
                img.FillRect(ox - 8, oy - 8, Width - 6, oy + legend.Count * 20 + 4, new Rgba(0xF4, 0xF4, 0xF4));
                for (int i = 0; i < legend.Count; i++)
                {
                    var (band, hex, label) = legend[i];
                    var (_, r, g, b) = HeatmapMath.RevitBandColors[band];
                    img.FillRect(ox, oy + i * 20, ox + 16, oy + i * 20 + 16, new Rgba(r, g, b));
                    img.Text(ox + 22, oy + i * 20 + 5, label, Ink);
                }
            }

            img.Text(10, 10, title, Ink, 2);
            img.Text(10, 30, plan == null
                ? $"REVIT FILLED REGIONS  MODE {mode}  NOTHING TO DRAW"
                : $"REVIT FILLED REGIONS  MODE {mode}  RANGE {ViewerImage.Fmt(plan.MinVal, mode)} TO " +
                  $"{ViewerImage.Fmt(plan.MaxVal, mode)} (MIN-MAX)  {regions} REGIONS", Dim);
            img.SavePng(path);
        }
    }
}
