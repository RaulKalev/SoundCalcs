using System.Collections.Generic;
using System.Linq;
using SoundCalcs.Domain;
using SoundCalcs.Report;
using SoundCalcs.UI.ViewModels;
using Xunit;

namespace SoundCalcs.Tests
{
    public class RoomStatisticsTests
    {
        static RoomPolygon Box(string name, double x0, double y0, double x1, double y1, double ceiling = 0) => new RoomPolygon
        {
            Name = name,
            Vertices = new List<Vec2> { new Vec2(x0, y0), new Vec2(x1, y0), new Vec2(x1, y1), new Vec2(x0, y1) },
            CeilingHeightM = ceiling
        };

        static ReceiverResult R(double x, double y, double spl, double sti, int room = -1) => new ReceiverResult
        {
            Position = new Vec3(x, y, 1.2), SplDb = spl, SplDbA = spl - 3, Sti = sti, C80Db = 1,
            SplDbByBand = Enumerable.Repeat(spl - 8.0, 7).ToArray(), RoomIndex = room
        };

        [Fact]
        public void ForValues_GivesMeanMinMaxPercentiles()
        {
            var s = RoomStatisticsCalculator.ForValues(VisualizationMode.SPL, new[] { 70.0, 72, 74, 76, 78 });
            Assert.Equal(5, s.Count);
            Assert.Equal(74, s.Mean, 9);
            Assert.Equal(70, s.Min);
            Assert.Equal(78, s.Max);
            Assert.Equal(8, s.Spread);
            Assert.Equal(74, s.Median, 9);
            Assert.Equal(70.8, s.P10, 9);   // 0.1 * 4 = 0.4 of the way from 70 to 72
            Assert.Equal(77.2, s.P90, 9);
            Assert.Equal(System.Math.Sqrt(8), s.StdDev, 9);
        }

        [Fact]
        public void ForValues_EmptyAndNonFinite()
        {
            var s = RoomStatisticsCalculator.ForValues(VisualizationMode.STI, new[] { double.NaN, double.PositiveInfinity });
            Assert.Equal(0, s.Count);
            Assert.True(double.IsNaN(s.Mean));
        }

        [Fact]
        public void StiCategories_FollowIec()
        {
            Assert.Equal(0, RoomStatisticsCalculator.StiCategory(0.29));
            Assert.Equal(1, RoomStatisticsCalculator.StiCategory(0.30));
            Assert.Equal(2, RoomStatisticsCalculator.StiCategory(0.45));
            Assert.Equal(3, RoomStatisticsCalculator.StiCategory(0.60));
            Assert.Equal(4, RoomStatisticsCalculator.StiCategory(0.75));
        }

        [Fact]
        public void Compute_GroupsByRecordedRoomAndGeometry()
        {
            var rooms = new List<RoomPolygon> { Box("A", 0, 0, 4, 5, 2.5), Box("B", 4, 0, 10, 5) };
            rooms[1].RT60ByBand = new[] { 1.0, 1, 1, 1, 1, 1, 1 };
            var input = new AcousticJobInput
            {
                Rooms = rooms,
                Sources = new List<ComputeSource> { new ComputeSource { Position = new Vec3(1, 1, 2.5) } }
            };
            var output = new AcousticJobOutput
            {
                Rooms = rooms,
                Results = new List<ReceiverResult>
                {
                    R(1, 1, 80, 0.70, 0), R(2, 2, 76, 0.55, 0),
                    R(5, 1, 60, 0.40),          // no recorded room: found by position (B)
                    R(20, 20, 50, 0.20)         // in no room
                }
            };
            var stats = RoomStatisticsCalculator.Compute(input, output);
            Assert.Equal(3, stats.Count);

            RoomStatistics a = stats[0];
            Assert.Equal("A", a.Name);
            Assert.Equal(2, a.ReceiverCount);
            Assert.Equal(20, a.AreaM2, 9);
            Assert.Equal(50, a.VolumeM3, 9);
            Assert.Equal(1, a.SpeakerCount);
            Assert.Equal(78, a[VisualizationMode.SPL].Mean, 9);
            Assert.Equal(4, a[VisualizationMode.SPL].Spread, 9);
            Assert.Equal(70, a[VisualizationMode.SPL_125].Mean, 9);   // bands are 8 dB under 80 and 76
            Assert.Equal(new[] { 0, 0, 1, 1, 0 }, a.StiCategoryCounts);
            Assert.False(a.Rt60Estimated);

            RoomStatistics b = stats[1];
            Assert.Equal("B", b.Name);
            Assert.Equal(1, b.ReceiverCount);
            Assert.Equal(90, b.VolumeM3, 9);       // 30 m² × the default 3 m ceiling
            Assert.True(b.Rt60Estimated);

            Assert.Equal(-1, stats[2].RoomIndex);
            Assert.Equal("Outside rooms", stats[2].Name);

            Assert.Equal(0.5, RoomStatisticsCalculator.ShareAtLeast(a, output, VisualizationMode.STI, 0.65), 9);
        }

        [Fact]
        public void AWeightedSum_MatchesHandComputation()
        {
            // 0 dB in every band: A-weighted sum = 10·log Σ 10^(A_k/10)
            double expected = 10 * System.Math.Log10(OctaveBands.AWeightingDb.Sum(a => System.Math.Pow(10, a / 10)));
            Assert.Equal(expected, RoomStatisticsCalculator.AWeightedDb(new double[7]), 9);
        }
    }
}
