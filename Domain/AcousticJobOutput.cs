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

        /// <summary>
        /// The exact min/max value used for colour banding in the last render.
        /// Written back by FilledRegionRenderer so the UI legend always matches
        /// what was drawn, regardless of threshold or filtering.
        /// </summary>
        public double RenderedMinVal { get; set; }
        public double RenderedMaxVal { get; set; }

        /// <summary>
        /// True if the job was canceled before completion.
        /// </summary>
        public bool WasCanceled { get; set; }
    }
}
