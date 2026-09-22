using System;
using System.Collections.Generic;
using System.Linq;
using SoundCalcs.Domain;
using SoundCalcs.UI.ViewModels;
using SoundCalcs.Visualization;

namespace SoundCalcs.Harness
{
    /// <summary>
    /// Invariants every job must satisfy, independent of the scenario:
    /// result bookkeeping, finite values, and that both heatmap pipelines
    /// (viewer bitmap and Revit filled regions) show every receiver exactly once
    /// in the colour its value maps to.
    /// </summary>
    public static class VisualChecks
    {
        public static readonly VisualizationMode[] AllModes =
            (VisualizationMode[])Enum.GetValues(typeof(VisualizationMode));

        public static void Run(AcousticJobInput input, AcousticJobOutput output,
            double viewerGridSpacing, CheckContext ctx)
        {
            var results = output.Results;

            // ---- Compute bookkeeping -------------------------------------------------
            ctx.Assert("compute: one result per receiver", results.Count == input.Receivers.Count,
                $"{results.Count} results for {input.Receivers.Count} receivers");
            if (results.Count == 0) return;

            bool sorted = results.Select((r, i) => r.ReceiverIndex == input.Receivers[i].Index).All(b => b);
            ctx.Assert("compute: results ordered by receiver index", sorted, "");

            int nonFinite = results.Count(r =>
                !IsFinite(r.SplDb) || !IsFinite(r.SplDbA) || !IsFinite(r.Sti) ||
                !IsFinite(r.C80Db) || !IsFinite(r.D50) ||
                r.SplDbByBand == null || r.SplDbByBand.Length != OctaveBands.Count ||
                r.SplDbByBand.Any(v => !IsFinite(v)));
            ctx.Assert("compute: all values finite, 7 bands", nonFinite == 0, $"{nonFinite} bad receivers");

            int stiOut = results.Count(r => r.Sti < 0 || r.Sti > 1);
            int d50Out = results.Count(r => r.D50 < 0 || r.D50 > 1);
            ctx.Assert("compute: STI and D50 within [0,1]", stiOut == 0 && d50Out == 0,
                $"STI out of range: {stiOut}, D50 out of range: {d50Out}");

            double worstBandSum = results.Max(r =>
                Math.Abs(r.SplDb - 10 * Math.Log10(r.SplDbByBand.Sum(b => Math.Pow(10, b / 10)))));
            ctx.Assert("compute: broadband SPL = energy sum of bands", worstBandSum <= 0.05,
                $"worst mismatch {CheckContext.F(worstBandSum)} dB");

            double worstA = results.Max(r =>
                Math.Abs(r.SplDbA - 10 * Math.Log10(r.SplDbByBand
                    .Select((b, k) => Math.Pow(10, (b + OctaveBands.AWeightingDb[k]) / 10)).Sum())));
            ctx.Assert("compute: dBA = A-weighted energy sum of bands", worstA <= 0.05,
                $"worst mismatch {CheckContext.F(worstA)} dB");

            // ---- Per-mode heatmap checks ---------------------------------------------
            var viewerFailures = new List<string>();
            var revitFailures = new List<string>();
            var legendFailures = new List<string>();

            foreach (VisualizationMode mode in AllModes)
            {
                double[] vals = results.Select(r => HeatmapMath.GetValue(r, mode)).ToArray();
                CheckViewer(results, vals, mode, viewerGridSpacing, viewerFailures);
                CheckRevit(results, vals, mode, revitFailures, legendFailures);
            }

            ctx.Assert("viewer: every receiver gets its own pixel in its gradient colour (all modes)",
                viewerFailures.Count == 0, string.Join("; ", viewerFailures.Take(6)));
            ctx.Assert("revit: filled-region strips tile every receiver cell exactly once in its band (all modes)",
                revitFailures.Count == 0, string.Join("; ", revitFailures.Take(6)));
            ctx.Assert("revit: legend bands contiguous, span the rendered range, STI categories per IEC (all modes)",
                legendFailures.Count == 0, string.Join("; ", legendFailures.Take(6)));
        }

        static void CheckViewer(List<ReceiverResult> results, double[] vals, VisualizationMode mode,
            double spacing, List<string> failures)
        {
            var (min, max) = HeatmapMath.ComputeViewerRange(vals, mode);
            if (!(max > min)) { failures.Add($"{mode}: empty range"); return; }

            HeatmapGrid grid = HeatmapMath.BuildViewerGrid(results, vals, min, max - min, spacing);
            int filled = grid.Filled.Count(f => f);
            if (grid.Collisions > 0 || grid.OutOfBounds > 0 || filled != results.Count)
                failures.Add($"{mode}: {grid.Collisions} collisions, {grid.OutOfBounds} out of bounds, " +
                             $"{filled}/{results.Count} pixels filled at spacing {spacing}");

            // Each receiver's pixel carries exactly the gradient colour of its value
            int wrong = 0;
            for (int i = 0; i < results.Count; i++)
            {
                int col = (int)Math.Round((results[i].Position.X - grid.XMin) / grid.Spacing);
                int row = (int)Math.Round((results[i].Position.Y - grid.YMin) / grid.Spacing);
                Rgba expect = HeatmapMath.SampleGradient((vals[i] - min) / (max - min));
                Rgba got = grid.Pixels[row * grid.Cols + col];
                if (got.R != expect.R || got.G != expect.G || got.B != expect.B) wrong++;
            }
            if (wrong > 0 && grid.Collisions == 0)
                failures.Add($"{mode}: {wrong} pixels in the wrong colour");

            // Loudest (largest) value is drawn at the green end, smallest at the red end
            int iMax = Array.IndexOf(vals, vals.Max()), iMin = Array.IndexOf(vals, vals.Min());
            Rgba top = HeatmapMath.ViewerGradientStops[HeatmapMath.ViewerGradientStops.Length - 1];
            Rgba bottom = HeatmapMath.ViewerGradientStops[0];
            Rgba cMax = HeatmapMath.SampleGradient((vals[iMax] - min) / (max - min));
            Rgba cMin = HeatmapMath.SampleGradient((vals[iMin] - min) / (max - min));
            // Only when the extremes lie outside the displayed range (the usual case; not when
            // a near-flat field was widened around its midpoint)
            if (vals[iMax] >= max && (cMax.G != top.G || cMax.R != top.R))
                failures.Add($"{mode}: largest value not drawn at the green end");
            if (vals[iMin] <= min && (cMin.R != bottom.R || cMin.G != bottom.G))
                failures.Add($"{mode}: smallest value not drawn at the red end");
        }

        static void CheckRevit(List<ReceiverResult> results, double[] vals, VisualizationMode mode,
            List<string> failures, List<string> legendFailures)
        {
            FilledRegionPlan plan = HeatmapMath.PlanFilledRegions(results, mode, null);
            if (plan == null) { failures.Add($"{mode}: nothing planned"); return; }

            double s = plan.GridSpacingM;
            var strips = plan.Strips.SelectMany(kv => kv.Value.Select(r => (Band: kv.Key, Rect: r))).ToList();

            double area = strips.Sum(x => (x.Rect.x1 - x.Rect.x0) * (x.Rect.y1 - x.Rect.y0));
            double expected = plan.Results.Count * s * s;
            if (Math.Abs(area - expected) > 1e-6 * Math.Max(1, expected))
                failures.Add($"{mode}: strip area {area:F3} m² != {plan.Results.Count} cells × {s}² = {expected:F3} m²");

            int uncovered = 0, multi = 0, wrongBand = 0;
            for (int i = 0; i < plan.Results.Count; i++)
            {
                double x = plan.Results[i].Position.X, y = plan.Results[i].Position.Y;
                int hits = 0, band = -1;
                foreach (var st in strips)
                {
                    if (x > st.Rect.x0 && x < st.Rect.x1 && y > st.Rect.y0 && y < st.Rect.y1)
                    { hits++; band = st.Band; }
                }
                if (hits == 0) uncovered++;
                else if (hits > 1) multi++;
                else if (band != plan.BandIndex[i]) wrongBand++;

                int expectBand = HeatmapMath.ValueToBand(vals[i], plan.MinVal, plan.MaxVal, plan.NumBands);
                if (expectBand != plan.BandIndex[i]) wrongBand++;
            }
            if (uncovered + multi + wrongBand > 0)
                failures.Add($"{mode}: {uncovered} receivers uncovered, {multi} covered twice, {wrongBand} in wrong band");

            // Legend: highest band first, contiguous, spanning [min, max]
            var legend = mode == VisualizationMode.STI
                ? HeatmapMath.GetStiLegendBands(plan.MinVal, plan.MaxVal)
                : HeatmapMath.GetLegendBands(plan.MinVal, plan.MaxVal);
            var ranges = legend.Select(l => ParseRange(l.Label)).ToList();
            bool ok = legend.Count == plan.NumBands &&
                      legend.Select(l => l.Band).SequenceEqual(Enumerable.Range(0, plan.NumBands).Reverse());
            string fmt = mode == VisualizationMode.STI ? "F2" : "F1";
            ok &= ranges[0].Hi.ToString(fmt) == plan.MaxVal.ToString(fmt);
            ok &= ranges[ranges.Count - 1].Lo.ToString(fmt) == plan.MinVal.ToString(fmt);
            for (int i = 0; i + 1 < ranges.Count; i++)
                ok &= Math.Abs(ranges[i].Lo - ranges[i + 1].Hi) < 1e-9;
            if (mode == VisualizationMode.STI)
            {
                double step = (plan.MaxVal - plan.MinVal) / plan.NumBands;
                foreach (var l in legend)
                {
                    // exact band edges (the label text is rounded)
                    double lo = plan.MinVal + l.Band * step;
                    double hi = l.Band == plan.NumBands - 1 ? plan.MaxVal : plan.MinVal + (l.Band + 1) * step;
                    ok &= l.Label.EndsWith($"({HeatmapMath.StiQuality((lo + hi) / 2)})");
                }
            }
            if (!ok)
                legendFailures.Add($"{mode}: [{string.Join(" | ", legend.Select(l => l.Label))}] for range {plan.MinVal:F2}..{plan.MaxVal:F2}");
        }

        static (double Lo, double Hi) ParseRange(string label)
        {
            // "12.3 – 45.6 dB" / "0.40 – 0.52 (Fair)"
            string[] parts = label.Split('–');
            double lo = double.Parse(parts[0].Trim(), System.Globalization.CultureInfo.CurrentCulture);
            string hiTxt = new string(parts[1].Trim().TakeWhile(c => char.IsDigit(c) || c == '.' || c == ',' || c == '-').ToArray());
            double hi = double.Parse(hiTxt, System.Globalization.CultureInfo.CurrentCulture);
            return (lo, hi);
        }

        static bool IsFinite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);
    }
}
