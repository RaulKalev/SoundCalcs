using System;
using System.Collections.Generic;
using System.Threading;
using Xunit;
using SoundCalcs.Compute;
using SoundCalcs.Domain;

namespace SoundCalcs.Tests
{
    /// <summary>
    /// Unit tests for the four precision improvements added in the "professional grade" update:
    ///   1. A-weighting constants  — OctaveBands.AWeightingDb
    ///   2. Humidity-dependent air absorption  — SPLCalculator + EnvironmentSettings.RelativeHumidityPct
    ///   3. A-weighted SPL (dBA)   — ReceiverResult.SplDbA
    ///   4. C80 / D50 clarity      — STICalculator.ComputeC80D50()
    ///   5. Eyring / Sabine RT60   — RoomAcoustics.EstimateEyringRt60() / EstimateSabineRt60()
    /// </summary>
    public class AcousticExtensionsTests
    {
        // -----------------------------------------------------------------------
        // Helpers (mirrors SPLCalculatorTests.BuildInput, adds humidity overload)
        // -----------------------------------------------------------------------

        private static AcousticJobInput BuildInput(
            Vec3 sourcePos,
            Vec3 sourceFacing,
            double onAxisSplDb,
            IEnumerable<Vec3> receiverPositions,
            double humidityPct = 50.0)
        {
            var sources = new List<ComputeSource>
            {
                new ComputeSource
                {
                    Position = sourcePos,
                    FacingDirection = sourceFacing,
                    Profile = new SpeakerProfileMapping
                    {
                        ProfileSource = ProfileSourceType.SimpleOmni,
                        OnAxisSplDb = onAxisSplDb
                    }
                }
            };

            var receivers = new List<ReceiverPoint>();
            int idx = 0;
            foreach (Vec3 pos in receiverPositions)
                receivers.Add(new ReceiverPoint(pos, idx++));

            return new AcousticJobInput
            {
                Sources = sources,
                Receivers = receivers,
                Walls = new List<ComputeWall>(),
                Rooms = new List<RoomPolygon>(),
                Environment = new EnvironmentSettings
                {
                    TemperatureC = 20.0,
                    RelativeHumidityPct = humidityPct,
                    RT60ByBand = new double[] { 0.5, 0.5, 0.5, 0.5, 0.5, 0.5, 0.5 },
                    BackgroundNoiseByBand = new double[] { 30, 30, 30, 30, 30, 30, 30 }
                },
                Quality = CalculationQuality.Draft
            };
        }

        private static ReceiverResult MakeResult(int idx) => new ReceiverResult
        {
            ReceiverIndex = idx,
            Position = new Vec3(0, 0, 0),
            SplDb = 80.0,
            SplDbByBand = new double[OctaveBands.Count]
        };

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

        // -----------------------------------------------------------------------
        // 1. A-weighting constants (OctaveBands.AWeightingDb)
        // -----------------------------------------------------------------------

        [Fact]
        public void AWeightingDb_HasSevenValues()
        {
            Assert.Equal(OctaveBands.Count, OctaveBands.AWeightingDb.Length);
        }

        [Fact]
        public void AWeightingDb_KnownValues_MatchIEC61672()
        {
            // IEC 61672-1 A-weighting corrections for octave bands 125 Hz – 8 kHz:
            //   125 Hz: -16.1 dB,  250 Hz: -8.6 dB,  500 Hz: -3.2 dB,  1 kHz: 0.0 dB,
            //   2 kHz: +1.2 dB,   4 kHz: +1.0 dB,   8 kHz: -1.1 dB
            double[] expected = { -16.1, -8.6, -3.2, 0.0, 1.2, 1.0, -1.1 };

            for (int k = 0; k < OctaveBands.Count; k++)
            {
                Assert.Equal(expected[k], OctaveBands.AWeightingDb[k],
                    precision: 1);
            }
        }

        [Fact]
        public void AWeightingDb_At1kHz_IsZero()
        {
            // A-weighting is defined as 0 dB at 1 kHz (reference frequency).
            Assert.Equal(0.0, OctaveBands.AWeightingDb[3]);
        }

        [Fact]
        public void AWeightingDb_LowFrequencies_AreNegative()
        {
            // Below 1 kHz the human ear is less sensitive → A-weight must be negative.
            Assert.True(OctaveBands.AWeightingDb[0] < 0, "125 Hz A-weight must be negative");
            Assert.True(OctaveBands.AWeightingDb[1] < 0, "250 Hz A-weight must be negative");
            Assert.True(OctaveBands.AWeightingDb[2] < 0, "500 Hz A-weight must be negative");
        }

        // -----------------------------------------------------------------------
        // 2. Humidity-dependent air absorption
        // -----------------------------------------------------------------------

        [Fact]
        public void ComputeAirAbsorption_DifferentHumidity_Produces8kHzDifference()
        {
            // ISO 9613-1: humidity strongly affects high-frequency air absorption.
            // At 20 °C the 8 kHz coefficient must differ measurably between 10 % and 90 % RH.
            double[] low  = OctaveBands.ComputeAirAbsorption(20.0, 10.0);
            double[] high = OctaveBands.ComputeAirAbsorption(20.0, 90.0);

            double diff8k = Math.Abs(low[6] - high[6]); // index 6 = 8 kHz
            Assert.True(diff8k > 0.01,
                $"8 kHz absorption must differ by > 0.01 dB/m between 10 % RH and 90 % RH, " +
                $"got Δ = {diff8k:F4} dB/m  (low={low[6]:F4}, high={high[6]:F4})");
        }

        [Fact]
        public void ComputeAirAbsorption_LowFrequency_IsSmall()
        {
            // 125 Hz absorption should be very small (< 0.01 dB/m) at any humidity.
            double[] coeff = OctaveBands.ComputeAirAbsorption(20.0, 50.0);
            Assert.True(coeff[0] < 0.01,
                $"125 Hz air absorption should be < 0.01 dB/m, got {coeff[0]:F5}");
        }

        [Fact]
        public void SPL_HighHumidity_vs_LowHumidity_DifferentAt8kHzBand_OverLongDistance()
        {
            // Over 50 m the accumulated air absorption difference at 8 kHz must be > 0.1 dB.
            // Humidity 10 % vs 90 %, all other settings identical.
            Vec3 srcPos = new Vec3(0, 0, 0);
            Vec3 recvPos = new Vec3(50, 0, 0);

            var calcLow  = new SPLCalculator();
            var (resLow,  _) = calcLow.Calculate(
                BuildInput(srcPos, new Vec3(1, 0, 0), 90.0, new[] { recvPos }, humidityPct: 10.0),
                CancellationToken.None, null);

            var calcHigh = new SPLCalculator();
            var (resHigh, _) = calcHigh.Calculate(
                BuildInput(srcPos, new Vec3(1, 0, 0), 90.0, new[] { recvPos }, humidityPct: 90.0),
                CancellationToken.None, null);

            // 8 kHz band index = 6
            double diff = Math.Abs(resLow[0].SplDbByBand[6] - resHigh[0].SplDbByBand[6]);
            Assert.True(diff > 0.1,
                $"8 kHz SPL must differ by > 0.1 dB between 10 % and 90 % RH over 50 m, " +
                $"got Δ = {diff:F3} dB");
        }

        // -----------------------------------------------------------------------
        // 3. A-weighted SPL (dBA) — ReceiverResult.SplDbA
        // -----------------------------------------------------------------------

        [Fact]
        public void SplDbA_IsPopulated_ForAllReceivers()
        {
            // SplDbA must be set (non-sentinel) for every receiver when the source is on-axis.
            var positions = new[] {
                new Vec3(1, 0, 0), new Vec3(3, 0, 0), new Vec3(5, 0, 0)
            };
            var input = BuildInput(new Vec3(0, 0, 0), new Vec3(1, 0, 0), 90.0, positions);

            var (results, _) = new SPLCalculator().Calculate(input, CancellationToken.None, null);

            foreach (var r in results)
            {
                Assert.True(r.SplDbA > -90.0,
                    $"SplDbA should be a real acoustic value, got {r.SplDbA} dB at receiver {r.ReceiverIndex}");
            }
        }

        [Fact]
        public void SplDbA_FlatSpectrum_IsLessThanBroadbandSpl()
        {
            // For a flat-spectrum (SimpleOmni) source the large negative A-weights at 125 Hz and
            // 250 Hz pull the A-weighted sum below the flat broadband total.
            // Expected: SplDbA ≈ 88.5 dBA vs SplDb ≈ 90 dB at 1 m.
            var input = BuildInput(new Vec3(0, 0, 0), new Vec3(1, 0, 0), 90.0,
                new[] { new Vec3(1, 0, 0) });

            var (results, _) = new SPLCalculator().Calculate(input, CancellationToken.None, null);

            Assert.True(results[0].SplDbA < results[0].SplDb,
                $"dBA ({results[0].SplDbA:F2}) should be less than broadband SPL ({results[0].SplDb:F2})");
        }

        [Fact]
        public void SplDbA_At1kHz_ApproximatelyEqualsBroadbandPerBand()
        {
            // For a flat-spectrum source, the 1 kHz per-band SPL has A-weighting = 0.0 dB.
            // If 1 kHz were the ONLY band, dBA = SplDbByBand[3].
            // Here we just verify SplDbA is within a physically plausible range of SplDb.
            var input = BuildInput(new Vec3(0, 0, 0), new Vec3(1, 0, 0), 90.0,
                new[] { new Vec3(2, 0, 0) });

            var (results, _) = new SPLCalculator().Calculate(input, CancellationToken.None, null);
            var r = results[0];

            // A-weighted result should not deviate by more than 5 dB from broadband for typical audio
            Assert.InRange(r.SplDb - r.SplDbA, 0.0, 5.0);
        }

        [Fact]
        public void SplDbA_DecreasesWithDistance()
        {
            // Like broadband SPL, dBA must decrease as distance from the source increases.
            var positions = new[] {
                new Vec3(1, 0, 0), new Vec3(2, 0, 0), new Vec3(4, 0, 0), new Vec3(8, 0, 0)
            };
            var input = BuildInput(new Vec3(0, 0, 0), new Vec3(1, 0, 0), 90.0, positions);

            var (results, _) = new SPLCalculator().Calculate(input, CancellationToken.None, null);
            results.Sort((a, b) => a.ReceiverIndex.CompareTo(b.ReceiverIndex));

            for (int i = 1; i < results.Count; i++)
            {
                Assert.True(results[i].SplDbA < results[i - 1].SplDbA,
                    $"SplDbA at index {i} ({results[i].SplDbA:F1}) must be less than at " +
                    $"index {i - 1} ({results[i - 1].SplDbA:F1})");
            }
        }

        // -----------------------------------------------------------------------
        // 4. C80 / D50  — STICalculator.ComputeC80D50()
        // -----------------------------------------------------------------------

        [Fact]
        public void C80D50_AllEarlyEnergy_YieldsMaxC80AndD50NearOne()
        {
            // No late energy → all direct/early arrivals → maximum clarity.
            // C80 = 15.0 (clamped) and D50 = 1.0
            var results  = new List<ReceiverResult> { MakeResult(0) };
            var bandData = new List<ReceiverBandData> { MakeBandData(0, 1e6, 0.0) };

            STICalculator.ComputeC80D50(results, bandData);

            Assert.Equal(15.0, results[0].C80Db, precision: 2);
            Assert.Equal(1.0,  results[0].D50,   precision: 3);
        }

        [Fact]
        public void C80D50_AllLateEnergy_YieldsMinC80AndD50NearZero()
        {
            // No early energy → fully reverberant field → minimum clarity.
            // C80 = -15.0 (clamped) and D50 = 0.0
            var results  = new List<ReceiverResult> { MakeResult(0) };
            var bandData = new List<ReceiverBandData> { MakeBandData(0, 0.0, 1e6) };

            STICalculator.ComputeC80D50(results, bandData);

            Assert.Equal(-15.0, results[0].C80Db, precision: 2);
            Assert.Equal(0.0,   results[0].D50,   precision: 3);
        }

        [Fact]
        public void C80D50_EqualEarlyAndLate_YieldsZeroC80AndD50Half()
        {
            // Equal early and late energy → C80 = 0 dB, D50 = 0.5
            double energy = 1e5;
            var results  = new List<ReceiverResult> { MakeResult(0) };
            var bandData = new List<ReceiverBandData> { MakeBandData(0, energy, energy) };

            STICalculator.ComputeC80D50(results, bandData);

            Assert.InRange(results[0].C80Db, -0.05, 0.05);
            Assert.InRange(results[0].D50, 0.499, 0.501);
        }

        [Fact]
        public void C80D50_D50_IsBoundedBetweenZeroAndOne()
        {
            // D50 must never go outside [0, 1] regardless of input values.
            var cases = new[]
            {
                (earlyLinear: 1e12, lateLinear: 0.0),
                (earlyLinear: 0.0,  lateLinear: 1e12),
                (earlyLinear: 1e3,  lateLinear: 1e9),
                (earlyLinear: 1e9,  lateLinear: 1e3),
                (earlyLinear: 0.0,  lateLinear: 0.0),
            };

            foreach (var (earlyLinear, lateLinear) in cases)
            {
                var results  = new List<ReceiverResult> { MakeResult(0) };
                var bandData = new List<ReceiverBandData> { MakeBandData(0, earlyLinear, lateLinear) };
                STICalculator.ComputeC80D50(results, bandData);

                Assert.InRange(results[0].D50, 0.0, 1.0);
            }
        }

        [Fact]
        public void C80D50_MoreEarlyEnergy_YieldsHigherC80AndD50()
        {
            // Good case (high early fraction) vs. bad case (low early fraction)
            var rGood = new List<ReceiverResult> { MakeResult(0) };
            STICalculator.ComputeC80D50(rGood,
                new List<ReceiverBandData> { MakeBandData(0, 1e8, 1e4) });

            var rBad = new List<ReceiverResult> { MakeResult(0) };
            STICalculator.ComputeC80D50(rBad,
                new List<ReceiverBandData> { MakeBandData(0, 1e4, 1e8) });

            Assert.True(rGood[0].C80Db > rBad[0].C80Db,
                $"Higher early fraction must yield higher C80: {rGood[0].C80Db} vs {rBad[0].C80Db}");
            Assert.True(rGood[0].D50 > rBad[0].D50,
                $"Higher early fraction must yield higher D50: {rGood[0].D50} vs {rBad[0].D50}");
        }

        [Fact]
        public void C80D50_UsesOnlySpeechBands_500HzAnd1kHz()
        {
            // C80/D50 must use only the 500 Hz (index 2) and 1 kHz (index 3) bands.
            // Setting early energy only in those bands and none elsewhere must give a
            // positive C80, whereas setting early energy in non-speech bands only must
            // give the all-late fallback (C80 = -15, D50 = 0).

            // Case A: energy only in speech bands (k=2 and k=3)
            var bdSpeech = new ReceiverBandData { ReceiverIndex = 0 };
            bdSpeech.EarlyLinearByBand[2] = 1e6;
            bdSpeech.EarlyLinearByBand[3] = 1e6;
            // All other bands are zero (default)

            var rSpeech = new List<ReceiverResult> { MakeResult(0) };
            STICalculator.ComputeC80D50(rSpeech, new List<ReceiverBandData> { bdSpeech });

            Assert.Equal(15.0, rSpeech[0].C80Db, precision: 2); // clamped max → all early
            Assert.Equal(1.0,  rSpeech[0].D50,   precision: 3);

            // Case B: energy only in NON-speech bands (k=0,1,4,5,6); speech bands = 0
            var bdNonSpeech = new ReceiverBandData { ReceiverIndex = 0 };
            for (int k = 0; k < OctaveBands.Count; k++)
            {
                if (k != 2 && k != 3)
                    bdNonSpeech.EarlyLinearByBand[k] = 1e6;
            }

            var rNonSpeech = new List<ReceiverResult> { MakeResult(0) };
            STICalculator.ComputeC80D50(rNonSpeech, new List<ReceiverBandData> { bdNonSpeech });

            // Speech bands are all zero → falls into the (earlySum == 0) branch → C80 = -15, D50 = 0
            Assert.Equal(-15.0, rNonSpeech[0].C80Db, precision: 2);
            Assert.Equal(0.0,   rNonSpeech[0].D50,   precision: 3);
        }

        [Fact]
        public void C80D50_MultipleReceivers_IndependentResults()
        {
            // Each receiver must receive an independent C80/D50 calculation.
            var results = new List<ReceiverResult> { MakeResult(0), MakeResult(1) };
            var bandData = new List<ReceiverBandData>
            {
                MakeBandData(0, 1e8, 1e2),  // receiver 0: mostly early → high C80
                MakeBandData(1, 1e2, 1e8)   // receiver 1: mostly late  → low C80
            };

            STICalculator.ComputeC80D50(results, bandData);

            Assert.True(results[0].C80Db > results[1].C80Db,
                $"Receiver 0 C80 ({results[0].C80Db}) must be higher than receiver 1 C80 ({results[1].C80Db})");
        }

        // -----------------------------------------------------------------------
        // 5. Eyring / Sabine RT60  — RoomAcoustics
        // -----------------------------------------------------------------------

        [Fact]
        public void EyringRT60_ReturnsSevenValues()
        {
            double[] alpha = new double[OctaveBands.Count];
            for (int k = 0; k < OctaveBands.Count; k++) alpha[k] = 0.2;

            double[] rt60 = RoomAcoustics.EstimateEyringRt60(200.0, 180.0, alpha);

            Assert.Equal(OctaveBands.Count, rt60.Length);
        }

        [Fact]
        public void EyringRT60_HighAbsorption_IsShorterThanSabine()
        {
            // For α = 0.7 (highly absorptive room), Eyring gives a shorter RT60 than Sabine
            // because it uses a more accurate logarithmic model.
            // V = 200 m³, S = 180 m²
            double v = 200.0, s = 180.0;
            double[] alpha = new double[OctaveBands.Count];
            for (int k = 0; k < OctaveBands.Count; k++) alpha[k] = 0.70;

            double[] eyring = RoomAcoustics.EstimateEyringRt60(v, s, alpha);
            double[] sabine = RoomAcoustics.EstimateSabineRt60(v, s, alpha);

            for (int k = 0; k < OctaveBands.Count; k++)
            {
                Assert.True(eyring[k] < sabine[k],
                    $"Band {k}: Eyring RT60 ({eyring[k]}) must be shorter than Sabine ({sabine[k]}) at α=0.70");
            }
        }

        [Fact]
        public void EyringRT60_LowAbsorption_CloseToSabine()
        {
            // At low absorption (α = 0.05), Eyring ≈ Sabine (difference < 10 %).
            double v = 500.0, s = 400.0;
            double[] alpha = new double[OctaveBands.Count];
            for (int k = 0; k < OctaveBands.Count; k++) alpha[k] = 0.05;

            double[] eyring = RoomAcoustics.EstimateEyringRt60(v, s, alpha);
            double[] sabine = RoomAcoustics.EstimateSabineRt60(v, s, alpha);

            for (int k = 0; k < OctaveBands.Count; k++)
            {
                double ratio = eyring[k] / sabine[k];
                Assert.InRange(ratio, 0.90, 1.10);
            }
        }

        [Fact]
        public void EyringRT60_ResultIsClamped_BetweenMinAndMax()
        {
            // Very large V with near-zero α must be clamped at 30 s.
            double[] nearZeroAlpha = new double[OctaveBands.Count];
            for (int k = 0; k < OctaveBands.Count; k++) nearZeroAlpha[k] = 0.001; // min clamp

            double[] maxClamped = RoomAcoustics.EstimateEyringRt60(100_000.0, 1_000.0, nearZeroAlpha);
            foreach (double t in maxClamped)
                Assert.InRange(t, 0.05, 30.0);

            // Very small V with α → 1 must be clamped at 0.05 s.
            double[] nearOneAlpha = new double[OctaveBands.Count];
            for (int k = 0; k < OctaveBands.Count; k++) nearOneAlpha[k] = 0.999; // max clamp

            double[] minClamped = RoomAcoustics.EstimateEyringRt60(0.5, 10.0, nearOneAlpha);
            foreach (double t in minClamped)
                Assert.InRange(t, 0.05, 30.0);
        }

        [Fact]
        public void EyringRT60_ZeroVolume_ReturnsDefaultRT60()
        {
            double[] alpha = new double[OctaveBands.Count];
            for (int k = 0; k < OctaveBands.Count; k++) alpha[k] = 0.3;

            double[] result = RoomAcoustics.EstimateEyringRt60(0.0, 100.0, alpha);

            // Should fall back to DefaultRT60
            for (int k = 0; k < OctaveBands.Count; k++)
                Assert.Equal(OctaveBands.DefaultRT60[k], result[k]);
        }

        [Fact]
        public void SabineRT60_ReturnsSevenValues()
        {
            double[] alpha = new double[OctaveBands.Count];
            for (int k = 0; k < OctaveBands.Count; k++) alpha[k] = 0.2;

            double[] rt60 = RoomAcoustics.EstimateSabineRt60(200.0, 180.0, alpha);

            Assert.Equal(OctaveBands.Count, rt60.Length);
        }

        [Fact]
        public void SabineRT60_KnownValue_MatchesFormula()
        {
            // Sabine formula: T60 = 0.161 · V / (S · α)
            // V = 200 m³, S = 180 m², α = 0.20 → T60 = 0.161·200/(180·0.2) = 32.2/36 ≈ 0.894 s
            double v = 200.0, s = 180.0;
            double alpha = 0.20;
            double expected = 0.161 * v / (s * alpha); // ≈ 0.894

            double[] alphaBands = new double[OctaveBands.Count];
            for (int k = 0; k < OctaveBands.Count; k++) alphaBands[k] = alpha;

            double[] result = RoomAcoustics.EstimateSabineRt60(v, s, alphaBands);

            foreach (double t in result)
                Assert.InRange(t, expected - 0.01, expected + 0.01);
        }

        [Fact]
        public void EstimateSurfaceArea_SquareRoom_MatchesFormula()
        {
            // 100 m² floor, 3 m ceiling:
            //   side = sqrt(100) = 10 m
            //   perimeter = 40 m
            //   wall area = 40 × 3 = 120 m²
            //   total = floor + ceiling + walls = 100 + 100 + 120 = 320 m²
            double area = RoomAcoustics.EstimateSurfaceArea(100.0, 3.0);
            Assert.Equal(320.0, area, precision: 1);
        }

        [Fact]
        public void EstimateSurfaceArea_ZeroDimension_ReturnsZero()
        {
            Assert.Equal(0.0, RoomAcoustics.EstimateSurfaceArea(0.0, 3.0));
            Assert.Equal(0.0, RoomAcoustics.EstimateSurfaceArea(100.0, 0.0));
        }
    }
}
