using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SoundCalcs.Domain;
using SoundCalcs.IO;

namespace SoundCalcs.Compute
{
    /// <summary>
    /// Intermediate per-receiver octave-band data produced by SPLCalculator
    /// and consumed by STICalculator. Times are relative to the first arrival.
    /// Energies are for the STI test signal (standard speech through each speaker's
    /// response) unless EnvironmentSettings.UseSpeechSpectrumForSti is false.
    /// </summary>
    public class ReceiverBandData
    {
        public int ReceiverIndex { get; set; }

        /// <summary>Energy arriving within 50 ms of the first arrival (D50 "early").</summary>
        public double[] EarlyLinearByBand { get; set; } = new double[OctaveBands.Count];

        /// <summary>Energy arriving more than 50 ms after the first arrival.</summary>
        public double[] LateLinearByBand { get; set; } = new double[OctaveBands.Count];

        /// <summary>Energy within 80 ms of the first arrival (C80). Null = use the 50 ms split.</summary>
        public double[] Early80LinearByBand { get; set; }

        /// <summary>Energy more than 80 ms after the first arrival. Null = use the 50 ms split.</summary>
        public double[] Late80LinearByBand { get; set; }

        /// <summary>
        /// Complex modulation sums of the energy impulse response, [band][modulation frequency]:
        /// Σ E·e^(−j2πF·t) over every arrival, plus the reverberant tail. Divided by the total
        /// energy this is the room part of the IEC 60268-16 MTF. Null when unavailable —
        /// STICalculator then treats the late energy as one exponential tail.
        /// </summary>
        public double[][] ModulationRe { get; set; }
        public double[][] ModulationIm { get; set; }
    }

    /// <summary>
    /// Computes per-octave-band and broadband SPL at each receiver point from all
    /// sources: direct sound, image-source wall reflections (1st order; 2nd order in
    /// Full quality), floor/ceiling reflections (Full quality) and a statistical
    /// reverberant tail (Barron's revised theory) for the energy the explicit
    /// reflections do not cover. Arrival times are tracked for STI, C80 and D50.
    /// Pure C# math — no Revit references. Thread-safe and parallelized.
    /// </summary>
    public class SPLCalculator
    {
        private const double RefDistanceM = 1.0;
        private const double MinDistanceM = 0.01;
        private const double DefaultCeilingHeightM = 3.0;

        /// <summary>Speakers within this distance of the ceiling are flush-mounted: no ceiling image.</summary>
        private const double FlushMountToleranceM = 0.10;

        /// <summary>D50 and C80 early/late split times in seconds.</summary>
        private const double Split50S = 0.050;
        private const double Split80S = 0.080;

        /// <summary>
        /// A wall image source. First order: the real source mirrored across one wall.
        /// Second order: a first-order image mirrored across a second wall.
        /// </summary>
        private struct ImageSource
        {
            public Vec2 ImagePos;          // Reflected position (2D)
            public int SourceIndex;         // Index of the real source
            public int WallIndex;           // Last reflecting wall
            public int FirstWallIndex;      // First reflecting wall (-1 for first order)
            public Vec2 FirstImagePos;      // First-order image (second order only)
            public double[] ReflectionCoeffByBand; // Π(1 − α) per octave band
        }

        /// <summary>Ceiling or floor image source: source mirrored across a horizontal plane.</summary>
        private struct HorizontalImageSource
        {
            public Vec3 ImagePos3D;
            public double SurfaceZ;
            public int SourceIndex;
            public double[] ReflectionCoeffByBand;
        }

        /// <summary>
        /// Compute SPL at all receiver points from all sources.
        /// Returns per-receiver results and intermediate band data for STI.
        /// </summary>
        public (List<ReceiverResult> Results, List<ReceiverBandData> BandData) Calculate(
            AcousticJobInput input,
            CancellationToken cancellationToken,
            IProgress<double> progress)
        {
            int totalReceivers = input.Receivers.Count;
            if (totalReceivers == 0)
                return (new List<ReceiverResult>(), new List<ReceiverBandData>());

            var walls = input.Walls ?? new List<ComputeWall>();
            int numBands = OctaveBands.Count;
            int numSources = input.Sources.Count;
            double[] modFreqs = OctaveBands.ModulationFrequencies;

            FileLogger.Log($"[SPLCalc] Wall count: {walls.Count}, " +
                $"Sources: {numSources}, Receivers: {totalReceivers}, Quality={input.Quality}");

            double speedOfSound = 331.3 + 0.606 * input.Environment.TemperatureC;

            // Temperature- and humidity-dependent air absorption (ISO 9613-1), dB/m
            double[] airAbsorption = OctaveBands.ComputeAirAbsorption(
                input.Environment.TemperatureC,
                input.Environment.RelativeHumidityPct);

            double[] rt60 = input.Environment.RT60ByBand;
            double[] t60 = new double[numBands];
            for (int k = 0; k < numBands; k++)
                t60[k] = Math.Max(rt60[k], 0.05);

            // Walls without an assigned material fall back to drywall; floor and ceiling
            // use the finishes chosen in the settings.
            double[] globalAbsorption = OctaveBands.AbsorptionPresets[WallAbsorptionPreset.Drywall];
            double[] floorAbsorption = OctaveBands.AbsorptionPresets[input.Environment.FloorSurface];
            double[] ceilingAbsorption = OctaveBands.AbsorptionPresets[input.Environment.CeilingSurface];

            var providers = new ISpeakerDirectivityProvider[numSources];
            for (int i = 0; i < numSources; i++)
                providers[i] = DirectivityProviderFactory.Create(input.Sources[i].Profile);

            // Per-source, per-band emitted power (on-axis at 1 m, linear). The speaker's frequency
            // response only shapes the spectrum; the broadband level stays OnAxisSplAtOneMeter.
            // For STI/C80/D50 the program is standard IEC speech played through that response:
            // speechFactor rescales each band from the program spectrum to the speech spectrum.
            double[] speechDb = input.Environment.SpeechWeightType == SpeechWeightType.Female
                ? OctaveBands.FemaleSpeechSpectrumDb
                : OctaveBands.MaleSpeechSpectrumDb;
            double[][] sourceBand = new double[numSources][];
            double[][] speechFactor = new double[numSources][];
            for (int s = 0; s < numSources; s++)
            {
                sourceBand[s] = new double[numBands];
                speechFactor[s] = new double[numBands];
                double broadband = Math.Pow(10.0, providers[s].OnAxisSplAtOneMeter / 10.0);
                double[] response = input.Sources[s].Profile?.SpectrumShapeByBand;
                if (response != null && response.Length != numBands) response = null;

                double[] program = OctaveBands.EnergyFractions(response);
                double[] speechThroughSpeaker = new double[numBands];
                for (int k = 0; k < numBands; k++)
                    speechThroughSpeaker[k] = speechDb[k] + (response?[k] ?? 0);
                double[] speech = OctaveBands.EnergyFractions(speechThroughSpeaker);

                for (int k = 0; k < numBands; k++)
                {
                    sourceBand[s][k] = broadband * program[k];
                    speechFactor[s][k] = input.Environment.UseSpeechSpectrumForSti ? speech[k] / program[k] : 1.0;
                }
            }

            // --- Rooms and reverberant field (Sabine) ---
            // Each source's diffuse-field energy density: W·16π/(Q·A) scaled by the room's
            // enclosure ratio (fraction of perimeter backed by walls; open areas get less).
            int numRooms = input.Rooms?.Count ?? 0;
            int[] sourceRoomIndex = new int[numSources];
            double[][] reverbBySource = null; // [source][band]
            bool hasReverb = false;

            for (int s = 0; s < numSources; s++)
            {
                sourceRoomIndex[s] = -1;
                Vec2 srcXY = new Vec2(input.Sources[s].Position.X, input.Sources[s].Position.Y);
                for (int r = 0; r < numRooms; r++)
                {
                    if (input.Rooms[r].ContainsPoint(srcXY)) { sourceRoomIndex[s] = r; break; }
                }
            }

            if (numRooms > 0)
            {
                reverbBySource = new double[numSources][];
                for (int s = 0; s < numSources; s++)
                {
                    reverbBySource[s] = new double[numBands];
                    int ri = sourceRoomIndex[s];
                    if (ri < 0) continue;

                    RoomPolygon room = input.Rooms[ri];
                    double ceilingH = room.CeilingHeightM > 0.5 ? room.CeilingHeightM : DefaultCeilingHeightM;
                    double vol = room.Area * ceilingH;
                    if (vol <= 1.0 || room.EnclosureRatio <= 0.01) continue;

                    hasReverb = true;
                    double q = Math.Max(providers[s].DirectivityFactor, 1.0);
                    for (int k = 0; k < numBands; k++)
                    {
                        double sabineA = Math.Max(0.161 * vol / t60[k], 1.0);
                        reverbBySource[s][k] = sourceBand[s][k] * 16.0 * Math.PI / (q * sabineA) * room.EnclosureRatio;
                    }
                }
            }

            // --- Wall image sources ---
            double[][] wallAbsorption = new double[walls.Count][];
            for (int w = 0; w < walls.Count; w++)
            {
                wallAbsorption[w] = walls[w].AbsorptionByBand != null && walls[w].AbsorptionByBand.Length == numBands
                    ? walls[w].AbsorptionByBand
                    : globalAbsorption;
            }

            bool isDraft = input.Quality == CalculationQuality.Draft;
            var imageSources = new List<ImageSource>();
            for (int s = 0; s < numSources; s++)
            {
                Vec2 srcXY = new Vec2(input.Sources[s].Position.X, input.Sources[s].Position.Y);
                for (int w = 0; w < walls.Count; w++)
                {
                    Vec2 mirrored = ReflectPointAcrossSegment(srcXY, walls[w].Start, walls[w].End);
                    if (double.IsNaN(mirrored.X)) continue; // degenerate wall

                    double[] coeffs = new double[numBands];
                    for (int k = 0; k < numBands; k++)
                        coeffs[k] = 1.0 - wallAbsorption[w][k];

                    imageSources.Add(new ImageSource
                    {
                        ImagePos = mirrored,
                        SourceIndex = s,
                        WallIndex = w,
                        FirstWallIndex = -1,
                        ReflectionCoeffByBand = coeffs
                    });
                }
            }

            // Second order (Full quality): mirror each first-order image across the other walls
            if (!isDraft)
            {
                int firstOrderCount = imageSources.Count;
                for (int i = 0; i < firstOrderCount; i++)
                {
                    ImageSource img1 = imageSources[i];
                    for (int w = 0; w < walls.Count; w++)
                    {
                        if (w == img1.WallIndex) continue;

                        Vec2 mirrored2 = ReflectPointAcrossSegment(img1.ImagePos, walls[w].Start, walls[w].End);
                        if (double.IsNaN(mirrored2.X)) continue;

                        double[] coeffs2 = new double[numBands];
                        for (int k = 0; k < numBands; k++)
                            coeffs2[k] = img1.ReflectionCoeffByBand[k] * (1.0 - wallAbsorption[w][k]);

                        imageSources.Add(new ImageSource
                        {
                            ImagePos = mirrored2,
                            SourceIndex = img1.SourceIndex,
                            WallIndex = w,
                            FirstWallIndex = img1.WallIndex,
                            FirstImagePos = img1.ImagePos,
                            ReflectionCoeffByBand = coeffs2
                        });
                    }
                }
            }
            var imageSourceArray = imageSources.ToArray();

            // --- Ceiling and floor image sources (Full quality) ---
            var horizImages = new List<HorizontalImageSource>();
            if (!isDraft)
            {
                double floorZ = 0;
                double ceilingZ = DefaultCeilingHeightM;
                if (numRooms > 0)
                {
                    floorZ = input.Rooms.Min(r => r.FloorElevationM);
                    double maxCeil = input.Rooms.Max(r => r.CeilingHeightM > 0.5 ? r.CeilingHeightM : DefaultCeilingHeightM);
                    ceilingZ = floorZ + maxCeil;
                }

                double[] floorCoeffs = new double[numBands];
                for (int k = 0; k < numBands; k++)
                    floorCoeffs[k] = 1.0 - floorAbsorption[k];

                for (int s = 0; s < numSources; s++)
                {
                    Vec3 srcPos = input.Sources[s].Position;

                    // Ceiling reflection, scaled by the room's enclosure ratio (open areas have
                    // less ceiling). A flush-mounted speaker's ceiling image would coincide with
                    // the speaker itself — its half-space radiation is already in the on-axis level.
                    int srcRoom = sourceRoomIndex[s];
                    double enclosure = srcRoom >= 0 ? input.Rooms[srcRoom].EnclosureRatio : 0;
                    if (ceilingZ - srcPos.Z > FlushMountToleranceM && enclosure > 0)
                    {
                        double[] ceilCoeffs = new double[numBands];
                        for (int k = 0; k < numBands; k++)
                            ceilCoeffs[k] = (1.0 - ceilingAbsorption[k]) * enclosure;
                        horizImages.Add(new HorizontalImageSource
                        {
                            ImagePos3D = new Vec3(srcPos.X, srcPos.Y, 2.0 * ceilingZ - srcPos.Z),
                            SurfaceZ = ceilingZ,
                            SourceIndex = s,
                            ReflectionCoeffByBand = ceilCoeffs
                        });
                    }

                    // Floor reflection (always present)
                    if (srcPos.Z - floorZ > FlushMountToleranceM)
                    {
                        horizImages.Add(new HorizontalImageSource
                        {
                            ImagePos3D = new Vec3(srcPos.X, srcPos.Y, 2.0 * floorZ - srcPos.Z),
                            SurfaceZ = floorZ,
                            SourceIndex = s,
                            ReflectionCoeffByBand = floorCoeffs
                        });
                    }
                }
            }
            var horizImageArray = horizImages.ToArray();

            // Free wall ends (not joined to another wall): sound diffracts around them
            bool[] startFree = new bool[walls.Count], endFree = new bool[walls.Count];
            for (int w = 0; w < walls.Count; w++)
            {
                startFree[w] = IsFreeEnd(walls[w].Start, w, walls);
                endFree[w] = IsFreeEnd(walls[w].End, w, walls);
            }

            FileLogger.Log($"[SPLCalc] ImageSources={imageSourceArray.Length}, HorizImages={horizImageArray.Length}, " +
                $"Reverb={hasReverb}");

            var resultsBag = new ConcurrentBag<ReceiverResult>();
            var bandDataBag = new ConcurrentBag<ReceiverBandData>();
            int completed = 0;
            int wallHitReceivers = 0;

            int cpuCount = Environment.ProcessorCount;
            ThreadPool.GetMinThreads(out int prevWorker, out int prevIO);
            if (prevWorker < cpuCount)
                ThreadPool.SetMinThreads(cpuCount, prevIO);

            Parallel.ForEach(
                Partitioner.Create(0, totalReceivers, Math.Max(1, totalReceivers / (cpuCount * 4))),
                new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = -1 },
                range =>
            {
                for (int ri = range.Item1; ri < range.Item2; ri++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ReceiverPoint receiver = input.Receivers[ri];
                    Vec3 recvPos = receiver.Position;
                    Vec2 recvXY = new Vec2(recvPos.X, recvPos.Y);

                    double[] totalByBand = new double[numBands];
                    var arrivals = new List<(double Time, double[] Power, int Source)>(numSources * 4);

                    double[] directTime = new double[numSources];
                    double[] directDist = new double[numSources];
                    double[] wallStcSums = new double[numSources];
                    double[][] imageEnergy = new double[numSources][];
                    for (int s = 0; s < numSources; s++)
                        imageEnergy[s] = new double[numBands];

                    // --- Direct sound ---
                    for (int s = 0; s < numSources; s++)
                    {
                        ComputeSource source = input.Sources[s];
                        Vec3 delta = recvPos - source.Position;
                        double distance = Math.Max(delta.Length, MinDistanceM);
                        directDist[s] = distance;
                        directTime[s] = distance / speedOfSound;

                        Vec3 toReceiver = delta / distance;
                        Vec3 facing = source.FacingDirection.Normalized();

                        Vec2 srcXY0 = new Vec2(source.Position.X, source.Position.Y);
                        var hits = new List<int>(2);
                        double stc = SumWallStc(srcXY0, recvXY, source.Position.Z, recvPos.Z, walls, -1, -1, hits);
                        wallStcSums[s] = stc;
                        if (stc > 0) Interlocked.Increment(ref wallHitReceivers);

                        // Unobstructed paths passing just clear of an edge (a free wall end or the top
                        // of a low wall) already lose some energy: the bright-zone part of the
                        // diffraction curve, 5 dB at the shadow boundary falling to 0 at N = −0.2.
                        double brightDetour = stc > 0 ? double.MaxValue
                            : SmallestEdgeDetour(source.Position, recvPos, distance, walls, startFree, endFree);

                        double[] p = new double[numBands];
                        for (int k = 0; k < numBands; k++)
                        {
                            double gain = providers[s].GetDirectivityGainForBand(facing, toReceiver, k);
                            double brightDb = brightDetour < double.MaxValue
                                ? DiffractionAttenuationDb(-2 * brightDetour * OctaveBands.CenterFrequencies[k] / speedOfSound)
                                : 0;
                            p[k] = sourceBand[s][k] * Spread(distance) * gain * gain
                                 * LossFactor(stc, k, airAbsorption[k] * distance)
                                 * Math.Pow(10.0, -brightDb / 10.0);
                            totalByBand[k] += p[k];
                        }
                        arrivals.Add((directTime[s], p, s));

                        // --- Diffraction around a single obstructing wall ---
                        // Around its free vertical ends and, for partial-height walls, over the top.
                        // Maekawa / Kurze–Anderson thin-screen attenuation per band.
                        if (hits.Count == 1)
                        {
                            int wi = hits[0];
                            ComputeWall bw = walls[wi];
                            var edges = new List<Vec3>(3);
                            if (startFree[wi]) edges.Add(new Vec3(bw.Start.X, bw.Start.Y, double.NaN));
                            if (endFree[wi]) edges.Add(new Vec3(bw.End.X, bw.End.Y, double.NaN));
                            if (bw.HeightM > 0)
                            {
                                double tc = BlockParam(srcXY0, recvXY - srcXY0, bw);
                                if (tc > 0)
                                {
                                    Vec2 c = srcXY0 + (recvXY - srcXY0) * tc;
                                    edges.Add(new Vec3(c.X, c.Y, bw.BaseElevationM + bw.HeightM));
                                }
                            }

                            foreach (Vec3 edge in edges)
                            {
                                Vec2 e2 = new Vec2(edge.X, edge.Y);
                                double legA = Vec2.Distance(srcXY0, e2), legB = Vec2.Distance(e2, recvXY);
                                double pathLen;
                                Vec3 toEdge;
                                if (double.IsNaN(edge.Z))
                                {
                                    // Vertical edge: plan path S→E→R, height interpolated along it
                                    double dzv = recvPos.Z - source.Position.Z;
                                    double planLen = legA + legB;
                                    if (planLen < 1e-9) continue;
                                    pathLen = Math.Sqrt(planLen * planLen + dzv * dzv);
                                    double zEdge = source.Position.Z + dzv * legA / planLen;
                                    // The legs must not be blocked by other walls
                                    if (SumWallStc(srcXY0, e2, source.Position.Z, zEdge, walls, wi, -1, null) > 0 ||
                                        SumWallStc(e2, recvXY, zEdge, recvPos.Z, walls, wi, -1, null) > 0)
                                        continue;
                                    toEdge = new Vec3(e2.X - srcXY0.X, e2.Y - srcXY0.Y, zEdge - source.Position.Z);
                                }
                                else
                                {
                                    // Top edge: straight up to the top of the wall and down again
                                    Vec3 top = edge;
                                    pathLen = (top - source.Position).Length + (recvPos - top).Length;
                                    toEdge = top - source.Position;
                                }

                                double detour = pathLen - distance;
                                if (detour <= 0) continue;
                                double toEdgeLen = toEdge.Length;
                                Vec3 edgeDir = toEdgeLen > 1e-9 ? toEdge / toEdgeLen : toReceiver;

                                double[] pd = new double[numBands];
                                for (int k = 0; k < numBands; k++)
                                {
                                    double lambda = speedOfSound / OctaveBands.CenterFrequencies[k];
                                    double atten = DiffractionAttenuationDb(2 * detour / lambda);
                                    double gain = providers[s].GetDirectivityGainForBand(facing, edgeDir, k);
                                    pd[k] = sourceBand[s][k] * Spread(pathLen) * gain * gain
                                          * Math.Pow(10.0, -Math.Min(atten + airAbsorption[k] * pathLen, MaxTotalLossDb) / 10.0);
                                    totalByBand[k] += pd[k];
                                }
                                arrivals.Add((pathLen / speedOfSound, pd, s));
                            }
                        }
                    }

                    // --- Wall reflections (image sources) ---
                    for (int r = 0; r < imageSourceArray.Length; r++)
                    {
                        ImageSource img = imageSourceArray[r];
                        ComputeSource source = input.Sources[img.SourceIndex];
                        Vec2 srcXY = new Vec2(source.Position.X, source.Position.Y);

                        Vec2 imgToRecv = recvXY - img.ImagePos;
                        double len2D = imgToRecv.Length;
                        if (len2D < MinDistanceM) continue;

                        // Last bounce: image→receiver must cross its wall
                        double tLast = SegmentIntersectT(img.ImagePos, imgToRecv, walls[img.WallIndex].Start, walls[img.WallIndex].End);
                        if (tLast <= 0.0 || tLast >= 1.0) continue;
                        Vec2 lastPt = img.ImagePos + imgToRecv * tLast;

                        // Heights along the unfolded path: linear in plan distance from the source
                        double dz = recvPos.Z - source.Position.Z;
                        double zSrc = source.Position.Z;
                        double ZAt(double planDist) => zSrc + dz * planDist / len2D;

                        // Path legs in plan: source → [first bounce →] last bounce → receiver
                        Vec2 firstPt = lastPt;
                        double otherStc;
                        double zLast = ZAt(len2D - Vec2.Distance(lastPt, recvXY));
                        if (!BelowTop(walls[img.WallIndex], zLast)) continue; // passes over a low wall
                        if (img.FirstWallIndex >= 0)
                        {
                            // First bounce: first-order image → last bounce point must cross the first wall
                            Vec2 d1 = lastPt - img.FirstImagePos;
                            double tFirst = SegmentIntersectT(img.FirstImagePos, d1,
                                walls[img.FirstWallIndex].Start, walls[img.FirstWallIndex].End);
                            if (tFirst <= 0.0 || tFirst >= 1.0) continue;
                            firstPt = img.FirstImagePos + d1 * tFirst;
                            double zFirst = ZAt(Vec2.Distance(srcXY, firstPt));
                            if (!BelowTop(walls[img.FirstWallIndex], zFirst)) continue;

                            otherStc = SumWallStc(srcXY, firstPt, zSrc, zFirst, walls, img.FirstWallIndex, img.WallIndex, null)
                                     + SumWallStc(firstPt, lastPt, zFirst, zLast, walls, img.FirstWallIndex, img.WallIndex, null)
                                     + SumWallStc(lastPt, recvXY, zLast, recvPos.Z, walls, img.FirstWallIndex, img.WallIndex, null);
                        }
                        else
                        {
                            otherStc = SumWallStc(srcXY, lastPt, zSrc, zLast, walls, img.WallIndex, -1, null)
                                     + SumWallStc(lastPt, recvXY, zLast, recvPos.Z, walls, img.WallIndex, -1, null);
                        }

                        // Unfolded path length in 3D: plan length plus the height difference
                        double pathLen = Math.Max(Math.Sqrt(len2D * len2D + dz * dz), MinDistanceM);

                        // Leave the source toward the first bounce point (height interpolated along the path)
                        double firstLeg2D = Vec2.Distance(srcXY, firstPt);
                        Vec3 toFirst = new Vec3(firstPt.X - srcXY.X, firstPt.Y - srcXY.Y, dz * firstLeg2D / len2D);
                        double toFirstLen = toFirst.Length;
                        Vec3 dir = toFirstLen > 1e-9 ? toFirst / toFirstLen : Vec3.Zero;
                        Vec3 facing = source.FacingDirection.Normalized();

                        double[] p = new double[numBands];
                        for (int k = 0; k < numBands; k++)
                        {
                            double gain = providers[img.SourceIndex].GetDirectivityGainForBand(facing, dir, k);
                            p[k] = sourceBand[img.SourceIndex][k] * Spread(pathLen) * gain * gain
                                 * LossFactor(otherStc, k, airAbsorption[k] * pathLen)
                                 * img.ReflectionCoeffByBand[k];
                            totalByBand[k] += p[k];
                            imageEnergy[img.SourceIndex][k] += p[k];
                        }
                        arrivals.Add((pathLen / speedOfSound, p, img.SourceIndex));
                    }

                    // --- Ceiling / floor reflections ---
                    for (int h = 0; h < horizImageArray.Length; h++)
                    {
                        HorizontalImageSource himg = horizImageArray[h];
                        ComputeSource source = input.Sources[himg.SourceIndex];
                        Vec3 srcPos = source.Position;

                        Vec3 imgToRecv = recvPos - himg.ImagePos3D;
                        double pathLen = Math.Max(imgToRecv.Length, MinDistanceM);

                        // Reflection point: where image→receiver crosses the surface plane
                        double dzImg = recvPos.Z - himg.ImagePos3D.Z;
                        double u = Math.Abs(dzImg) > 1e-9 ? (himg.SurfaceZ - himg.ImagePos3D.Z) / dzImg : 0.5;
                        Vec3 reflPt = himg.ImagePos3D + imgToRecv * Math.Max(0, Math.Min(1, u));
                        Vec3 toRefl = reflPt - srcPos;
                        double trLen = toRefl.Length;
                        Vec3 dir = trLen > 1e-9 ? toRefl / trLen : Vec3.Zero;
                        Vec3 facing = source.FacingDirection.Normalized();

                        double stc = wallStcSums[himg.SourceIndex]; // plan path is source → receiver

                        double[] p = new double[numBands];
                        for (int k = 0; k < numBands; k++)
                        {
                            double gain = providers[himg.SourceIndex].GetDirectivityGainForBand(facing, dir, k);
                            p[k] = sourceBand[himg.SourceIndex][k] * Spread(pathLen) * gain * gain
                                 * LossFactor(stc, k, airAbsorption[k] * pathLen)
                                 * himg.ReflectionCoeffByBand[k];
                            totalByBand[k] += p[k];
                            imageEnergy[himg.SourceIndex][k] += p[k];
                        }
                        arrivals.Add((pathLen / speedOfSound, p, himg.SourceIndex));
                    }

                    // --- Reverberant tail (Barron's revised theory) ---
                    // Reflected energy at distance r from a source in a diffuse room is the Sabine
                    // level decayed over the direct-sound travel time: R·e^(−13.82·r/(c·T)).
                    // The explicit reflections above are part of that energy, so only the
                    // remainder is added, as an exponential tail starting at the direct arrival.
                    // Only for sources in the receiver's room with an unobstructed direct path.
                    double[][] tail = new double[numSources][];
                    if (hasReverb)
                    {
                        int recvRoom = receiver.RoomIndex;
                        for (int s = 0; s < numSources; s++)
                        {
                            if (wallStcSums[s] > 0 || recvRoom < 0 || sourceRoomIndex[s] != recvRoom) continue;
                            tail[s] = new double[numBands];
                            for (int k = 0; k < numBands; k++)
                            {
                                double barron = reverbBySource[s][k] * Math.Exp(-13.82 * directTime[s] / t60[k]);
                                tail[s][k] = Math.Max(0, barron - imageEnergy[s][k]);
                                totalByBand[k] += tail[s][k];
                            }
                        }
                    }

                    // --- Per-band SPL, broadband and A-weighted ---
                    double[] splDbByBand = new double[numBands];
                    double totalLinear = 0, aWeightedLinear = 0;
                    for (int k = 0; k < numBands; k++)
                    {
                        splDbByBand[k] = totalByBand[k] > 0 ? Math.Round(10.0 * Math.Log10(totalByBand[k]), 2) : -100.0;
                        totalLinear += totalByBand[k];
                        aWeightedLinear += Math.Pow(10.0, OctaveBands.AWeightingDb[k] / 10.0) * totalByBand[k];
                    }

                    resultsBag.Add(new ReceiverResult
                    {
                        ReceiverIndex = receiver.Index,
                        Position = recvPos,
                        SplDb = totalLinear > 0 ? Math.Round(10.0 * Math.Log10(totalLinear), 2) : -100.0,
                        SplDbA = aWeightedLinear > 0 ? Math.Round(10.0 * Math.Log10(aWeightedLinear), 2) : -100.0,
                        SplDbByBand = splDbByBand
                    });

                    bandDataBag.Add(BuildBandData(receiver.Index, arrivals, tail, directTime, t60, modFreqs, speechFactor));

                    int done = Interlocked.Increment(ref completed);
                    if (done % Math.Max(1, totalReceivers / 100) == 0)
                        progress?.Report((double)done / totalReceivers);
                }
            });

            var sortedResults = resultsBag.ToList();
            sortedResults.Sort((a, b) => a.ReceiverIndex.CompareTo(b.ReceiverIndex));
            var sortedBandData = bandDataBag.ToList();
            sortedBandData.Sort((a, b) => a.ReceiverIndex.CompareTo(b.ReceiverIndex));

            FileLogger.Log($"[SPLCalc] Done. Blocked source→receiver paths: {wallHitReceivers}");

            progress?.Report(1.0);
            return (sortedResults, sortedBandData);
        }

        /// <summary>
        /// Split each arrival into 50/80 ms early/late energy relative to the first arrival
        /// and accumulate the complex modulation sums Σ E·e^(−j2πF·t) for STI.
        /// </summary>
        private static ReceiverBandData BuildBandData(
            int receiverIndex,
            List<(double Time, double[] Power, int Source)> arrivals,
            double[][] tail,
            double[] directTime,
            double[] t60,
            double[] modFreqs,
            double[][] speechFactor)
        {
            int nb = OctaveBands.Count, nf = modFreqs.Length;
            var bd = new ReceiverBandData
            {
                ReceiverIndex = receiverIndex,
                Early80LinearByBand = new double[nb],
                Late80LinearByBand = new double[nb],
                ModulationRe = new double[nb][],
                ModulationIm = new double[nb][]
            };
            for (int k = 0; k < nb; k++)
            {
                bd.ModulationRe[k] = new double[nf];
                bd.ModulationIm[k] = new double[nf];
            }

            double tFirst = double.MaxValue;
            foreach (var a in arrivals)
                if (a.Time < tFirst) tFirst = a.Time;
            if (arrivals.Count == 0) return bd;

            double[] cos = new double[nf], sin = new double[nf];
            foreach (var (time, power, source) in arrivals)
            {
                double dt = time - tFirst;
                for (int f = 0; f < nf; f++)
                {
                    double w = 2.0 * Math.PI * modFreqs[f] * dt;
                    cos[f] = Math.Cos(w);
                    sin[f] = Math.Sin(w);
                }
                for (int k = 0; k < nb; k++)
                {
                    double e = power[k] * speechFactor[source][k];
                    if (e <= 0) continue;
                    if (dt <= Split50S) bd.EarlyLinearByBand[k] += e; else bd.LateLinearByBand[k] += e;
                    if (dt <= Split80S) bd.Early80LinearByBand[k] += e; else bd.Late80LinearByBand[k] += e;
                    for (int f = 0; f < nf; f++)
                    {
                        bd.ModulationRe[k][f] += e * cos[f];
                        bd.ModulationIm[k][f] -= e * sin[f];
                    }
                }
            }

            // Exponential tails: energy density (R/τ)·e^(−(t−t0)/τ) for t ≥ t0,
            // whose Fourier transform is R·e^(−jωt0)/(1 + jωτ).
            for (int s = 0; s < tail.Length; s++)
            {
                if (tail[s] == null) continue;
                double dt0 = directTime[s] - tFirst;
                for (int k = 0; k < nb; k++)
                {
                    double r = tail[s][k] * speechFactor[s][k];
                    if (r <= 0) continue;
                    double tau = t60[k] / 13.82;

                    double e50 = r * (1 - Math.Exp(-Math.Max(0, Split50S - dt0) / tau));
                    double e80 = r * (1 - Math.Exp(-Math.Max(0, Split80S - dt0) / tau));
                    bd.EarlyLinearByBand[k] += e50;
                    bd.LateLinearByBand[k] += r - e50;
                    bd.Early80LinearByBand[k] += e80;
                    bd.Late80LinearByBand[k] += r - e80;

                    for (int f = 0; f < nf; f++)
                    {
                        double w = 2.0 * Math.PI * modFreqs[f];
                        double c = Math.Cos(w * dt0), sn = Math.Sin(w * dt0);
                        double wt = w * tau, den = 1 + wt * wt;
                        bd.ModulationRe[k][f] += r * (c - sn * wt) / den;
                        bd.ModulationIm[k][f] += r * (-c * wt - sn) / den;
                    }
                }
            }

            return bd;
        }

        // --- Propagation helpers ---

        /// <summary>
        /// Field correction penalty in dB, subtracted from per-band nominal TL.
        /// Accounts for flanking paths, leaks and installation imperfections
        /// that degrade lab STC to in-situ performance (typically 3–8 dB).
        /// </summary>
        private const double FieldPenaltyDb = 5.0;
        private const double MaxTotalLossDb = 60.0;
        private const double TMin = 0.02;
        private const double TMax = 0.98;

        private static double Spread(double distance)
        {
            double ratio = RefDistanceM / distance;
            return ratio * ratio;
        }

        /// <summary>
        /// Linear loss for band k from wall transmission (nominal STC contour per ASTM E413
        /// minus field penalty) and air absorption, capped at <see cref="MaxTotalLossDb"/>.
        /// No TL at all when no wall is crossed, so the field penalty never adds attenuation.
        /// </summary>
        private static double LossFactor(double stcSum, int k, double airLossDb)
        {
            double tl = stcSum > 0 ? Math.Max(0, stcSum + OctaveBands.StcBandOffsets[k] - FieldPenaltyDb) : 0;
            return Math.Pow(10.0, -Math.Min(tl + airLossDb, MaxTotalLossDb) / 10.0);
        }

        // --- Wall transmission loss helpers ---

        /// <summary>
        /// Sums the STC ratings of all walls blocking the path p→q (heights zp→zq), skipping up
        /// to two walls by index (the reflecting walls of a reflected path; pass -1 for none).
        /// Indices of blocking walls are added to <paramref name="hits"/> when given.
        /// </summary>
        private static double SumWallStc(Vec2 p, Vec2 q, double zp, double zq, List<ComputeWall> walls,
            int exclude1, int exclude2, List<int> hits)
        {
            if (walls.Count == 0) return 0;

            Vec2 d = q - p;
            if (d.Length < 1e-9) return 0;

            double total = 0;
            for (int i = 0; i < walls.Count; i++)
            {
                if (i == exclude1 || i == exclude2) continue;
                ComputeWall w = walls[i];
                if (w.StcRating <= 0) continue;
                double t = BlockParam(p, d, w);
                if (t < 0) continue;
                if (!BelowTop(w, zp + (zq - zp) * t)) continue; // passes over a low wall
                total += w.StcRating;
                hits?.Add(i);
            }
            return total;
        }

        /// <summary>True when height z is below the top of the wall (always for full-height walls).</summary>
        private static bool BelowTop(ComputeWall w, double z) =>
            w.HeightM <= 0 || z <= w.BaseElevationM + w.HeightM;

        /// <summary>
        /// Where the wall blocks the path p→p+d in plan: the path parameter t of the crossing
        /// (or closest approach), or -1 when it doesn't block. A wall blocks if its centreline
        /// crosses the path, or passes within its half-thickness of it (bridges small gaps at
        /// corners and T-junctions). Only the middle part of the path (TMin..TMax) counts, so a
        /// wall right next to the speaker or the receiver — but not between them — never blocks.
        /// </summary>
        internal static double BlockParam(Vec2 p, Vec2 d, ComputeWall w)
        {
            double t = SegmentIntersectT(p, d, w.Start, w.End);
            if (t > TMin && t < TMax)
                return t;

            double halfThick = w.HalfThicknessM;
            if (halfThick <= 0) return -1;

            Vec2 a = p + d * TMin;
            Vec2 b = p + d * TMax;
            if (SegmentDistance(a, b, w.Start, w.End) > halfThick) return -1;

            // Closest approach: the wall end nearest the path, projected onto it
            double len2 = d.LengthSquared;
            double ts = Vec2.Dot(w.Start - p, d) / len2, te = Vec2.Dot(w.End - p, d) / len2;
            double ds = PointSegmentDistance(w.Start, a, b), de = PointSegmentDistance(w.End, a, b);
            return Math.Max(TMin, Math.Min(TMax, ds <= de ? ts : te));
        }

        /// <summary>Plan-view blocking test (kept for callers that only need yes/no).</summary>
        internal static bool RayBlockedByWall(Vec2 p, Vec2 d, ComputeWall w) => BlockParam(p, d, w) >= 0;

        /// <summary>A wall end is free (a diffracting edge) unless another wall passes within 0.2 m of it.</summary>
        private static bool IsFreeEnd(Vec2 end, int wallIndex, List<ComputeWall> walls)
        {
            for (int i = 0; i < walls.Count; i++)
            {
                if (i == wallIndex || walls[i].StcRating <= 0) continue;
                if (PointSegmentDistance(end, walls[i].Start, walls[i].End) < 0.2) return false;
            }
            return true;
        }

        /// <summary>
        /// Thin-screen diffraction attenuation in dB for Fresnel number N (Kurze–Anderson fit to
        /// Maekawa's data). Shadow zone, N = 2δ/λ &gt; 0: 5 + 20·log10(√(2πN) / tanh √(2πN)),
        /// capped at 25 dB. Bright zone (path clears the edge by a detour δ, N = −2δ/λ):
        /// 5 + 20·log10(√(2π|N|) / tan √(2π|N|)) for −0.2 &lt; N &lt; 0, else 0.
        /// Continuous: 5 dB at the shadow boundary N = 0.
        /// </summary>
        internal static double DiffractionAttenuationDb(double fresnelN)
        {
            if (fresnelN > 0)
            {
                double x = Math.Sqrt(2 * Math.PI * fresnelN);
                return Math.Min(25.0, 5.0 + 20.0 * Math.Log10(x / Math.Tanh(x)));
            }
            if (fresnelN <= -0.2) return 0;
            if (fresnelN > -1e-9) return 5.0;
            double y = Math.Sqrt(2 * Math.PI * -fresnelN);
            return Math.Max(0, 5.0 + 20.0 * Math.Log10(y / Math.Tan(y)));
        }

        /// <summary>
        /// For an unobstructed path S→R: the smallest detour δ (m) of a path via a nearby edge —
        /// a free wall end, or the top of a low wall the path passes over. MaxValue if none.
        /// </summary>
        private static double SmallestEdgeDetour(Vec3 s, Vec3 r, double direct, List<ComputeWall> walls,
            bool[] startFree, bool[] endFree)
        {
            double best = double.MaxValue;
            Vec2 s2 = new Vec2(s.X, s.Y), r2 = new Vec2(r.X, r.Y);
            Vec2 d = r2 - s2;
            double planLen = d.Length;
            for (int i = 0; i < walls.Count; i++)
            {
                ComputeWall w = walls[i];
                if (w.StcRating <= 0) continue;

                // Free vertical ends
                for (int e = 0; e < 2; e++)
                {
                    if (!(e == 0 ? startFree[i] : endFree[i])) continue;
                    Vec2 end = e == 0 ? w.Start : w.End;
                    double a = Vec2.Distance(s2, end), b = Vec2.Distance(end, r2);
                    double viaPlan = a + b;
                    double dz = r.Z - s.Z;
                    double detour = Math.Sqrt(viaPlan * viaPlan + dz * dz) - direct;
                    if (detour < best) best = detour;
                }

                // Top of a low wall that the path passes over
                if (w.HeightM > 0 && planLen > 1e-9)
                {
                    double t = SegmentIntersectT(s2, d, w.Start, w.End);
                    if (t > 0 && t < 1)
                    {
                        Vec2 c = s2 + d * t;
                        Vec3 top = new Vec3(c.X, c.Y, w.BaseElevationM + w.HeightM);
                        double detour = (top - s).Length + (r - top).Length - direct;
                        if (detour < best) best = detour;
                    }
                }
            }
            return best;
        }

        /// <summary>Shortest distance between segments AB and CD.</summary>
        private static double SegmentDistance(Vec2 a, Vec2 b, Vec2 c, Vec2 d)
        {
            if (SegmentsIntersect(a, b, c, d)) return 0;
            return Math.Min(
                Math.Min(PointSegmentDistance(a, c, d), PointSegmentDistance(b, c, d)),
                Math.Min(PointSegmentDistance(c, a, b), PointSegmentDistance(d, a, b)));
        }

        private static bool SegmentsIntersect(Vec2 a, Vec2 b, Vec2 c, Vec2 d)
        {
            double d1 = Vec2.Cross(d - c, a - c), d2 = Vec2.Cross(d - c, b - c);
            double d3 = Vec2.Cross(b - a, c - a), d4 = Vec2.Cross(b - a, d - a);
            return ((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) &&
                   ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0));
        }

        private static double PointSegmentDistance(Vec2 p, Vec2 a, Vec2 b)
        {
            Vec2 ab = b - a;
            double len2 = ab.LengthSquared;
            double t = len2 > 1e-18 ? Math.Max(0, Math.Min(1, Vec2.Dot(p - a, ab) / len2)) : 0;
            return Vec2.Distance(p, a + ab * t);
        }

        /// <summary>
        /// Reflect a 2D point across the infinite line defined by segment A→B.
        /// Returns a Vec2 with NaN if the segment is degenerate (zero length).
        /// </summary>
        private static Vec2 ReflectPointAcrossSegment(Vec2 point, Vec2 a, Vec2 b)
        {
            Vec2 ab = b - a;
            double lenSq = ab.LengthSquared;
            if (lenSq < 1e-12) return new Vec2(double.NaN, double.NaN);

            double t = Vec2.Dot(point - a, ab) / lenSq;
            Vec2 proj = a + ab * t;
            return proj * 2.0 - point;
        }

        /// <summary>
        /// Parameter t along p→p+d where it crosses segment A→B, or -1 if it doesn't
        /// (within the open interval 0 &lt; t &lt; 1).
        /// </summary>
        private static double SegmentIntersectT(Vec2 p, Vec2 d, Vec2 a, Vec2 b)
        {
            Vec2 e = b - a;
            double dxe = Vec2.Cross(d, e);

            if (Math.Abs(dxe) < 1e-12)
                return -1;

            Vec2 f = a - p;
            double t = Vec2.Cross(f, e) / dxe;
            double u = Vec2.Cross(f, d) / dxe;

            if (t > 0.0 && t < 1.0 && u >= 0.0 && u <= 1.0)
                return t;
            return -1;
        }
    }
}
