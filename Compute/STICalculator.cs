using System;
using System.Collections.Generic;
using SoundCalcs.Domain;

namespace SoundCalcs.Compute
{
    /// <summary>
    /// Computes Speech Transmission Index (STI) per IEC 60268-16:2011 (indirect method)
    /// over 7 octave bands (125 Hz – 8 kHz) and 14 modulation frequencies (0.63 – 12.5 Hz).
    ///
    /// For each band k and modulation frequency F:
    ///   m_room(F)  = |Σ E_i·e^(−j2πF·t_i)| / Σ E_i          (MTF of the energy impulse response:
    ///                 direct sound, reflections and the reverberant tail, each counted once)
    ///   m          = m_room · S/(S+N)                         (background noise)
    ///   m'         = m · I_k/(I_k + I_am + I_rt)              (auditory masking, reception threshold)
    ///   SNR_eff    = 10·log10(m'/(1−m')) clipped to ±15 dB, TI = (SNR_eff + 15)/30
    ///   MTI_k      = mean TI over the 14 modulation frequencies
    ///   STI        = Σ α_k·MTI_k − Σ β_k·√(MTI_k·MTI_k+1)   (male or female weighting)
    /// </summary>
    public static class STICalculator
    {
        private const double MaxSnr = 15.0;

        /// <summary>
        /// Populate <see cref="ReceiverResult.Sti"/> on each result.
        /// </summary>
        /// <param name="results">Receiver results.</param>
        /// <param name="bandData">Per-receiver energies and modulation sums from SPLCalculator.
        /// When <see cref="ReceiverBandData.ModulationRe"/> is null the early energy is taken as
        /// arriving at once and the late energy as an exponential tail with the band's RT60.</param>
        /// <param name="backgroundNoiseByBand">Per-band ambient noise in dB SPL (7 elements).</param>
        /// <param name="rt60ByBand">Per-band RT60 in seconds (7 elements); only used without modulation data.</param>
        /// <param name="speechWeightType">Male or Female speech weighting (default Male).</param>
        public static void Calculate(
            List<ReceiverResult> results,
            List<ReceiverBandData> bandData,
            double[] backgroundNoiseByBand,
            double[] rt60ByBand,
            SpeechWeightType speechWeightType = SpeechWeightType.Male)
        {
            for (int i = 0; i < results.Count; i++)
            {
                results[i].Sti = Math.Round(
                    ComputeSti(bandData[i], backgroundNoiseByBand, rt60ByBand, speechWeightType), 3);
            }
        }

        /// <summary>STI for one receiver (unrounded).</summary>
        public static double ComputeSti(
            ReceiverBandData bd,
            double[] backgroundNoiseByBand,
            double[] rt60ByBand,
            SpeechWeightType speechWeightType = SpeechWeightType.Male)
        {
            int nb = OctaveBands.Count;
            double[] modFreqs = OctaveBands.ModulationFrequencies;
            double[] alpha = speechWeightType == SpeechWeightType.Female ? OctaveBands.FemaleAlpha : OctaveBands.MaleAlpha;
            double[] beta = speechWeightType == SpeechWeightType.Female ? OctaveBands.FemaleBeta : OctaveBands.MaleBeta;

            var signal = new double[nb];
            var noise = new double[nb];
            var intensity = new double[nb];
            for (int k = 0; k < nb; k++)
            {
                signal[k] = bd.EarlyLinearByBand[k] + bd.LateLinearByBand[k];
                noise[k] = Math.Pow(10.0, backgroundNoiseByBand[k] / 10.0);
                intensity[k] = signal[k] + noise[k];
            }

            double[] mti = new double[nb];
            for (int k = 0; k < nb; k++)
            {
                if (signal[k] <= 0) { mti[k] = 0; continue; }

                // Auditory masking by the band below, and the absolute reception threshold
                double masking = 0;
                if (k > 0 && intensity[k - 1] > 0)
                {
                    double lowerLevel = 10.0 * Math.Log10(intensity[k - 1]);
                    masking = intensity[k - 1] * Math.Pow(10.0, OctaveBands.AuditoryMaskingDb(lowerLevel) / 10.0);
                }
                double threshold = Math.Pow(10.0, OctaveBands.ReceptionThresholdDb[k] / 10.0);
                double perceptual = intensity[k] / (intensity[k] + masking + threshold);
                double noiseFactor = signal[k] / (signal[k] + noise[k]);

                double tiSum = 0;
                for (int f = 0; f < modFreqs.Length; f++)
                {
                    double mRoom = RoomMtf(bd, k, f, modFreqs[f], rt60ByBand, signal[k]);
                    double m = mRoom * noiseFactor * perceptual;

                    double snr = m <= 0 ? -MaxSnr
                        : m >= 1 ? MaxSnr
                        : 10.0 * Math.Log10(m / (1.0 - m));
                    snr = Math.Max(-MaxSnr, Math.Min(MaxSnr, snr));
                    tiSum += (snr + MaxSnr) / (2 * MaxSnr);
                }
                mti[k] = tiSum / modFreqs.Length;
            }

            double sti = 0;
            for (int k = 0; k < nb; k++) sti += alpha[k] * mti[k];
            for (int k = 0; k < nb - 1; k++) sti -= beta[k] * Math.Sqrt(mti[k] * mti[k + 1]);
            return Math.Max(0, Math.Min(1, sti));
        }

        /// <summary>Room (impulse-response) part of the MTF for band k at modulation frequency F.</summary>
        private static double RoomMtf(ReceiverBandData bd, int k, int f, double freq, double[] rt60ByBand, double total)
        {
            double re, im;
            if (bd.ModulationRe != null && bd.ModulationIm != null)
            {
                re = bd.ModulationRe[k][f];
                im = bd.ModulationIm[k][f];
            }
            else
            {
                // Early energy as a burst at t = 0, late energy as an exponential tail:
                // E + L/(1 + jωτ), τ = T60/13.82
                double tau = Math.Max(rt60ByBand[k], 0) / 13.82;
                double wt = 2.0 * Math.PI * freq * tau, den = 1 + wt * wt;
                double late = bd.LateLinearByBand[k];
                re = bd.EarlyLinearByBand[k] + late / den;
                im = -late * wt / den;
            }
            return Math.Min(1.0, Math.Sqrt(re * re + im * im) / total);
        }

        /// <summary>
        /// Compute Clarity (C80) and Definition (D50) for each receiver, over the
        /// speech-critical 500 Hz and 1 kHz bands.
        ///
        /// C80 (dB) = 10·log₁₀(E₀₋₈₀ms / E₈₀ms₋∞)  — higher = clearer
        /// D50      = E₀₋₅₀ms / E_total            — range 0–1, target &gt; 0.5
        /// </summary>
        public static void ComputeC80D50(
            List<ReceiverResult> results,
            List<ReceiverBandData> bandData)
        {
            int[] speechBands = { 2, 3 };

            for (int i = 0; i < results.Count; i++)
            {
                ReceiverBandData bd = bandData[i];
                double[] early80 = bd.Early80LinearByBand ?? bd.EarlyLinearByBand;
                double[] late80 = bd.Late80LinearByBand ?? bd.LateLinearByBand;

                double early50 = 0, late50 = 0, e80 = 0, l80 = 0;
                foreach (int k in speechBands)
                {
                    early50 += bd.EarlyLinearByBand[k];
                    late50 += bd.LateLinearByBand[k];
                    e80 += early80[k];
                    l80 += late80[k];
                }

                results[i].C80Db = (e80 > 0 && l80 > 0)
                    ? Math.Round(10.0 * Math.Log10(e80 / l80), 2)
                    : (e80 > 0 ? 15.0 : -15.0);  // clamp at extremes

                double total = early50 + late50;
                results[i].D50 = total > 0 ? Math.Round(early50 / total, 3) : 0.0;
            }
        }
    }
}
