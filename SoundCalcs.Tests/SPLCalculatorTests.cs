using System;
using System.Collections.Generic;
using System.Threading;
using Xunit;
using SoundCalcs.Compute;
using SoundCalcs.Domain;

namespace SoundCalcs.Tests
{
    /// <summary>
    /// Unit tests for <see cref="SPLCalculator"/> verifying per-receiver Sound Pressure Level
    /// calculations: inverse-square-law falloff, multi-source summing, wall attenuation,
    /// per-band energy consistency, and early/late band-data output.
    ///
    /// All tests use Draft quality (no 2nd-order or ceiling/floor reflections) with no rooms
    /// defined, so no reverberant field is added.  This isolates the direct-path propagation
    /// physics and makes expected values analytically tractable.
    /// </summary>
    public class SPLCalculatorTests
    {
        // ---------------------------------------------------------------------------
        // Helpers
        // ---------------------------------------------------------------------------

        private static AcousticJobInput BuildInput(
            Vec3 sourcePos,
            Vec3 sourceFacing,
            double onAxisSplDb,
            IEnumerable<Vec3> receiverPositions,
            List<ComputeWall>? walls = null,
            CalculationQuality quality = CalculationQuality.Draft,
            List<ComputeSource>? extraSources = null)
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

            if (extraSources != null)
                sources.AddRange(extraSources);

            var receivers = new List<ReceiverPoint>();
            int idx = 0;
            foreach (Vec3 pos in receiverPositions)
                receivers.Add(new ReceiverPoint(pos, idx++));

            return new AcousticJobInput
            {
                Sources = sources,
                Receivers = receivers,
                Walls = walls ?? new List<ComputeWall>(),
                Rooms = new List<RoomPolygon>(), // no rooms → no reverberant field
                Environment = new EnvironmentSettings
                {
                    TemperatureC = 20.0,
                    RT60ByBand = new double[] { 0.5, 0.5, 0.5, 0.5, 0.5, 0.5, 0.5 },
                    BackgroundNoiseByBand = new double[] { 30, 30, 30, 30, 30, 30, 30 }
                },
                Quality = quality
            };
        }

        // ---------------------------------------------------------------------------
        // Input / output count tests
        // ---------------------------------------------------------------------------

        [Fact]
        public void SPL_EmptyReceiverList_ReturnsEmptyResults()
        {
            var input = BuildInput(new Vec3(0, 0, 0), new Vec3(1, 0, 0), 90.0,
                Array.Empty<Vec3>());

            var calc = new SPLCalculator();
            var (results, bandData) = calc.Calculate(input, CancellationToken.None, null);

            Assert.Empty(results);
            Assert.Empty(bandData);
        }

        [Fact]
        public void SPL_ResultCount_MatchesReceiverCount()
        {
            var positions = new[] {
                new Vec3(1, 0, 0), new Vec3(2, 0, 0), new Vec3(3, 0, 0),
                new Vec3(4, 0, 0), new Vec3(5, 0, 0)
            };
            var input = BuildInput(new Vec3(0, 0, 0), new Vec3(1, 0, 0), 90.0, positions);

            var calc = new SPLCalculator();
            var (results, bandData) = calc.Calculate(input, CancellationToken.None, null);

            Assert.Equal(5, results.Count);
            Assert.Equal(5, bandData.Count);
        }

        // ---------------------------------------------------------------------------
        // Acoustic physics tests
        // ---------------------------------------------------------------------------

        [Fact]
        public void SPL_AtOneMeter_IsNearOnAxisRating()
        {
            // Omni source at origin, receiver at 1 m.
            // Expected: SPL ≈ OnAxisSplDb (≈ 90 dB) with negligible air absorption.
            var input = BuildInput(new Vec3(0, 0, 0), new Vec3(1, 0, 0), 90.0,
                new[] { new Vec3(1, 0, 0) });

            var calc = new SPLCalculator();
            var (results, _) = calc.Calculate(input, CancellationToken.None, null);

            Assert.InRange(results[0].SplDb, 88.0, 91.5);
        }

        [Fact]
        public void SPL_InverseSquareLaw_DoublingDistanceDrops6dB()
        {
            // Receivers at 1 m and 2 m from the same omni source.
            // For free-field direct sound: ΔSPl = 20·log10(2) ≈ 6.02 dB.
            // Air absorption adds a slight extra loss at the higher distance,
            // so the difference will be ≥ 6 dB but only marginally above it
            // (< 0.5 dB extra at 2 m).
            var input = BuildInput(new Vec3(0, 0, 0), new Vec3(1, 0, 0), 90.0,
                new[] { new Vec3(1, 0, 0), new Vec3(2, 0, 0) });

            var calc = new SPLCalculator();
            var (results, _) = calc.Calculate(input, CancellationToken.None, null);

            results.Sort((a, b) => a.ReceiverIndex.CompareTo(b.ReceiverIndex));
            double diff = results[0].SplDb - results[1].SplDb;

            Assert.InRange(diff, 5.5, 6.5);
        }

        [Fact]
        public void SPL_SPLMonotonicallyDecreasesWithDistance()
        {
            // Receivers at 1–10 m from the source (no walls, Draft, no reverb).
            var positions = new List<Vec3>();
            for (int i = 1; i <= 10; i++)
                positions.Add(new Vec3(i, 0, 0));

            var input = BuildInput(new Vec3(0, 0, 0), new Vec3(1, 0, 0), 90.0, positions);

            var calc = new SPLCalculator();
            var (results, _) = calc.Calculate(input, CancellationToken.None, null);

            results.Sort((a, b) => a.ReceiverIndex.CompareTo(b.ReceiverIndex));

            for (int i = 1; i < results.Count; i++)
            {
                Assert.True(results[i].SplDb < results[i - 1].SplDb,
                    $"SPL at index {i} ({results[i].SplDb:F1} dB) should be less than at " +
                    $"index {i - 1} ({results[i - 1].SplDb:F1} dB)");
            }
        }

        [Fact]
        public void SPL_AddingSecondSource_RaisesSPL()
        {
            Vec3 recvPos = new Vec3(3, 0, 0);

            // Single source
            var input = BuildInput(new Vec3(0, 0, 0), new Vec3(1, 0, 0), 90.0,
                new[] { recvPos });
            var calc = new SPLCalculator();
            var (res1, _) = calc.Calculate(input, CancellationToken.None, null);
            double splSingle = res1[0].SplDb;

            // Two equal sources equidistant from the receiver
            var secondSource = new ComputeSource
            {
                Position = new Vec3(6, 0, 0),
                FacingDirection = new Vec3(-1, 0, 0),
                Profile = new SpeakerProfileMapping
                {
                    ProfileSource = ProfileSourceType.SimpleOmni,
                    OnAxisSplDb = 90.0
                }
            };
            var input2 = BuildInput(new Vec3(0, 0, 0), new Vec3(1, 0, 0), 90.0,
                new[] { recvPos }, extraSources: new List<ComputeSource> { secondSource });
            var (res2, _) = calc.Calculate(input2, CancellationToken.None, null);
            double splDouble = res2[0].SplDb;

            Assert.True(splDouble > splSingle,
                $"Two-source SPL ({splDouble:F1} dB) must exceed one-source SPL ({splSingle:F1} dB)");
        }

        [Fact]
        public void SPL_WallBetweenSourceAndReceiver_AttenuatesSPL()
        {
            Vec3 srcPos = new Vec3(0, 0, 0);
            Vec3 recvPos = new Vec3(5, 0, 0);

            // No wall
            var inputNoWall = BuildInput(srcPos, new Vec3(1, 0, 0), 90.0, new[] { recvPos });
            var calcA = new SPLCalculator();
            var (resNoWall, _) = calcA.Calculate(inputNoWall, CancellationToken.None, null);
            double splNoWall = resNoWall[0].SplDb;

            // STC-40 wall perpendicular to the direct path at x = 2.5 m
            var wall = new ComputeWall
            {
                Start = new Vec2(2.5, -5),
                End = new Vec2(2.5, 5),
                StcRating = 40,
                HalfThicknessM = 0.1
            };
            var inputWithWall = BuildInput(srcPos, new Vec3(1, 0, 0), 90.0, new[] { recvPos },
                walls: new List<ComputeWall> { wall });
            var calcB = new SPLCalculator();
            var (resWithWall, _) = calcB.Calculate(inputWithWall, CancellationToken.None, null);
            double splWithWall = resWithWall[0].SplDb;

            Assert.True(splWithWall < splNoWall,
                $"SPL behind wall ({splWithWall:F1} dB) must be less than without wall ({splNoWall:F1} dB)");
        }

        // ---------------------------------------------------------------------------
        // Per-band / broadband consistency
        // ---------------------------------------------------------------------------

        [Fact]
        public void SPL_BroadbandSPL_EqualsEnergySum_OfPerBandValues()
        {
            // The broadband SplDb must equal 10·log10( Σ 10^(SplDbByBand[k]/10) ).
            var input = BuildInput(new Vec3(0, 0, 0), new Vec3(1, 0, 0), 90.0,
                new[] { new Vec3(2, 0, 0) });

            var calc = new SPLCalculator();
            var (results, _) = calc.Calculate(input, CancellationToken.None, null);

            var r = results[0];
            double totalLinear = 0;
            for (int k = 0; k < OctaveBands.Count; k++)
                totalLinear += Math.Pow(10.0, r.SplDbByBand[k] / 10.0);
            double reconstructed = 10.0 * Math.Log10(totalLinear);

            Assert.InRange(r.SplDb - reconstructed, -0.05, 0.05);
        }

        [Fact]
        public void SPL_BandData_EarlyPlusLate_SumsToTotalBroadbandEnergy()
        {
            // With no reverberant field and one source, every path is classified as
            // early or late, so the total early+late linear energy must equal the
            // total energy that produced SplDb.
            var input = BuildInput(new Vec3(0, 0, 0), new Vec3(1, 0, 0), 90.0,
                new[] { new Vec3(3, 0, 0) });

            var calc = new SPLCalculator();
            var (results, bandData) = calc.Calculate(input, CancellationToken.None, null);

            var bd = bandData[0];
            double totalEarlyPlusLate = 0;
            for (int k = 0; k < OctaveBands.Count; k++)
                totalEarlyPlusLate += bd.EarlyLinearByBand[k] + bd.LateLinearByBand[k];

            double bandDataSpl = 10.0 * Math.Log10(totalEarlyPlusLate);

            Assert.InRange(bandDataSpl - results[0].SplDb, -0.05, 0.05);
        }

        [Fact]
        public void SPL_SingleDirectSource_AllEnergyClassifiedAsEarly()
        {
            // With one source and no reflections (Draft, no walls), the direct path is
            // the earliest (and only) arrival, so all energy must be "early".
            var input = BuildInput(new Vec3(0, 0, 0), new Vec3(1, 0, 0), 90.0,
                new[] { new Vec3(4, 0, 0) });

            var calc = new SPLCalculator();
            var (_, bandData) = calc.Calculate(input, CancellationToken.None, null);

            var bd = bandData[0];
            for (int k = 0; k < OctaveBands.Count; k++)
            {
                Assert.True(bd.EarlyLinearByBand[k] > 0,
                    $"Band {k}: early energy should be positive");
                Assert.Equal(0.0, bd.LateLinearByBand[k]);
            }
        }

        // ---------------------------------------------------------------------------
        // Cancellation test
        // ---------------------------------------------------------------------------

        [Fact]
        public void SPL_CancelledToken_ThrowsOperationCanceledException()
        {
            var positions = new List<Vec3>();
            for (int i = 1; i <= 20; i++)
                positions.Add(new Vec3(i, 0, 0));

            var input = BuildInput(new Vec3(0, 0, 0), new Vec3(1, 0, 0), 90.0, positions);

            using var cts = new CancellationTokenSource();
            cts.Cancel(); // pre-cancel

            var calc = new SPLCalculator();
            Assert.Throws<OperationCanceledException>(() =>
                calc.Calculate(input, cts.Token, null));
        }
    }
}
