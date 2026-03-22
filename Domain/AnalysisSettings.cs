namespace SoundCalcs.Domain
{
    /// <summary>
    /// How the analysis scope is defined.
    /// </summary>
    public enum AnalysisScopeType
    {
        SelectedRooms,
        ActiveViewCropRegion,
        EntireModel
    }

    /// <summary>
    /// Grid and analysis parameters set by the user.
    /// </summary>
    public class AnalysisSettings
    {
        /// <summary>
        /// Distance between receiver grid points in meters.
        /// </summary>
        public double GridSpacingM { get; set; } = 1.0;

        /// <summary>
        /// Elevation of receiver grid above floor level in meters.
        /// Typical ear height for seated (1.2m) or standing (1.5m).
        /// </summary>
        public double ReceiverHeightM { get; set; } = 1.2;

        /// <summary>
        /// Inset distance from room boundaries in meters.
        /// Avoids placing receivers directly on walls.
        /// </summary>
        public double BoundaryOffsetM { get; set; } = 0.3;

        /// <summary>
        /// Scope for analysis.
        /// </summary>
        public AnalysisScopeType Scope { get; set; } = AnalysisScopeType.SelectedRooms;

        /// <summary>
        /// Revit category (BuiltInCategory name) to collect speakers from.
        /// </summary>
        public string SpeakerCategoryName { get; set; } = "OST_DataDevices";

        /// <summary>
        /// When true, receiver points with SPL below MinSplThresholdDb are not rendered.
        /// </summary>
        public bool UseMinSplThreshold { get; set; }

        /// <summary>
        /// Minimum SPL (dB) to display. Points below this value are transparent.
        /// </summary>
        public double MinSplThresholdDb { get; set; } = 65.0;

        /// <summary>
        /// Broadband background noise in dB SPL (kept for backward compatibility).
        /// </summary>
        public double BackgroundNoiseDb { get; set; } = 35.0;

        /// <summary>
        /// Per-octave-band RT60 reverberation time in seconds (7 elements, 125 Hz – 8 kHz).
        /// </summary>
        public double[] RT60ByBand { get; set; } = (double[])OctaveBands.DefaultRT60.Clone();

        /// <summary>
        /// Per-octave-band background noise level in dB SPL (7 elements, 125 Hz – 8 kHz).
        /// </summary>
        public double[] BackgroundNoiseByBand { get; set; } = (double[])OctaveBands.DefaultBackgroundNoise.Clone();
    }
}
