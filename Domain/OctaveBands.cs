using System.Collections.Generic;

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
        /// IEC 60268-16 female-speech octave-band weighting factors.
        /// Table 2 of IEC 60268-16:2011.
        /// </summary>
        public static readonly double[] FemaleSpeechWeights =
            { 0.000, 0.117, 0.223, 0.216, 0.328, 0.250, 0.000 }; // sum ≈ 1.134 → internally normalized

        /// <summary>
        /// IEC 60268-16 auditory masking correction coefficients (α).
        /// Applied as: TI'_k = TI_k - α_k × abs(I_{k-1})
        /// where I_{k-1} is the level-dependent factor from the adjacent lower band.
        /// Index 0 (125 Hz band) has no lower neighbor so α_0 = 0.
        /// </summary>
        public static readonly double[] MaskingAlpha =
            { 0.0, 0.45, 0.45, 0.45, 0.45, 0.45, 0.45 };

        /// <summary>
        /// IEC 60268-16 auditory masking β coefficients.
        /// I_{k-1} = (L_{k-1} - L_k - β_k)  (clamped ≥ 0 before use in α correction).
        /// </summary>
        public static readonly double[] MaskingBeta =
            { 0.0, 0.45, 0.45, 0.45, 0.45, 0.45, 0.45 };

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

        // --- Wall absorption presets (per-octave-band α coefficients) ---
        // Values from published acoustic data tables.

        /// <summary>
        /// Per-octave-band absorption coefficients for common wall surface materials.
        /// Key = <see cref="WallAbsorptionPreset"/>, Value = double[7] (125–8k Hz).
        /// </summary>
        public static readonly Dictionary<WallAbsorptionPreset, double[]> AbsorptionPresets =
            new Dictionary<WallAbsorptionPreset, double[]>
            {
                { WallAbsorptionPreset.Drywall,       new[] { 0.29, 0.10, 0.05, 0.04, 0.07, 0.09, 0.09 } },
                { WallAbsorptionPreset.Concrete,       new[] { 0.01, 0.01, 0.02, 0.02, 0.02, 0.03, 0.03 } },
                { WallAbsorptionPreset.Brick,          new[] { 0.03, 0.03, 0.03, 0.04, 0.05, 0.07, 0.07 } },
                { WallAbsorptionPreset.Glass,          new[] { 0.35, 0.25, 0.18, 0.12, 0.07, 0.05, 0.05 } },
                { WallAbsorptionPreset.AcousticPanel,  new[] { 0.20, 0.50, 0.80, 0.90, 0.85, 0.80, 0.75 } },
                { WallAbsorptionPreset.Carpet,         new[] { 0.02, 0.06, 0.14, 0.37, 0.60, 0.65, 0.65 } },
                { WallAbsorptionPreset.AcousticTile,   new[] { 0.50, 0.70, 0.60, 0.70, 0.70, 0.50, 0.50 } },
                { WallAbsorptionPreset.Custom,         new[] { 0.10, 0.10, 0.10, 0.10, 0.10, 0.10, 0.10 } },
            };

        /// <summary>
        /// Compute ISO 9613-1 air absorption coefficients for given temperature and humidity.
        /// Returns dB/m for each of the 7 octave bands.
        /// </summary>
        public static double[] ComputeAirAbsorption(double temperatureC, double relativeHumidityPct = 50.0)
        {
            double T = temperatureC + 273.15; // Kelvin
            double T0 = 293.15;
            double Trel = T / T0;
            double h = relativeHumidityPct;

            // Saturation vapor pressure ratio (ISO 9613-1 Eq. 3)
            double psat = System.Math.Pow(10.0, -6.8346 * System.Math.Pow(273.16 / T, 1.261) + 4.6151);
            double hAbs = h * psat; // molar concentration of water vapor

            // Relaxation frequencies (ISO 9613-1 Eq. 4–5)
            double frO = 24.0 + 4.04e4 * hAbs * (0.02 + hAbs) / (0.391 + hAbs);
            double frN = System.Math.Pow(Trel, -0.5) * (9.0 + 280.0 * hAbs
                * System.Math.Exp(-4.170 * (System.Math.Pow(Trel, -1.0 / 3.0) - 1.0)));

            var result = new double[Count];
            for (int k = 0; k < Count; k++)
            {
                double f = CenterFrequencies[k];
                double f2 = f * f;

                // ISO 9613-1 Eq. 1: absorption in Np/m, converted to dB/m
                double alpha = 8.686 * f2 * (
                    1.84e-11 * System.Math.Pow(Trel, 0.5)
                    + System.Math.Pow(Trel, -2.5) * (
                        0.01275 * System.Math.Exp(-2239.1 / T) / (frO + f2 / frO)
                      + 0.1068  * System.Math.Exp(-3352.0 / T) / (frN + f2 / frN)
                    )
                );

                result[k] = System.Math.Max(alpha, 0.0001);
            }
            return result;
        }
    }

    /// <summary>
    /// Predefined wall surface absorption presets.
    /// </summary>
    public enum WallAbsorptionPreset
    {
        Drywall,
        Concrete,
        Brick,
        Glass,
        AcousticPanel,
        Carpet,
        AcousticTile,
        Custom
    }
}
