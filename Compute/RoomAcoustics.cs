using System;
using SoundCalcs.Domain;

namespace SoundCalcs.Compute
{
    /// <summary>
    /// Room acoustics parameter estimation utilities.
    /// All methods are pure C# — no Revit references.
    /// </summary>
    public static class RoomAcoustics
    {
        /// <summary>
        /// Estimate RT60 per octave band using the Eyring-Norris formula.
        /// More accurate than Sabine when average absorption α > 0.2 (furnished rooms,
        /// offices, classrooms with acoustic treatment).
        ///
        /// Formula: T₆₀ = 0.161 · V / (−S · ln(1 − α_avg) + 4 · m · V)
        ///
        /// Where:
        ///   V = room volume in m³
        ///   S = total surface area in m²
        ///   α_avg = mean absorption coefficient for the band
        ///   m = air absorption coefficient in Np/m (ignored here; included for completeness)
        /// </summary>
        /// <param name="volumeM3">Room volume in cubic metres.</param>
        /// <param name="surfaceAreaM2">Total room surface area in square metres (floor + ceiling + walls).</param>
        /// <param name="avgAbsorptionByBand">Mean absorption coefficient per octave band (7 values, 125–8 kHz).</param>
        /// <returns>RT60 in seconds per octave band (7 values). Clamped to [0.05, 30] s.</returns>
        public static double[] EstimateEyringRt60(
            double volumeM3,
            double surfaceAreaM2,
            double[] avgAbsorptionByBand)
        {
            if (volumeM3 <= 0 || surfaceAreaM2 <= 0)
                return (double[])OctaveBands.DefaultRT60.Clone();

            var result = new double[OctaveBands.Count];
            for (int k = 0; k < OctaveBands.Count; k++)
            {
                double alpha = Math.Max(0.001, Math.Min(0.999, avgAbsorptionByBand[k]));
                // Eyring: -S · ln(1 - α)
                double eyringAbsorption = -surfaceAreaM2 * Math.Log(1.0 - alpha);
                double t60 = 0.161 * volumeM3 / eyringAbsorption;
                result[k] = Math.Round(Math.Max(0.05, Math.Min(30.0, t60)), 2);
            }
            return result;
        }

        /// <summary>
        /// Estimate RT60 per octave band using the classical Sabine formula.
        /// More accurate in large, lightly damped spaces (α &lt; 0.15).
        ///
        /// Formula: T₆₀ = 0.161 · V / (S · α_avg)
        /// </summary>
        public static double[] EstimateSabineRt60(
            double volumeM3,
            double surfaceAreaM2,
            double[] avgAbsorptionByBand)
        {
            if (volumeM3 <= 0 || surfaceAreaM2 <= 0)
                return (double[])OctaveBands.DefaultRT60.Clone();

            var result = new double[OctaveBands.Count];
            for (int k = 0; k < OctaveBands.Count; k++)
            {
                double alpha = Math.Max(0.001, avgAbsorptionByBand[k]);
                double t60 = 0.161 * volumeM3 / (surfaceAreaM2 * alpha);
                result[k] = Math.Round(Math.Max(0.05, Math.Min(30.0, t60)), 2);
            }
            return result;
        }

        /// <summary>
        /// Estimate total room surface area from a floor area and ceiling height.
        /// Assumes a box-shaped room: floor + ceiling + 4 walls.
        /// Perimeter is estimated as 4 × sqrt(floor area) (square room approximation).
        /// </summary>
        public static double EstimateSurfaceArea(double floorAreaM2, double ceilingHeightM)
        {
            if (floorAreaM2 <= 0 || ceilingHeightM <= 0) return 0;
            double side = Math.Sqrt(floorAreaM2); // square room approximation
            double perimeter = 4.0 * side;
            double wallArea = perimeter * ceilingHeightM;
            return floorAreaM2 * 2.0 + wallArea; // floor + ceiling + walls
        }
    }
}
