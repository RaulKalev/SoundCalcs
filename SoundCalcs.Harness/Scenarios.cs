using System;
using System.Collections.Generic;
using System.Linq;
using SoundCalcs.Compute;
using SoundCalcs.Domain;
using SoundCalcs.UI.ViewModels;
using SoundCalcs.Visualization;

namespace SoundCalcs.Harness
{
    /// <summary>A scenario plus the physics it is expected to reproduce.</summary>
    public class Scenario
    {
        public ScenarioSpec Spec { get; set; }

        /// <summary>Scenario-specific checks; generic visual checks always run too.</summary>
        public Action<ScenarioRun, CheckContext> Checks { get; set; }

        public VisualizationMode[] ViewerModes { get; set; } =
            { VisualizationMode.SPL, VisualizationMode.STI };

        public VisualizationMode[] RevitModes { get; set; } = { VisualizationMode.SPL };

        /// <summary>False for scenarios that only exercise calculators directly.</summary>
        public bool HasScene { get; set; } = true;
    }

    public class ScenarioRun
    {
        public ScenarioSpec Spec { get; set; }
        public AcousticJobInput Input { get; set; }
        public AcousticJobOutput Output { get; set; }

        public static ScenarioRun Execute(ScenarioSpec spec)
        {
            var input = spec.BuildInput();
            var output = JobRunner.Compute(input, System.Threading.CancellationToken.None, null);
            return new ScenarioRun { Spec = spec, Input = input, Output = output };
        }

        public ReceiverResult Nearest(double x, double y) =>
            Output.Results.OrderBy(r => Sq(r.Position.X - x) + Sq(r.Position.Y - y)).First();

        static double Sq(double v) => v * v;
    }

    /// <summary>Closed-form expectations, derived independently of SPLCalculator.</summary>
    public static class Analytic
    {
        /// <summary>
        /// Direct-path band power from an omni (or on-axis) source: flat 7-band split,
        /// inverse-square spreading, ISO 9613-1 air absorption and optional per-band TL.
        /// </summary>
        public static double[] DirectBandPower(double splAt1m, double distance, double[] airDbPerM,
            double[] tlDb = null, double[] gainDb = null)
        {
            var p = new double[7];
            double perBand = Math.Pow(10, splAt1m / 10) / 7;
            for (int k = 0; k < 7; k++)
            {
                double lossDb = airDbPerM[k] * distance + (tlDb?[k] ?? 0) - (gainDb?[k] ?? 0);
                p[k] = perBand / (distance * distance) * Math.Pow(10, -lossDb / 10);
            }
            return p;
        }

        public static double Db(double linear) => 10 * Math.Log10(linear);

        public static double Sum(IEnumerable<double[]> bandPowers) => bandPowers.Sum(b => b.Sum());

        /// <summary>Documented wall model: TL_k = max(0, ΣSTC + StcBandOffsets[k] − 5 dB field penalty).</summary>
        public static double[] WallTl(double stcSum) =>
            OctaveBands.StcBandOffsets.Select(o => stcSum > 0 ? Math.Max(0, stcSum + o - 5.0) : 0).ToArray();
    }

    public static class BuiltInScenarios
    {
        public static List<Scenario> All()
        {
            return new List<Scenario>
            {
                FreeFieldOmni(),
                TwoSourcesSum(),
                WallPartition(),
                ConeCeiling(),
                WallMountedAim(),
                ReverberantRoom(),
                StiReference(),
            };
        }

        static double Dist(Vec3 a, Vec3 b) => (a - b).Length;

        static double[] Air(ScenarioSpec s) =>
            OctaveBands.ComputeAirAbsorption(s.Environment.TemperatureC, s.Environment.RelativeHumidityPct);

        // -----------------------------------------------------------------
        // 1. Free field: one omni, no walls, no reflections → inverse square law
        // -----------------------------------------------------------------
        static Scenario FreeFieldOmni()
        {
            var spec = new ScenarioSpec
            {
                Name = "free_field_omni",
                Description = "Single 90 dB omni at ear height in an open 16×16 m area, Draft quality " +
                              "(no reflections), no walls. SPL must follow 90 − 20·log10(r) − air absorption.",
                Boundary = ScenarioSpec.Rectangle(-8, -8, 8, 8),
                Quality = CalculationQuality.Draft,
                Speakers = { new SpeakerSpec { X = 0, Y = 0, HeightM = 1.2, Profile = ScenarioSpec.Omni(90) } }
            };

            return new Scenario
            {
                Spec = spec,
                ViewerModes = new[] { VisualizationMode.SPL, VisualizationMode.STI, VisualizationMode.SPL_8k },
                RevitModes = new[] { VisualizationMode.SPL, VisualizationMode.STI },
                Checks = (run, ctx) =>
                {
                    var air = Air(run.Spec);
                    var src = run.Input.Sources[0].Position;

                    double worst = 0; ReceiverResult worstR = null;
                    foreach (var r in run.Output.Results)
                    {
                        double d = Dist(r.Position, src);
                        if (d < 0.5) continue;
                        double expect = Analytic.Db(Analytic.DirectBandPower(90, d, air).Sum());
                        double err = Math.Abs(r.SplDb - expect);
                        if (err > worst) { worst = err; worstR = r; }
                    }
                    ctx.Assert("SPL matches 90 − 20log(r) − air at every receiver (±0.05 dB)", worst <= 0.05,
                        $"worst error {CheckContext.F(worst)} dB at {worstR?.Position}");

                    var r2 = run.Nearest(2.2, 0.3); var r4 = run.Nearest(4.2, 0.3);
                    double d2 = Dist(r2.Position, src), d4 = Dist(r4.Position, src);
                    ctx.Near("inverse square: level drop between r and ≈2r", r2.SplDb - r4.SplDb,
                        20 * Math.Log10(d4 / d2), 0.1, " dB");

                    var loudest = run.Output.Results.OrderByDescending(r => r.SplDb).First();
                    ctx.Assert("loudest receiver is the one nearest the speaker",
                        Math.Abs(loudest.Position.X - src.X) <= run.Spec.GridSpacingM &&
                        Math.Abs(loudest.Position.Y - src.Y) <= run.Spec.GridSpacingM,
                        $"loudest at {loudest.Position}, speaker at {src}");

                    ctx.Near("no walls → room enclosure ratio 0 (no reverberant field)",
                        run.Input.Rooms[0].EnclosureRatio, 0, 1e-9);
                    ctx.Assert("no late energy → D50 = 1 and C80 = +15 dB everywhere",
                        run.Output.Results.All(r => r.D50 == 1.0 && r.C80Db == 15.0),
                        $"D50 range {run.Output.Results.Min(r => r.D50)}..{run.Output.Results.Max(r => r.D50)}");

                    var near = run.Output.Results.Where(r => Dist(r.Position, src) < 2).Average(r => r.Sti);
                    var far = run.Output.Results.Where(r => Dist(r.Position, src) > 7).Average(r => r.Sti);
                    ctx.Assert("STI falls with distance as SNR drops", near > far,
                        $"mean STI < 2 m: {CheckContext.F(near)}, > 7 m: {CheckContext.F(far)}");

                    double hf = run.Output.Results.Where(r => Dist(r.Position, src) > 7).Average(r => r.SplDbByBand[3] - r.SplDbByBand[6]);
                    double expHf = run.Output.Results.Where(r => Dist(r.Position, src) > 7)
                        .Average(r => (air[6] - air[3]) * Dist(r.Position, src));
                    ctx.Near("8 kHz band loses extra air absorption vs 1 kHz far away", hf, expHf, 0.05, " dB");

                    // The viewer's GridSpacing is bound to the live settings field, not to the
                    // spacing the results were computed with. Simulate the user changing the
                    // field from 0.5 m to 1.0 m after a run.
                    var vals = run.Output.Results.Select(r => r.SplDb).ToArray();
                    var (lo, hi) = HeatmapMath.ComputeViewerRange(vals, VisualizationMode.SPL);
                    var stale = HeatmapMath.BuildViewerGrid(run.Output.Results, vals, lo, hi - lo, 1.0);
                    ctx.Assert("viewer heatmap survives editing Grid Spacing after a run",
                        stale.Collisions == 0,
                        $"with the field changed 0.5 → 1.0 m, {stale.Collisions}/{vals.Length} receivers " +
                        "overwrite each other in the viewer bitmap until the analysis is re-run",
                        CheckSeverity.Warn);
                }
            };
        }

        // -----------------------------------------------------------------
        // 2. Two incoherent sources add in energy (+3 dB at equal distance)
        // -----------------------------------------------------------------
        static Scenario TwoSourcesSum()
        {
            var spec = new ScenarioSpec
            {
                Name = "two_sources_sum",
                Description = "Two identical 90 dB omnis 6 m apart, Draft, no walls. Levels add as " +
                              "energies: +3.01 dB on the perpendicular bisector.",
                // 0.3 m boundary offset + 0.5 m grid → receivers on x = 0, ±0.5, …
                Boundary = ScenarioSpec.Rectangle(-8.3, -6.3, 8.3, 6.3),
                Quality = CalculationQuality.Draft,
                Speakers =
                {
                    new SpeakerSpec { X = -3, Y = 0, HeightM = 1.2, Profile = ScenarioSpec.Omni(90) },
                    new SpeakerSpec { X = 3, Y = 0, HeightM = 1.2, Profile = ScenarioSpec.Omni(90) },
                }
            };

            return new Scenario
            {
                Spec = spec,
                Checks = (run, ctx) =>
                {
                    var air = Air(run.Spec);
                    double worst = 0;
                    foreach (var r in run.Output.Results)
                    {
                        var powers = run.Input.Sources.Select(s =>
                            Analytic.DirectBandPower(90, Math.Max(0.01, Dist(r.Position, s.Position)), air));
                        double expect = Analytic.Db(Analytic.Sum(powers));
                        worst = Math.Max(worst, Math.Abs(r.SplDb - expect));
                    }
                    ctx.Assert("SPL = energy sum of both sources at every receiver (±0.05 dB)", worst <= 0.05,
                        $"worst error {CheckContext.F(worst)} dB");

                    var mid = run.Output.Results.Where(r => Math.Abs(r.Position.X) < 1e-6).ToList();
                    ctx.Assert("receivers exist on the bisector x = 0", mid.Count > 0, $"{mid.Count}");
                    double gain = mid.Average(r =>
                    {
                        double d = Dist(r.Position, run.Input.Sources[0].Position);
                        return r.SplDb - Analytic.Db(Analytic.DirectBandPower(90, d, air).Sum());
                    });
                    ctx.Near("equidistant receivers are +3 dB over one source", gain, 3.01, 0.1, " dB");
                }
            };
        }

        // -----------------------------------------------------------------
        // 3. Partition wall: shadow side attenuated by the documented STC model,
        //    open side untouched (catches spurious wall hits near boundaries)
        // -----------------------------------------------------------------
        static Scenario WallPartition()
        {
            var walls = ScenarioSpec.RectangleWalls(-10, -5, 10, 5, 50);
            walls.Add(new WallSpec { X1 = 0, Y1 = -5, X2 = 0, Y2 = 5, Stc = 45 });
            var spec = new ScenarioSpec
            {
                Name = "wall_partition",
                Description = "20×10 m room of STC-50 walls split by a full-width STC-45 partition at x=0. " +
                              "Omni at (−5,0), Draft, walls made fully absorbing so only direct sound and " +
                              "transmission remain. Left half must be pure free field; right half must " +
                              "carry exactly one partition's per-band TL.",
                Walls = walls,
                AnechoicWalls = true,
                Quality = CalculationQuality.Draft,
                Speakers = { new SpeakerSpec { X = -5, Y = 0, HeightM = 1.2, Profile = ScenarioSpec.Omni(90) } }
            };

            return new Scenario
            {
                Spec = spec,
                RevitModes = new[] { VisualizationMode.SPL, VisualizationMode.SPL_125 },
                ViewerModes = new[] { VisualizationMode.SPL, VisualizationMode.SPL_125, VisualizationMode.STI },
                Checks = (run, ctx) =>
                {
                    var air = Air(run.Spec);
                    var src = run.Input.Sources[0].Position;
                    var tl = Analytic.WallTl(45);

                    var left = run.Output.Results.Where(r => r.Position.X < -0.2).ToList();
                    var right = run.Output.Results.Where(r => r.Position.X > 0.2).ToList();

                    var leftBad = left.Where(r => Math.Abs(r.SplDb -
                        Analytic.Db(Analytic.DirectBandPower(90, Dist(r.Position, src), air).Sum())) > 0.05).ToList();
                    ctx.Assert("source side: no wall attenuation on unobstructed paths", leftBad.Count == 0,
                        $"{leftBad.Count}/{left.Count} receivers attenuated although the straight path to the speaker " +
                        $"crosses no wall; e.g. {Describe(leftBad, src, air)}");

                    var rightBad = right.Where(r => Math.Abs(r.SplDb -
                        Analytic.Db(Analytic.DirectBandPower(90, Dist(r.Position, src), air, tl).Sum())) > 0.05).ToList();
                    ctx.Assert("shadow side: exactly one STC-45 partition in the path", rightBad.Count == 0,
                        $"{rightBad.Count}/{right.Count} receivers deviate from the one-wall TL; e.g. {Describe(rightBad, src, air, tl)}");

                    var a = run.Nearest(-9.5, 0); var b = run.Nearest(-0.5, 0); var c = run.Nearest(0.5, 0);
                    ctx.InRange("level step across the partition", b.SplDb - c.SplDb, 20, 45, " dB");
                    ctx.Assert("receiver behind the speaker (same room) louder than one behind the wall",
                        a.SplDb > c.SplDb, $"{CheckContext.F(a.SplDb)} vs {CheckContext.F(c.SplDb)} dB");
                }
            };
        }

        static string Describe(List<ReceiverResult> bad, Vec3 src, double[] air, double[] tl = null)
        {
            if (bad.Count == 0) return "-";
            var r = bad[0];
            double expect = Analytic.Db(Analytic.DirectBandPower(90, Dist(r.Position, src), air, tl).Sum());
            return $"({r.Position.X:F1}, {r.Position.Y:F1}) got {r.SplDb:F1} dB, expected {expect:F1} dB";
        }

        // -----------------------------------------------------------------
        // 4. Ceiling cone speaker: −6 dB at the rated half-angle, beaming with frequency
        // -----------------------------------------------------------------
        static Scenario ConeCeiling()
        {
            var spec = new ScenarioSpec
            {
                Name = "cone_ceiling",
                Description = "One 90 dB conical speaker (60° half-angle @1 kHz, −12 dB off-axis) at 3 m " +
                              "aiming down, Draft, no walls. Level relative to on-axis must be 0 dB below the " +
                              "speaker, −6 dB at 60°, never below −12 dB, and narrower at high frequencies.",
                Boundary = ScenarioSpec.Rectangle(-8, -8, 8, 8),
                Quality = CalculationQuality.Draft,
                Speakers = { new SpeakerSpec { X = 0, Y = 0, HeightM = 3.0, Profile = ScenarioSpec.Cone(90, 60, -12) } }
            };

            return new Scenario
            {
                Spec = spec,
                ViewerModes = new[] { VisualizationMode.SPL, VisualizationMode.SPL_1k, VisualizationMode.SPL_8k },
                Checks = (run, ctx) =>
                {
                    var air = Air(run.Spec);
                    var src = run.Input.Sources[0].Position;
                    double bandRef = 90 - 10 * Math.Log10(7);

                    // Directivity excess in dB for band k: measured − (on-axis spreading + air)
                    double Excess(ReceiverResult r, int k)
                    {
                        double d = Dist(r.Position, src);
                        return r.SplDbByBand[k] - (bandRef - 20 * Math.Log10(d) - air[k] * d);
                    }
                    double Angle(ReceiverResult r)
                    {
                        var v = r.Position - src;
                        return Math.Acos(-v.Z / v.Length) * 180 / Math.PI;
                    }

                    var below = run.Nearest(src.X, src.Y);
                    ctx.Near("on-axis (straight below) 1 kHz excess ≈ 0 dB", Excess(below, 3), 0, 0.3, " dB");

                    var edge = run.Output.Results.Where(r => Math.Abs(Angle(r) - 60) < 1.5).ToList();
                    ctx.Assert("receivers exist near the 60° edge", edge.Count > 0, $"{edge.Count}");
                    if (edge.Count > 0)
                        ctx.Near("1 kHz excess at the rated 60° half-angle ≈ −6 dB", edge.Average(r => Excess(r, 3)), -6.02, 0.6, " dB");

                    double floor = run.Output.Results.Min(r => Enumerable.Range(0, 7).Min(k => Excess(r, k)));
                    ctx.Assert("no band drops below the −12 dB off-axis floor", floor >= -12.05,
                        $"lowest excess {CheckContext.F(floor)} dB");

                    var mid = run.Output.Results.Where(r => Math.Abs(Angle(r) - 45) < 2).ToList();
                    double e125 = mid.Average(r => Excess(r, 0)), e1k = mid.Average(r => Excess(r, 3)), e8k = mid.Average(r => Excess(r, 6));
                    ctx.Assert("beaming: at 45° off-axis 8 kHz < 1 kHz < 125 Hz", e8k < e1k && e1k < e125,
                        $"125 Hz {CheckContext.F(e125)}, 1 kHz {CheckContext.F(e1k)}, 8 kHz {CheckContext.F(e8k)} dB");

                    var loudest = run.Output.Results.OrderByDescending(r => r.SplDb).First();
                    ctx.Assert("loudest receiver directly below the speaker",
                        Math.Abs(loudest.Position.X) <= spec.GridSpacingM && Math.Abs(loudest.Position.Y) <= spec.GridSpacingM,
                        $"loudest at {loudest.Position}");
                }
            };
        }

        // -----------------------------------------------------------------
        // 5. Wall-mounted speaker aims horizontally along its drag line
        // -----------------------------------------------------------------
        static Scenario WallMountedAim()
        {
            var spec = new ScenarioSpec
            {
                Name = "wall_mounted_aim",
                Description = "Wall-mounted 90 dB speaker at (0,0) 2 m high, aimed +X, Draft, no walls. " +
                              "Front must be ≈12 dB louder than the back at equal distance.",
                Boundary = ScenarioSpec.Rectangle(-8.3, -6.3, 8.3, 6.3),
                Quality = CalculationQuality.Draft,
                Speakers =
                {
                    new SpeakerSpec { X = 0, Y = 0, HeightM = 2.0, FacingX = 1, FacingY = 0,
                                      Profile = ScenarioSpec.WallMount(90, 60, -12) }
                }
            };

            return new Scenario
            {
                Spec = spec,
                Checks = (run, ctx) =>
                {
                    ctx.Assert("facing resolved to horizontal +X",
                        run.Input.Sources[0].FacingDirection.Equals(new Vec3(1, 0, 0)),
                        $"{run.Input.Sources[0].FacingDirection}");

                    var front = run.Nearest(4.2, 0.2); var back = run.Nearest(-4.2, 0.2);
                    ctx.InRange("front − back at 4 m", front.SplDb - back.SplDb, 10.5, 12.5, " dB");

                    var byPos = run.Output.Results.ToDictionary(r => (Math.Round(r.Position.X, 2), Math.Round(r.Position.Y, 2)));
                    int mirrored = 0, louderFront = 0;
                    foreach (var r in run.Output.Results.Where(r => r.Position.X > 1))
                    {
                        if (byPos.TryGetValue((Math.Round(-r.Position.X, 2), Math.Round(r.Position.Y, 2)), out var m))
                        {
                            mirrored++;
                            if (r.SplDb > m.SplDb) louderFront++;
                        }
                    }
                    ctx.Assert("every receiver in front is louder than its mirror behind",
                        mirrored > 0 && louderFront == mirrored, $"{louderFront}/{mirrored}");

                    // Directivity should be continuous: the level change over x = −0.5 → 0 → +0.5
                    // across the speaker's 90° plane must not contain a step of many dB.
                    double worstJump = 0; int worstBand = 0; double worstY = 0;
                    foreach (var r in run.Output.Results.Where(r => Math.Abs(r.Position.X) < 1e-6 && Math.Abs(r.Position.Y) > 2))
                    {
                        if (!byPos.TryGetValue((Math.Round(r.Position.X + 0.5, 2), Math.Round(r.Position.Y, 2)), out var ahead)) continue;
                        if (!byPos.TryGetValue((Math.Round(r.Position.X - 0.5, 2), Math.Round(r.Position.Y, 2)), out var behind)) continue;
                        for (int k = 0; k < 7; k++)
                        {
                            // second difference: ~0 for a smooth falloff, large for a step
                            double jump = Math.Abs(ahead.SplDbByBand[k] - 2 * r.SplDbByBand[k] + behind.SplDbByBand[k]);
                            if (jump > worstJump) { worstJump = jump; worstBand = k; worstY = r.Position.Y; }
                        }
                    }
                    ctx.Assert("directivity continuous across the 90° plane (no step > 3 dB)", worstJump <= 3,
                        $"{CheckContext.F(worstJump)} dB step at {OctaveBands.Labels[worstBand]} Hz, y = {CheckContext.F(worstY)} m: " +
                        "low bands are almost omni in front but drop straight to the off-axis floor behind",
                        CheckSeverity.Warn);
                }
            };
        }

        // -----------------------------------------------------------------
        // 6. Closed reverberant room: reflections, STI trends, C80/D50
        // -----------------------------------------------------------------
        static Scenario ReverberantRoom()
        {
            var spec = new ScenarioSpec
            {
                Name = "reverberant_room",
                Description = "12×8 m room of STC-50 walls, two ceiling cones at 3 m, Full quality " +
                              "(1st/2nd-order wall reflections + floor/ceiling + Sabine reverb). Checks " +
                              "energy conservation vs Draft, STI trends with RT60/noise, and C80/D50.",
                Walls = ScenarioSpec.RectangleWalls(0, 0, 12, 8, 50),
                Quality = CalculationQuality.Full,
                Speakers =
                {
                    new SpeakerSpec { X = 3, Y = 4, HeightM = 3.0, Profile = ScenarioSpec.Cone(90, 60, -12) },
                    new SpeakerSpec { X = 9, Y = 4, HeightM = 3.0, Profile = ScenarioSpec.Cone(90, 60, -12) },
                }
            };

            return new Scenario
            {
                Spec = spec,
                ViewerModes = new[] { VisualizationMode.SPL, VisualizationMode.STI, VisualizationMode.C80 },
                RevitModes = new[] { VisualizationMode.SPL, VisualizationMode.STI },
                Checks = (run, ctx) =>
                {
                    ctx.Near("closed room → enclosure ratio 1", run.Input.Rooms[0].EnclosureRatio, 1, 0.02);
                    ctx.Near("ceiling height taken from speaker elevation", run.Input.Rooms[0].CeilingHeightM, 3.0, 1e-9, " m");

                    var draftSpec = run.Spec.Clone("_draft"); draftSpec.Quality = CalculationQuality.Draft;
                    var draft = ScenarioRun.Execute(draftSpec);
                    int lower = run.Output.Results.Zip(draft.Output.Results, (f, d) => f.SplDb < d.SplDb - 1e-9 ? 1 : 0).Sum();
                    ctx.Assert("Full ≥ Draft SPL everywhere (reflections only add energy)", lower == 0,
                        $"{lower} receivers quieter in Full");
                    double gain = run.Output.Results.Zip(draft.Output.Results, (f, d) => f.SplDb - d.SplDb).Average();
                    ctx.InRange("mean reflection gain Full − Draft", gain, 0.5, 10, " dB");

                    var slowSpec = run.Spec.Clone("_rt60x3");
                    slowSpec.Environment.RT60ByBand = slowSpec.Environment.RT60ByBand.Select(t => t * 3).ToArray();
                    var slow = ScenarioRun.Execute(slowSpec);
                    double sti0 = run.Output.Results.Average(r => r.Sti), sti1 = slow.Output.Results.Average(r => r.Sti);
                    ctx.Assert("tripling RT60 lowers mean STI", sti1 < sti0 - 0.05,
                        $"{CheckContext.F(sti0)} → {CheckContext.F(sti1)}");

                    var noisySpec = run.Spec.Clone("_noise+30");
                    noisySpec.Environment.BackgroundNoiseByBand = noisySpec.Environment.BackgroundNoiseByBand.Select(n => n + 30).ToArray();
                    var noisy = ScenarioRun.Execute(noisySpec);
                    double sti2 = noisy.Output.Results.Average(r => r.Sti);
                    ctx.Assert("+30 dB background noise lowers mean STI", sti2 < sti0 - 0.01,
                        $"{CheckContext.F(sti0)} → {CheckContext.F(sti2)}");

                    // Reference STI from the same early/late/noise energies, treating the late
                    // energy as one exponential tail (reverberation counted once).
                    var (_, bandData) = new SPLCalculator().Calculate(run.Input, System.Threading.CancellationToken.None, null);
                    double[] noiseLin = run.Spec.Environment.BackgroundNoiseByBand.Select(n => Math.Pow(10, n / 10)).ToArray();
                    double refSti = bandData.Average(b => IecReference.StiFromEnergies(
                        b.EarlyLinearByBand, b.LateLinearByBand, noiseLin, run.Spec.Environment.RT60ByBand));
                    ctx.Near("mean STI vs reference that counts reverberation once", sti0, refSti, 0.05,
                        severity: CheckSeverity.Warn);

                    // C80 by definition uses an 80 ms early/late split; D50 uses 50 ms.
                    // If C80 == 10·log10(D50/(1−D50)) everywhere, C80 is really C50.
                    var both = run.Output.Results.Where(r => r.D50 > 0.001 && r.D50 < 0.999).ToList();
                    int identical = both.Count(r => Math.Abs(r.C80Db - 10 * Math.Log10(r.D50 / (1 - r.D50))) < 0.05);
                    ctx.Assert("C80 uses an 80 ms early/late split (not the 50 ms D50 split)",
                        both.Count == 0 || identical < both.Count,
                        $"C80 equals 10·log10(D50/(1−D50)) at {identical}/{both.Count} receivers, i.e. the map shows C50",
                        CheckSeverity.Warn);

                    // Diffuse field: in a closed room SPL should level off at the Sabine
                    // reverberant level L ≈ Lw + 10·log10(4/A) far from the speakers.
                    double rt = run.Spec.Environment.RT60ByBand.Average();
                    double vol = run.Input.Rooms[0].Area * 3.0;
                    double A = 0.161 * vol / rt;
                    double q = new SimpleConeProvider(90, 60, -12).DirectivityFactor;
                    double revPerSrc = Math.Pow(10, 9) * 16 * Math.PI / (q * A); // on-axis 1 m intensity × 16π/(Q·A)
                    double revDb = Analytic.Db(revPerSrc * run.Input.Sources.Count);
                    double farSpl = run.Output.Results.OrderBy(r => r.SplDb).Take(10).Average(r => r.SplDb);
                    ctx.Assert("quietest SPL not below the Sabine reverberant level",
                        farSpl >= revDb - 1.0,
                        $"quietest 10 receivers average {CheckContext.F(farSpl)} dB, Sabine diffuse level " +
                        $"{CheckContext.F(revDb)} dB (A = {CheckContext.F(A)} m² Sabine). The reverberant field only " +
                        "feeds STI/C80 as late energy and is not added to the SPL map.",
                        CheckSeverity.Warn);
                }
            };
        }

        // -----------------------------------------------------------------
        // 7. STI calculator against closed-form and IEC 60268-16 reference values
        // -----------------------------------------------------------------
        static Scenario StiReference()
        {
            return new Scenario
            {
                HasScene = false,
                Spec = new ScenarioSpec
                {
                    Name = "sti_reference",
                    Description = "STICalculator fed synthetic early/late energies with flat spectra: " +
                                  "exact TI end points, and comparison with an independent IEC 60268-16:2011 " +
                                  "(indirect method, male) implementation for pure noise and pure reverberation."
                },
                Checks = (run, ctx) =>
                {
                    double noiseDb = 40;
                    double Plugin(double snr, double t60)
                    {
                        var bd = new ReceiverBandData();
                        for (int k = 0; k < 7; k++)
                            bd.EarlyLinearByBand[k] = double.IsPositiveInfinity(snr)
                                ? Math.Pow(10, 12) : Math.Pow(10, (noiseDb + snr) / 10);
                        var res = new List<ReceiverResult> { new ReceiverResult() };
                        STICalculator.Calculate(res, new List<ReceiverBandData> { bd },
                            IecReference.Fill(double.IsPositiveInfinity(snr) ? -200 : noiseDb),
                            IecReference.Fill(t60));
                        return res[0].Sti;
                    }

                    ctx.Near("SNR +15 dB, no reverb → STI 1.0", Plugin(15, 0), 1.0, 0.01);
                    ctx.Near("SNR 0 dB, no reverb → STI 0.5", Plugin(0, 0), 0.5, 0.01);
                    ctx.Near("SNR −15 dB, no reverb → STI 0.0", Plugin(-15, 0), 0.0, 0.01);

                    foreach (double snr in new[] { -6.0, 0, 6, 12 })
                        ctx.Near($"noise only, SNR {snr:+0;-0} dB vs IEC reference",
                            Plugin(snr, 0), IecReference.Sti(IecReference.Fill(snr), IecReference.Fill(0)), 0.03,
                            severity: CheckSeverity.Warn);

                    foreach (double t in new[] { 0.5, 1.0, 2.0, 4.0 })
                        ctx.Near($"reverb only, T60 {t:0.0} s vs IEC reference",
                            Plugin(double.PositiveInfinity, t),
                            IecReference.Sti(IecReference.Fill(double.PositiveInfinity), IecReference.Fill(t)), 0.03,
                            severity: CheckSeverity.Warn);

                    ctx.Near("T60 1.0 s + SNR 6 dB vs IEC reference", Plugin(6, 1.0),
                        IecReference.Sti(IecReference.Fill(6), IecReference.Fill(1.0)), 0.03,
                        severity: CheckSeverity.Warn);
                }
            };
        }
    }
}
