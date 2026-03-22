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
        /// Sound Pressure Level in dB at this receiver.
        /// </summary>
        public double SplDb { get; set; }

        /// <summary>
        /// Placeholder for future STI value (0.0 - 1.0).
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
        /// True if the job was canceled before completion.
        /// </summary>
        public bool WasCanceled { get; set; }
    }
}
