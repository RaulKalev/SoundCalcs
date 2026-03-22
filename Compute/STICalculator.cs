using System;
using System.Collections.Generic;
using SoundCalcs.Domain;

namespace SoundCalcs.Compute
{
    /// <summary>
    /// Computes Speech Transmission Index (STI) per IEC 60268-16 using the
    /// Modulation Transfer Function (MTF) method across 7 octave bands
    /// (125 Hz – 8 kHz) and 14 modulation frequencies (0.63 – 12.5 Hz).
    ///
    /// Accounts for:
    ///   • Per-band signal-to-noise ratio (early source energy vs. background noise + late energy)
    ///   • Reverberation degradation via RT60 per octave band
    ///   • Speaker overlap timing (50 ms early/late threshold, classified by SPLCalculator)
    ///   • IEC auditory masking corrections (inter-band α/β factors)
    ///   • Male or female speech octave-band weighting
    /// </summary>
    public static class STICalculator
    {
        private const double MinApparentSnr = -15.0;
        private const double MaxApparentSnr = 15.0;
        private const double SnrRange = MaxApparentSnr - MinApparentSnr; // 30
        private const double TwoPi = 2.0 * Math.PI;

        // MTF clamping bounds to avoid log(0) or log(∞)
        private const double MtfMin = 0.0039;  // corresponds to SNR_app = -24 dB
        private const double MtfMax = 0.9961;  // corresponds to SNR_app = +24 dB

        /// <summary>
        /// Populate <see cref="ReceiverResult.Sti"/> on each result using the
        /// full IEC 60268-16 MTF-based calculation with auditory masking corrections.
        /// </summary>
        /// <param name="results">Receiver results with SplDb/SplDbByBand already computed.</param>
        /// <param name="bandData">Per-receiver early/late energy split from SPLCalculator.</param>
        /// <param name="backgroundNoiseByBand">Per-band ambient noise in dB SPL (7 elements).</param>
        /// <param name="rt60ByBand">Per-band RT60 in seconds (7 elements).</param>
        /// <param name="speechWeightType">Male or Female speech weighting (default Male).</param>
        public static void Calculate(
            List<ReceiverResult> results,
            List<ReceiverBandData> bandData,
            double[] backgroundNoiseByBand,
            double[] rt60ByBand,
            SpeechWeightType speechWeightType = SpeechWeightType.Male)
        {
            int numBands = OctaveBands.Count;
            double[] modFreqs = OctaveBands.ModulationFrequencies;

            // Select speech weighting based on type
            double[] weights;
            if (speechWeightType == SpeechWeightType.Female)
            {
                // Normalize female weights to sum to 1.0
                double sum = 0;
                for (int k = 0; k < OctaveBands.FemaleSpeechWeights.Length; k++)
                    sum += OctaveBands.FemaleSpeechWeights[k];
                weights = new double[numBands];
                for (int k = 0; k < numBands; k++)
                    weights[k] = sum > 0 ? OctaveBands.FemaleSpeechWeights[k] / sum : 0;
            }
            else
            {
                weights = OctaveBands.SpeechWeights;
            }

            double[] maskAlpha = OctaveBands.MaskingAlpha;
            double[] maskBeta = OctaveBands.MaskingBeta;

            for (int i = 0; i < results.Count; i++)
            {
                ReceiverBandData bd = bandData[i];
                double[] snrAppBands = new double[numBands];
                double[] earlyDbBands = new double[numBands];

                for (int k = 0; k < numBands; k++)
                {
                    // Signal = early source energy (linear)
                    double earlyLinear = bd.EarlyLinearByBand[k];
                    double earlyDb = earlyLinear > 0 ? 10.0 * Math.Log10(earlyLinear) : -100;
                    earlyDbBands[k] = earlyDb;

                    // Noise = background + late source energy (linear sum)
                    double bgLinear = Math.Pow(10.0, backgroundNoiseByBand[k] / 10.0);
                    double lateLinear = bd.LateLinearByBand[k];
                    double noiseLinear = bgLinear + lateLinear;
                    double noiseDb = noiseLinear > 0 ? 10.0 * Math.Log10(noiseLinear) : -100;

                    double snrDb = earlyDb - noiseDb;

                    // RT60 for this band
                    double t60 = rt60ByBand[k];

                    // Compute MTF for each of the 14 modulation frequencies
                    double mtfSum = 0;
                    for (int f = 0; f < modFreqs.Length; f++)
                    {
                        double F = modFreqs[f];

                        // Reverberation degradation
                        double rt_arg = TwoPi * F * t60 / 13.8;
                        double m_rt = 1.0 / Math.Sqrt(1.0 + rt_arg * rt_arg);

                        // Noise degradation
                        double m_noise = 1.0 / (1.0 + Math.Pow(10.0, -snrDb / 10.0));

                        // Combined modulation transfer
                        mtfSum += m_rt * m_noise;
                    }

                    double m_avg = mtfSum / modFreqs.Length;

                    // Clamp to avoid log singularities
                    m_avg = Math.Max(MtfMin, Math.Min(MtfMax, m_avg));

                    // Apparent SNR from averaged MTF
                    double snrApp = 10.0 * Math.Log10(m_avg / (1.0 - m_avg));
                    snrApp = Math.Max(MinApparentSnr, Math.Min(MaxApparentSnr, snrApp));

                    snrAppBands[k] = snrApp;
                }

                // Apply IEC 60268-16 auditory masking corrections
                double sti = 0.0;
                for (int k = 0; k < numBands; k++)
                {
                    double snrApp = snrAppBands[k];

                    if (k > 0)
                    {
                        // Inter-band masking: reduce apparent SNR when lower band is louder
                        double levelDiff = earlyDbBands[k - 1] - earlyDbBands[k] - maskBeta[k];
                        if (levelDiff > 0)
                        {
                            snrApp -= maskAlpha[k] * levelDiff;
                            snrApp = Math.Max(MinApparentSnr, snrApp);
                        }
                    }

                    // Transmission Index for this band
                    double ti = (snrApp - MinApparentSnr) / SnrRange;
                    sti += weights[k] * ti;
                }

                results[i].Sti = Math.Round(Math.Max(0, Math.Min(1, sti)), 3);
            }
        }
    }
}
