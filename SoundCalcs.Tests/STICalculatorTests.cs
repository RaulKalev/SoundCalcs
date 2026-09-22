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
            // Very high early energy (90 dB), near-silent background, no late energy:
            // m_room = 1, m_noise → 1. The only degradation left is IEC level-dependent
            // auditory masking by the band below (amdB = 0.5·90 − 59.8 ≈ −15 dB), which
            // keeps STI just under 1 at high speech levels.
            var results = new List<ReceiverResult> { MakeResult(0) };
            var bandData = new List<ReceiverBandData> { MakeBandData(0, 1e9, 0) };
            var bgNoise = new double[] { -100, -100, -100, -100, -100, -100, -100 };
            var rt60 = new double[7]; // all 0.0

            STICalculator.Calculate(results, bandData, bgNoise, rt60);

            Assert.InRange(results[0].Sti, 0.98, 1.0);
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

            // The late energy is the reverberant tail; its decay time sets the MTF loss.
            var rShort = new List<ReceiverResult> { MakeResult(0) };
            STICalculator.Calculate(rShort,
                new List<ReceiverBandData> { MakeBandData(0, earlyLinear, earlyLinear) },
                bgNoise,
                new double[] { 0.2, 0.2, 0.2, 0.2, 0.2, 0.2, 0.2 });

            var rLong = new List<ReceiverResult> { MakeResult(0) };
            STICalculator.Calculate(rLong,
                new List<ReceiverBandData> { MakeBandData(0, earlyLinear, earlyLinear) },
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
            // Receiver 1: 0 dB signal, far below the IEC speech reception threshold → STI ≈ 0.0.
            // Each receiver must be processed independently.
            var results = new List<ReceiverResult> { MakeResult(0), MakeResult(1) };
            var bandData = new List<ReceiverBandData>
            {
                MakeBandData(0, 1e9, 0),     // perfect signal, no reverb
                MakeBandData(1, 1e-3, 0)     // −30 dB: inaudible
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
            //   STI     = (Σα − Σβ)·0.5 = 0.5  (IEC male weights)
            // Levels are 70 dB so the reception threshold and masking are negligible.
            double earlyLinear = Math.Pow(10.0, 70.0 / 10.0); // 70 dB early
            var bgNoise = new double[] { 70, 70, 70, 70, 70, 70, 70 }; // 70 dB → SNR = 0 dB
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
            // IEC 60268-16:2011 level-dependent auditory masking (§A.2.3):
            //   I_am,k = I_{k-1} · 10^(amdB/10), amdB = −10 dB for L_{k-1} ≥ 100 dB
            //   m'_k   = m_k · I_k / (I_k + I_am,k + I_rt,k)
            //
            // Setup: band 3 (1 kHz) at 100 dB, all other bands at 70 dB, bgNoise = 60 dB.
            // Band 4 (2 kHz) is masked by ≈ 90 dB of 1 kHz energy, 20 dB above its own
            // level, so its TI collapses. Uniform 70 dB reference gives STI ≈ 0.83.
            // (1 kHz → 2 kHz is used because α₄ = 0.309 dominates the redundancy terms;
            // at 125 → 250 Hz the IEC formula can rise when band 1 is lost.)
            var bgNoise = new double[] { 60, 60, 60, 60, 60, 60, 60 };
            var rt60 = new double[7]; // zero RT60

            // Reference: uniform 70 dB across all bands (SNR = 10 dB everywhere)
            var rUniform = new List<ReceiverResult> { MakeResult(0) };
            STICalculator.Calculate(rUniform,
                new List<ReceiverBandData> { MakeBandData(0, Math.Pow(10, 70.0 / 10.0), 0) },
                bgNoise, rt60);

            // Masked case: band 3 is 30 dB louder than the rest
            var bd = new ReceiverBandData { ReceiverIndex = 0 };
            for (int k = 0; k < OctaveBands.Count; k++)
                bd.EarlyLinearByBand[k] = Math.Pow(10, 70.0 / 10.0); // 70 dB
            bd.EarlyLinearByBand[3] = Math.Pow(10, 100.0 / 10.0);    // 100 dB

            var rMasked = new List<ReceiverResult> { MakeResult(0) };
            STICalculator.Calculate(rMasked, new List<ReceiverBandData> { bd }, bgNoise, rt60);

            Assert.True(rMasked[0].Sti < rUniform[0].Sti,
                $"Masking should reduce STI: {rMasked[0].Sti:F3} must be < uniform {rUniform[0].Sti:F3}");
        }

        [Fact]
        public void STI_FemaleWeights_ZeroForFirstAndLastBand()
        {
            // IEC female α = [0.000, 0.117, 0.223, 0.216, 0.328, 0.250, 0.194]
            // Band 0 (125 Hz) carries zero female weight.
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
            //   all speech energy arrives as a reverberant tail with T60 = 0.5 s
            //   SNR = 70 − 60 = +10 dB
            //
            // m(F)  = 1/√(1 + (2πF·τ)²) · 1/(1 + 10^(−1)),  τ = 0.5/13.82
            // TI(F) = (clip(10·log10(m/(1−m)), ±15) + 15)/30,  MTI = mean over 14 F
            // STI   = (Σα − Σβ)·MTI ≈ 0.641  (masking/threshold negligible at 70 dB)

            double lateLinear = Math.Pow(10.0, 70.0 / 10.0);
            var bd = MakeBandData(0, 0, lateLinear);
            var bgNoise = new double[] { 60, 60, 60, 60, 60, 60, 60 };
            var rt60 = new double[] { 0.5, 0.5, 0.5, 0.5, 0.5, 0.5, 0.5 };

            var results = new List<ReceiverResult> { MakeResult(0) };
            STICalculator.Calculate(results, new List<ReceiverBandData> { bd }, bgNoise, rt60);

            Assert.InRange(results[0].Sti, 0.631, 0.645);
        }
    }
}
