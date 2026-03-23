using System;
using System.Collections.Generic;

namespace SoundCalcs.Domain
{
    /// <summary>
    /// Result for a single receiver point.
    /// </summary>
    public class ReceiverResult
    {
        public int ReceiverIndex { get; set; }
        public Vec3 Position { get; set; }

        /// <summary>
        /// Sound Pressure Level in dB at this receiver (broadband).
        /// Derived as energy sum of per-band values.
        /// </summary>
        public double SplDb { get; set; }

        /// <summary>
        /// Per-octave-band SPL in dB (7 elements, 125 Hz – 8 kHz).
        /// Indexed by <see cref="OctaveBands.CenterFrequencies"/>.
        /// </summary>
        public double[] SplDbByBand { get; set; }

        /// <summary>
        /// Speech Transmission Index (0.0 – 1.0), computed via IEC 60268-16 MTF.
        /// </summary>
        public double Sti { get; set; } = 0.0;

        /// <summary>
        /// A-weighted SPL in dBA (IEC 61672-1). Derived from per-band SPL with A-weighting applied.
        /// </summary>
        public double SplDbA { get; set; }

        /// <summary>
        /// Clarity C80 in dB: 10·log₁₀(early energy / late energy) across speech bands.
        /// Positive = more direct/early sound. Target for speech intelligibility: C80 > 0 dB.
        /// </summary>
        public double C80Db { get; set; }

        /// <summary>
        /// Definition D50: ratio of early (0–50 ms) energy to total energy, across speech bands.
        /// Range 0–1. Values above 0.5 indicate good clarity.
        /// </summary>
        public double D50 { get; set; }
    }

    /// <summary>
    /// Complete output from an acoustic computation job.
    /// </summary>
    public class AcousticJobOutput
    {
        public string JobId { get; set; } = "";

        /// <summary>
        /// UTC timestamp when the job completed.
        /// </summary>
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Duration of the computation in seconds.
        /// </summary>
        public double ComputeTimeSeconds { get; set; }

        /// <summary>
        /// Number of sources used.
        /// </summary>
        public int SourceCount { get; set; }

        /// <summary>
        /// Number of receivers computed.
        /// </summary>
        public int ReceiverCount { get; set; }

        /// <summary>
        /// Per-receiver results.
        /// </summary>
        public List<ReceiverResult> Results { get; set; } = new List<ReceiverResult>();

        /// <summary>
        /// The room polygons used for this analysis. 
        /// Passed through for visualization purposes.
        /// </summary>
        public List<RoomPolygon> Rooms { get; set; } = new List<RoomPolygon>();

        /// <summary>
        /// Min SPL across all results, for legend scaling.
        /// </summary>
        public double MinSplDb { get; set; }

        /// <summary>
        /// Max SPL across all results, for legend scaling.
        /// </summary>
        public double MaxSplDb { get; set; }

        /// <summary>
        /// Min STI across all results, for legend scaling.
        /// </summary>
        public double MinSti { get; set; }

        /// <summary>
        /// Max STI across all results, for legend scaling.
        /// </summary>
        public double MaxSti { get; set; }

        /// <summary>
        /// Per-octave-band minimum SPL across all results, for legend scaling.
        /// </summary>
        public double[] MinSplDbByBand { get; set; }

        /// <summary>
        /// Per-octave-band maximum SPL across all results, for legend scaling.
        /// </summary>
        public double[] MaxSplDbByBand { get; set; }

        /// <summary>Min A-weighted SPL across all results.</summary>
        public double MinSplDbA { get; set; }

        /// <summary>Max A-weighted SPL across all results.</summary>
        public double MaxSplDbA { get; set; }

        /// <summary>Minimum C80 across all results in dB.</summary>
        public double MinC80Db { get; set; }

        /// <summary>Maximum C80 across all results in dB.</summary>
        public double MaxC80Db { get; set; }

        /// <summary>Average C80 across all results in dB.</summary>
        public double AvgC80Db { get; set; }

        /// <summary>Average D50 across all results (0–1).</summary>
        public double AvgD50 { get; set; }

        /// <summary>
        /// The exact min/max value used for colour banding in the last render.
        /// Written back by FilledRegionRenderer so the UI legend always matches
        /// what was drawn, regardless of threshold or filtering.
        /// </summary>
        public double RenderedMinVal { get; set; }
        public double RenderedMaxVal { get; set; }

        /// <summary>
        /// The visualization mode string (e.g. "SPL", "STI", "SPL_125") that
        /// last wrote RenderedMinVal/MaxVal. Prevents stale SPL ranges from
        /// being used when the legend switches to STI and vice-versa.
        /// </summary>
        public string RenderedMode { get; set; } = "";

        /// <summary>
        /// True if the job was canceled before completion.
        /// </summary>
        public bool WasCanceled { get; set; }

        /// <summary>
        /// The calculation quality level used for this run.
        /// </summary>
        public CalculationQuality Quality { get; set; } = CalculationQuality.Full;
    }
}
