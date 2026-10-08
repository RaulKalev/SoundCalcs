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

            // Collinear pieces of one wall overlap at their joints (each end is extended): a ray through a joint
            // would pay the wall's TL twice
            var walls = JobInputBuilder.RemoveCollinearOverlaps(input.Walls ?? new List<ComputeWall>());
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
                sourceRoomIndex[s] = SourceRoom(input.Rooms, input.Sources[s]);

            // Per-room RT60: the room's own when set, else the job's
            double[][] roomT60 = new double[numRooms][];
            for (int r = 0; r < numRooms; r++)
            {
                double[] rt = input.Rooms[r].RT60ByBand;
                roomT60[r] = rt != null && rt.Length == numBands ? rt.Select(v => Math.Max(v, 0.05)).ToArray() : t60;
            }

            // Per-source RT60: the source's room's own RT60 when set, else the job's
            double[][] sourceT60 = new double[numSources][];
            for (int s = 0; s < numSources; s++)
            {
                int ri = sourceRoomIndex[s];
                double[] roomRt = ri >= 0 ? input.Rooms[ri].RT60ByBand : null;
                sourceT60[s] = roomRt != null && roomRt.Length == numBands
                    ? roomRt.Select(v => Math.Max(v, 0.05)).ToArray()
                    : t60;
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
                    double vol = room.EffectiveAreaM2 * ceilingH;
                    // No walls around the area at all: no diffuse field (see EnclosureFactor)
                    double enclosure = EnclosureFactor(room, numBands);
                    if (vol <= 1.0 || enclosure <= 0.01) continue;

                    hasReverb = true;
                    // A flush-mounted speaker radiates only into the room below the ceiling. Q per band:
                    // a cone beams at high frequencies and is nearly omni at low ones.
                    bool flush = room.FloorElevationM + ceilingH - input.Sources[s].Position.Z <= FlushMountToleranceM;
                    Vec3 facing = input.Sources[s].FacingDirection.Normalized();
                    for (int k = 0; k < numBands; k++)
                    {
                        double q = flush ? LowerHemisphereQ(providers[s], facing, k) : DirectivityQ(providers[s], facing, k);
                        double sabineA = Math.Max(0.161 * vol / sourceT60[s][k], 1.0);
                        reverbBySource[s][k] = sourceBand[s][k] * 16.0 * Math.PI / (q * sabineA) * enclosure;
                    }
                }
            }

            // --- Reverberant sound passing into neighbouring rooms ---
            // A source's reverberant field reaches the partitions of its room and is transmitted through them,
            // becoming reverberant sound in the next room (EN ISO 12354-1 / SEA): L2 = L1 − R + 10·log(S/A2).
            // The direct ray through a partition is traced separately; this is the diffuse part, the same
            // everywhere in the receiving room. transmitted[s][room] = per-band energy, transmitVia = the
            // partition it mostly comes through (for its arrival time).
            double[][][] transmitted = new double[numSources][][];
            Vec2[][] transmitVia = new Vec2[numSources][];
            if (hasReverb && numRooms > 1)
            {
                List<Partition> partitions = FindPartitions(input.Rooms, walls, DefaultCeilingHeightM);
                for (int s = 0; s < numSources; s++)
                {
                    int ri = sourceRoomIndex[s];
                    if (ri < 0) continue;
                    Vec2 srcXY = new Vec2(input.Sources[s].Position.X, input.Sources[s].Position.Y);
                    var best = new double[numRooms];
                    foreach (Partition part in partitions)
                    {
                        int other = part.RoomA == ri ? part.RoomB : part.RoomB == ri ? part.RoomA : -1;
                        if (other < 0) continue;
                        RoomPolygon recvRoomPoly = input.Rooms[other];
                        double hB = recvRoomPoly.CeilingHeightM > 0.5 ? recvRoomPoly.CeilingHeightM : DefaultCeilingHeightM;
                        double volB = recvRoomPoly.EffectiveAreaM2 * hB;
                        if (volB <= 1.0) continue;
                        double[] rtB = recvRoomPoly.RT60ByBand != null && recvRoomPoly.RT60ByBand.Length == numBands ? recvRoomPoly.RT60ByBand : t60;

                        // Source-room reverberant level at the partition (Barron: decayed over the travel time)
                        double timeToWall = Vec2.Distance(srcXY, part.Midpoint) / speedOfSound;
                        if (transmitted[s] == null) { transmitted[s] = new double[numRooms][]; transmitVia[s] = new Vec2[numRooms]; }
                        if (transmitted[s][other] == null) transmitted[s][other] = new double[numBands];
                        double sum = 0;
                        for (int k = 0; k < numBands; k++)
                        {
                            double atWall = reverbBySource[s][k] * Math.Exp(-13.82 * timeToWall / sourceT60[s][k]);
                            double tau = Math.Pow(10.0, -WallTlDb(walls[part.Wall].StcRating, k) / 10.0);
                            double aB = Math.Max(0.161 * volB / Math.Max(rtB[k], 0.05), 1.0);
                            double e = atWall * tau * part.AreaM2 / aB * EnclosureFactor(recvRoomPoly, numBands);
                            transmitted[s][other][k] += e;
                            sum += e;
                        }
                        if (sum > best[other]) { best[other] = sum; transmitVia[s][other] = part.Midpoint; }
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
                    if (walls[w].IsObstacle) continue;   // column faces block but don't mirror
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
                    ComputeWall w1 = walls[img1.WallIndex];
                    Vec2 d1 = w1.End - w1.Start;
                    double imageSide = Vec2.Cross(d1, img1.ImagePos - w1.Start);
                    for (int w = 0; w < walls.Count; w++)
                    {
                        if (w == img1.WallIndex || walls[w].IsObstacle) continue;
                        // The second bounce comes after the first, so its wall must reach the source's side of
                        // the first wall: one lying wholly on the image side can't be hit (the path check
                        // would reject every receiver anyway)
                        if (Vec2.Cross(d1, walls[w].Start - w1.Start) * imageSide > 1e-9 &&
                            Vec2.Cross(d1, walls[w].End - w1.Start) * imageSide > 1e-9) continue;

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
                    // The ceiling of the speaker's own room (the ceiling is there whether or not walls are drawn)
                    int srcRoom = sourceRoomIndex[s];
                    double roomCeilingZ = ceilingZ;
                    if (srcRoom >= 0)
                    {
                        RoomPolygon sr = input.Rooms[srcRoom];
                        roomCeilingZ = sr.FloorElevationM + (sr.CeilingHeightM > 0.5 ? sr.CeilingHeightM : DefaultCeilingHeightM);
                    }
                    if (roomCeilingZ - srcPos.Z > FlushMountToleranceM && srcRoom >= 0)
                    {
                        double[] ceilCoeffs = new double[numBands];
                        for (int k = 0; k < numBands; k++)
                            ceilCoeffs[k] = 1.0 - ceilingAbsorption[k];
                        horizImages.Add(new HorizontalImageSource
                        {
                            ImagePos3D = new Vec3(srcPos.X, srcPos.Y, 2.0 * roomCeilingZ - srcPos.Z),
                            SurfaceZ = roomCeilingZ,
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

            // --- Shoebox lattice for (near-)rectangular rooms (Full quality) ---
            // Replaces the explicit reflections and the diffuse tail for sources and receivers in
            // the same box-shaped room; other rooms keep Barron's diffuse-field model.
            var lattice = new ShoeboxLattice[numRooms];
            if (!isDraft && input.Environment.UseRoomShapeModel)
            {
                for (int r = 0; r < numRooms; r++)
                {
                    double[] roomRt = input.Rooms[r].RT60ByBand;
                    double[] rt = roomRt != null && roomRt.Length == numBands ? roomRt : t60;
                    lattice[r] = ShoeboxLattice.TryCreate(input.Rooms[r], walls, floorAbsorption, ceilingAbsorption,
                        rt, airAbsorption, DefaultCeilingHeightM);
                }
                FileLogger.Log($"[SPLCalc] Shoebox lattice rooms: {lattice.Count(l => l != null)}/{numRooms}");
            }
            int latticeBins = (int)Math.Ceiling(ShoeboxLattice.CutoffTimeS / ShoeboxLattice.BinWidthS);
            // Power each source radiates into its box (for the lattice's diffuse part), computed on first
            // use; threads racing here compute the same array, so the race is harmless
            var latticePower = new double[numSources][];

            // Where each speaker's paths start in plan, for blocking: a speaker on a wall line (wall-mounted,
            // the line traced along the face it hangs on) is in front of that wall, not in it
            Vec2[] srcPlan = new Vec2[numSources];
            for (int s = 0; s < numSources; s++)
                srcPlan[s] = PlanOrigin(input.Sources[s], walls);

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

                    // Sources whose reflections come from the shoebox lattice at this receiver
                    bool[] useLattice = new bool[numSources];
                    for (int s = 0; s < numSources; s++)
                    {
                        int sr = sourceRoomIndex[s];
                        useLattice[s] = sr >= 0 && sr == receiver.RoomIndex && lattice.Length > sr && lattice[sr] != null;
                    }

                    double[] directTime = new double[numSources];
                    double[] directDist = new double[numSources];
                    double[][] directTl = new double[numSources][];   // per-band TL of the walls on the direct path
                    double[] imageTl = new double[numBands];
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

                        Vec2 srcXY0 = srcPlan[s];
                        var hits = new List<int>(2);
                        directTl[s] = new double[numBands];
                        double stc = SumWallStc(srcXY0, recvXY, source.Position.Z, recvPos.Z, walls, -1, -1, hits, directTl[s]);
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
                                 * LossFactor(directTl[s], k, airAbsorption[k] * distance)
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
                        if (useLattice[img.SourceIndex]) continue;
                        ComputeSource source = input.Sources[img.SourceIndex];
                        Vec2 srcXY = srcPlan[img.SourceIndex];

                        Vec2 imgToRecv = recvXY - img.ImagePos;
                        double len2D = imgToRecv.Length;
                        if (len2D < MinDistanceM) continue;

                        // Last bounce: image→receiver must cross its wall
                        double tLast = SegmentIntersectT(img.ImagePos, imgToRecv, walls[img.WallIndex].Start, walls[img.WallIndex].End);
                        if (tLast <= 0.0 || tLast >= 1.0) continue;
                        Vec2 lastPt = img.ImagePos + imgToRecv * tLast;
                        // A bounce inside another wall (where a partition meets the reflecting wall) is no reflection
                        if (InsideOtherWall(lastPt, walls, img.WallIndex, img.FirstWallIndex)) continue;

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
                            if (InsideOtherWall(firstPt, walls, img.WallIndex, img.FirstWallIndex)) continue;
                            double zFirst = ZAt(Vec2.Distance(srcXY, firstPt));
                            if (!BelowTop(walls[img.FirstWallIndex], zFirst)) continue;

                            // Bounce points lie on the reflecting walls (excluded by index), so the
                            // legs aren't trimmed there: a wall right next to a bounce point still blocks.
                            Array.Clear(imageTl, 0, numBands);
                            otherStc = SumWallStc(srcXY, firstPt, zSrc, zFirst, walls, img.FirstWallIndex, img.WallIndex, null, imageTl, true, false)
                                     + SumWallStc(firstPt, lastPt, zFirst, zLast, walls, img.FirstWallIndex, img.WallIndex, null, imageTl, false, false)
                                     + SumWallStc(lastPt, recvXY, zLast, recvPos.Z, walls, img.FirstWallIndex, img.WallIndex, null, imageTl, false, true);
                        }
                        else
                        {
                            Array.Clear(imageTl, 0, numBands);
                            otherStc = SumWallStc(srcXY, lastPt, zSrc, zLast, walls, img.WallIndex, -1, null, imageTl, true, false)
                                     + SumWallStc(lastPt, recvXY, zLast, recvPos.Z, walls, img.WallIndex, -1, null, imageTl, false, true);
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
                                 * LossFactor(imageTl, k, airAbsorption[k] * pathLen)
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
                        if (useLattice[himg.SourceIndex]) continue;
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

                        double[] tl = directTl[himg.SourceIndex]; // plan path is source → receiver

                        double[] p = new double[numBands];
                        for (int k = 0; k < numBands; k++)
                        {
                            double gain = providers[himg.SourceIndex].GetDirectivityGainForBand(facing, dir, k);
                            p[k] = sourceBand[himg.SourceIndex][k] * Spread(pathLen) * gain * gain
                                 * LossFactor(tl, k, airAbsorption[k] * pathLen)
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
                    // Only for sources in the receiver's room (the reverberant field fills the room,
                    // also behind screens and partitions inside it).
                    // In box-shaped rooms the shoebox lattice gives the reflections instead, binned
                    // in time, with an exponential tail after its cutoff.
                    double[][] tail = new double[numSources][];
                    // Each tail's start and decay time constant per band
                    double[][] tailStart = new double[numSources][];
                    double[][] tailTau = new double[numSources][];
                    int recvRoom = receiver.RoomIndex;
                    for (int s = 0; s < numSources; s++)
                    {
                        if (recvRoom < 0) continue;
                        if (sourceRoomIndex[s] != recvRoom)
                        {
                            // Reverberant sound from the source's room, through the partition between them
                            double[] through = sourceRoomIndex[s] >= 0 ? transmitted[s]?[recvRoom] : null;
                            if (through == null) continue;
                            Vec2 via = transmitVia[s][recvRoom];
                            Vec2 srcPlanXY = new Vec2(input.Sources[s].Position.X, input.Sources[s].Position.Y);
                            tail[s] = new double[numBands];
                            tailStart[s] = new double[numBands];
                            tailTau[s] = new double[numBands];
                            double arrive = (Vec2.Distance(srcPlanXY, via) + Vec2.Distance(via, recvXY)) / speedOfSound;
                            for (int k = 0; k < numBands; k++)
                            {
                                tail[s][k] = through[k];
                                totalByBand[k] += through[k];
                                tailStart[s][k] = arrive;
                                // Decaying in the source room and again in this one: the two decays in series
                                tailTau[s][k] = (sourceT60[s][k] + roomT60[recvRoom][k]) / 13.82;
                            }
                            continue;
                        }

                        if (useLattice[s])
                        {
                            ComputeSource source = input.Sources[s];
                            Vec3 facing = source.FacingDirection.Normalized();
                            var provider = providers[s];
                            var bins = new double[numBands][];
                            for (int k = 0; k < numBands; k++) bins[k] = new double[latticeBins];
                            var tailEnergy = new double[numBands];
                            ShoeboxLattice box = lattice[recvRoom];
                            Func<Vec3, int, double> gainSquared =
                                (dir, k) => { double g = provider.GetDirectivityGainForBand(facing, dir, k); return g * g; };
                            if (latticePower[s] == null)
                                latticePower[s] = box.RadiatedPower(source.Position, gainSquared);
                            box.Accumulate(source.Position, recvPos, sourceBand[s], gainSquared,
                                airAbsorption, speedOfSound, bins, tailEnergy, latticePower[s]);

                            for (int b = 0; b < latticeBins; b++)
                            {
                                double[] pb = new double[numBands];
                                bool any = false;
                                for (int k = 0; k < numBands; k++)
                                {
                                    pb[k] = bins[k][b];
                                    if (pb[k] > 0) { any = true; totalByBand[k] += pb[k]; }
                                }
                                if (any) arrivals.Add(((b + 0.5) * ShoeboxLattice.BinWidthS, pb, s));
                            }

                            tail[s] = new double[numBands];
                            tailStart[s] = new double[numBands];
                            tailTau[s] = new double[numBands];
                            for (int k = 0; k < numBands; k++)
                            {
                                tail[s][k] = tailEnergy[k];
                                totalByBand[k] += tail[s][k];
                                tailStart[s][k] = ShoeboxLattice.CutoffTimeS;
                                tailTau[s][k] = sourceT60[s][k] / 13.82;
                            }
                            continue;
                        }

                        if (!hasReverb) continue;
                        tail[s] = new double[numBands];
                        tailStart[s] = new double[numBands];
                        tailTau[s] = new double[numBands];
                        for (int k = 0; k < numBands; k++)
                        {
                            double tau = sourceT60[s][k] / 13.82;
                            double barron = reverbBySource[s][k] * Math.Exp(-directTime[s] / tau);
                            double rest = Math.Max(0, barron - imageEnergy[s][k]);
                            tail[s][k] = rest;
                            totalByBand[k] += rest;
                            // The explicit reflections are the head of Barron's decay; the rest is its later part,
                            // starting where the energy still to come equals what is left
                            tailStart[s][k] = directTime[s] + (rest > 0 && barron > rest ? tau * Math.Log(barron / rest) : 0);
                            tailTau[s][k] = tau;
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

                    bandDataBag.Add(BuildBandData(receiver.Index, arrivals, tail, tailStart, tailTau, modFreqs, speechFactor));

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
            double[][] tailStart,
            double[][] tailTau,
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
                for (int k = 0; k < nb; k++)
                {
                    double r = tail[s][k] * speechFactor[s][k];
                    if (r <= 0) continue;
                    double dt0 = tailStart[s][k] - tFirst;
                    double tau = tailTau[s][k];

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
        /// <summary>
        /// A speaker or receiver this close to a wall line (in plan) is on the wall: the wall does not block its
        /// paths. Absolute, not a fraction of the path: a speaker 10 cm in front of a wall must stay behind it
        /// for listeners 20 m away too.
        /// </summary>
        private const double EndToleranceM = 0.02;

        /// <summary>Path parameter that leaves <see cref="EndToleranceM"/> at an end of a path of length <paramref name="len"/>.</summary>
        private static double EndTrim(double len) => Math.Min(0.25, EndToleranceM / Math.Max(len, 1e-9));

        private static double Spread(double distance)
        {
            double ratio = RefDistanceM / distance;
            return ratio * ratio;
        }

        /// <summary>
        /// In-situ transmission loss of one wall in band k: its rating on the reference contour
        /// (<see cref="OctaveBands.StcBandOffsets"/>) minus the field penalty, never negative.
        /// </summary>
        internal static double WallTlDb(int stc, int k) =>
            stc > 0 ? Math.Max(0, stc + OctaveBands.StcBandOffsets[k] - FieldPenaltyDb) : 0;

        /// <summary>
        /// Linear loss for band k from the walls crossed (<paramref name="tlByBand"/>: their TLs summed per band,
        /// each wall on its own contour) and air absorption, capped at <see cref="MaxTotalLossDb"/>.
        /// </summary>
        private static double LossFactor(double[] tlByBand, int k, double airLossDb)
        {
            double tl = tlByBand?[k] ?? 0;
            return Math.Pow(10.0, -Math.Min(tl + airLossDb, MaxTotalLossDb) / 10.0);
        }

        // --- Wall transmission loss helpers ---

        /// <summary>
        /// Sums the STC ratings of all walls blocking the path p→q (heights zp→zq). Up to two walls
        /// (the reflecting walls of a reflected path; -1 for none) block only where the leg really crosses
        /// them, not at its ends (the bounce points on them).
        /// Indices of blocking walls are added to <paramref name="hits"/> when given; each blocking wall's
        /// per-band TL is added to <paramref name="tlByBand"/> when given (walls in series add in dB, each
        /// on its own contour).
        /// </summary>
        private static double SumWallStc(Vec2 p, Vec2 q, double zp, double zq, List<ComputeWall> walls,
            int exclude1, int exclude2, List<int> hits, double[] tlByBand = null, bool trimStart = true, bool trimEnd = true)
        {
            if (walls.Count == 0) return 0;

            Vec2 d = q - p;
            if (d.Length < 1e-9) return 0;

            double total = 0;
            double trim = EndTrim(d.Length);
            for (int i = 0; i < walls.Count; i++)
            {
                ComputeWall w = walls[i];
                if (w.StcRating <= 0) continue;
                double t;
                if (i == exclude1 || i == exclude2)
                {
                    // A wall the path reflects off (or diffracts around): the leg touches it at its bounce point
                    // (a leg end), which is no crossing. A straight leg can't meet the wall anywhere else, except
                    // when the bounce is on another leg: then crossing it costs its TL like any wall (a path
                    // through a partition that comes back off the partition's far face).
                    t = SegmentIntersectT(p, d, w.Start, w.End);
                    if (!(t > trim && t < 1 - trim)) continue;
                }
                else
                {
                    t = BlockParam(p, d, w, trimStart ? trim : 1e-9, trimEnd ? 1 - trim : 1 - 1e-9);
                    if (t < 0) continue;
                }
                if (!BelowTop(w, zp + (zq - zp) * t)) continue; // passes over a low wall
                total += w.StcRating;
                hits?.Add(i);
                if (tlByBand != null)
                    for (int k = 0; k < tlByBand.Length; k++) tlByBand[k] += WallTlDb(w.StcRating, k);
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
        /// corners and T-junctions, so only near a wall end). The path's ends are trimmed by
        /// <see cref="EndToleranceM"/>, so a wall the speaker or receiver stands on never blocks.
        /// </summary>
        internal static double BlockParam(Vec2 p, Vec2 d, ComputeWall w, double tMin = double.NaN, double tMax = double.NaN)
        {
            if (double.IsNaN(tMin)) tMin = EndTrim(d.Length);
            if (double.IsNaN(tMax)) tMax = 1 - EndTrim(d.Length);
            double t = SegmentIntersectT(p, d, w.Start, w.End);
            if (t > tMin && t < tMax)
                return t;

            double halfThick = w.HalfThicknessM;
            if (halfThick <= 0) return -1;

            Vec2 a = p + d * tMin;
            Vec2 b = p + d * tMax;
            if (SegmentDistance(a, b, w.Start, w.End) > halfThick) return -1;

            // Closest approach: the wall end nearest the path, projected onto it
            double len2 = d.LengthSquared;
            double ts = Vec2.Dot(w.Start - p, d) / len2, te = Vec2.Dot(w.End - p, d) / len2;
            double ds = PointSegmentDistance(w.Start, a, b), de = PointSegmentDistance(w.End, a, b);
            // Bridge gaps at wall ends only. Passing close to the wall's body without crossing it means the
            // speaker or receiver is in or on the wall (a wall-mounted speaker): that wall does not block.
            if (Math.Min(ds, de) > halfThick) return -1;
            return Math.Max(tMin, Math.Min(tMax, ds <= de ? ts : te));
        }

        /// <summary>Plan-view blocking test (kept for callers that only need yes/no).</summary>
        internal static bool RayBlockedByWall(Vec2 p, Vec2 d, ComputeWall w) => BlockParam(p, d, w) >= 0;

        /// <summary>
        /// The room a speaker radiates into. A wall-mounted speaker usually sits on a wall line, where the
        /// point-in-room test can fall on either side: it is tested 0.25 m in front of it. A speaker in no room
        /// (just outside a room's edge) belongs to the nearest room within 0.5 m.
        /// </summary>
        private static int SourceRoom(List<RoomPolygon> rooms, ComputeSource src)
        {
            if (rooms == null || rooms.Count == 0) return -1;
            Vec2 p = new Vec2(src.Position.X, src.Position.Y);
            Vec3 f = src.FacingDirection;
            double h = Math.Sqrt(f.X * f.X + f.Y * f.Y);
            var probes = new List<Vec2>();
            if (h > 0.5) probes.Add(p + new Vec2(f.X / h, f.Y / h) * 0.25);
            probes.Add(p);
            foreach (Vec2 q in probes)
                for (int r = 0; r < rooms.Count; r++)
                    if (rooms[r].ContainsPoint(q)) return r;

            int best = -1;
            double bestD = 0.5;
            for (int r = 0; r < rooms.Count; r++)
                foreach (List<Vec2> ring in rooms[r].Rings())
                    for (int i = 0; i < ring.Count; i++)
                    {
                        double d = PointSegmentDistance(p, ring[i], ring[(i + 1) % ring.Count]);
                        if (d < bestD) { bestD = d; best = r; }
                    }
            return best;
        }

        /// <summary>
        /// Whether a room has a reverberant field: 1 when any of its perimeter is walled, 0 for an area with no walls
        /// around it at all. The level itself follows from the RT60 (4W/A with A = 0.161·V/T), which already includes
        /// the sound leaving through open sides: scaling it by the walled fraction again would count them twice (and
        /// made the image-source path disagree with the room-shape model by 10·log of that fraction).
        /// </summary>
        private static double EnclosureFactor(RoomPolygon room, int numBands) =>
            room.EnclosureRatio > 0.01 ? 1.0 : 0.0;

        /// <summary>
        /// Where a speaker's paths start in plan for the blocking tests. A speaker facing sideways within 4 cm of a
        /// wall line hangs on that wall: its paths start 4 cm in front of it, so the wall is behind it (else a
        /// speaker on the line would radiate through its own wall into the next room).
        /// </summary>
        private static Vec2 PlanOrigin(ComputeSource src, List<ComputeWall> walls)
        {
            var p = new Vec2(src.Position.X, src.Position.Y);
            Vec3 f = src.FacingDirection;
            double h = Math.Sqrt(f.X * f.X + f.Y * f.Y);
            if (h < 0.5) return p;
            double reach = 2 * EndToleranceM;
            foreach (ComputeWall w in walls)
                if (w.StcRating > 0 && PointSegmentDistance(p, w.Start, w.End) < reach)
                    return p + new Vec2(f.X / h, f.Y / h) * reach;
            return p;
        }

        /// <summary>
        /// Whether a bounce point lies inside a wall other than the reflecting ones (within its half thickness of
        /// its centreline): where a partition meets the reflecting wall, the bounce would be in the partition.
        /// </summary>
        private static bool InsideOtherWall(Vec2 p, List<ComputeWall> walls, int reflecting1, int reflecting2)
        {
            for (int i = 0; i < walls.Count; i++)
            {
                if (i == reflecting1 || i == reflecting2) continue;
                ComputeWall w = walls[i];
                if (w.StcRating <= 0) continue;
                if (PointSegmentDistance(p, w.Start, w.End) < w.HalfThicknessM) return true;
            }
            return false;
        }

        /// <summary>A wall between two rooms: the area through which one room's sound reaches the other.</summary>
        private class Partition
        {
            public int Wall, RoomA, RoomB;
            public double AreaM2;
            public Vec2 Midpoint;
        }

        /// <summary>
        /// The full-height walls that separate two rooms, with the area they share: sample points along each wall,
        /// just off both faces, tell which rooms lie on either side.
        /// </summary>
        private static List<Partition> FindPartitions(List<RoomPolygon> rooms, List<ComputeWall> walls, double defaultCeilingM)
        {
            var result = new List<Partition>();
            for (int w = 0; w < walls.Count; w++)
            {
                ComputeWall wall = walls[w];
                if (wall.StcRating <= 0 || (wall.HeightM > 0 && wall.HeightM < JobInputBuilder.EnclosingHeightM)) continue;
                Vec2 d = wall.End - wall.Start;
                double len = d.Length;
                if (len < 0.1) continue;
                Vec2 u = d * (1.0 / len), n = new Vec2(-u.Y, u.X);
                double off = wall.HalfThicknessM + 0.05;
                int samples = Math.Max(4, (int)Math.Ceiling(len / 0.25));
                var shared = new Dictionary<(int, int), (double Length, Vec2 Sum, int Count)>();
                for (int i = 0; i < samples; i++)
                {
                    Vec2 p = wall.Start + d * ((i + 0.5) / samples);
                    int left = RoomAt(rooms, p + n * off), right = RoomAt(rooms, p - n * off);
                    if (left < 0 || right < 0 || left == right) continue;
                    var key = left < right ? (left, right) : (right, left);
                    shared.TryGetValue(key, out var acc);
                    shared[key] = (acc.Length + len / samples, acc.Sum + p, acc.Count + 1);
                }
                foreach (var kv in shared)
                {
                    double hA = rooms[kv.Key.Item1].CeilingHeightM > 0.5 ? rooms[kv.Key.Item1].CeilingHeightM : defaultCeilingM;
                    double hB = rooms[kv.Key.Item2].CeilingHeightM > 0.5 ? rooms[kv.Key.Item2].CeilingHeightM : defaultCeilingM;
                    double h = Math.Min(hA, hB);
                    if (wall.HeightM > 0) h = Math.Min(h, wall.HeightM);
                    result.Add(new Partition
                    {
                        Wall = w, RoomA = kv.Key.Item1, RoomB = kv.Key.Item2,
                        AreaM2 = kv.Value.Length * h,
                        Midpoint = kv.Value.Sum * (1.0 / kv.Value.Count)
                    });
                }
            }
            return result;
        }

        private static int RoomAt(List<RoomPolygon> rooms, Vec2 p)
        {
            for (int r = 0; r < rooms.Count; r++)
                if (rooms[r].ContainsPoint(p)) return r;
            return -1;
        }

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
        /// Directivity factor of a speaker in one band: Q = 4π / ∫ g² dΩ over the whole sphere (omni = 1).
        /// The reverberant field is fed by the power the speaker radiates in that band, so its Q must follow
        /// the band's beaming, not one broadband value.
        /// </summary>
        public static double DirectivityQ(ISpeakerDirectivityProvider provider, Vec3 facing, int band)
        {
            const int nTheta = 48, nPhi = 48;
            double sum = 0;
            for (int i = 0; i < nTheta; i++)
            {
                double theta = (i + 0.5) * Math.PI / nTheta; // from straight down
                double dOmega = Math.Sin(theta) * (Math.PI / nTheta) * (2 * Math.PI / nPhi);
                for (int j = 0; j < nPhi; j++)
                {
                    double phi = (j + 0.5) * 2 * Math.PI / nPhi;
                    var dir = new Vec3(Math.Sin(theta) * Math.Cos(phi), Math.Sin(theta) * Math.Sin(phi), -Math.Cos(theta));
                    double g = provider.GetDirectivityGainForBand(facing, dir, band);
                    sum += g * g * dOmega;
                }
            }
            return sum > 1e-9 ? Math.Max(1.0, 4 * Math.PI / sum) : 1.0;
        }

        /// <summary>
        /// Directivity factor of a speaker that radiates only downwards (flush in the ceiling):
        /// Q = 4π / ∫ g² dΩ over the lower hemisphere. An omni gives 2.
        /// </summary>
        internal static double LowerHemisphereQ(ISpeakerDirectivityProvider provider, Vec3 facing, int band)
        {
            const int nTheta = 24, nPhi = 48;
            double sum = 0;
            for (int i = 0; i < nTheta; i++)
            {
                double theta = (i + 0.5) * (Math.PI / 2) / nTheta; // from straight down
                double dOmega = Math.Sin(theta) * (Math.PI / 2 / nTheta) * (2 * Math.PI / nPhi);
                for (int j = 0; j < nPhi; j++)
                {
                    double phi = (j + 0.5) * 2 * Math.PI / nPhi;
                    var dir = new Vec3(Math.Sin(theta) * Math.Cos(phi), Math.Sin(theta) * Math.Sin(phi), -Math.Cos(theta));
                    double g = provider.GetDirectivityGainForBand(facing, dir, band);
                    sum += g * g * dOmega;
                }
            }
            return sum > 1e-9 ? 4 * Math.PI / sum : 1.0;
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
