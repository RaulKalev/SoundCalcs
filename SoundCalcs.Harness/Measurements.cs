using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using SoundCalcs.Domain;

namespace SoundCalcs.Harness
{
    /// <summary>
    /// One measured position. X/Y are metres in the model's coordinates (the same as the job
    /// input: Revit internal origin). Any metric may be left empty.
    /// </summary>
    public class MeasuredPoint
    {
        public string Name { get; set; } = "";
        public double X { get; set; }
        public double Y { get; set; }
        public double? Spl { get; set; }
        public double? SplA { get; set; }
        public double? Sti { get; set; }
        public double? C80 { get; set; }
        /// <summary>Octave-band SPL, 125 Hz – 8 kHz (entries may be null).</summary>
        public double?[] Bands { get; set; }
    }

    public class MeasurementTolerances
    {
        public double SplDb { get; set; } = 3.0;
        public double Sti { get; set; } = 0.05;
        public double C80Db { get; set; } = 2.0;
    }

    /// <summary>
    /// Compares predicted results with measurements: per-point errors, and bias / RMS / max
    /// error per metric. Writes measured_vs_predicted.csv next to the scenario's images.
    /// </summary>
    public static class Measurements
    {
        static readonly string[] BandColumns = { "spl125", "spl250", "spl500", "spl1k", "spl2k", "spl4k", "spl8k" };

        /// <summary>
        /// Load a JSON array of <see cref="MeasuredPoint"/> or a CSV with a header row:
        /// name,x,y,spl,spla,sti,c80,spl125,spl250,spl500,spl1k,spl2k,spl4k,spl8k
        /// (any subset of the metric columns, in any order; empty cells are skipped).
        /// </summary>
        public static List<MeasuredPoint> Load(string path)
        {
            string text = File.ReadAllText(path).Trim();
            if (text.StartsWith("["))
                return JsonConvert.DeserializeObject<List<MeasuredPoint>>(text);

            var lines = text.Split('\n').Select(l => l.Trim().TrimEnd('\r')).Where(l => l.Length > 0 && !l.StartsWith("#")).ToList();
            char sep = lines[0].Contains(';') && !lines[0].Contains(',') ? ';' : ',';
            string[] header = lines[0].Split(sep).Select(h => h.Trim().ToLowerInvariant()).ToArray();
            int Col(string n) => Array.IndexOf(header, n);
            if (Col("x") < 0 || Col("y") < 0)
                throw new FormatException("Measurement CSV needs at least x and y columns.");

            var points = new List<MeasuredPoint>();
            for (int i = 1; i < lines.Count; i++)
            {
                string[] c = lines[i].Split(sep);
                double? Get(string n)
                {
                    int j = Col(n);
                    if (j < 0 || j >= c.Length || string.IsNullOrWhiteSpace(c[j])) return null;
                    return double.Parse(c[j].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture);
                }
                var p = new MeasuredPoint
                {
                    Name = Col("name") >= 0 && Col("name") < c.Length ? c[Col("name")].Trim() : $"P{i}",
                    X = Get("x").Value,
                    Y = Get("y").Value,
                    Spl = Get("spl"),
                    SplA = Get("spla"),
                    Sti = Get("sti"),
                    C80 = Get("c80"),
                };
                if (BandColumns.Any(b => Col(b) >= 0))
                    p.Bands = BandColumns.Select(b => Get(b)).ToArray();
                points.Add(p);
            }
            return points;
        }

        public static void Compare(ScenarioRun run, List<MeasuredPoint> points, MeasurementTolerances tol,
            CheckContext ctx, string outDir)
        {
            double spacing = Visualization.HeatmapMath.EstimateGridSpacing(run.Output.Results);
            var rows = new List<(MeasuredPoint P, ReceiverResult R, double Dist)>();
            foreach (var p in points)
            {
                var r = run.Nearest(p.X, p.Y);
                double d = Math.Sqrt(Math.Pow(r.Position.X - p.X, 2) + Math.Pow(r.Position.Y - p.Y, 2));
                rows.Add((p, r, d));
            }

            var outside = rows.Where(x => x.Dist > spacing * 0.75).ToList();
            ctx.Assert("measurement points lie on the receiver grid", outside.Count == 0,
                $"{outside.Count} point(s) more than ¾ grid spacing from any receiver: " +
                string.Join(", ", outside.Take(5).Select(x => $"{x.P.Name} ({CheckContext.F(x.Dist)} m)")) +
                ". Check that coordinates are in model metres.");

            void Metric(string label, string unit, double tolerance,
                Func<MeasuredPoint, double?> measured, Func<ReceiverResult, double> predicted)
            {
                var pairs = rows.Where(x => measured(x.P).HasValue)
                    .Select(x => (x.P.Name, Err: predicted(x.R) - measured(x.P).Value)).ToList();
                if (pairs.Count == 0) return;
                double bias = pairs.Average(e => e.Err);
                double rms = Math.Sqrt(pairs.Average(e => e.Err * e.Err));
                var worst = pairs.OrderByDescending(e => Math.Abs(e.Err)).First();
                int over = pairs.Count(e => Math.Abs(e.Err) > tolerance);
                ctx.Assert($"{label}: prediction within ±{CheckContext.F(tolerance)}{unit} of measurement",
                    over == 0,
                    $"{pairs.Count} points, bias {CheckContext.F(bias)}{unit}, RMS {CheckContext.F(rms)}{unit}, " +
                    $"worst {worst.Name} {CheckContext.F(worst.Err)}{unit}, {over} outside tolerance " +
                    "(error = predicted − measured)");
            }

            Metric("SPL", " dB", tol.SplDb, p => p.Spl, r => r.SplDb);
            Metric("SPL (A)", " dBA", tol.SplDb, p => p.SplA, r => r.SplDbA);
            Metric("STI", "", tol.Sti, p => p.Sti, r => r.Sti);
            Metric("C80", " dB", tol.C80Db, p => p.C80, r => r.C80Db);
            for (int k = 0; k < OctaveBands.Count; k++)
            {
                int band = k;
                Metric($"SPL {OctaveBands.Labels[k]} Hz", " dB", tol.SplDb,
                    p => p.Bands != null && p.Bands.Length > band ? p.Bands[band] : null,
                    r => r.SplDbByBand[band]);
            }

            // Per-point table for spreadsheets
            var csv = new StringBuilder("name,x,y,grid_x,grid_y,metric,measured,predicted,error\n");
            foreach (var (p, r, _) in rows)
            {
                void Line(string metric, double? m, double pred)
                {
                    if (!m.HasValue) return;
                    csv.AppendLine(string.Join(",", p.Name, F(p.X), F(p.Y), F(r.Position.X), F(r.Position.Y),
                        metric, F(m.Value), F(pred), F(pred - m.Value)));
                }
                Line("spl", p.Spl, r.SplDb);
                Line("spla", p.SplA, r.SplDbA);
                Line("sti", p.Sti, r.Sti);
                Line("c80", p.C80, r.C80Db);
                for (int k = 0; k < OctaveBands.Count; k++)
                    if (p.Bands != null && p.Bands.Length > k)
                        Line(BandColumns[k], p.Bands[k], r.SplDbByBand[k]);
            }
            string dir = Path.Combine(outDir, run.Spec.Name);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "measured_vs_predicted.csv"), csv.ToString());
        }

        static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    }
}
