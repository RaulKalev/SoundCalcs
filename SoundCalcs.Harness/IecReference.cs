using System;

namespace SoundCalcs.Harness
{
    /// <summary>
    /// Independent, textbook implementation of the indirect STI method of
    /// IEC 60268-16:2011 (rev. 4), used only as a yard-stick for the plugin's
    /// STICalculator. Level-dependent auditory masking and the absolute speech
    /// reception threshold are omitted, so compare with flat spectra only.
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

        public static readonly double[] ModulationFrequencies =
        {
            0.63, 0.80, 1.00, 1.25, 1.60, 2.00, 2.50,
            3.15, 4.00, 5.00, 6.30, 8.00, 10.0, 12.5
        };

        /// <param name="snrDb">Per-band signal-to-noise ratio (use +∞ for noise-free).</param>
        /// <param name="t60">Per-band reverberation time in seconds.</param>
        public static double Sti(double[] snrDb, double[] t60)
        {
            var mti = new double[7];
            for (int k = 0; k < 7; k++)
            {
                double sum = 0;
                foreach (double f in ModulationFrequencies)
                {
                    double a = 2 * Math.PI * f * t60[k] / 13.8;
                    double m = 1.0 / Math.Sqrt(1 + a * a);
                    if (!double.IsPositiveInfinity(snrDb[k]))
                        m *= 1.0 / (1 + Math.Pow(10, -snrDb[k] / 10));
                    double snrEff = m >= 1 ? 15 : 10 * Math.Log10(m / (1 - m));
                    snrEff = Math.Max(-15, Math.Min(15, snrEff));
                    sum += (snrEff + 15) / 30;
                }
                mti[k] = sum / ModulationFrequencies.Length;
            }

            double sti = 0;
            for (int k = 0; k < 7; k++) sti += MaleAlpha[k] * mti[k];
            for (int k = 0; k < 6; k++) sti -= MaleBeta[k] * Math.Sqrt(mti[k] * mti[k + 1]);
            return Math.Max(0, Math.Min(1, sti));
        }

        /// <summary>
        /// STI for a receiver whose squared impulse response is an early part E (arriving
        /// as a burst), a late exponential tail of energy L with time constant T60/13.8,
        /// and steady noise N (all linear, per band). This is Schroeder's MTF of that
        /// impulse response, so the reverberant tail is counted exactly once.
        /// </summary>
        public static double StiFromEnergies(double[] early, double[] late, double[] noise, double[] t60)
        {
            var mti = new double[7];
            for (int k = 0; k < 7; k++)
            {
                double e = early[k], l = late[k], total = e + l;
                double tau = t60[k] / 13.8;
                double sum = 0;
                foreach (double f in ModulationFrequencies)
                {
                    double wt = 2 * Math.PI * f * tau;
                    double den = 1 + wt * wt;
                    double re = e + l / den, im = l * wt / den;
                    double m = total > 0 ? Math.Sqrt(re * re + im * im) / total : 0;
                    m *= total > 0 ? 1.0 / (1 + noise[k] / total) : 0;
                    double snrEff = m <= 0 ? -15 : m >= 1 ? 15 : 10 * Math.Log10(m / (1 - m));
                    snrEff = Math.Max(-15, Math.Min(15, snrEff));
                    sum += (snrEff + 15) / 30;
                }
                mti[k] = sum / ModulationFrequencies.Length;
            }

            double sti = 0;
            for (int k = 0; k < 7; k++) sti += MaleAlpha[k] * mti[k];
            for (int k = 0; k < 6; k++) sti -= MaleBeta[k] * Math.Sqrt(mti[k] * mti[k + 1]);
            return Math.Max(0, Math.Min(1, sti));
        }

        public static double[] Fill(double v)
        {
            var a = new double[7];
            for (int i = 0; i < 7; i++) a[i] = v;
            return a;
        }
    }
}
