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
            // m_avg  = mean(m_rt(F) * m_noise)  across 14 frequencies ≈ 0.6902
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
