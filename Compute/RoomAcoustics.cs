using System;
using System.Collections.Generic;
using System.Linq;
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
        /// Area-weighted mean absorption per band. The floor and the ceiling each cover the
        /// floor area; the remaining wall area is split between wall types in proportion to
        /// their line length. With no wall types the walls use <paramref name="defaultWallAbsorption"/>.
        /// </summary>
        public static double[] AverageAbsorption(
            double floorAreaM2, double totalSurfaceAreaM2,
            IEnumerable<(double LengthM, double[] Absorption)> wallMix,
            double[] floorAbsorption, double[] ceilingAbsorption, double[] defaultWallAbsorption)
        {
            if (totalSurfaceAreaM2 <= 0) return (double[])defaultWallAbsorption.Clone();

            var mix = wallMix.Where(w => w.LengthM > 0 && w.Absorption != null).ToList();
            double totalLength = mix.Sum(w => w.LengthM);
            double horizontal = Math.Min(floorAreaM2, totalSurfaceAreaM2 / 2);
            double walls = totalSurfaceAreaM2 - 2 * horizontal;

            var result = new double[OctaveBands.Count];
            for (int k = 0; k < OctaveBands.Count; k++)
            {
                double wallAlpha = totalLength > 0
                    ? mix.Sum(w => w.LengthM * w.Absorption[k]) / totalLength
                    : defaultWallAbsorption[k];
                result[k] = (horizontal * (floorAbsorption[k] + ceilingAbsorption[k]) + walls * wallAlpha)
                          / totalSurfaceAreaM2;
            }
            return result;
        }

        /// <summary>
        /// Sabine absorption area of one occupant (m²) per octave band, 125 Hz – 8 kHz.
        /// Typical adult, seated or standing, lightly clothed.
        /// </summary>
        public static readonly double[] PersonAbsorptionM2 = { 0.15, 0.25, 0.40, 0.45, 0.50, 0.50, 0.50 };

        /// <summary>
        /// Eyring-Norris RT60 per band from the actual room geometry:
        ///   V = floor area × ceiling height
        ///   surfaces: floor, ceiling, wall lines on the perimeter (one face), internal wall
        ///   lines (both faces), and the open part of the perimeter (α = 1: sound leaves)
        ///   T = 0.161·V / (−S·ln(1 − ᾱ) + N·A_person + 4·m·V)
        /// where m is the ISO 9613-1 air attenuation (Np/m, energy) for the given climate.
        /// </summary>
        /// <param name="wallLines">Every wall line group: total length and surface absorption.</param>
        /// <param name="perimeterCoveredM">Length of the perimeter backed by wall lines (enclosure).</param>
        public static double[] EstimateRt60FromGeometry(
            double floorAreaM2, double perimeterM, double ceilingHeightM,
            IEnumerable<(double LengthM, double[] Absorption)> wallLines, double perimeterCoveredM,
            double[] floorAbsorption, double[] ceilingAbsorption,
            int occupants, double temperatureC, double relativeHumidityPct)
        {
            if (floorAreaM2 <= 0 || ceilingHeightM <= 0)
                return (double[])OctaveBands.DefaultRT60.Clone();

            var lines = wallLines.Where(w => w.LengthM > 0 && w.Absorption != null).ToList();
            double totalLineLength = lines.Sum(w => w.LengthM);
            double onPerimeter = Math.Min(Math.Max(perimeterCoveredM, 0), Math.Min(totalLineLength, perimeterM));
            double internalLength = totalLineLength - onPerimeter;
            double openPerimeter = Math.Max(0, perimeterM - onPerimeter);

            double h = ceilingHeightM;
            double volume = floorAreaM2 * h;
            double lineArea = (onPerimeter + 2 * internalLength) * h;
            double openArea = openPerimeter * h;
            double surface = 2 * floorAreaM2 + lineArea + openArea;

            double[] airDbPerM = OctaveBands.ComputeAirAbsorption(temperatureC, relativeHumidityPct);
            var result = new double[OctaveBands.Count];
            for (int k = 0; k < OctaveBands.Count; k++)
            {
                double lineAlpha = totalLineLength > 0 ? lines.Sum(w => w.LengthM * w.Absorption[k]) / totalLineLength : 0;
                double alpha = (floorAreaM2 * floorAbsorption[k] + floorAreaM2 * ceilingAbsorption[k]
                              + lineArea * lineAlpha + openArea * 1.0) / surface;
                alpha = Math.Max(0.001, Math.Min(0.999, alpha));

                double airM = airDbPerM[k] / (10 * Math.Log10(Math.E));   // dB/m → Np/m (energy)
                double denom = -surface * Math.Log(1 - alpha)
                             + Math.Max(0, occupants) * PersonAbsorptionM2[k]
                             + 4 * airM * volume;
                result[k] = Math.Round(Math.Max(0.05, Math.Min(30.0, 0.161 * volume / denom)), 2);
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
