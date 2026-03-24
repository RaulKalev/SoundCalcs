using System.Collections.Generic;

namespace SoundCalcs.Domain
{
    /// <summary>
    /// Source description for the compute engine. Revit-free.
    /// </summary>
    public class ComputeSource
    {
        public Vec3 Position { get; set; }
        public Vec3 FacingDirection { get; set; }

        /// <summary>
        /// Profile type + parameters for directivity lookup.
        /// </summary>
        public SpeakerProfileMapping Profile { get; set; }
    }

    /// <summary>
    /// A wall segment for transmission-loss calculations.
    /// Defined as a 2D line segment (plan view) with an STC rating.
    /// </summary>
    public class ComputeWall
    {
        public Vec2 Start { get; set; }
        public Vec2 End { get; set; }

        /// <summary>Sound Transmission Class rating in dB.</summary>
        public int StcRating { get; set; }

        /// <summary>Half the physical wall thickness in meters, for proximity blocking.</summary>
        public double HalfThicknessM { get; set; } = 0.10;

        /// <summary>
        /// Per-octave-band absorption coefficients for reflection calculations.
        /// Null = use global default (backward compatible).
        /// </summary>
        public double[] AbsorptionByBand { get; set; }
    }

    /// <summary>
    /// Environment settings for future use (background noise, humidity, etc.).
    /// </summary>
    public class EnvironmentSettings
    {
        /// <summary>
        /// Broadband background noise in dB SPL (kept for backward compatibility).
        /// </summary>
        public double BackgroundNoiseDb { get; set; } = 35.0;

        /// <summary>
        /// Air temperature in Celsius. Used for speed of sound: c = 331.3 + 0.606 × T.
        /// </summary>
        public double TemperatureC { get; set; } = 20.0;

        /// <summary>
        /// Relative humidity as a percentage (0–100). Used for ISO 9613-1 air absorption.
        /// At low humidity, high-frequency absorption is greater. Default 50 %.
        /// </summary>
        public double RelativeHumidityPct { get; set; } = 50.0;

        /// <summary>
        /// Per-octave-band RT60 reverberation time in seconds (7 elements, 125 Hz – 8 kHz).
        /// </summary>
        public double[] RT60ByBand { get; set; } = (double[])OctaveBands.DefaultRT60.Clone();

        /// <summary>
        /// Per-octave-band background noise level in dB SPL (7 elements, 125 Hz – 8 kHz).
        /// </summary>
        public double[] BackgroundNoiseByBand { get; set; } = (double[])OctaveBands.DefaultBackgroundNoise.Clone();

        /// <summary>
        /// Speech weighting type for STI calculation (Male or Female).
        /// </summary>
        public SpeechWeightType SpeechWeightType { get; set; } = SpeechWeightType.Male;
    }

    /// <summary>
    /// Complete input for an acoustic computation job.
    /// All positions in meters. No Revit types.
    /// </summary>
    public class AcousticJobInput
    {
        public string JobId { get; set; } = "";

        public List<ComputeSource> Sources { get; set; } = new List<ComputeSource>();
        public List<Polygon> Surfaces { get; set; } = new List<Polygon>();
        public List<ReceiverPoint> Receivers { get; set; } = new List<ReceiverPoint>();
        
        /// <summary>
        /// The room polygons used for this analysis. 
        /// Passed through to output for visualization purposes (creating analysis surfaces).
        /// </summary>
        public List<RoomPolygon> Rooms { get; set; } = new List<RoomPolygon>();

        /// <summary>
        /// Wall segments with STC ratings for transmission-loss calculation.
        /// </summary>
        public List<ComputeWall> Walls { get; set; } = new List<ComputeWall>();

        public EnvironmentSettings Environment { get; set; } = new EnvironmentSettings();

        /// <summary>
        /// Calculation fidelity: Draft (fast) or Full (accurate).
        /// </summary>
        public CalculationQuality Quality { get; set; } = CalculationQuality.Full;
    }
}
