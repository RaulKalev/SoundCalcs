namespace SoundCalcs.Domain
{
    /// <summary>
    /// Central constants for 7-octave-band acoustic analysis (125 Hz – 8 kHz).
    /// All arrays are indexed 0–6 corresponding to the seven standard octave bands.
    /// </summary>
    public static class OctaveBands
    {
        /// <summary>Number of octave bands.</summary>
        public const int Count = 7;

        /// <summary>Center frequencies in Hz.</summary>
        public static readonly double[] CenterFrequencies =
            { 125, 250, 500, 1000, 2000, 4000, 8000 };

        /// <summary>Short display labels for UI headers.</summary>
        public static readonly string[] Labels =
            { "125", "250", "500", "1k", "2k", "4k", "8k" };

        /// <summary>
        /// IEC 60268-16 modulation frequencies in Hz (14 values per octave band).
        /// </summary>
        public static readonly double[] ModulationFrequencies =
        {
            0.63, 0.80, 1.00, 1.25, 1.60, 2.00, 2.50,
            3.15, 4.00, 5.00, 6.30, 8.00, 10.0, 12.5
        };

        /// <summary>
        /// IEC 60268-16 male-speech octave-band weighting factors (α).
        /// Sum ≈ 1.0. Table 1 of IEC 60268-16:2011.
        /// </summary>
        public static readonly double[] SpeechWeights =
            { 0.085, 0.127, 0.230, 0.233, 0.173, 0.072, 0.080 };

        /// <summary>
        /// Approximate STC-to-per-band transmission loss offsets in dB.
        /// TL for band k ≈ max(0, fieldCorrectedSTC + StcBandOffsets[k]).
        /// Based on mass-law frequency dependence (~6 dB/octave above reference).
        /// </summary>
        public static readonly double[] StcBandOffsets =
            { -16, -8, -3, 0, 3, 6, 9 };

        /// <summary>
        /// Air absorption coefficients in dB/m at 20 °C, 50 % RH (ISO 9613-1).
        /// </summary>
        public static readonly double[] AirAbsorption =
            { 0.001, 0.003, 0.006, 0.012, 0.026, 0.065, 0.175 };

        /// <summary>
        /// Default RT60 values in seconds (typical office / classroom).
        /// </summary>
        public static readonly double[] DefaultRT60 =
            { 0.8, 0.7, 0.6, 0.5, 0.5, 0.4, 0.4 };

        /// <summary>
        /// Default per-band background noise levels in dB SPL (NC-30 approximation).
        /// </summary>
        public static readonly double[] DefaultBackgroundNoise =
            { 52, 44, 38, 34, 30, 27, 25 };

        /// <summary>
        /// Offset to distribute a broadband SPL equally across 7 bands:
        /// perBandSpl = broadbandSpl + BroadbandToBandOffset.
        /// Equal energy split: -10*log10(7) ≈ -8.45 dB.
        /// </summary>
        public const double BroadbandToBandOffset = -8.4510;
    }
}
