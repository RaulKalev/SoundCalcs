using System;
using System.Collections.Generic;
using System.Linq;
using SoundCalcs.Domain;

namespace SoundCalcs.Compute
{
    /// <summary>
    /// Reverberant field of a (near-)rectangular room from the shoebox image-source lattice
    /// (Allen &amp; Berkley): the specular solution for six planes, summed to high order. Unlike a
    /// diffuse-field model it follows the room's shape — in flat rooms (open-plan) and long
    /// rooms (corridors) the level keeps falling with distance.
    ///
    /// Surface reflection factors come from the wall lines along each side (their materials;
    /// uncovered parts of a side are open and reflect nothing), the floor and the ceiling. They
    /// are then scaled per band so the lattice decays at the room's RT60 (Millington):
    ///   κ·Σ −S_i·ln R_i + S_open + 4mV = 0.161·V / T60,  R_i' = R_i^κ
    /// so the entered / estimated RT60 still sets the amount of reverberation, and the geometry
    /// decides where it goes.
    /// </summary>
    public class ShoeboxLattice
    {
        /// <summary>Lattice images are summed up to this travel time; the rest is an exponential tail.</summary>
        public const double CutoffTimeS = 0.15;

        /// <summary>Width of the time bins the lattice energy is collected in (for STI/C80/D50).</summary>
        public const double BinWidthS = 0.002;

        /// <summary>Minimum polygon-to-rectangle area ratio for a room to be treated as a box.</summary>
        public const double MinRectangularity = 0.90;

        /// <summary>
        /// Fraction of the energy scattered (non-specularly) at each reflection — typical of a
        /// furnished room. Scattered energy keeps its level but arrives spread out at the room's
        /// decay rate, which stops a bare box with one absorptive surface from ringing on in the
        /// grazing modes between its hard surfaces.
        /// </summary>
        public const double ScatteringCoefficient = 0.2;

        private const double FlushToleranceM = 0.10;

        // Local frame: origin at a rectangle corner, unit axes along its sides
        public Vec2 Origin { get; private set; }
        public Vec2 AxisX { get; private set; }
        public Vec2 AxisY { get; private set; }
        public double LengthX { get; private set; }
        public double LengthY { get; private set; }
        public double Height { get; private set; }
        public double FloorZ { get; private set; }

        /// <summary>Calibrated energy reflection factors [surface][band]: x=0, x=L, y=0, y=W, floor, ceiling.</summary>
        public double[][] Reflection { get; private set; }

        /// <summary>Per-band decay time constant τ = T60/13.82 the lattice was calibrated to.</summary>
        public double[] Tau { get; private set; }

        /// <summary>
        /// Fit a box to the room. Returns null when the room isn't rectangular enough, is the
        /// "open area" remainder of a boundary, or is degenerate.
        /// </summary>
        public static ShoeboxLattice TryCreate(RoomPolygon room, List<ComputeWall> walls,
            double[] floorAbsorption, double[] ceilingAbsorption, double[] t60, double[] airDbPerM,
            double defaultCeilingHeightM)
        {
            if (room == null || room.AreaOverrideM2.HasValue || room.Vertices.Count < 3) return null;
            if (!TryFitRectangle(room.Vertices, out Vec2 origin, out Vec2 ax, out Vec2 ay, out double lx, out double ly))
                return null;
            if (lx < 1.0 || ly < 1.0 || room.Area / (lx * ly) < MinRectangularity) return null;

            double h = room.CeilingHeightM > 0.5 ? room.CeilingHeightM : defaultCeilingHeightM;
            var box = new ShoeboxLattice
            {
                Origin = origin, AxisX = ax, AxisY = ay, LengthX = lx, LengthY = ly,
                Height = h, FloorZ = room.FloorElevationM
            };

            // Sides: (start, end) in world coordinates
            var sides = new[]
            {
                (origin, origin + ay * ly),                         // x = 0
                (origin + ax * lx, origin + ax * lx + ay * ly),     // x = L
                (origin, origin + ax * lx),                         // y = 0
                (origin + ay * ly, origin + ay * ly + ax * lx),     // y = W
            };
            double[] sideArea = { ly * h, ly * h, lx * h, lx * h };

            int nb = OctaveBands.Count;
            double[] drywall = OctaveBands.AbsorptionPresets[WallAbsorptionPreset.Drywall];
            var closedFraction = new double[6];
            var closedAlpha = new double[6][];
            for (int i = 0; i < 4; i++)
            {
                var (f, alpha) = SideCoverage(sides[i].Item1, sides[i].Item2, walls, drywall);
                closedFraction[i] = f;
                closedAlpha[i] = alpha;
            }
            closedFraction[4] = 1; closedAlpha[4] = floorAbsorption;
            closedFraction[5] = 1; closedAlpha[5] = ceilingAbsorption;
            double[] area = { sideArea[0], sideArea[1], sideArea[2], sideArea[3], lx * ly, lx * ly };
            double volume = lx * ly * h;

            box.Reflection = new double[6][];
            for (int i = 0; i < 6; i++) box.Reflection[i] = new double[nb];
            box.Tau = new double[nb];
            for (int k = 0; k < nb; k++)
            {
                double t = Math.Max(t60[k], 0.05);
                box.Tau[k] = t / 13.82;
                double airM = airDbPerM[k] / (10 * Math.Log10(Math.E));
                double target = 0.161 * volume / t - 4 * airM * volume;

                double open = 0, closedLog = 0;
                for (int i = 0; i < 6; i++)
                {
                    open += area[i] * (1 - closedFraction[i]);
                    double r = Math.Min(1 - 1e-6, Math.Max(1e-6, 1 - closedAlpha[i][k]));
                    closedLog += -area[i] * closedFraction[i] * Math.Log(r);
                }
                double kappa = closedLog > 1e-12 ? Math.Max(0.05, (target - open) / closedLog) : 1.0;
                for (int i = 0; i < 6; i++)
                {
                    double r = Math.Min(1 - 1e-6, Math.Max(1e-6, 1 - closedAlpha[i][k]));
                    box.Reflection[i][k] = closedFraction[i] * Math.Pow(r, kappa);
                }
            }
            return box;
        }

        /// <summary>World position → local box coordinates (x along AxisX, y along AxisY, z above the floor).</summary>
        public Vec3 ToLocal(Vec3 p)
        {
            Vec2 d = new Vec2(p.X, p.Y) - Origin;
            return new Vec3(Vec2.Dot(d, AxisX), Vec2.Dot(d, AxisY), p.Z - FloorZ);
        }

        /// <summary>Local direction → world direction.</summary>
        public Vec3 ToWorldDirection(Vec3 d)
        {
            Vec2 xy = AxisX * d.X + AxisY * d.Y;
            return new Vec3(xy.X, xy.Y, d.Z);
        }

        /// <summary>
        /// Sum the lattice images (all except the direct sound) for one source and receiver into
        /// time bins per band. <paramref name="bins"/>[band][bin] receives energy (linear,
        /// relative to on-axis at 1 m = <paramref name="sourceBand"/>);
        /// <paramref name="tailAfterCutoff"/>[band] receives the energy arriving after the cutoff.
        ///
        /// Each image keeps its specular share (1−s)^n of n reflections. The scattered rest joins
        /// the room's diffuse field — the same at every receiver in the room — which builds up as
        /// the reflections scatter and decays at the calibrated RT60:
        ///   E_diffuse(t) dt = 4·P/A · (1 − (1−s)^(c·t / l)) · e^(−t/τ) dt/τ,  l = 4V/S,
        /// for t after the direct sound (as in Barron's revised theory; so the diffuse part also
        /// falls with distance), where P = ∫ g² dΩ is the power the source radiates into the room (see
        /// <see cref="RadiatedPower"/>) and A = 0.161·V/T60. Without scattering 4·P/A is the
        /// Sabine steady-state level.
        /// </summary>
        public void Accumulate(Vec3 sourceWorld, Vec3 receiverWorld, double[] sourceBand,
            Func<Vec3, int, double> gainSquared, double[] airDbPerM, double speedOfSound,
            double[][] bins, double[] tailAfterCutoff, double[] radiatedPower = null)
        {
            Vec3 s = ToLocal(sourceWorld), r = ToLocal(receiverWorld);
            double[] L = { LengthX, LengthY, Height };
            double[] sp = { s.X, s.Y, s.Z }, rp = { r.X, r.Y, r.Z };
            double maxDist = CutoffTimeS * speedOfSound;
            int nb = OctaveBands.Count;
            int nBins = bins[0].Length;
            double windowStart = CutoffTimeS - TailWindowS;
            var lastWindow = new double[nb];

            // Flush-mounted on a surface: no rays leave into that surface
            bool[] flushLow = new bool[3], flushHigh = new bool[3];
            for (int a = 0; a < 3; a++)
            {
                flushLow[a] = sp[a] < FlushToleranceM;
                flushHigh[a] = L[a] - sp[a] < FlushToleranceM;
            }

            // Per axis: image offsets (position, reflections off the low plane, off the high plane, emission sign)
            var axes = new List<(double Pos, int Low, int High, int Sign)>[3];
            for (int a = 0; a < 3; a++)
            {
                axes[a] = new List<(double, int, int, int)>();
                int mMax = (int)Math.Ceiling(maxDist / (2 * L[a])) + 1;
                for (int m = -mMax; m <= mMax; m++)
                    for (int q = 0; q <= 1; q++)
                    {
                        double pos = (1 - 2 * q) * sp[a] + 2 * m * L[a];
                        if (Math.Abs(pos - rp[a]) > maxDist) continue;
                        axes[a].Add((pos, Math.Abs(m - q), Math.Abs(m), 1 - 2 * q));
                    }
            }

            // Powers of the reflection factors, and the largest factor over bands per surface,
            // so images already 50 dB down can be skipped before the per-band loop.
            int maxN = 0;
            for (int a = 0; a < 3; a++)
                foreach (var im in axes[a]) maxN = Math.Max(maxN, Math.Max(im.Low, im.High));
            var pow = new double[6][][];
            var powMax = new double[6][];
            for (int i = 0; i < 6; i++)
            {
                pow[i] = new double[nb][];
                powMax[i] = new double[maxN + 1];
                double rMax = Reflection[i].Max();
                for (int k = 0; k < nb; k++)
                {
                    pow[i][k] = new double[maxN + 1];
                    double p = 1;
                    for (int n = 0; n <= maxN; n++) { pow[i][k][n] = p; p *= Reflection[i][k]; }
                }
                double pm = 1;
                for (int n = 0; n <= maxN; n++) { powMax[i][n] = pm; pm *= rMax; }
            }

            foreach (var ix in axes[0])
            {
                double dx = rp[0] - ix.Pos;
                foreach (var iy in axes[1])
                {
                    double dy = rp[1] - iy.Pos;
                    double dxy2 = dx * dx + dy * dy;
                    if (dxy2 > maxDist * maxDist) continue;
                    foreach (var iz in axes[2])
                    {
                        if (ix.Low + ix.High + iy.Low + iy.High + iz.Low + iz.High == 0) continue; // direct sound
                        double dz = rp[2] - iz.Pos;
                        double d2 = dxy2 + dz * dz;
                        if (d2 > maxDist * maxDist) continue;
                        double reflMax = powMax[0][ix.Low] * powMax[1][ix.High] * powMax[2][iy.Low] *
                                         powMax[3][iy.High] * powMax[4][iz.Low] * powMax[5][iz.High];
                        if (reflMax < 1e-5) continue;
                        double d = Math.Max(Math.Sqrt(d2), 0.01);

                        // Direction the ray leaves the real source (unfold the reflections)
                        double ex = dx * ix.Sign, ey = dy * iy.Sign, ez = dz * iz.Sign;
                        if ((flushLow[0] && ex < 0) || (flushHigh[0] && ex > 0) ||
                            (flushLow[1] && ey < 0) || (flushHigh[1] && ey > 0) ||
                            (flushLow[2] && ez < 0) || (flushHigh[2] && ez > 0))
                            continue;
                        Vec3 emit = ToWorldDirection(new Vec3(ex / d, ey / d, ez / d));

                        double t = d / speedOfSound;
                        int bin = Math.Min(nBins - 1, (int)(t / BinWidthS));
                        int reflections = ix.Low + ix.High + iy.Low + iy.High + iz.Low + iz.High;
                        double specular = Math.Pow(1 - ScatteringCoefficient, reflections);
                        for (int k = 0; k < nb; k++)
                        {
                            double refl =
                                pow[0][k][ix.Low] * pow[1][k][ix.High] *
                                pow[2][k][iy.Low] * pow[3][k][iy.High] *
                                pow[4][k][iz.Low] * pow[5][k][iz.High];
                            if (refl < 1e-9) continue;
                            double e = sourceBand[k] * gainSquared(emit, k) * refl / (d * d)
                                     * Math.Pow(10.0, -airDbPerM[k] * d / 10.0);
                            bins[k][bin] += e * specular;
                            if (t >= windowStart) lastWindow[k] += e * specular;
                        }
                    }
                }
            }

            // Specular energy after the cutoff: extrapolated from the last window at the room's decay
            for (int k = 0; k < nb; k++)
                tailAfterCutoff[k] += lastWindow[k] / (Math.Exp(TailWindowS / Tau[k]) - 1);

            // Diffuse (scattered) field
            double[] power = radiatedPower ?? RadiatedPower(sourceWorld, gainSquared);
            double volume = LengthX * LengthY * Height;
            double meanFreePath = 4 * volume / (2 * (LengthX * LengthY + LengthX * Height + LengthY * Height));
            double directTime = (r - s).Length / speedOfSound;
            for (int k = 0; k < nb; k++)
            {
                double steady = sourceBand[k] * 4 * power[k] / (0.161 * volume / (13.82 * Tau[k]));
                for (int b = 0; b < nBins; b++)
                {
                    double t = (b + 0.5) * BinWidthS;
                    if (t < directTime) continue; // nothing scattered reaches the receiver before the direct sound
                    bins[k][b] += steady * ScatteredFraction(t, speedOfSound, meanFreePath)
                                * Math.Exp(-t / Tau[k]) * BinWidthS / Tau[k];
                }
                tailAfterCutoff[k] += steady * ScatteredFraction(CutoffTimeS, speedOfSound, meanFreePath)
                                    * Math.Exp(-CutoffTimeS / Tau[k]);
            }
        }

        private const double TailWindowS = 0.05;

        private static double ScatteredFraction(double t, double speedOfSound, double meanFreePath) =>
            1 - Math.Pow(1 - ScatteringCoefficient, speedOfSound * t / meanFreePath);

        /// <summary>
        /// Power the source radiates into the room per band, P = ∫ g² dΩ over the directions it
        /// can radiate into (a flush-mounted source radiates into a half space: an omni gives 2π).
        /// Constant per source — compute once and pass to <see cref="Accumulate"/>.
        /// </summary>
        public double[] RadiatedPower(Vec3 sourceWorld, Func<Vec3, int, double> gainSquared)
        {
            Vec3 s = ToLocal(sourceWorld);
            double[] L = { LengthX, LengthY, Height };
            double[] sp = { s.X, s.Y, s.Z };
            const int nTheta = 36, nPhi = 72;
            int nb = OctaveBands.Count;
            var power = new double[nb];
            for (int i = 0; i < nTheta; i++)
            {
                double theta = (i + 0.5) * Math.PI / nTheta;
                double dOmega = Math.Sin(theta) * (Math.PI / nTheta) * (2 * Math.PI / nPhi);
                for (int j = 0; j < nPhi; j++)
                {
                    double phi = (j + 0.5) * 2 * Math.PI / nPhi;
                    double[] dir = { Math.Sin(theta) * Math.Cos(phi), Math.Sin(theta) * Math.Sin(phi), Math.Cos(theta) };
                    bool blocked = false;
                    for (int a = 0; a < 3; a++)
                        if ((sp[a] < FlushToleranceM && dir[a] < 0) || (L[a] - sp[a] < FlushToleranceM && dir[a] > 0))
                            blocked = true;
                    if (blocked) continue;
                    Vec3 world = ToWorldDirection(new Vec3(dir[0], dir[1], dir[2]));
                    for (int k = 0; k < nb; k++) power[k] += gainSquared(world, k) * dOmega;
                }
            }
            return power;
        }

        /// <summary>
        /// Fraction of a side covered by enclosing wall lines, and the length-weighted absorption
        /// of those lines. Lines count when roughly parallel and within 0.3 m of the side.
        /// </summary>
        private static (double Fraction, double[] Alpha) SideCoverage(Vec2 a, Vec2 b, List<ComputeWall> walls, double[] fallback)
        {
            Vec2 dir = b - a;
            double len = dir.Length;
            Vec2 u = dir * (1.0 / len);
            Vec2 n = new Vec2(-u.Y, u.X);
            var intervals = new List<(double S, double E, double[] Alpha)>();
            foreach (ComputeWall w in walls)
            {
                if (w.HeightM > 0 && w.HeightM < JobInputBuilder.EnclosingHeightM) continue; // low screens don't close the room
                if (w.AbsorptionByBand != null && w.AbsorptionByBand.All(x => x >= 0.99)) continue;  // openings
                if (Math.Abs(Vec2.Dot(w.Start - a, n)) > 0.3 || Math.Abs(Vec2.Dot(w.End - a, n)) > 0.3) continue;
                double s0 = Vec2.Dot(w.Start - a, u), s1 = Vec2.Dot(w.End - a, u);
                double lo = Math.Max(0, Math.Min(s0, s1)), hi = Math.Min(len, Math.Max(s0, s1));
                if (hi - lo > 1e-6)
                    intervals.Add((lo, hi, w.AbsorptionByBand != null && w.AbsorptionByBand.Length == OctaveBands.Count ? w.AbsorptionByBand : fallback));
            }

            // Union of covered intervals (first-come material on overlaps)
            intervals.Sort((p, q) => p.S.CompareTo(q.S));
            double covered = 0;
            var alphaSum = new double[OctaveBands.Count];
            double reach = 0;
            foreach (var iv in intervals)
            {
                double s = Math.Max(iv.S, reach), e = iv.E;
                if (e <= s) continue;
                covered += e - s;
                for (int k = 0; k < OctaveBands.Count; k++) alphaSum[k] += (e - s) * iv.Alpha[k];
                reach = e;
            }
            var alpha = new double[OctaveBands.Count];
            for (int k = 0; k < OctaveBands.Count; k++)
                alpha[k] = covered > 0 ? alphaSum[k] / covered : fallback[k];
            return (Math.Min(1, covered / len), alpha);
        }

        /// <summary>Minimum-area bounding rectangle (rotating calipers over the convex hull edges).</summary>
        private static bool TryFitRectangle(List<Vec2> vertices, out Vec2 origin, out Vec2 ax, out Vec2 ay,
            out double lx, out double ly)
        {
            origin = ax = ay = Vec2.Zero; lx = ly = 0;
            var hull = JobInputBuilder.ConvexHull(vertices);
            if (hull.Count < 3) return false;
            double bestArea = double.MaxValue;
            for (int i = 0; i < hull.Count; i++)
            {
                Vec2 e = hull[(i + 1) % hull.Count] - hull[i];
                if (e.Length < 1e-9) continue;
                Vec2 u = e.Normalized(), v = new Vec2(-u.Y, u.X);
                double minU = double.MaxValue, maxU = double.MinValue, minV = double.MaxValue, maxV = double.MinValue;
                foreach (Vec2 p in hull)
                {
                    double pu = Vec2.Dot(p, u), pv = Vec2.Dot(p, v);
                    minU = Math.Min(minU, pu); maxU = Math.Max(maxU, pu);
                    minV = Math.Min(minV, pv); maxV = Math.Max(maxV, pv);
                }
                double areaRect = (maxU - minU) * (maxV - minV);
                if (areaRect < bestArea - 1e-9)
                {
                    bestArea = areaRect;
                    origin = u * minU + v * minV;
                    ax = u; ay = v; lx = maxU - minU; ly = maxV - minV;
                }
            }
            return bestArea < double.MaxValue;
        }
    }
}
