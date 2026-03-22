namespace SoundCalcs.Domain
{
    /// <summary>
    /// How the speaker directivity profile is sourced.
    /// </summary>
    public enum ProfileSourceType
    {
        SimpleOmni,
        SimpleConical,
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
    }
}
