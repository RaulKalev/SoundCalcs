using System;
using System.Collections.Generic;
using System.Linq;
using SoundCalcs.Compute;
using SoundCalcs.Domain;
using SoundCalcs.UI.ViewModels;
using SoundCalcs.Visualization;

namespace SoundCalcs.Report
{
    /// <summary>Distribution of one result quantity (SPL, STI, ...) over a set of receivers.</summary>
    public class ModeStatistics
    {
        public VisualizationMode Mode { get; set; }
        public int Count { get; set; }

        /// <summary>Arithmetic mean of the values (dB values are averaged as numbers, as coverage reports do).</summary>
        public double Mean { get; set; }
        public double Min { get; set; }
        public double Max { get; set; }

        /// <summary>Max − min.</summary>
        public double Spread => Max - Min;

        public double P10 { get; set; }
        public double Median { get; set; }
        public double P90 { get; set; }

        /// <summary>Population standard deviation.</summary>
        public double StdDev { get; set; }
    }

    /// <summary>Per-room numbers of one calculated job: geometry, acoustics and result statistics.</summary>
    public class RoomStatistics
    {
        /// <summary>Index into the job's rooms; -1 for receivers that lie in no room.</summary>
        public int RoomIndex { get; set; }
        public string Name { get; set; } = "";

        public double AreaM2 { get; set; }
        public double CeilingHeightM { get; set; }
        public double VolumeM3 { get; set; }
        public double EnclosureRatio { get; set; } = 1.0;

        /// <summary>RT60 per band the calculation used in this room (its own estimate, else the job's).</summary>
        public double[] Rt60ByBand { get; set; }

        /// <summary>True when <see cref="Rt60ByBand"/> is the room's own estimate.</summary>
        public bool Rt60Estimated { get; set; }

        public int ReceiverCount { get; set; }

        /// <summary>Speakers whose position lies in this room.</summary>
        public int SpeakerCount { get; set; }

        /// <summary>A-weighted background noise of the job (dBA), for the signal-to-noise criterion.</summary>
        public double BackgroundDbA { get; set; }

        public Dictionary<VisualizationMode, ModeStatistics> Modes { get; set; } =
            new Dictionary<VisualizationMode, ModeStatistics>();

        /// <summary>Receivers per IEC 60268-16 STI category: Bad, Poor, Fair, Good, Excellent.</summary>
        public int[] StiCategoryCounts { get; set; } = new int[5];

        /// <summary>Indices into the job output's results of this room's receivers.</summary>
        public List<int> ResultIndices { get; set; } = new List<int>();

        public ModeStatistics this[VisualizationMode mode] =>
            Modes.TryGetValue(mode, out ModeStatistics s) ? s : null;
    }

    /// <summary>Computes <see cref="RoomStatistics"/> from a job's input and output. Revit-free.</summary>
    public static class RoomStatisticsCalculator
    {
        /// <summary>The quantities summarised for every room.</summary>
        public static readonly VisualizationMode[] SummaryModes =
        {
            VisualizationMode.SPL, VisualizationMode.SPL_A, VisualizationMode.STI, VisualizationMode.C80,
            VisualizationMode.SPL_125, VisualizationMode.SPL_250, VisualizationMode.SPL_500, VisualizationMode.SPL_1k,
            VisualizationMode.SPL_2k, VisualizationMode.SPL_4k, VisualizationMode.SPL_8k
        };

        /// <summary>STI category boundaries (IEC 60268-16): Bad &lt; 0.30 ≤ Poor &lt; 0.45 ≤ Fair &lt; 0.60 ≤ Good &lt; 0.75 ≤ Excellent.</summary>
        public static int StiCategory(double sti) =>
            sti < 0.30 ? 0 : sti < 0.45 ? 1 : sti < 0.60 ? 2 : sti < 0.75 ? 3 : 4;

        /// <summary>
        /// The room of a result: its recorded room index, else (results written before rooms were recorded) the
        /// first room that contains it, else -1.
        /// </summary>
        public static int RoomOf(ReceiverResult r, IList<RoomPolygon> rooms)
        {
            if (rooms == null || rooms.Count == 0) return -1;
            if (r.RoomIndex >= 0 && r.RoomIndex < rooms.Count) return r.RoomIndex;
            var p = new Vec2(r.Position.X, r.Position.Y);
            for (int i = 0; i < rooms.Count; i++)
                if (rooms[i].ContainsPoint(p)) return i;
            return -1;
        }

        /// <summary>A-weighted sum of a per-band level spectrum in dBA.</summary>
        public static double AWeightedDb(double[] bands)
        {
            if (bands == null || bands.Length == 0) return double.NaN;
            double sum = 0;
            for (int k = 0; k < Math.Min(bands.Length, OctaveBands.Count); k++)
                sum += Math.Pow(10.0, (bands[k] + OctaveBands.AWeightingDb[k]) / 10.0);
            return sum > 0 ? 10.0 * Math.Log10(sum) : double.NaN;
        }

        /// <summary>
        /// One entry per room that has receivers, in the job's room order, then one for receivers in no room
        /// (named <paramref name="outsideName"/>) if there are any.
        /// </summary>
        public static List<RoomStatistics> Compute(AcousticJobInput input, AcousticJobOutput output,
            string outsideName = "Outside rooms")
        {
            var rooms = output?.Rooms != null && output.Rooms.Count > 0 ? output.Rooms : input?.Rooms ?? new List<RoomPolygon>();
            var results = output?.Results ?? new List<ReceiverResult>();
            double[] jobRt = input?.Environment?.RT60ByBand ?? OctaveBands.DefaultRT60;
            double bgA = AWeightedDb(input?.Environment?.BackgroundNoiseByBand ?? OctaveBands.DefaultBackgroundNoise);

            var byRoom = new Dictionary<int, List<int>>();
            for (int i = 0; i < results.Count; i++)
            {
                int ri = RoomOf(results[i], rooms);
                if (!byRoom.TryGetValue(ri, out List<int> list)) byRoom[ri] = list = new List<int>();
                list.Add(i);
            }

            var stats = new List<RoomStatistics>();
            foreach (int ri in byRoom.Keys.OrderBy(k => k < 0 ? int.MaxValue : k))
            {
                List<int> idx = byRoom[ri];
                RoomPolygon room = ri >= 0 ? rooms[ri] : null;
                double h = room != null && room.CeilingHeightM > 0.5 ? room.CeilingHeightM : SPLCalculator.DefaultCeilingHeightM;
                var s = new RoomStatistics
                {
                    RoomIndex = ri,
                    Name = room?.Name ?? outsideName,
                    AreaM2 = room?.EffectiveAreaM2 ?? 0,
                    CeilingHeightM = h,
                    VolumeM3 = (room?.EffectiveAreaM2 ?? 0) * h,
                    EnclosureRatio = room?.EnclosureRatio ?? 0,
                    Rt60ByBand = (double[])(room?.RT60ByBand ?? jobRt).Clone(),
                    Rt60Estimated = room?.RT60ByBand != null,
                    ReceiverCount = idx.Count,
                    BackgroundDbA = bgA,
                    ResultIndices = idx
                };
                if (room != null && input?.Sources != null)
                    s.SpeakerCount = input.Sources.Count(src => room.ContainsSpeakerPosition(src.Position));

                foreach (VisualizationMode mode in SummaryModes)
                {
                    if (HeatmapMath.GetOctaveBandIndex(mode) >= 0 && idx.Any(i => results[i].SplDbByBand == null)) continue;
                    s.Modes[mode] = ForValues(mode, idx.Select(i => HeatmapMath.GetValue(results[i], mode)));
                }
                foreach (int i in idx) s.StiCategoryCounts[StiCategory(results[i].Sti)]++;
                stats.Add(s);
            }
            return stats;
        }

        /// <summary>Statistics of a set of values; Count = 0 (and NaN numbers) when it is empty.</summary>
        public static ModeStatistics ForValues(VisualizationMode mode, IEnumerable<double> values)
        {
            double[] v = values.Where(x => !double.IsNaN(x) && !double.IsInfinity(x)).ToArray();
            Array.Sort(v);
            var s = new ModeStatistics { Mode = mode, Count = v.Length };
            if (v.Length == 0)
            {
                s.Mean = s.Min = s.Max = s.P10 = s.Median = s.P90 = s.StdDev = double.NaN;
                return s;
            }
            s.Min = v[0];
            s.Max = v[v.Length - 1];
            s.Mean = v.Average();
            double mean = s.Mean;
            s.StdDev = Math.Sqrt(v.Sum(x => (x - mean) * (x - mean)) / v.Length);
            s.P10 = Percentile(v, 0.10);
            s.Median = Percentile(v, 0.50);
            s.P90 = Percentile(v, 0.90);
            return s;
        }

        /// <summary>Linear-interpolated percentile of sorted values (p in 0..1).</summary>
        public static double Percentile(double[] sorted, double p)
        {
            if (sorted.Length == 0) return double.NaN;
            if (sorted.Length == 1) return sorted[0];
            double pos = p * (sorted.Length - 1);
            int lo = (int)Math.Floor(pos);
            int hi = Math.Min(sorted.Length - 1, lo + 1);
            return sorted[lo] + (sorted[hi] - sorted[lo]) * (pos - lo);
        }

        /// <summary>Share (0..1) of the room's receivers whose value is at least <paramref name="threshold"/>.</summary>
        public static double ShareAtLeast(RoomStatistics room, AcousticJobOutput output, VisualizationMode mode, double threshold)
        {
            if (room.ResultIndices.Count == 0) return double.NaN;
            int n = room.ResultIndices.Count(i => HeatmapMath.GetValue(output.Results[i], mode) >= threshold - 1e-9);
            return n / (double)room.ResultIndices.Count;
        }
    }
}
