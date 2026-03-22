using System;
using System.Collections.Generic;
using SoundCalcs.Domain;

namespace SoundCalcs.Compute
{
    /// <summary>
    /// Computes a simplified broadband Speech Transmission Index (STI) for each
    /// receiver point based on the signal-to-noise ratio.
    ///
    /// The simplified formula maps the effective SNR (SPL − background noise)
    /// to a 0–1 STI value using the IEC 60268-16 apparent SNR mapping:
    ///   STI = (clamp(SNR, −15, +15) + 15) / 30
    ///
    /// This provides a practical estimate suitable for early-stage design.
    /// For full accuracy, extend to per-octave-band STI (125 Hz – 8 kHz)
    /// with modulation transfer functions.
    /// </summary>
    public static class STICalculator
    {
        private const double MinSnrDb = -15.0;
        private const double MaxSnrDb = 15.0;
        private const double SnrRange = MaxSnrDb - MinSnrDb; // 30 dB

        /// <summary>
        /// Populate the <see cref="ReceiverResult.Sti"/> field on each result
        /// using the simplified broadband SNR-to-STI mapping.
        /// </summary>
        /// <param name="results">Receiver results with SplDb already computed.</param>
        /// <param name="backgroundNoiseDb">Background noise level in dB SPL.</param>
        public static void Calculate(List<ReceiverResult> results, double backgroundNoiseDb)
        {
            for (int i = 0; i < results.Count; i++)
            {
                double snr = results[i].SplDb - backgroundNoiseDb;
                double clamped = Math.Max(MinSnrDb, Math.Min(MaxSnrDb, snr));
                results[i].Sti = Math.Round((clamped - MinSnrDb) / SnrRange, 3);
            }
        }
    }
}
