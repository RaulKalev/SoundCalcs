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

        /// <summary>
        /// Global wall surface absorption preset. Applied to all walls (detail lines have no material).
        /// Default = Drywall to match previous 0.10 single-value behavior.
        /// </summary>
        public WallAbsorptionPreset WallAbsorptionPreset { get; set; } = WallAbsorptionPreset.Drywall;

        /// <summary>
        /// Custom per-band absorption coefficients (only used when preset = Custom).
        /// </summary>
        public double[] CustomAbsorptionByBand { get; set; } = new double[] { 0.10, 0.10, 0.10, 0.10, 0.10, 0.10, 0.10 };

        /// <summary>
        /// Speech weighting type for STI calculation.
        /// </summary>
        public SpeechWeightType SpeechWeightType { get; set; } = SpeechWeightType.Male;

        /// <summary>
        /// Default wall height in meters (assigned to detail-line walls for 3D blocking).
        /// Set to 0 to keep current 2D-only blocking behavior.
        /// When > 0, walls are treated as surfaces from floor to floor + WallHeightM.
        /// </summary>
        public double WallHeightM { get; set; } = 0.0;

        /// <summary>
        /// Name of the Revit instance parameter that holds the A/B line designation (e.g. "A" or "B").
        /// Leave empty to disable A/B line labelling.
        /// </summary>
        public string AbLineParameterName { get; set; } = "";
    }

    /// <summary>
    /// Speech weighting for STI (male vs. female voice spectrum).
    /// </summary>
    public enum SpeechWeightType
    {
        Male,
        Female
    }

    /// <summary>
    /// Calculation fidelity level. Draft is faster (skips second-order and
    /// ceiling/floor reflections); Full runs the complete acoustic model.
    /// </summary>
    public enum CalculationQuality
    {
        Draft,
        Full
    }

    /// <summary>
    /// Which speaker line (A, B, or both) to include in the calculation.
    /// </summary>
    public enum SpeakerLineFilterType
    {
        Both,
        ALine,
        BLine
    }
}
