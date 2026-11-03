using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SoundCalcs.Domain;
using SoundCalcs.UI.ViewModels;
using SoundCalcs.Visualization;

namespace SoundCalcs.Report
{
    public enum RoomOutcome
    {
        Pass,
        Fail,
        NotAssessed
    }

    /// <summary>One criterion applied to one room.</summary>
    public class CriterionResult
    {
        public string Name { get; set; } = "";
        public string Target { get; set; } = "";
        public string Value { get; set; } = "";
        public bool Passed { get; set; }
    }

    public class RoomAssessment
    {
        public RoomStatistics Room { get; set; }
        public RoomOutcome Outcome { get; set; } = RoomOutcome.NotAssessed;
        public List<CriterionResult> Results { get; set; } = new List<CriterionResult>();
    }

    /// <summary>Judges rooms against <see cref="ReportCriteria"/>. Revit-free.</summary>
    public static class CriteriaEvaluator
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static RoomAssessment Assess(RoomStatistics room, AcousticJobOutput output, ReportCriteria c, ReportText t)
        {
            var a = new RoomAssessment { Room = room };
            if (room.ReceiverCount == 0 || (c.SkipRoomsWithoutSpeakers && room.SpeakerCount == 0))
                return a;

            // Point criteria use the value that the required share of points reaches (the minimum at 100 %)
            double coverage = Math.Max(0.5, Math.Min(1.0, c.PointCoverage));
            double q = 1.0 - coverage;
            double[] Sorted(VisualizationMode m)
            {
                double[] v = room.ResultIndices.Select(i => HeatmapMath.GetValue(output.Results[i], m)).ToArray();
                Array.Sort(v);
                return v;
            }
            string covNote = coverage < 1.0 ? string.Format(Inv, " (P{0:0})", q * 100) : "";

            if (c.CheckStiMin)
            {
                double v = RoomStatisticsCalculator.Percentile(Sorted(VisualizationMode.STI), q);
                a.Results.Add(new CriterionResult
                {
                    Name = t.CritStiMin, Target = "≥ " + c.StiMin.ToString("0.00", Inv),
                    Value = v.ToString("0.00", Inv) + covNote, Passed = v >= c.StiMin - 1e-9
                });
            }
            if (c.CheckStiMean)
            {
                double v = room[VisualizationMode.STI].Mean;
                a.Results.Add(new CriterionResult
                {
                    Name = t.CritStiMean, Target = "≥ " + c.StiMeanMin.ToString("0.00", Inv),
                    Value = v.ToString("0.00", Inv), Passed = v >= c.StiMeanMin - 1e-9
                });
            }
            if (c.CheckSplSpread)
            {
                double[] spl = Sorted(VisualizationMode.SPL);
                double lo = RoomStatisticsCalculator.Percentile(spl, q / 2), hi = RoomStatisticsCalculator.Percentile(spl, 1 - q / 2);
                double v = hi - lo;
                a.Results.Add(new CriterionResult
                {
                    Name = t.CritSplSpread, Target = "≤ " + c.SplSpreadMaxDb.ToString("0.#", Inv) + " dB",
                    Value = v.ToString("0.0", Inv) + " dB", Passed = v <= c.SplSpreadMaxDb + 1e-9
                });
            }
            if (c.CheckSplMinA || c.CheckSignalToNoise)
            {
                double v = RoomStatisticsCalculator.Percentile(Sorted(VisualizationMode.SPL_A), q);
                if (c.CheckSplMinA)
                    a.Results.Add(new CriterionResult
                    {
                        Name = t.CritSplMinA, Target = "≥ " + c.SplMinDbA.ToString("0.#", Inv) + " dBA",
                        Value = v.ToString("0.0", Inv) + " dBA" + covNote, Passed = v >= c.SplMinDbA - 1e-9
                    });
                if (c.CheckSignalToNoise && !double.IsNaN(room.BackgroundDbA))
                {
                    double snr = v - room.BackgroundDbA;
                    a.Results.Add(new CriterionResult
                    {
                        Name = t.CritSnr, Target = "≥ " + c.SignalToNoiseMinDb.ToString("0.#", Inv) + " dB",
                        Value = snr.ToString("0.0", Inv) + " dB" + covNote, Passed = snr >= c.SignalToNoiseMinDb - 1e-9
                    });
                }
            }

            a.Outcome = a.Results.Count == 0 ? RoomOutcome.NotAssessed
                : a.Results.All(r => r.Passed) ? RoomOutcome.Pass : RoomOutcome.Fail;
            return a;
        }

        public static string OutcomeText(RoomOutcome o, ReportText t) =>
            o == RoomOutcome.Pass ? t.Pass : o == RoomOutcome.Fail ? t.Fail : t.NotAssessed;

        public static CellStatus OutcomeStatus(RoomOutcome o) =>
            o == RoomOutcome.Pass ? CellStatus.Pass : o == RoomOutcome.Fail ? CellStatus.Fail : CellStatus.Muted;
    }
}
