using System;
using System.Collections.Generic;
using Xunit;
using SoundCalcs.Compute;
using SoundCalcs.Domain;

namespace SoundCalcs.Tests
{
    /// <summary>
    /// Unit tests for <see cref="STICalculator"/> verifying IEC 60268-16 MTF-based
    /// Speech Transmission Index calculations.
    /// </summary>
    public class STICalculatorTests
    {
        // ---------------------------------------------------------------------------
        // Helpers
        // ---------------------------------------------------------------------------

        private static ReceiverResult MakeResult(int idx) => new ReceiverResult
        {
            ReceiverIndex = idx,
            Position = new Vec3(0, 0, 0),
            SplDb = 80.0,
            SplDbByBand = new double[OctaveBands.Count]
        };

        /// <summary>
        /// Creates band data with the same early and late linear energy in every octave band.
        /// </summary>
        private static ReceiverBandData MakeBandData(int idx, double earlyLinear, double lateLinear)
        {
            var bd = new ReceiverBandData { ReceiverIndex = idx };
            for (int k = 0; k < OctaveBands.Count; k++)
            {
                bd.EarlyLinearByBand[k] = earlyLinear;
                bd.LateLinearByBand[k] = lateLinear;
            }
            return bd;
        }

        // ---------------------------------------------------------------------------
        // Basic boundary tests
        // ---------------------------------------------------------------------------

        [Fact]
        public void STI_HighSNR_NoReverb_YieldsMaximumSTI()
        {
            // Very high early energy, near-silent background, zero RT60:
            // m_noise → 1, m_rt → 1, snrApp → clamped at +15 dB, TI → 1.0 for all bands.
            var results = new List<ReceiverResult> { MakeResult(0) };
            var bandData = new List<ReceiverBandData> { MakeBandData(0, 1e9, 0) };
            var bgNoise = new double[] { -100, -100, -100, -100, -100, -100, -100 };
            var rt60 = new double[7]; // all 0.0

            STICalculator.Calculate(results, bandData, bgNoise, rt60);

            Assert.Equal(1.0, results[0].Sti, 2);
        }

        [Fact]
        public void STI_NoEarlyEnergy_YieldsZeroSTI()
        {
            // Zero early energy, loud background noise, significant RT60:
            // snrApp → clamped at -15 dB, TI → 0 for all bands.
            var results = new List<ReceiverResult> { MakeResult(0) };
            var bandData = new List<ReceiverBandData> { MakeBandData(0, 0.0, 0.0) };
            var bgNoise = new double[] { 60, 60, 60, 60, 60, 60, 60 };
            var rt60 = new double[] { 0.5, 0.5, 0.5, 0.5, 0.5, 0.5, 0.5 };

            STICalculator.Calculate(results, bandData, bgNoise, rt60);

            Assert.Equal(0.0, results[0].Sti, 2);
        }

        [Fact]
        public void STI_AlwaysBoundedBetweenZeroAndOne()
        {
            // Extreme good case
            var rGood = new List<ReceiverResult> { MakeResult(0) };
            STICalculator.Calculate(rGood,
                new List<ReceiverBandData> { MakeBandData(0, 1e12, 0) },
                new double[7], new double[7]);
            Assert.InRange(rGood[0].Sti, 0.0, 1.0);

            // Extreme bad case
            var rBad = new List<ReceiverResult> { MakeResult(0) };
            STICalculator.Calculate(rBad,
                new List<ReceiverBandData> { MakeBandData(0, 0, 1e12) },
                new double[] { 80, 80, 80, 80, 80, 80, 80 },
                new double[] { 5.0, 5.0, 5.0, 5.0, 5.0, 5.0, 5.0 });
            Assert.InRange(rBad[0].Sti, 0.0, 1.0);
        }

        // ---------------------------------------------------------------------------
        // Physical relationship tests
        // ---------------------------------------------------------------------------

        [Fact]
        public void STI_IncreasesWithBetterSNR()
        {
            var bgNoise = new double[] { 40, 40, 40, 40, 40, 40, 40 };
            var rt60 = new double[] { 0.5, 0.5, 0.5, 0.5, 0.5, 0.5, 0.5 };

            // Low SNR: early energy ≈ bgNoise level → SNR ≈ 0 dB
            var rLow = new List<ReceiverResult> { MakeResult(0) };
            STICalculator.Calculate(rLow,
                new List<ReceiverBandData> { MakeBandData(0, Math.Pow(10, 40.0 / 10), 0) },
                bgNoise, rt60);

            // High SNR: early energy 40 dB above bgNoise → SNR ≈ +40 dB
            var rHigh = new List<ReceiverResult> { MakeResult(0) };
            STICalculator.Calculate(rHigh,
                new List<ReceiverBandData> { MakeBandData(0, Math.Pow(10, 80.0 / 10), 0) },
                bgNoise, rt60);

            Assert.True(rHigh[0].Sti > rLow[0].Sti,
                $"High-SNR STI ({rHigh[0].Sti}) must exceed low-SNR STI ({rLow[0].Sti})");
        }

        [Fact]
        public void STI_DecreasesWithLongerRT60()
        {
            double earlyLinear = 1e6;
            var bgNoise = new double[] { 25, 25, 25, 25, 25, 25, 25 };

            var rShort = new List<ReceiverResult> { MakeResult(0) };
            STICalculator.Calculate(rShort,
                new List<ReceiverBandData> { MakeBandData(0, earlyLinear, 0) },
                bgNoise,
                new double[] { 0.2, 0.2, 0.2, 0.2, 0.2, 0.2, 0.2 });

            var rLong = new List<ReceiverResult> { MakeResult(0) };
            STICalculator.Calculate(rLong,
                new List<ReceiverBandData> { MakeBandData(0, earlyLinear, 0) },
                bgNoise,
                new double[] { 2.0, 2.0, 2.0, 2.0, 2.0, 2.0, 2.0 });

            Assert.True(rShort[0].Sti > rLong[0].Sti,
                $"Short-RT60 STI ({rShort[0].Sti}) must exceed long-RT60 STI ({rLong[0].Sti})");
        }

        [Fact]
        public void STI_LateEnergyDegradesSNR()
        {
            double earlyLinear = 1e6;
            var bgNoise = new double[] { 20, 20, 20, 20, 20, 20, 20 };
            var rt60 = new double[] { 0.3, 0.3, 0.3, 0.3, 0.3, 0.3, 0.3 };

            var rNoLate = new List<ReceiverResult> { MakeResult(0) };
            STICalculator.Calculate(rNoLate,
                new List<ReceiverBandData> { MakeBandData(0, earlyLinear, 0) },
                bgNoise, rt60);

            // Late energy = 10× early → adds significant noise
            var rWithLate = new List<ReceiverResult> { MakeResult(0) };
            STICalculator.Calculate(rWithLate,
                new List<ReceiverBandData> { MakeBandData(0, earlyLinear, earlyLinear * 10) },
                bgNoise, rt60);

            Assert.True(rNoLate[0].Sti > rWithLate[0].Sti,
                $"STI without late energy ({rNoLate[0].Sti}) must exceed STI with late energy ({rWithLate[0].Sti})");
        }

        // ---------------------------------------------------------------------------
        // Speech weighting tests
        // ---------------------------------------------------------------------------

        [Fact]
        public void STI_MaleAndFemaleWeightsProduceDifferentValues()
        {
            // Unequal early energy per band: higher-frequency bands are louder,
            // which favours female weighting (heavier weight on 2 kHz / 4 kHz).
            var bd = new ReceiverBandData { ReceiverIndex = 0 };
            double[] earlyPerBand = { 1e3, 1e4, 1e5, 1e6, 1e8, 1e8, 1e5 };
            for (int k = 0; k < OctaveBands.Count; k++)
                bd.EarlyLinearByBand[k] = earlyPerBand[k];

            var bgNoise = new double[] { 20, 20, 20, 20, 20, 20, 20 };
            var rt60 = new double[] { 0.5, 0.5, 0.5, 0.5, 0.5, 0.5, 0.5 };

            var rMale = new List<ReceiverResult> { MakeResult(0) };
            STICalculator.Calculate(rMale, new List<ReceiverBandData> { bd }, bgNoise, rt60,
                SpeechWeightType.Male);

            var rFemale = new List<ReceiverResult> { MakeResult(0) };
            STICalculator.Calculate(rFemale, new List<ReceiverBandData> { bd }, bgNoise, rt60,
                SpeechWeightType.Female);

            Assert.NotEqual(rMale[0].Sti, rFemale[0].Sti);
        }

        // ---------------------------------------------------------------------------
        // Multi-receiver tests
        // ---------------------------------------------------------------------------

        [Fact]
        public void STI_EmptyReceiverList_DoesNotThrow()
        {
            var ex = Record.Exception(() =>
                STICalculator.Calculate(
                    new List<ReceiverResult>(),
                    new List<ReceiverBandData>(),
                    OctaveBands.DefaultBackgroundNoise,
                    OctaveBands.DefaultRT60));

            Assert.Null(ex);
        }

        [Fact]
        public void STI_MultipleReceivers_EachReceivesIndependentResult()
        {
            // Receiver 0: perfect SNR (1e9 early vs −100 dB noise), zero RT60 → STI ≈ 1.0.
            // Receiver 1: no signal, loud noise → STI ≈ 0.0.
            // Each receiver must be processed independently.
            var results = new List<ReceiverResult> { MakeResult(0), MakeResult(1) };
            var bandData = new List<ReceiverBandData>
            {
                MakeBandData(0, 1e9, 0),     // perfect signal, no reverb
                MakeBandData(1, 1.0, 1e9)    // no signal, very poor
            };
            var bgNoise = new double[] { -100, -100, -100, -100, -100, -100, -100 };
            var rt60 = new double[7]; // zero RT60 → no reverberation degradation

            STICalculator.Calculate(results, bandData, bgNoise, rt60);

            Assert.True(results[0].Sti > 0.9,
                $"Receiver 0 (good) STI should be > 0.9, got {results[0].Sti}");
            Assert.True(results[1].Sti < 0.1,
                $"Receiver 1 (poor) STI should be < 0.1, got {results[1].Sti}");
        }

        // ---------------------------------------------------------------------------
        // Additional known-value regression tests
        // ---------------------------------------------------------------------------

        [Fact]
        public void STI_ZeroRT60_SNR0dB_YieldsSTIHalf()
        {
            // When RT60 = 0: m_rt = 1/√(1+0) = 1 for every modulation frequency.
            // When SNR = earlyDb − noiseDb = 40 − 40 = 0 dB:
            //   m_noise = 1/(1 + 10^0) = 0.5
            //   m_avg   = 1 × 0.5 = 0.5  (no reverberation degradation)
            //   snrApp  = 10·log10(0.5/0.5) = 0 dB
            //   TI      = (0 + 15) / 30 = 0.5 for every band
            //   STI     = Σ weight_k · 0.5 = 0.5  (male weights sum to 1.0)
            double earlyLinear = Math.Pow(10.0, 40.0 / 10.0); // 40 dB early
            var bgNoise = new double[] { 40, 40, 40, 40, 40, 40, 40 }; // 40 dB → SNR = 0 dB
            var rt60 = new double[7]; // all 0.0

            var results = new List<ReceiverResult> { MakeResult(0) };
            STICalculator.Calculate(results,
                new List<ReceiverBandData> { MakeBandData(0, earlyLinear, 0) },
                bgNoise, rt60);

            Assert.InRange(results[0].Sti, 0.495, 0.505);
        }

        [Fact]
        public void STI_ZeroRT60_SNR10dB_YieldsAnalyticalTI()
        {
            // RT60 = 0 removes reverberation degradation entirely: m_rt = 1.
            // SNR = 70 − 60 = +10 dB:
            //   m_noise = 1/(1 + 10^(-1)) = 1/1.1 ≈ 0.9091
            //   m_avg   = 0.9091  (m_rt = 1 → no averaging across mod freqs changes anything)
            //   snrApp  = 10·log10(0.9091/0.0909) = 10·log10(10) = 10 dB  (exact)
            //   TI      = (10 + 15) / 30 = 25/30 ≈ 0.8333
            //   STI     = 0.8333  (uniform bands, no masking, weights sum to 1)
            double earlyLinear = Math.Pow(10.0, 70.0 / 10.0);
            var bgNoise = new double[] { 60, 60, 60, 60, 60, 60, 60 };
            var rt60 = new double[7]; // all 0.0

            var results = new List<ReceiverResult> { MakeResult(0) };
            STICalculator.Calculate(results,
                new List<ReceiverBandData> { MakeBandData(0, earlyLinear, 0) },
                bgNoise, rt60);

            Assert.InRange(results[0].Sti, 0.828, 0.838);
        }

        [Fact]
        public void STI_MaskingCorrection_ReducesSTIWhenLowerBandIsLouder()
        {
            // IEC 60268-16 auditory masking correction (§A.3):
            //   levelDiff = L_{k-1} − L_k − β_k
            //   if levelDiff > 0: snrApp_k -= α_k × levelDiff
            //
            // Setup: band 0 (125 Hz) at 80 dB, bands 1–6 at 40 dB, bgNoise=30 dB, RT60=0.
            // For band 1 (250 Hz):
            //   snrApp before masking = 10 dB  (SNR = 40-30 = 10 dB, no reverb)
            //   levelDiff = 80 − 40 − 0.45 = 39.55 > 0
            //   correction = 0.45 × 39.55 ≈ 17.8 dB  → snrApp ≈ -7.8 dB
            //   TI drops from 0.833 to 0.24
            // Uniform 40 dB reference gives STI ≈ 0.833; masked case must be lower.
            var bgNoise = new double[] { 30, 30, 30, 30, 30, 30, 30 };
            var rt60 = new double[7]; // zero RT60

            // Reference: uniform 40 dB across all bands (SNR = 10 dB everywhere, no masking)
            var rUniform = new List<ReceiverResult> { MakeResult(0) };
            STICalculator.Calculate(rUniform,
                new List<ReceiverBandData> { MakeBandData(0, Math.Pow(10, 40.0 / 10.0), 0) },
                bgNoise, rt60);

            // Masked case: band 0 is 40 dB louder than the rest
            var bd = new ReceiverBandData { ReceiverIndex = 0 };
            bd.EarlyLinearByBand[0] = Math.Pow(10, 80.0 / 10.0); // 80 dB
            for (int k = 1; k < OctaveBands.Count; k++)
                bd.EarlyLinearByBand[k] = Math.Pow(10, 40.0 / 10.0); // 40 dB

            var rMasked = new List<ReceiverResult> { MakeResult(0) };
            STICalculator.Calculate(rMasked, new List<ReceiverBandData> { bd }, bgNoise, rt60);

            Assert.True(rMasked[0].Sti < rUniform[0].Sti,
                $"Masking should reduce STI: {rMasked[0].Sti:F3} must be < uniform {rUniform[0].Sti:F3}");
        }

        [Fact]
        public void STI_FemaleWeights_ZeroForFirstAndLastBand()
        {
            // FemaleSpeechWeights = [0.000, 0.117, 0.223, 0.216, 0.328, 0.250, 0.000]
            // Bands 0 (125 Hz) and 6 (8 kHz) carry zero female weight.
            // If only band 0 has signal and all other bands have near-zero SNR:
            //   Male   STI ≈ weight_male[0] × TI[0] = 0.085 × 1.0 = 0.085  (non-zero)
            //   Female STI ≈ weight_female[0] × TI[0] = 0.000 × 1.0 = 0.000
            var bd = new ReceiverBandData { ReceiverIndex = 0 };
            bd.EarlyLinearByBand[0] = 1e9; // very strong 125 Hz signal
            // bands 1-6: zero early energy, loud background → near-zero TI

            var bgNoise = new double[] { -100, 80, 80, 80, 80, 80, 80 };
            var rt60 = new double[7];

            var rMale = new List<ReceiverResult> { MakeResult(0) };
            STICalculator.Calculate(rMale, new List<ReceiverBandData> { bd }, bgNoise, rt60,
                SpeechWeightType.Male);

            var rFemale = new List<ReceiverResult> { MakeResult(0) };
            STICalculator.Calculate(rFemale, new List<ReceiverBandData> { bd }, bgNoise, rt60,
                SpeechWeightType.Female);

            // Male uses 125 Hz band (weight 0.085): STI must be clearly above zero
            Assert.True(rMale[0].Sti > 0.05,
                $"Male STI with strong 125 Hz signal should be > 0.05, got {rMale[0].Sti}");
            // Female weight for 125 Hz = 0.000: STI must be effectively zero
            Assert.InRange(rFemale[0].Sti, 0.0, 0.01);
        }

        // ---------------------------------------------------------------------------
        // Known-value regression test
        // ---------------------------------------------------------------------------

        [Fact]
        public void STI_KnownValue_SNR10dB_RT60_0p5s_YieldsExpectedResult()
        {
            // Analytically verifiable case (uniform across all 7 bands):
            //   SNR = earlyDb - noiseDb = 70 - 60 = +10 dB
            //   RT60 = 0.5 s
            //
            // m_noise = 1 / (1 + 10^(-10/10)) = 1/1.1 ≈ 0.9091
            //
            // m_rt(F) = 1 / sqrt(1 + (2π·F·0.5/13.8)²)  for each of 14 mod freqs
            //
            // m_avg  = mean(m_rt(F) * m_noise)  across 14 frequencies ≈ 0.6901
            //
            // snrApp = 10·log10(m_avg / (1−m_avg)) ≈ +3.48 dB
            //
            // TI     = (snrApp + 15) / 30                ≈ 0.616
            //
            // No masking (uniform energy per band)
            // STI    = Σ weight_k · TI                   ≈ 0.616  (male weights, sum=1)

            double earlyLinear = Math.Pow(10.0, 70.0 / 10.0);
            var bd = MakeBandData(0, earlyLinear, 0);
            var bgNoise = new double[] { 60, 60, 60, 60, 60, 60, 60 };
            var rt60 = new double[] { 0.5, 0.5, 0.5, 0.5, 0.5, 0.5, 0.5 };

            var results = new List<ReceiverResult> { MakeResult(0) };
            STICalculator.Calculate(results, new List<ReceiverBandData> { bd }, bgNoise, rt60);

            // Expected ≈ 0.616; allow ±0.01 for floating-point rounding
            Assert.InRange(results[0].Sti, 0.606, 0.626);
        }
    }
}
