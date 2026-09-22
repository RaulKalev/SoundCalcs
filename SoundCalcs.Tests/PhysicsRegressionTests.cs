using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Xunit;
using SoundCalcs.Compute;
using SoundCalcs.Domain;

namespace SoundCalcs.Tests
{
    /// <summary>
    /// Regression tests for acoustic-model bugs found with the headless harness:
    /// spurious wall blocking near walls, reflections using plan distance, reverberant
    /// field missing from SPL / double-counted in STI, C80 using the 50 ms split, and
    /// the directivity step behind speakers.
    /// </summary>
    public class PhysicsRegressionTests
    {
        private static readonly double[] Anechoic = Enumerable.Repeat(1.0, 7).ToArray();

        private static ComputeSource Omni(double x, double y, double z, double spl = 90) => new ComputeSource
        {
            Position = new Vec3(x, y, z),
            FacingDirection = new Vec3(0, 0, -1),
            Profile = new SpeakerProfileMapping { ProfileSource = ProfileSourceType.SimpleOmni, OnAxisSplDb = spl }
        };

        private static ComputeWall Wall(double x1, double y1, double x2, double y2, int stc, double[] absorption = null) =>
            new ComputeWall { Start = new Vec2(x1, y1), End = new Vec2(x2, y2), StcRating = stc, HalfThicknessM = 0.05, AbsorptionByBand = absorption };

        private static RoomPolygon Room(double x0, double y0, double x1, double y1, double enclosure) => new RoomPolygon
        {
            Vertices = { new Vec2(x0, y0), new Vec2(x1, y0), new Vec2(x1, y1), new Vec2(x0, y1) },
            EnclosureRatio = enclosure,
            CeilingHeightM = 3.0
        };

        private static (List<ReceiverResult> R, List<ReceiverBandData> B) Run(
            List<ComputeSource> sources, List<Vec3> receivers, List<ComputeWall> walls = null,
            List<RoomPolygon> rooms = null, CalculationQuality quality = CalculationQuality.Draft, double rt60 = 0.5)
        {
            var input = new AcousticJobInput
            {
                Sources = sources,
                Receivers = receivers.Select((p, i) => new ReceiverPoint(p, i) { RoomIndex = rooms != null ? 0 : -1 }).ToList(),
                Walls = walls ?? new List<ComputeWall>(),
                Rooms = rooms ?? new List<RoomPolygon>(),
                Environment = new EnvironmentSettings
                {
                    RT60ByBand = Enumerable.Repeat(rt60, 7).ToArray(),
                    BackgroundNoiseByBand = Enumerable.Repeat(20.0, 7).ToArray()
                },
                Quality = quality
            };
            return new SPLCalculator().Calculate(input, CancellationToken.None, null);
        }

        [Fact]
        public void WallBeyondTheReceiver_DoesNotBlock()
        {
            // Wall along y = −5 from x = −10 to 10. Receiver at (−9.7, −4.7): the infinite line
            // through speaker and receiver crosses the wall just beyond the receiver, which
            // used to count as blocked (≈60 dB loss).
            var walls = new List<ComputeWall> { Wall(-10.1, -5, 10.1, -5, 50, Anechoic) };
            var (res, _) = Run(new List<ComputeSource> { Omni(-5, 0, 1.2) },
                new List<Vec3> { new Vec3(-9.7, -4.7, 1.2) }, walls);

            double d = Math.Sqrt(4.7 * 4.7 * 2);
            double freeField = 90 - 20 * Math.Log10(d);
            Assert.InRange(res[0].SplDb, freeField - 0.3, freeField + 0.1);
        }

        [Fact]
        public void PathGrazingPastWallEnd_WithinHalfThickness_StillBlocks()
        {
            // Wall x = 0 from y = −5 to y = 1.00; path from (−2, 1.03) to (2, 1.03) passes 3 cm
            // above its end (half-thickness 5 cm) → bridged gap, counts as blocked.
            var walls = new List<ComputeWall> { Wall(0, -5, 0, 1.0, 40, Anechoic) };
            var (res, _) = Run(new List<ComputeSource> { Omni(-2, 1.03, 1.2) },
                new List<Vec3> { new Vec3(2, 1.03, 1.2) }, walls);
            Assert.True(res[0].SplDb < 90 - 20 * Math.Log10(4) - 15, $"got {res[0].SplDb}");
        }

        [Fact]
        public void WallReflection_UsesThreeDimensionalPathLength()
        {
            // Source at 3 m, receiver at 1.2 m, a fully reflective wall 2 m to the side.
            // Mirror-image path: plan length 4 m, height difference 1.8 m → 4.39 m.
            var reflective = Enumerable.Repeat(0.0, 7).ToArray();
            var walls = new List<ComputeWall> { Wall(-2, -50, -2, 50, 0, reflective) };
            var withWall = Run(new List<ComputeSource> { Omni(0, 0, 3) }, new List<Vec3> { new Vec3(0, 0, 1.2) }, walls).R[0];
            var without = Run(new List<ComputeSource> { Omni(0, 0, 3) }, new List<Vec3> { new Vec3(0, 0, 1.2) }).R[0];

            double reflected = Math.Pow(10, withWall.SplDb / 10) - Math.Pow(10, without.SplDb / 10);
            double expected = Math.Pow(10, 9) / (4 * 4 + 1.8 * 1.8); // air loss negligible at 4 m
            Assert.InRange(10 * Math.Log10(reflected), 10 * Math.Log10(expected) - 0.3, 10 * Math.Log10(expected) + 0.1);
        }

        [Fact]
        public void ReverberantField_RaisesFarFieldSpl_ToBarronLevel()
        {
            // 20×20 m closed room, RT60 1 s, omni at the centre, receiver 9 m away.
            var room = Room(-10, -10, 10, 10, 1.0);
            var src = new List<ComputeSource> { Omni(0, 0, 1.2) };
            var recv = new List<Vec3> { new Vec3(9, 0, 1.2) };
            var open = Run(src, recv).R[0];
            var closed = Run(src, recv, rooms: new List<RoomPolygon> { room }, rt60: 1.0).R[0];

            double vol = 400 * 3.0, sabineA = 0.161 * vol / 1.0;
            double c = 331.3 + 0.606 * 20;
            double barron = Math.Pow(10, 9) * 16 * Math.PI / sabineA * Math.Exp(-13.82 * 9 / (c * 1.0));
            double expected = 10 * Math.Log10(Math.Pow(10, open.SplDb / 10) + barron);
            Assert.InRange(closed.SplDb, expected - 0.1, expected + 0.1);
            Assert.True(closed.SplDb > open.SplDb + 5, $"reverberant field should dominate at 9 m: {open.SplDb} → {closed.SplDb}");
        }

        [Fact]
        public void ReverberantField_IsNotDoubleCountedInSti()
        {
            // Speech 30 dB above noise; RT60 0.5 s. The old model treated the reverberant energy
            // both as MTF decay and as noise and put STI in the "poor" range.
            var room = Room(-10, -10, 10, 10, 1.0);
            var (res, bd) = Run(new List<ComputeSource> { Omni(0, 0, 1.2) },
                new List<Vec3> { new Vec3(9, 0, 1.2) }, rooms: new List<RoomPolygon> { room });
            STICalculator.Calculate(res, bd, Enumerable.Repeat(20.0, 7).ToArray(), Enumerable.Repeat(0.5, 7).ToArray());
            Assert.InRange(res[0].Sti, 0.65, 0.85);
        }

        [Fact]
        public void SpeakerResponse_IsNormalised_BroadbandLevelUnchanged()
        {
            // A strongly shaped response must not change the broadband on-axis level at 1 m.
            var shaped = Omni(0, 0, 1.2);
            shaped.Profile.SpectrumShapeByBand = new double[] { -12, -5, -1, 0, 0, -2, -6 };
            var (res, _) = Run(new List<ComputeSource> { shaped }, new List<Vec3> { new Vec3(1, 0, 1.2) });
            Assert.InRange(res[0].SplDb, 89.9, 90.0);   // only air absorption over 1 m
            // …and the band levels follow the shape (1 kHz vs 125 Hz: 12 dB apart)
            Assert.InRange(res[0].SplDbByBand[3] - res[0].SplDbByBand[0], 11.9, 12.1);
        }

        [Fact]
        public void StiSignal_IsIecSpeechThroughSpeakerResponse()
        {
            // Flat speaker, 1 m: STI band energies follow the IEC male speech spectrum,
            // normalised to the speaker's broadband level; SPL stays flat.
            var (res, bd) = Run(new List<ComputeSource> { Omni(0, 0, 1.2) }, new List<Vec3> { new Vec3(1, 0, 1.2) });
            double[] e = Enumerable.Range(0, 7).Select(k => bd[0].EarlyLinearByBand[k] + bd[0].LateLinearByBand[k]).ToArray();
            double[] speech = OctaveBands.EnergyFractions(OctaveBands.MaleSpeechSpectrumDb);
            double total = e.Sum();
            for (int k = 0; k < 5; k++)   // below 4 kHz air absorption over 1 m is negligible
                Assert.InRange(e[k] / total, speech[k] * 0.995, speech[k] * 1.005);
            Assert.InRange(10 * Math.Log10(total), 89.9, 90.0);
            Assert.InRange(res[0].SplDbByBand[3] - res[0].SplDbByBand[0], -0.05, 0.05);
        }

        private static double[] A(WallAbsorptionPreset p) => OctaveBands.AbsorptionPresets[p];

        [Fact]
        public void Rt60FromGeometry_ClosedBox_MatchesHandCalculation()
        {
            // 10 × 8 × 3 m, concrete walls fully on the perimeter, concrete floor, plasterboard ceiling.
            var walls = new[] { (36.0, A(WallAbsorptionPreset.Concrete)) };
            double[] rt = RoomAcoustics.EstimateRt60FromGeometry(80, 36, 3, walls, 36,
                A(WallAbsorptionPreset.Concrete), A(WallAbsorptionPreset.Drywall), 0, 20, 50);

            // Hand calculation at 500 Hz (k = 2): S = 2·80 + 36·3 = 268 m², V = 240 m³
            double alpha = (80 * 0.02 + 80 * 0.05 + 108 * 0.02) / 268.0;
            double m = OctaveBands.ComputeAirAbsorption(20, 50)[2] / 4.3429;
            double expected = 0.161 * 240 / (-268 * Math.Log(1 - alpha) + 4 * m * 240);
            Assert.Equal(Math.Round(expected, 2), rt[2], 2);
            Assert.InRange(rt[2], 4.0, 5.0);
        }

        [Fact]
        public void Rt60FromGeometry_OpenBoundaryOccupantsAndPartitionsShortenIt()
        {
            var concrete = A(WallAbsorptionPreset.Concrete);
            double Rt(double lineLength, double covered, int people) => RoomAcoustics.EstimateRt60FromGeometry(
                80, 36, 3, new[] { (lineLength, concrete) }, covered, concrete, concrete, people, 20, 50)[3];

            double closed = Rt(36, 36, 0);
            Assert.True(Rt(18, 18, 0) < closed * 0.5, "half the perimeter open: sound leaves the room");
            Assert.True(Rt(36, 36, 40) < closed * 0.6, "40 people absorb ≈18 m² at 1 kHz");
            // 10 m internal partition: both faces add (hard) surface → slightly shorter, never longer
            Assert.True(Rt(46, 36, 0) < closed);
        }

        [Fact]
        public void Rt60FromGeometry_AirAbsorptionShortensHighBandsInLargeHalls()
        {
            // 40 × 30 × 12 m hall, hard surfaces: at 8 kHz air absorption dominates.
            var concrete = A(WallAbsorptionPreset.Concrete);
            double[] rt = RoomAcoustics.EstimateRt60FromGeometry(1200, 140, 12, new[] { (140.0, concrete) }, 140,
                concrete, concrete, 0, 20, 50);
            Assert.True(rt[6] < rt[3] * 0.5, $"8 kHz {rt[6]} s vs 1 kHz {rt[3]} s");
        }

        [Fact]
        public void C80_UsesEightyMillisecondSplit()
        {
            // An echo 65 ms after the direct sound is late for D50 but early for C80.
            var bd = new ReceiverBandData
            {
                Early80LinearByBand = Enumerable.Repeat(2.0, 7).ToArray(),
                Late80LinearByBand = Enumerable.Repeat(0.0, 7).ToArray()
            };
            for (int k = 0; k < 7; k++) { bd.EarlyLinearByBand[k] = 1; bd.LateLinearByBand[k] = 1; }
            var r = new List<ReceiverResult> { new ReceiverResult() };
            STICalculator.ComputeC80D50(r, new List<ReceiverBandData> { bd });
            Assert.Equal(0.5, r[0].D50, 3);
            Assert.Equal(15.0, r[0].C80Db);
        }

        [Fact]
        public void ConeSpeaker_LowBandsHaveShallowRearFloor_AndNoStepAt90Degrees()
        {
            var cone = new SimpleConeProvider(90, 60, -12);
            Vec3 facing = new Vec3(1, 0, 0);
            double Db(double g) => 20 * Math.Log10(g);

            // Behind: −12 dB at ≥ 1 kHz, −1.5 dB at 125 Hz
            Assert.Equal(-12.0, Db(cone.GetDirectivityGainForBand(facing, new Vec3(-1, 0, 0), 3)), 3);
            Assert.Equal(-1.5, Db(cone.GetDirectivityGainForBand(facing, new Vec3(-1, 0, 0), 0)), 3);

            // Continuity across 90° for every band
            Vec3 justFront = new Vec3(Math.Cos(Math.PI / 2 - 0.01), Math.Sin(Math.PI / 2 - 0.01), 0);
            Vec3 justBehind = new Vec3(Math.Cos(Math.PI / 2 + 0.01), Math.Sin(Math.PI / 2 + 0.01), 0);
            for (int k = 0; k < 7; k++)
            {
                double step = Db(cone.GetDirectivityGainForBand(facing, justFront, k))
                            - Db(cone.GetDirectivityGainForBand(facing, justBehind, k));
                Assert.True(Math.Abs(step) < 3.0, $"band {k}: {step:F2} dB step at 90°");
            }
        }
    }
}
