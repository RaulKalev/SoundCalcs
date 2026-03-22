using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using SoundCalcs.Domain;

namespace SoundCalcs.Compute
{
    /// <summary>
    /// Computes direct-sound SPL at each receiver point from all sources.
    /// Pure C# math -- no Revit references. Thread-safe and parallelized.
    /// </summary>
    public class SPLCalculator
    {
        /// <summary>
        /// Reference distance for SPL specifications (1 meter).
        /// </summary>
        private const double RefDistanceM = 1.0;

        /// <summary>
        /// Minimum distance to avoid singularity at source position.
        /// </summary>
        private const double MinDistanceM = 0.01;

        /// <summary>
        /// Compute SPL at all receiver points from all sources.
        /// Uses Parallel.ForEach for multi-threaded execution.
        /// </summary>
        /// <param name="input">Job input with sources and receivers.</param>
        /// <param name="cancellationToken">Token for cancellation.</param>
        /// <param name="progress">Reports progress as fraction 0.0 to 1.0.</param>
        /// <returns>List of per-receiver results.</returns>
        public List<ReceiverResult> Calculate(
            AcousticJobInput input,
            CancellationToken cancellationToken,
            IProgress<double> progress)
        {
            int totalReceivers = input.Receivers.Count;
            if (totalReceivers == 0)
                return new List<ReceiverResult>();

            // Snapshot wall list for thread-safe access
            var walls = input.Walls ?? new List<ComputeWall>();

            // Build directivity providers for each source
            var providers = new ISpeakerDirectivityProvider[input.Sources.Count];
            for (int i = 0; i < input.Sources.Count; i++)
            {
                providers[i] = DirectivityProviderFactory.Create(input.Sources[i].Profile);
            }

            var results = new ConcurrentBag<ReceiverResult>();
            int completed = 0;

            Parallel.ForEach(input.Receivers, new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = Environment.ProcessorCount
            },
            (receiver) =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                double totalLinearPower = 0.0;

                // Project receiver position onto XY for wall intersection tests
                Vec2 recvXY = new Vec2(receiver.Position.X, receiver.Position.Y);

                for (int s = 0; s < input.Sources.Count; s++)
                {
                    ComputeSource source = input.Sources[s];
                    ISpeakerDirectivityProvider provider = providers[s];

                    Vec3 delta = receiver.Position - source.Position;
                    double distance = delta.Length;

                    if (distance < MinDistanceM)
                        distance = MinDistanceM;

                    Vec3 toReceiver = delta / distance; // normalized direction

                    // Directivity gain (linear, 0..1)
                    double directivityGain = provider.GetDirectivityGain(
                        source.FacingDirection.Normalized(), toReceiver);

                    // SPL at receiver from this source:
                    // SPL_receiver = SPL_1m - 20*log10(d/d_ref) + 20*log10(directivityGain)
                    // In linear pressure: p = p_ref * (d_ref / d) * gain
                    // Power is proportional to p^2
                    double pressureRatio = (RefDistanceM / distance) * directivityGain;
                    double powerContribution = pressureRatio * pressureRatio;

                    // Wall transmission loss: sum STC of all walls crossed by source→receiver ray
                    double wallLossDb = SumWallTransmissionLoss(
                        new Vec2(source.Position.X, source.Position.Y), recvXY, walls);
                    if (wallLossDb > 0)
                    {
                        // Convert dB loss to linear power factor: 10^(-loss/10)
                        powerContribution *= Math.Pow(10.0, -wallLossDb / 10.0);
                    }

                    // Convert source SPL to linear power, scale, and accumulate
                    // p_ref^2 = 10^(SPL_1m / 10) [in arbitrary linear units]
                    double sourceLinearPower = Math.Pow(10.0, provider.OnAxisSplAtOneMeter / 10.0);
                    totalLinearPower += sourceLinearPower * powerContribution;
                }

                // Convert back to dB
                double splDb = totalLinearPower > 0
                    ? 10.0 * Math.Log10(totalLinearPower)
                    : 0.0;

                results.Add(new ReceiverResult
                {
                    ReceiverIndex = receiver.Index,
                    Position = receiver.Position,
                    SplDb = Math.Round(splDb, 2)
                });

                int done = Interlocked.Increment(ref completed);
                if (done % Math.Max(1, totalReceivers / 100) == 0)
                {
                    progress?.Report((double)done / totalReceivers);
                }
            });

            // Sort by index for deterministic output
            var sortedResults = new List<ReceiverResult>(results);
            sortedResults.Sort((a, b) => a.ReceiverIndex.CompareTo(b.ReceiverIndex));

            progress?.Report(1.0);
            return sortedResults;
        }

        /// <summary>
        /// Computes the effective transmission loss for the ray from sourceXY
        /// to receiverXY through all intervening walls.
        /// 
        /// Applies a field-correction factor (real-world performance is lower
        /// than lab STC) and caps the total loss to avoid complete silence.
        /// 
        /// Endpoint hits (t near 0 or 1) are excluded so that the speaker's
        /// own boundary wall and the receiver's nearest wall don't count.
        /// </summary>
        private static double SumWallTransmissionLoss(Vec2 p, Vec2 q, List<ComputeWall> walls)
        {
            if (walls.Count == 0) return 0;

            // Field transmission loss is typically 5–10 dB below lab STC.
            // Using 60% of STC as a practical broadband estimate.
            const double FieldFactor = 0.6;

            // Maximum total transmission loss cap (dB). Even with multiple heavy
            // walls the flanking/HVAC/structural paths limit real isolation.
            const double MaxTotalLossDb = 60.0;

            // Exclude intersections very close to the ray endpoints to avoid
            // self-intersection with the boundary wall nearest to source/receiver.
            const double TMin = 0.02;
            const double TMax = 0.98;

            double total = 0;
            Vec2 d = q - p;

            for (int i = 0; i < walls.Count; i++)
            {
                ComputeWall w = walls[i];
                if (w.StcRating <= 0) continue;

                double t = SegmentIntersectT(p, d, w.Start, w.End);
                if (t > TMin && t < TMax)
                    total += w.StcRating * FieldFactor;
            }

            return Math.Min(total, MaxTotalLossDb);
        }

        /// <summary>
        /// Returns the parameter t (0..1) at which the segment (p, p+d)
        /// intersects segment (a, b), or -1 if no intersection.
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
