using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SoundCalcs.Domain;
using SoundCalcs.IO;

namespace SoundCalcs.Compute
{
    /// <summary>
    /// Intermediate per-receiver octave-band data produced by SPLCalculator
    /// and consumed by STICalculator.
    /// </summary>
    public class ReceiverBandData
    {
        public int ReceiverIndex { get; set; }

        /// <summary>Energy sum of sources arriving within 50 ms of the earliest (signal).</summary>
        public double[] EarlyLinearByBand { get; set; } = new double[OctaveBands.Count];

        /// <summary>Energy sum of sources arriving more than 50 ms after the earliest (late/noise).</summary>
        public double[] LateLinearByBand { get; set; } = new double[OctaveBands.Count];
    }

    /// <summary>
    /// Computes per-octave-band and broadband SPL at each receiver point
    /// from all sources. Tracks per-source arrival time for early/late
    /// classification used by the STI calculator.
    /// Pure C# math — no Revit references. Thread-safe and parallelized.
    /// </summary>
    public class SPLCalculator
    {
        private const double RefDistanceM = 1.0;
        private const double MinDistanceM = 0.01;
        private const double DefaultCeilingHeightM = 3.0;

        /// <summary>
        /// IEC 60268-16 early/late threshold in seconds.
        /// Sources whose sound arrives more than 50 ms after the earliest
        /// source at a receiver are classified as "late" (degrading STI).
        /// </summary>
        private const double EarlyLateThresholdS = 0.050;

        // Per-band global wall absorption (set at start of Calculate)
        private double[] _globalAbsorption;
        // Per-band air absorption computed from temperature (set at start of Calculate)
        private double[] _airAbsorption;

        /// <summary>
        /// Pre-computed first-order image source: a real source reflected
        /// across a single wall surface.
        /// </summary>
        private struct ImageSource
        {
            public Vec2 ImagePos;        // Reflected position (2D)
            public int SourceIndex;       // Index of the real source
            public int WallIndex;         // Index of the reflecting wall
            public double[] ReflectionCoeffByBand; // (1 − α_k) per octave band
        }

        /// <summary>
        /// Ceiling or floor image source: source mirrored across a horizontal plane.
        /// </summary>
        private struct HorizontalImageSource
        {
            public Vec3 ImagePos3D;       // Full 3D mirrored position
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

            // --- Wall diagnostics ---
            FileLogger.Log($"[SPLCalc] Wall count: {walls.Count}, " +
                $"Sources: {input.Sources.Count}, Receivers: {input.Receivers.Count}");
            for (int wi = 0; wi < Math.Min(walls.Count, 20); wi++)
            {
                var w = walls[wi];
                FileLogger.Log($"  Wall[{wi}] STC={w.StcRating}  " +
                    $"({w.Start.X:F3},{w.Start.Y:F3})→({w.End.X:F3},{w.End.Y:F3})  " +
                    $"len={Vec2.Distance(w.Start, w.End):F2}m");
            }
            if (input.Sources.Count > 0)
            {
                var s0 = input.Sources[0];
                FileLogger.Log($"  Source[0] pos=({s0.Position.X:F3},{s0.Position.Y:F3},{s0.Position.Z:F3})");
            }
            if (input.Receivers.Count > 0)
            {
                var r0 = input.Receivers[0];
                var rL = input.Receivers[input.Receivers.Count - 1];
                FileLogger.Log($"  Recv[0] pos=({r0.Position.X:F3},{r0.Position.Y:F3},{r0.Position.Z:F3})");
                FileLogger.Log($"  Recv[last] pos=({rL.Position.X:F3},{rL.Position.Y:F3},{rL.Position.Z:F3})");
            }

            // Speed of sound from temperature
            double speedOfSound = 331.3 + 0.606 * input.Environment.TemperatureC;

            // Compute temperature- and humidity-dependent air absorption (ISO 9613-1)
            _airAbsorption = OctaveBands.ComputeAirAbsorption(
                input.Environment.TemperatureC,
                input.Environment.RelativeHumidityPct);

            // Resolve global wall absorption from preset
            _globalAbsorption = OctaveBands.AbsorptionPresets.ContainsKey(WallAbsorptionPreset.Drywall)
                ? OctaveBands.AbsorptionPresets[WallAbsorptionPreset.Drywall]
                : new double[] { 0.10, 0.10, 0.10, 0.10, 0.10, 0.10, 0.10 };

            // Build directivity providers for each source
            var providers = new ISpeakerDirectivityProvider[input.Sources.Count];
            for (int i = 0; i < input.Sources.Count; i++)
                providers[i] = DirectivityProviderFactory.Create(input.Sources[i].Profile);

            // --- Reverberant field pre-computation (Sabine room acoustics) ---
            // Estimate room volume from room polygons × ceiling height.
            // Ceiling height is derived from the tallest speaker in each room
            // (speakers are ceiling-mounted), falling back to default if unknown.
            // Per-band room constant R_k = 0.161·V / T60_k determines how much
            // source energy builds up as a diffuse reverberant field.
            double roomVolumeM3 = 0;
            if (input.Rooms != null)
            {
                foreach (var room in input.Rooms)
                {
                    double ceilingH = room.CeilingHeightM > 0.5
                        ? room.CeilingHeightM
                        : DefaultCeilingHeightM;
                    roomVolumeM3 += room.Area * ceilingH;
                }
            }
            bool hasReverb = roomVolumeM3 > 1.0;

            double[] roomConstant = null;
            double[][] reverbBySource = null;

            if (hasReverb)
            {
                double[] rt60 = input.Environment.RT60ByBand;
                int nb = OctaveBands.Count;
                roomConstant = new double[nb];
                for (int k = 0; k < nb; k++)
                {
                    double t60 = Math.Max(rt60[k], 0.05);
                    roomConstant[k] = Math.Max(0.161 * roomVolumeM3 / t60, 1.0);
                }

                // Reverberant p² at any point = source p²(1m) × 16π / (Q × R_k)
                reverbBySource = new double[input.Sources.Count][];
                for (int s = 0; s < input.Sources.Count; s++)
                {
                    reverbBySource[s] = new double[nb];
                    double srcBb = Math.Pow(10.0, providers[s].OnAxisSplAtOneMeter / 10.0);
                    double perBand = srcBb / nb;
                    double Q = Math.Max(providers[s].DirectivityFactor, 1.0);
                    for (int k = 0; k < nb; k++)
                        reverbBySource[s][k] = perBand * 16.0 * Math.PI / (Q * roomConstant[k]);
                }
            }

            // --- First-order image sources (reflections off wall surfaces) ---
            // Mirror each real source across each wall to create virtual sources.
            // Per-receiver we check if the reflected path is geometrically valid.
            var imageSources = new List<ImageSource>();

            // Resolve per-band absorption for each wall
            double[][] wallAbsorption = new double[walls.Count][];
            for (int w = 0; w < walls.Count; w++)
            {
                if (walls[w].AbsorptionByBand != null && walls[w].AbsorptionByBand.Length == OctaveBands.Count)
                    wallAbsorption[w] = walls[w].AbsorptionByBand;
                else
                    wallAbsorption[w] = _globalAbsorption;
            }

            for (int s = 0; s < input.Sources.Count; s++)
            {
                Vec2 srcXY = new Vec2(input.Sources[s].Position.X, input.Sources[s].Position.Y);
                for (int w = 0; w < walls.Count; w++)
                {
                    Vec2 mirrored = ReflectPointAcrossSegment(srcXY, walls[w].Start, walls[w].End);
                    if (double.IsNaN(mirrored.X)) continue; // degenerate wall

                    double[] reflCoeffs = new double[OctaveBands.Count];
                    for (int k = 0; k < OctaveBands.Count; k++)
                        reflCoeffs[k] = 1.0 - wallAbsorption[w][k];

                    imageSources.Add(new ImageSource
                    {
                        ImagePos = mirrored,
                        SourceIndex = s,
                        WallIndex = w,
                        ReflectionCoeffByBand = reflCoeffs
                    });
                }
            }

            // --- Second-order image sources (mirror first-order images across other walls) ---
            // Skipped in Draft mode for performance.
            bool isDraft = input.Quality == CalculationQuality.Draft;
            if (!isDraft)
            {
                int firstOrderCount = imageSources.Count;
                for (int i = 0; i < firstOrderCount; i++)
                {
                    ImageSource img1 = imageSources[i];
                    for (int w = 0; w < walls.Count; w++)
                    {
                        if (w == img1.WallIndex) continue; // don't re-reflect off same wall

                        Vec2 mirrored2 = ReflectPointAcrossSegment(img1.ImagePos, walls[w].Start, walls[w].End);
                        if (double.IsNaN(mirrored2.X)) continue;

                        // Second bounce absorption = first bounce × second wall absorption
                        double[] reflCoeffs2 = new double[OctaveBands.Count];
                        for (int k = 0; k < OctaveBands.Count; k++)
                            reflCoeffs2[k] = img1.ReflectionCoeffByBand[k] * (1.0 - wallAbsorption[w][k]);

                        imageSources.Add(new ImageSource
                        {
                            ImagePos = mirrored2,
                            SourceIndex = img1.SourceIndex,
                            WallIndex = w,
                            ReflectionCoeffByBand = reflCoeffs2
                        });
                    }
                }
            }

            var imageSourceArray = imageSources.ToArray();

            // --- Ceiling and floor image sources ---
            // Mirror each source across horizontal planes (floor and ceiling).
            // Skipped in Draft mode for performance.
            var horizImages = new List<HorizontalImageSource>();
            double floorZ = 0;
            double ceilingZ = DefaultCeilingHeightM;
            if (!isDraft)
            {
                if (input.Rooms != null && input.Rooms.Count > 0)
                {
                    floorZ = input.Rooms[0].FloorElevationM;
                    double maxCeil = 0;
                    foreach (var room in input.Rooms)
                    {
                        double ch = room.CeilingHeightM > 0.5
                            ? room.CeilingHeightM
                            : DefaultCeilingHeightM;
                        if (ch > maxCeil) maxCeil = ch;
                        if (room.FloorElevationM < floorZ) floorZ = room.FloorElevationM;
                    }
                    ceilingZ = floorZ + maxCeil;
                }

                // Use global absorption for floor/ceiling surfaces
                double[] horizReflCoeffs = new double[OctaveBands.Count];
                for (int k = 0; k < OctaveBands.Count; k++)
                    horizReflCoeffs[k] = 1.0 - _globalAbsorption[k];

                for (int s = 0; s < input.Sources.Count; s++)
                {
                    Vec3 srcPos = input.Sources[s].Position;

                    // Ceiling reflection: mirror Z across ceiling plane
                    double ceilImageZ = 2.0 * ceilingZ - srcPos.Z;
                    horizImages.Add(new HorizontalImageSource
                    {
                        ImagePos3D = new Vec3(srcPos.X, srcPos.Y, ceilImageZ),
                        SourceIndex = s,
                        ReflectionCoeffByBand = horizReflCoeffs
                    });

                    // Floor reflection: mirror Z across floor plane
                    double floorImageZ = 2.0 * floorZ - srcPos.Z;
                    horizImages.Add(new HorizontalImageSource
                    {
                        ImagePos3D = new Vec3(srcPos.X, srcPos.Y, floorImageZ),
                        SourceIndex = s,
                        ReflectionCoeffByBand = horizReflCoeffs
                    });
                }
            }
            var horizImageArray = horizImages.ToArray();

            var resultsBag = new ConcurrentBag<ReceiverResult>();
            var bandDataBag = new ConcurrentBag<ReceiverBandData>();
            int completed = 0;
            int wallHitReceivers = 0;  // diagnostic: how many receivers had ≥1 wall crossing
            int loggedSamples = 0;     // diagnostic: limit per-receiver logs

            // Pre-provision ThreadPool threads to avoid slow ramp-up on .NET Framework
            int cpuCount = Environment.ProcessorCount;
            ThreadPool.GetMinThreads(out int prevWorker, out int prevIO);
            if (prevWorker < cpuCount)
                ThreadPool.SetMinThreads(cpuCount, prevIO);

            FileLogger.Log($"[SPLCalc] Quality={input.Quality}, ImageSources={imageSourceArray.Length} " +
                $"(1st-order={imageSources.Count - (isDraft ? 0 : imageSourceArray.Length - imageSources.Count)}), " +
                $"HorizImages={horizImageArray.Length}, CPUs={cpuCount}");

            Parallel.ForEach(
                Partitioner.Create(0, input.Receivers.Count, Math.Max(1, input.Receivers.Count / (cpuCount * 4))),
                new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = -1
            },
            (range) =>
            {
                for (int ri = range.Item1; ri < range.Item2; ri++)
                {
                cancellationToken.ThrowIfCancellationRequested();
                ReceiverPoint receiver = input.Receivers[ri];

                int numBands = OctaveBands.Count;
                int numSources = input.Sources.Count;

                // Per-band linear power totals (all sources combined)
                double[] totalByBand = new double[numBands];

                // Per-source arrival data for early/late classification
                double[] arrivalTimes = new double[numSources];
                double[] wallStcSums = new double[numSources];
                // Per-source, per-band linear power contribution
                double[][] srcBandPower = new double[numSources][];
                for (int s = 0; s < numSources; s++)
                    srcBandPower[s] = new double[numBands];

                Vec2 recvXY = new Vec2(receiver.Position.X, receiver.Position.Y);

                for (int s = 0; s < numSources; s++)
                {
                    ComputeSource source = input.Sources[s];
                    ISpeakerDirectivityProvider provider = providers[s];

                    Vec3 delta = receiver.Position - source.Position;
                    double distance = delta.Length;
                    if (distance < MinDistanceM)
                        distance = MinDistanceM;

                    // Arrival time for this source
                    arrivalTimes[s] = distance / speedOfSound;

                    Vec3 toReceiver = delta / distance;

                    Vec3 facingNorm = source.FacingDirection.Normalized();

                    // Base distance attenuation (directivity applied per-band below)
                    double distRatio = RefDistanceM / distance;
                    double distPower = distRatio * distRatio;

                    // Wall transmission loss (broadband STC sum)
                    double wallStcSum = SumWallStc(
                        new Vec2(source.Position.X, source.Position.Y), recvXY, walls);
                    wallStcSums[s] = wallStcSum;

                    // Log a handful of sample source→receiver wall hits for diagnostics
                    if (wallStcSum > 0 && Interlocked.Increment(ref wallHitReceivers) <= 5)
                    {
                        FileLogger.Log($"  [WallHit] recv={receiver.Index} src={s} " +
                            $"stcSum={wallStcSum} srcXY=({source.Position.X:F3},{source.Position.Y:F3}) " +
                            $"recvXY=({recvXY.X:F3},{recvXY.Y:F3})");
                    }
                    else if (wallStcSum == 0 && Interlocked.CompareExchange(ref loggedSamples, 1, 0) == 0)
                    {
                        FileLogger.Log($"  [NoWall] recv={receiver.Index} src={s} " +
                            $"stcSum=0 srcXY=({source.Position.X:F3},{source.Position.Y:F3}) " +
                            $"recvXY=({recvXY.X:F3},{recvXY.Y:F3})");
                    }

                    // Source broadband SPL → per-band source power
                    double broadbandLinear = Math.Pow(10.0, provider.OnAxisSplAtOneMeter / 10.0);
                    double[] spectrumShape = source.Profile?.SpectrumShapeByBand;
                    bool hasSpectrum = spectrumShape != null && spectrumShape.Length == numBands;

                    for (int k = 0; k < numBands; k++)
                    {
                        // Per-band source power: flat split + optional spectrum shape offset
                        double perBandSourceLinear = broadbandLinear / numBands;
                        if (hasSpectrum)
                            perBandSourceLinear *= Math.Pow(10.0, spectrumShape[k] / 10.0);

                        // Per-band directivity (higher freqs beam more narrowly)
                        double bandGain = provider.GetDirectivityGainForBand(facingNorm, toReceiver, k);
                        double basePower = distPower * bandGain * bandGain;

                        // Per-band wall TL: nominal STC contour value minus field penalty.
                        // StcBandOffsets[k] maps the rated STC to each frequency per ASTM E413.
                        // Guard against wallStcSum == 0: the field penalty must not produce
                        // spurious attenuation on high-frequency bands in free-field conditions.
                        double bandTlDb = wallStcSum > 0
                            ? Math.Max(0, wallStcSum + OctaveBands.StcBandOffsets[k] - FieldPenaltyDb)
                            : 0;

                        // Air absorption for this band (temperature-dependent)
                        double airLossDb = _airAbsorption[k] * distance;

                        // Total per-band loss in linear
                        double totalLossDb = Math.Min(bandTlDb + airLossDb, MaxTotalLossDb);
                        double lossFactor = Math.Pow(10.0, -totalLossDb / 10.0);

                        double bandPower = perBandSourceLinear * basePower * lossFactor;
                        srcBandPower[s][k] = bandPower;
                        totalByBand[k] += bandPower;
                    }
                }

                // --- First-order reflections ---
                // For each image source, check if the reflection point lies on
                // the wall segment and accumulate the reflected contribution.
                int numImages = imageSourceArray.Length;
                double[] reflArrivalTimes = new double[numImages];
                double[][] reflBandPower = new double[numImages][];
                bool[] reflValid = new bool[numImages];

                for (int r = 0; r < numImages; r++)
                {
                    reflBandPower[r] = new double[numBands];
                    ImageSource img = imageSourceArray[r];
                    ComputeWall reflWall = walls[img.WallIndex];

                    // The reflection point is where image→receiver crosses the wall
                    Vec2 imgToRecv = recvXY - img.ImagePos;
                    double len = imgToRecv.Length;
                    if (len < MinDistanceM) { reflValid[r] = false; continue; }

                    double tWall = SegmentIntersectT(img.ImagePos, imgToRecv, reflWall.Start, reflWall.End);
                    if (tWall <= 0.0 || tWall >= 1.0) { reflValid[r] = false; continue; }

                    // Valid reflection — total path length = |image → receiver|
                    double reflDistance = len;
                    if (reflDistance < MinDistanceM) reflDistance = MinDistanceM;

                    reflArrivalTimes[r] = reflDistance / speedOfSound;
                    reflValid[r] = true;

                    // Source directivity toward the reflection point
                    Vec2 srcXY = new Vec2(input.Sources[img.SourceIndex].Position.X,
                                          input.Sources[img.SourceIndex].Position.Y);
                    Vec2 reflPt = img.ImagePos + imgToRecv * tWall;
                    Vec3 srcPos = input.Sources[img.SourceIndex].Position;
                    Vec3 toReflPt = new Vec3(reflPt.X - srcPos.X, reflPt.Y - srcPos.Y, 0);
                    double toReflLen = toReflPt.Length;
                    Vec3 toReflDir = toReflLen > 1e-9 ? toReflPt / toReflLen : Vec3.Zero;
                    Vec3 srcFacing = input.Sources[img.SourceIndex].FacingDirection.Normalized();
                    ISpeakerDirectivityProvider provider = providers[img.SourceIndex];

                    // Distance attenuation (per-band directivity applied in loop below)
                    double reflDistRatio = RefDistanceM / reflDistance;
                    double reflDistPower = reflDistRatio * reflDistRatio;

                    // Check both legs of the reflected path for wall crossings
                    double incomingStc = SumWallStcExcluding(
                        srcXY, reflPt, walls, img.WallIndex);
                    double outgoingStc = SumWallStcExcluding(
                        reflPt, recvXY, walls, img.WallIndex);
                    double otherStc = incomingStc + outgoingStc;

                    double broadbandLinear = Math.Pow(10.0, provider.OnAxisSplAtOneMeter / 10.0);

                    for (int k = 0; k < numBands; k++)
                    {
                        double perBandSourceLinear = broadbandLinear / numBands;

                        // Per-band directivity at source→reflectionPoint angle
                        double reflBandGain = provider.GetDirectivityGainForBand(srcFacing, toReflDir, k);
                        double basePower = reflDistPower * reflBandGain * reflBandGain;

                        double bandTlDb = otherStc > 0
                            ? Math.Max(0, otherStc + OctaveBands.StcBandOffsets[k] - FieldPenaltyDb)
                            : 0;
                        double airLossDb = _airAbsorption[k] * reflDistance;
                        double totalLossDb = Math.Min(bandTlDb + airLossDb, MaxTotalLossDb);
                        double lossFactor = Math.Pow(10.0, -totalLossDb / 10.0);

                        double pw = perBandSourceLinear * basePower * lossFactor * img.ReflectionCoeffByBand[k];
                        reflBandPower[r][k] = pw;
                        totalByBand[k] += pw;
                    }
                }

                // --- Ceiling/floor reflections ---
                int numHorizImages = horizImageArray.Length;
                double[] horizArrivalTimes = new double[numHorizImages];
                double[][] horizBandPower = new double[numHorizImages][];

                for (int h = 0; h < numHorizImages; h++)
                {
                    horizBandPower[h] = new double[numBands];
                    HorizontalImageSource himg = horizImageArray[h];

                    Vec3 delta3D = receiver.Position - himg.ImagePos3D;
                    double hDist = delta3D.Length;
                    if (hDist < MinDistanceM) hDist = MinDistanceM;

                    horizArrivalTimes[h] = hDist / speedOfSound;

                    double hDistRatio = RefDistanceM / hDist;
                    double hDistPower = hDistRatio * hDistRatio;

                    // Directivity: source direction toward the reflection point on the surface
                    Vec3 srcPos = input.Sources[himg.SourceIndex].Position;
                    Vec3 srcFacing = input.Sources[himg.SourceIndex].FacingDirection.Normalized();
                    // The reflection point is at (srcPos.X, srcPos.Y, reflSurfaceZ)
                    // For ceiling: surfaceZ = ceilingZ, for floor: surfaceZ = floorZ
                    // The image is at (srcX, srcY, 2*surfaceZ - srcZ)
                    // surfaceZ = (imageZ + srcZ) / 2
                    double surfaceZ = (himg.ImagePos3D.Z + srcPos.Z) * 0.5;
                    Vec3 toSurface = new Vec3(0, 0, surfaceZ - srcPos.Z);
                    // For horizontal surface, the XY component toward receiver matters too
                    Vec3 reflPt3D = new Vec3(
                        (srcPos.X + receiver.Position.X) * 0.5,
                        (srcPos.Y + receiver.Position.Y) * 0.5,
                        surfaceZ); // approximate
                    Vec3 toRefl = (reflPt3D - srcPos);
                    double trLen = toRefl.Length;
                    Vec3 toReflDir = trLen > 1e-9 ? toRefl / trLen : Vec3.Zero;
                    ISpeakerDirectivityProvider hProvider = providers[himg.SourceIndex];

                    // Check wall blocking on XY projection of reflected path
                    double hWallStc = SumWallStc(
                        new Vec2(srcPos.X, srcPos.Y), recvXY, walls);

                    double hBroadband = Math.Pow(10.0, hProvider.OnAxisSplAtOneMeter / 10.0);

                    for (int k = 0; k < numBands; k++)
                    {
                        double perBandSrc = hBroadband / numBands;
                        double hBandGain = hProvider.GetDirectivityGainForBand(srcFacing, toReflDir, k);
                        double hBasePower = hDistPower * hBandGain * hBandGain;

                        double bandTlDb = hWallStc > 0
                            ? Math.Max(0, hWallStc + OctaveBands.StcBandOffsets[k] - FieldPenaltyDb)
                            : 0;
                        double airLossDb = _airAbsorption[k] * hDist;
                        double totalLossDb = Math.Min(bandTlDb + airLossDb, MaxTotalLossDb);
                        double lossFactor = Math.Pow(10.0, -totalLossDb / 10.0);

                        double pw = perBandSrc * hBasePower * lossFactor * himg.ReflectionCoeffByBand[k];
                        horizBandPower[h][k] = pw;
                        totalByBand[k] += pw;
                    }
                }

                // --- Early/late classification ---
                // Find earliest arrival across direct + reflected + ceiling/floor paths
                double earliestArrival = double.MaxValue;
                for (int s = 0; s < numSources; s++)
                {
                    if (arrivalTimes[s] < earliestArrival)
                        earliestArrival = arrivalTimes[s];
                }
                for (int r = 0; r < numImages; r++)
                {
                    if (reflValid[r] && reflArrivalTimes[r] < earliestArrival)
                        earliestArrival = reflArrivalTimes[r];
                }
                for (int h = 0; h < numHorizImages; h++)
                {
                    if (horizArrivalTimes[h] < earliestArrival)
                        earliestArrival = horizArrivalTimes[h];
                }

                var bandData = new ReceiverBandData
                {
                    ReceiverIndex = receiver.Index
                };

                // Direct sources
                for (int s = 0; s < numSources; s++)
                {
                    bool isEarly = (arrivalTimes[s] - earliestArrival) <= EarlyLateThresholdS;
                    double[] target = isEarly
                        ? bandData.EarlyLinearByBand
                        : bandData.LateLinearByBand;

                    for (int k = 0; k < numBands; k++)
                        target[k] += srcBandPower[s][k];
                }

                // Reflected sources
                for (int r = 0; r < numImages; r++)
                {
                    if (!reflValid[r]) continue;
                    bool isEarly = (reflArrivalTimes[r] - earliestArrival) <= EarlyLateThresholdS;
                    double[] target = isEarly
                        ? bandData.EarlyLinearByBand
                        : bandData.LateLinearByBand;

                    for (int k = 0; k < numBands; k++)
                        target[k] += reflBandPower[r][k];
                }

                // Ceiling/floor reflected sources
                for (int h = 0; h < numHorizImages; h++)
                {
                    bool isEarly = (horizArrivalTimes[h] - earliestArrival) <= EarlyLateThresholdS;
                    double[] target = isEarly
                        ? bandData.EarlyLinearByBand
                        : bandData.LateLinearByBand;

                    for (int k = 0; k < numBands; k++)
                        target[k] += horizBandPower[h][k];
                }

                // --- Reverberant field → late energy ---
                // The diffuse reverberant field from each source adds late
                // (noise) energy that degrades STI. Reverberant energy is
                // distance-independent (uniform in the room). Only applied
                // for sources sharing the same room (no walls in between).
                if (hasReverb)
                {
                    for (int s = 0; s < numSources; s++)
                    {
                        if (wallStcSums[s] > 0) continue;
                        for (int k = 0; k < numBands; k++)
                            bandData.LateLinearByBand[k] += reverbBySource[s][k];
                    }
                }

                // --- Build per-band SPL (total) and broadband ---
                double[] splDbByBand = new double[numBands];
                double totalLinearPower = 0;

                for (int k = 0; k < numBands; k++)
                {
                    splDbByBand[k] = totalByBand[k] > 0
                        ? Math.Round(10.0 * Math.Log10(totalByBand[k]), 2)
                        : -100.0;
                    totalLinearPower += totalByBand[k];
                }

                double splDb = totalLinearPower > 0
                    ? 10.0 * Math.Log10(totalLinearPower)
                    : -100.0;

                // Compute A-weighted SPL (IEC 61672-1): sum bands with A-weighting offsets
                double aWeightedLinear = 0.0;
                for (int k = 0; k < numBands; k++)
                {
                    if (totalByBand[k] > 0)
                        aWeightedLinear += Math.Pow(10.0, OctaveBands.AWeightingDb[k] / 10.0) * totalByBand[k];
                }
                double splDbA = aWeightedLinear > 0
                    ? Math.Round(10.0 * Math.Log10(aWeightedLinear), 2)
                    : -100.0;

                resultsBag.Add(new ReceiverResult
                {
                    ReceiverIndex = receiver.Index,
                    Position = receiver.Position,
                    SplDb = Math.Round(splDb, 2),
                    SplDbA = splDbA,
                    SplDbByBand = splDbByBand
                });

                bandDataBag.Add(bandData);

                // --- Detailed per-receiver energy breakdown for diagnostics ---
                // Log 3 sample receivers to show blocked vs unblocked source contributions.
                int recvIdx = receiver.Index;
                if (recvIdx == 0 || recvIdx == totalReceivers / 2 || recvIdx == totalReceivers - 1)
                {
                    double blockedEnergy = 0, unblockedEnergy = 0;
                    int blockedCount = 0, unblockedCount = 0;
                    string topUnblocked = "";
                    double topUnblockedPwr = 0;

                    for (int s = 0; s < numSources; s++)
                    {
                        double srcTotal = 0;
                        for (int k = 0; k < numBands; k++)
                            srcTotal += srcBandPower[s][k];

                        if (wallStcSums[s] > 0)
                        {
                            blockedEnergy += srcTotal;
                            blockedCount++;
                        }
                        else
                        {
                            unblockedEnergy += srcTotal;
                            unblockedCount++;
                            if (srcTotal > topUnblockedPwr)
                            {
                                topUnblockedPwr = srcTotal;
                                double d = (receiver.Position - input.Sources[s].Position).Length;
                                topUnblocked = $"src={s} dist={d:F1}m pwr={10 * Math.Log10(Math.Max(srcTotal, 1e-30)):F1}dB";
                            }
                        }
                    }

                    double blockedDb = blockedEnergy > 0 ? 10 * Math.Log10(blockedEnergy) : -999;
                    double unblockedDb = unblockedEnergy > 0 ? 10 * Math.Log10(unblockedEnergy) : -999;

                    FileLogger.Log($"  [RecvDetail] recv={recvIdx} totalSPL={splDb:F1}dB " +
                        $"blocked={blockedCount}src/{blockedDb:F1}dB " +
                        $"unblocked={unblockedCount}src/{unblockedDb:F1}dB " +
                        $"topUnblocked=[{topUnblocked}]");
                }

                int done = Interlocked.Increment(ref completed);
                if (done % Math.Max(1, totalReceivers / 100) == 0)
                    progress?.Report((double)done / totalReceivers);
                } // end for ri in range
            });

            // Sort by index for deterministic output
            var sortedResults = resultsBag.ToList();
            sortedResults.Sort((a, b) => a.ReceiverIndex.CompareTo(b.ReceiverIndex));

            var sortedBandData = bandDataBag.ToList();
            sortedBandData.Sort((a, b) => a.ReceiverIndex.CompareTo(b.ReceiverIndex));

            FileLogger.Log($"[SPLCalc] Done. wallHitReceivers={wallHitReceivers}/{totalReceivers}");

            progress?.Report(1.0);
            return (sortedResults, sortedBandData);
        }

        // --- Wall transmission loss helpers ---

        /// <summary>
        /// Field correction penalty in dB, subtracted from per-band nominal TL.
        /// Accounts for flanking paths, leaks and installation imperfections
        /// that degrade lab STC to in-situ performance (typically 3–8 dB).
        /// </summary>
        private const double FieldPenaltyDb = 5.0;
        private const double MaxTotalLossDb = 60.0;
        private const double TMin = 0.02;
        private const double TMax = 0.98;

        /// <summary>
        /// Sums the raw STC ratings of all walls intersected by the source→receiver ray.
        /// Uses both centerline crossing and perpendicular proximity (half-thickness)
        /// to handle gaps at corners and T-junctions between detail line segments.
        /// </summary>
        private static double SumWallStc(Vec2 p, Vec2 q, List<ComputeWall> walls)
        {
            if (walls.Count == 0) return 0;

            double total = 0;
            Vec2 d = q - p;
            double rayLen = d.Length;
            if (rayLen < 1e-9) return 0;

            for (int i = 0; i < walls.Count; i++)
            {
                ComputeWall w = walls[i];
                if (w.StcRating <= 0) continue;

                if (RayBlockedByWall(p, d, rayLen, w))
                    total += w.StcRating;
            }

            return total;
        }

        /// <summary>
        /// Like <see cref="SumWallStc"/> but skips one wall by index
        /// (used for reflected paths that originate from a wall surface).
        /// </summary>
        private static double SumWallStcExcluding(Vec2 p, Vec2 q, List<ComputeWall> walls, int excludeIndex)
        {
            if (walls.Count == 0) return 0;

            double total = 0;
            Vec2 d = q - p;
            double rayLen = d.Length;
            if (rayLen < 1e-9) return 0;

            for (int i = 0; i < walls.Count; i++)
            {
                if (i == excludeIndex) continue;
                ComputeWall w = walls[i];
                if (w.StcRating <= 0) continue;

                if (RayBlockedByWall(p, d, rayLen, w))
                    total += w.StcRating;
            }

            return total;
        }

        /// <summary>
        /// Tests if a ray from p along direction d is blocked by a wall,
        /// using both centerline intersection and perpendicular proximity.
        /// </summary>
        private static bool RayBlockedByWall(Vec2 p, Vec2 d, double rayLen, ComputeWall w)
        {
            // Fast path: exact centerline crossing
            double t = SegmentIntersectT(p, d, w.Start, w.End);
            if (t > TMin && t < TMax)
                return true;

            // Proximity path: find closest point between the ray segment (p→p+d)
            // and the wall segment (w.Start→w.End). If distance < wall half-thickness
            // and the closest point on the ray is in (TMin..TMax), count as blocked.
            double halfThick = w.HalfThicknessM;
            if (halfThick <= 0) return false;

            // Closest approach between two finite line segments
            Vec2 wallDir = w.End - w.Start;
            double wallLen = wallDir.Length;
            if (wallLen < 1e-9) return false;

            // Project wall midpoint onto ray to check if it's in the relevant zone
            Vec2 wallMid = (w.Start + w.End) * 0.5;
            Vec2 toMid = wallMid - p;
            double tMid = Vec2.Dot(toMid, d) / (rayLen * rayLen);
            if (tMid < 0.0 || tMid > 1.0) return false;

            // Find perpendicular distance from the ray line to the wall segment
            Vec2 rayUnit = d * (1.0 / rayLen);
            Vec2 rayNormal = new Vec2(-rayUnit.Y, rayUnit.X);

            // Signed distances of wall endpoints from the ray line
            double dStartSigned = Vec2.Dot(w.Start - p, rayNormal);
            double dEndSigned = Vec2.Dot(w.End - p, rayNormal);

            // Minimum absolute distance from ray line to wall segment
            double minDist;
            if (dStartSigned * dEndSigned <= 0)
                minDist = 0; // wall straddles the ray line — centerline should have caught it
            else
                minDist = Math.Min(Math.Abs(dStartSigned), Math.Abs(dEndSigned));

            if (minDist > halfThick) return false;

            // Check that the wall's closest point on the ray is within bounds
            // Project both wall endpoints onto the ray and check the range
            double tStart = Vec2.Dot(w.Start - p, d) / (rayLen * rayLen);
            double tEnd = Vec2.Dot(w.End - p, d) / (rayLen * rayLen);
            double tWallMin = Math.Min(tStart, tEnd);
            double tWallMax = Math.Max(tStart, tEnd);

            // The wall must overlap with the (TMin..TMax) portion of the ray
            return tWallMax > TMin && tWallMin < TMax;
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

            // Project point onto line A→B
            double t = Vec2.Dot(point - a, ab) / lenSq;
            Vec2 proj = a + ab * t;

            // Mirror: P' = 2·proj − P
            return proj * 2.0 - point;
        }

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
