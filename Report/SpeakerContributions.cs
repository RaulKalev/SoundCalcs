using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using SoundCalcs.Compute;
using SoundCalcs.Domain;

namespace SoundCalcs.Report
{
    public class SpeakerContribution
    {
        public int SourceIndex { get; set; }
        public string Label { get; set; } = "";
        public string TypeName { get; set; } = "";
        public double LevelDbA { get; set; }
        public double DistanceM { get; set; }

        /// <summary>Share of the total energy at the point (0..1).</summary>
        public double Share { get; set; }
    }

    /// <summary>Each speaker's level at one reference point of a room.</summary>
    public class RoomContributions
    {
        public int RoomIndex { get; set; }
        public Vec3 Point { get; set; }

        /// <summary>A-weighted level of all speakers together at the point, as in the job's results.</summary>
        public double TotalDbA { get; set; }

        /// <summary>Energy sum of the individual contributions (equals <see cref="TotalDbA"/> when the model is additive).</summary>
        public double SumDbA { get; set; }

        /// <summary>Loudest first.</summary>
        public List<SpeakerContribution> Items { get; set; } = new List<SpeakerContribution>();
    }

    /// <summary>
    /// Per-speaker levels at each room's reference point (the grid point nearest its centre), by rerunning the job's
    /// SPL model with one speaker at a time on just those points. Revit-free.
    /// </summary>
    public static class SpeakerContributionCalculator
    {
        /// <summary>Above this many speakers the table is skipped (one model run per speaker).</summary>
        public const int MaxSources = 80;

        public static Dictionary<int, RoomContributions> Compute(AcousticJobInput input, AcousticJobOutput output,
            IList<RoomStatistics> rooms, CancellationToken token)
        {
            var result = new Dictionary<int, RoomContributions>();
            if (input.Sources.Count == 0 || input.Sources.Count > MaxSources) return result;

            // Reference point per room: the receiver nearest the room's label point (its centroid when inside)
            var refs = new List<(RoomStatistics Room, ReceiverResult Point)>();
            foreach (RoomStatistics room in rooms)
            {
                if (room.ResultIndices.Count == 0) continue;
                Vec2 c;
                if (room.RoomIndex >= 0 && room.RoomIndex < output.Rooms.Count)
                    c = PlanFigureBuilder.LabelPoint(output.Rooms[room.RoomIndex], output, room.RoomIndex);
                else
                    c = new Vec2(room.ResultIndices.Average(i => output.Results[i].Position.X),
                                 room.ResultIndices.Average(i => output.Results[i].Position.Y));
                ReceiverResult best = room.ResultIndices.Select(i => output.Results[i])
                    .OrderBy(r => (r.Position.X - c.X) * (r.Position.X - c.X) + (r.Position.Y - c.Y) * (r.Position.Y - c.Y))
                    .First();
                refs.Add((room, best));
            }
            if (refs.Count == 0) return result;

            var receivers = refs.Select((r, i) => new ReceiverPoint(r.Point.Position, i) { RoomIndex = r.Point.RoomIndex }).ToList();
            var levels = new double[input.Sources.Count, refs.Count];
            for (int s = 0; s < input.Sources.Count; s++)
            {
                token.ThrowIfCancellationRequested();
                var single = new AcousticJobInput
                {
                    JobId = input.JobId + "_contribution",
                    Sources = new List<ComputeSource> { input.Sources[s] },
                    Receivers = receivers,
                    Rooms = input.Rooms,
                    Walls = input.Walls,
                    Surfaces = input.Surfaces,
                    Environment = input.Environment,
                    Quality = input.Quality
                };
                var (res, _) = new SPLCalculator().Calculate(single, token, null);
                foreach (ReceiverResult r in res) levels[s, r.ReceiverIndex] = r.SplDbA;
            }

            for (int k = 0; k < refs.Count; k++)
            {
                var rc = new RoomContributions { RoomIndex = refs[k].Room.RoomIndex, Point = refs[k].Point.Position, TotalDbA = refs[k].Point.SplDbA };
                double sum = 0;
                for (int s = 0; s < input.Sources.Count; s++) sum += Math.Pow(10, levels[s, k] / 10);
                rc.SumDbA = sum > 0 ? 10 * Math.Log10(sum) : double.NaN;
                for (int s = 0; s < input.Sources.Count; s++)
                {
                    Vec3 p = input.Sources[s].Position;
                    rc.Items.Add(new SpeakerContribution
                    {
                        SourceIndex = s,
                        Label = PlanFigureBuilder.SpeakerLabel(s),
                        TypeName = input.Sources[s].TypeName ?? "",
                        LevelDbA = levels[s, k],
                        DistanceM = Vec3.Distance(p, rc.Point),
                        Share = sum > 0 ? Math.Pow(10, levels[s, k] / 10) / sum : 0
                    });
                }
                rc.Items.Sort((a, b) => b.LevelDbA.CompareTo(a.LevelDbA));
                result[rc.RoomIndex] = rc;
            }
            return result;
        }
    }
}
