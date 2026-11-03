using System.Collections.Generic;
using System.Linq;
using SoundCalcs.Domain;
using SoundCalcs.Report;

namespace SoundCalcs.Harness
{
    /// <summary>Checks of the report inputs: room bookkeeping on the results and the per-room statistics.</summary>
    public static class ReportChecks
    {
        /// <summary>Every result carries its receiver's room, and the room statistics account for every receiver once.</summary>
        public static void RoomIndices(AcousticJobInput input, AcousticJobOutput output, CheckContext ctx)
        {
            if (output.Results.Count == 0) return;
            var byIndex = input.Receivers.ToDictionary(r => r.Index, r => r.RoomIndex);
            int wrong = output.Results.Count(r => !byIndex.TryGetValue(r.ReceiverIndex, out int ri) || ri != r.RoomIndex);
            ctx.Assert("report: each result carries its receiver's room", wrong == 0, $"{wrong} result(s) with another room index");

            List<RoomStatistics> rooms = RoomStatisticsCalculator.Compute(input, output);
            int counted = rooms.Sum(r => r.ReceiverCount);
            int distinct = rooms.SelectMany(r => r.ResultIndices).Distinct().Count();
            ctx.Assert("report: room statistics cover every receiver once",
                counted == output.Results.Count && distinct == counted,
                $"{counted} counted, {distinct} distinct, {output.Results.Count} results");
        }
    }
}
