using System;

namespace SoundCalcs.Harness
{
    /// <summary>
    /// Independent, textbook implementation of the indirect STI method of
    /// IEC 60268-16:2011 (rev. 4), used only as a yard-stick for the plugin's
    /// STICalculator. Includes level-dependent auditory masking and the absolute
    /// speech reception threshold (Annex A); tables are typed in here separately
    /// from OctaveBands on purpose.
    ///
    ///   m(F)  = 1/sqrt(1 + (2πF·T/13.8)²) · 1/(1 + 10^(−SNR/10))
    ///   SNReff = 10·log10(m / (1 − m)), clipped to ±15 dB
    ///   TI    = (SNReff + 15) / 30,  MTI_k = mean over the 14 F of TI
    ///   STI   = Σ α_k·MTI_k − Σ β_k·sqrt(MTI_k·MTI_k+1)
    /// </summary>
    public static class IecReference
    {
        // Male speech, IEC 60268-16:2011 Table A.1 (125 Hz .. 8 kHz)
        public static readonly double[] MaleAlpha = { 0.085, 0.127, 0.230, 0.233, 0.309, 0.224, 0.173 };
        public static readonly double[] MaleBeta = { 0.085, 0.078, 0.065, 0.011, 0.047, 0.095 };

        static readonly double[] ReceptionThreshold = { 46, 27, 12, 6.5, 7.5, 8, 12 };

        static double MaskingDb(double l) =>
            l < 63 ? 0.5 * l - 65 : l < 67 ? 1.8 * l - 146.9 : l < 100 ? 0.5 * l - 59.8 : -10;

        /// <summary>m' = m · I_k/(I_k + I_am + I_rt) with I = signal + noise intensities.</summary>
        static double Perceptual(double[] intensity, int k)
        {
            double am = k > 0 ? intensity[k - 1] * Math.Pow(10, MaskingDb(10 * Math.Log10(intensity[k - 1])) / 10) : 0;
            return intensity[k] / (intensity[k] + am + Math.Pow(10, ReceptionThreshold[k] / 10));
        }

        static double Ti(double m)
        {
            double snrEff = m <= 0 ? -15 : m >= 1 ? 15 : 10 * Math.Log10(m / (1 - m));
            return (Math.Max(-15, Math.Min(15, snrEff)) + 15) / 30;
        }

        static double Combine(double[] mti)
        {
            double sti = 0;
            for (int k = 0; k < 7; k++) sti += MaleAlpha[k] * mti[k];
            for (int k = 0; k < 6; k++) sti -= MaleBeta[k] * Math.Sqrt(mti[k] * mti[k + 1]);
            return Math.Max(0, Math.Min(1, sti));
        }

        public static readonly double[] ModulationFrequencies =
        {
            0.63, 0.80, 1.00, 1.25, 1.60, 2.00, 2.50,
            3.15, 4.00, 5.00, 6.30, 8.00, 10.0, 12.5
        };

        /// <param name="signalDb">Per-band speech level, dB SPL.</param>
        /// <param name="noiseDb">Per-band noise level, dB SPL (−∞ allowed).</param>
        /// <param name="t60">Per-band reverberation time; the whole signal is a diffuse tail when &gt; 0.</param>
        public static double Sti(double[] signalDb, double[] noiseDb, double[] t60)
        {
            var intensity = new double[7];
            for (int k = 0; k < 7; k++)
                intensity[k] = Math.Pow(10, signalDb[k] / 10) + Math.Pow(10, noiseDb[k] / 10);

            var mti = new double[7];
            for (int k = 0; k < 7; k++)
            {
                double snr = signalDb[k] - noiseDb[k];
                double sum = 0;
                foreach (double f in ModulationFrequencies)
                {
                    double a = 2 * Math.PI * f * t60[k] / 13.8;
                    double m = 1.0 / Math.Sqrt(1 + a * a) / (1 + Math.Pow(10, -snr / 10));
                    sum += Ti(m * Perceptual(intensity, k));
                }
                mti[k] = sum / ModulationFrequencies.Length;
            }
            return Combine(mti);
        }

        /// <summary>
        /// STI for a receiver whose squared impulse response is an early part E (arriving
        /// as a burst), a late exponential tail of energy L with time constant T60/13.8,
        /// and steady noise N (all linear, per band). This is Schroeder's MTF of that
        /// impulse response, so the reverberant tail is counted exactly once.
        /// </summary>
        public static double StiFromEnergies(double[] early, double[] late, double[] noise, double[] t60)
        {
            var intensity = new double[7];
            for (int k = 0; k < 7; k++) intensity[k] = early[k] + late[k] + noise[k];

            var mti = new double[7];
            for (int k = 0; k < 7; k++)
            {
                double e = early[k], l = late[k], total = e + l;
                if (total <= 0) continue;
                double tau = t60[k] / 13.8;
                double sum = 0;
                foreach (double f in ModulationFrequencies)
                {
                    double wt = 2 * Math.PI * f * tau;
                    double den = 1 + wt * wt;
                    double re = e + l / den, im = l * wt / den;
                    double m = Math.Sqrt(re * re + im * im) / total / (1 + noise[k] / total);
                    sum += Ti(m * Perceptual(intensity, k));
                }
                mti[k] = sum / ModulationFrequencies.Length;
            }
            return Combine(mti);
        }

        public static double[] Fill(double v)
        {
            var a = new double[7];
            for (int i = 0; i < 7; i++) a[i] = v;
            return a;
        }
    }
}
