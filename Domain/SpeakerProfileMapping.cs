namespace SoundCalcs.Domain
{
    /// <summary>
    /// How the speaker directivity profile is sourced.
    /// </summary>
    public enum ProfileSourceType
    {
        SimpleOmni,
        SimpleConical,
        WallMounted,
        GllFile
    }

    /// <summary>
    /// Mapping between a speaker FamilyType and its acoustic profile.
    /// Serialized as part of plugin settings.
    /// </summary>
    public class SpeakerProfileMapping
    {
        /// <summary>
        /// The type key this mapping belongs to: "FamilyName : TypeName".
        /// </summary>
        public string TypeKey { get; set; } = "";

        public ProfileSourceType ProfileSource { get; set; } = ProfileSourceType.SimpleOmni;

        // --- GLL ---
        /// <summary>
        /// Path to GLL file on disk. Only used when ProfileSource == GllFile.
        /// </summary>
        public string GllFilePath { get; set; } = "";

        // --- Simple Omni ---
        /// <summary>
        /// On-axis Sound Pressure Level in dB at 1 meter.
        /// </summary>
        public double OnAxisSplDb { get; set; } = 90.0;

        // --- Simple Conical ---
        /// <summary>
        /// Half-angle of the coverage cone in degrees.
        /// Only used when ProfileSource == SimpleConical.
        /// </summary>
        public double ConeHalfAngleDeg { get; set; } = 60.0;

        /// <summary>
        /// Attenuation in dB applied outside the cone boundary.
        /// Negative value (e.g., -12 dB). Only used when ProfileSource == SimpleConical.
        /// </summary>
        public double OffAxisAttenuationDb { get; set; } = -12.0;

        // --- Per-band spectrum shape ---
        /// <summary>
        /// Optional per-octave-band offset in dB relative to flat spectrum (7 elements, 125 Hz–8 kHz).
        /// Null or all-zeros = flat (equal energy split, current behavior).
        /// Example for speech: { -3, -1, 0, 0, -2, -5, -8 } emphasizing 500–1k Hz.
        /// </summary>
        public double[] SpectrumShapeByBand { get; set; }
    }
}
