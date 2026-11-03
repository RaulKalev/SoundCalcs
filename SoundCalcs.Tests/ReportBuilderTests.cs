using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SoundCalcs.Domain;
using SoundCalcs.Report;
using SoundCalcs.UI.ViewModels;
using Xunit;

namespace SoundCalcs.Tests
{
    public class ReportBuilderTests
    {
        static RoomPolygon Box(string name, double x0, double x1) => new RoomPolygon
        {
            Name = name,
            Vertices = new List<Vec2> { new Vec2(x0, 0), new Vec2(x1, 0), new Vec2(x1, 4), new Vec2(x0, 4) }
        };

        /// <summary>Two 4×4 m rooms with a 1 m grid; room A has a speaker, values chosen per room.</summary>
        static ReportAreaData Area(string id, double stiA = 0.62, double splA = 75)
        {
            var rooms = new List<RoomPolygon> { Box("Boundary – A", 0, 4), Box("Boundary – B", 4, 8) };
            var results = new List<ReceiverResult>();
            var receivers = new List<ReceiverPoint>();
            int n = 0;
            for (int x = 0; x < 8; x++)
                for (int y = 0; y < 4; y++)
                {
                    int room = x < 4 ? 0 : 1;
                    double spl = room == 0 ? splA + (x + y) * 0.5 : 50;
                    results.Add(new ReceiverResult
                    {
                        ReceiverIndex = n, RoomIndex = room, Position = new Vec3(x + 0.5, y + 0.5, 1.2),
                        SplDb = spl, SplDbA = spl - 2, Sti = room == 0 ? stiA + y * 0.01 : 0.2, C80Db = 2,
                        SplDbByBand = Enumerable.Repeat(spl - 8, 7).ToArray()
                    });
                    receivers.Add(new ReceiverPoint(new Vec3(x + 0.5, y + 0.5, 1.2), n) { RoomIndex = room });
                    n++;
                }
            var input = new AcousticJobInput
            {
                AreaId = id, Rooms = rooms, Receivers = receivers,
                Sources = new List<ComputeSource> { new ComputeSource { Position = new Vec3(2, 2, 3), FacingDirection = new Vec3(0, 0, -1), Profile = new SpeakerProfileMapping() } }
            };
            return new ReportAreaData
            {
                AreaId = id, AreaName = "Area " + id, Input = input,
                Output = new AcousticJobOutput { Rooms = rooms, Results = results }
            };
        }

        static ReportRequest Request(params ReportAreaData[] areas) => new ReportRequest
        {
            Areas = areas.ToList(),
            Options = new ReportOptions { IncludeSpeakerContributions = false }
        };

        [Fact]
        public void Criteria_PassFailAndNotAssessed()
        {
            var a = ReportBuilder.Analyse(Request(Area("1"))).Single();
            Assert.Equal(2, a.Rooms.Count);
            // Room A: STI ≥ 0.62, SPL 75..78 (spread 3 ≤ 6), dBA ≥ 73 → pass; room B has no speaker → not assessed
            Assert.Equal(RoomOutcome.Pass, a.Assessments[0].Outcome);
            Assert.Equal(RoomOutcome.NotAssessed, a.Assessments[1].Outcome);

            var low = ReportBuilder.Analyse(Request(Area("1", stiA: 0.40))).Single();
            Assert.Equal(RoomOutcome.Fail, low.Assessments[0].Outcome);
            Assert.False(low.Assessments[0].Results.Single(r => r.Name == "STI at every point").Passed);
        }

        [Fact]
        public void Criteria_CoverageIgnoresTheWorstPoints()
        {
            var req = Request(Area("1", stiA: 0.49));    // STI 0.49 .. 0.52 over the rows
            Assert.Equal(RoomOutcome.Fail, ReportBuilder.Analyse(req).Single().Assessments[0].Outcome);
            req.Criteria.PointCoverage = 0.7;            // the worst 30 % (the 0.49 row = 25 %) left out
            Assert.Equal(RoomOutcome.Pass, ReportBuilder.Analyse(req).Single().Assessments[0].Outcome);
            req.Criteria.SkipRoomsWithoutSpeakers = false;
            Assert.Equal(RoomOutcome.Fail, ReportBuilder.Analyse(req).Single().Assessments[1].Outcome);
        }

        [Fact]
        public void Options_SelectAreasAndExcludeRooms()
        {
            var req = Request(Area("1"), Area("2"), new ReportAreaData { AreaId = "3", AreaName = "Not calculated" });
            Assert.Equal(new[] { "1", "2" }, ReportBuilder.IncludedAreas(req).Select(a => a.AreaId));
            req.Options.ExcludedAreaIds = new List<string> { "1" };
            req.Options.ExcludedRooms = new List<string> { ReportOptions.RoomKey("2", "Boundary – B") };
            var a = ReportBuilder.Analyse(req).Single();
            Assert.Equal("2", a.Area.AreaId);
            Assert.Equal(new[] { "Boundary – A" }, a.Rooms.Select(r => r.Name));
        }

        [Fact]
        public void Document_HasTheRequestedSections()
        {
            var req = Request(Area("1"), Area("2"));
            req.Options.IncludeCover = true;
            var doc = ReportBuilder.Build(req).Document;
            Assert.IsType<CoverBlock>(doc.Blocks[0]);
            Assert.IsType<ContentsBlock>(doc.Blocks[1]);
            var h1 = doc.Blocks.OfType<HeadingBlock>().Where(h => h.Level == 1).Select(h => h.Text).ToList();
            Assert.Equal(new[] { "Summary", "Area: Area 1", "Area: Area 2", "Criteria", "Calculation method" }, h1);
            Assert.Equal(doc.Blocks.OfType<HeadingBlock>().Count(), doc.Blocks.OfType<HeadingBlock>().Select(h => h.Anchor).Distinct().Count());

            // Two plan figures per area, a value chart per room and quantity
            var figs = doc.Blocks.OfType<FigureBlock>().ToList();
            Assert.Equal(2 * 2 + 2 * 2 * 2, figs.Count);

            req.Options = new ReportOptions
            {
                IncludeCover = false, IncludeContents = false, IncludeValueCharts = false, IncludeMethod = false,
                IncludeSpeakerContributions = false, PlanModes = new List<VisualizationMode>(), Language = ReportLanguage.Estonian
            };
            doc = ReportBuilder.Build(req).Document;
            Assert.Empty(doc.Blocks.OfType<FigureBlock>());
            Assert.DoesNotContain(doc.Blocks, b => b is CoverBlock || b is ContentsBlock);
            Assert.Contains(doc.Blocks.OfType<HeadingBlock>(), h => h.Text == "Kokkuvõte");
        }

        [Fact]
        public void SummaryCsv_UsesTheCulturesSeparators()
        {
            var areas = ReportBuilder.Analyse(Request(Area("1")));
            var t = new ReportText(ReportLanguage.English);
            string et = ReportBuilder.SummaryCsv(areas, t, new CultureInfo("et-EE"));
            string[] lines = et.Trim().Split('\n');
            Assert.Equal(3, lines.Length);
            Assert.Contains(";", lines[1]);
            Assert.Contains(";16,0;", lines[1]);      // floor area with a decimal comma
            string inv = ReportBuilder.SummaryCsv(areas, t, CultureInfo.InvariantCulture);
            Assert.Contains(",16.0,", inv);
            string values = ReportBuilder.ValuesCsv(areas, t, CultureInfo.InvariantCulture);
            Assert.Equal(1 + 32, values.Trim().Split('\n').Length);
        }

        [Fact]
        public void Figures_RoomLayersAndValuesStayInTheirRoom()
        {
            ReportAreaData area = Area("1");
            var stats = RoomStatisticsCalculator.Compute(area.Input, area.Output);
            var fig = PlanFigureBuilder.ForRoom(area.Input, area.Output, stats[0], VisualizationMode.STI, (0.2, 0.7), true, false);
            Assert.Single(fig.Layers);
            Assert.NotNull(fig.Layers[0].ClipRings);
            Assert.Equal(16, fig.Values.Count);
            Assert.All(fig.Values, v => Assert.True(v.Position.X < 4));
            Assert.Equal(1, fig.Values.Count(v => v.IsMin));
            Assert.Equal(1, fig.Values.Count(v => v.IsMax));

            var whole = PlanFigureBuilder.ForArea(area.Input, area.Output, VisualizationMode.SPL, true);
            Assert.Equal(2, whole.Layers.Count);
            Assert.Equal("S1", whole.Speakers.Single().Label);
            Assert.Equal(8, whole.Legend.Count);
            Assert.Equal("A", whole.Rooms[0].Label);
        }
    }
}
