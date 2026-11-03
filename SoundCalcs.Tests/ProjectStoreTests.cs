using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SoundCalcs.Domain;
using SoundCalcs.IO;
using SoundCalcs.Report;
using Xunit;

namespace SoundCalcs.Tests
{
    public class ProjectStoreTests : IDisposable
    {
        readonly string _dir = Path.Combine(Path.GetTempPath(), "sc_projects_" + Guid.NewGuid().ToString("N"));

        public ProjectStoreTests() { ProjectStore.Folder = _dir; }

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        static AnalysisArea Layout(string name = "Office")
        {
            var area = new AnalysisArea { Name = name };
            area.BoundaryRooms.Add(new RoomPolygon
            {
                Name = "Boundary",
                Vertices = new List<Vec2> { new Vec2(0, 0), new Vec2(8, 0), new Vec2(8, 6), new Vec2(0, 6) },
                Holes = new List<List<Vec2>> { new List<Vec2> { new Vec2(1, 1), new Vec2(2, 1), new Vec2(2, 2) } }
            });
            area.WallGroups.Add(new WallLineGroup
            {
                LineStyleName = "Walls", WallType = WallTypeCatalog.All[2], SegmentCount = 1, TotalLengthM = 8,
                Segments = new List<WallSegment2D> { new WallSegment2D { Start = new Vec2(0, 0), End = new Vec2(8, 0), ThicknessM = 0.2 } }
            });
            area.SpeakerGroups.Add(new SpeakerTypeGroup
            {
                TypeKey = "Fam:Type", FamilyName = "Fam", TypeName = "Type",
                Mapping = new SpeakerProfileMapping { TypeKey = "Fam:Type", ProfileSource = ProfileSourceType.SimpleConical, SpectrumShapeByBand = new double[7] },
                Instances = new List<SpeakerInstance> { new SpeakerInstance { ElementId = 7, TypeKey = "Fam:Type", Position = new Vec3(4, 3, 2.8) } }
            });
            area.Settings.GridSpacingM = 0.5;
            return area;
        }

        [Fact]
        public void RoundTrip_KeepsAreasGeometryAndReportSettings()
        {
            var p = new SoundCalcsProject { ProjectKey = @"C:\Jobs\Office.rvt", ProjectName = "Office" };
            p.Areas.Add(Layout("Ground floor"));
            p.Areas.Add(Layout("First floor"));
            p.ActiveAreaId = p.Areas[1].Id;
            p.ProjectInfo.ProjectNumber = "P-42";
            p.Criteria.StiMin = 0.45;
            p.ReportOptions.Language = ReportLanguage.Estonian;
            ProjectStore.Save(p);

            SoundCalcsProject q = ProjectStore.Load(@"c:\jobs\office.RVT");    // the path compares without case
            Assert.NotNull(q);
            Assert.Equal(2, q.Areas.Count);
            Assert.Equal("First floor", q.ActiveArea.Name);
            Assert.Equal(new Vec2(8, 6), q.Areas[0].BoundaryRooms[0].Vertices[2]);
            Assert.Single(q.Areas[0].BoundaryRooms[0].Holes);
            Assert.Same(WallTypeCatalog.All[2], q.Areas[0].WallGroups[0].WallType);   // catalog entry, for the combo box
            Assert.Equal(new Vec3(4, 3, 2.8), q.Areas[0].SpeakerGroups[0].Instances[0].Position);
            Assert.Equal("P-42", q.ProjectInfo.ProjectNumber);
            Assert.Equal(0.45, q.Criteria.StiMin);
            Assert.Equal(ReportLanguage.Estonian, q.ReportOptions.Language);
            Assert.Equal(AreaFingerprint.Compute(p.Areas[0]), AreaFingerprint.Compute(q.Areas[0]));
        }

        [Fact]
        public void PathFor_DiffersForSameNameInAnotherFolder()
        {
            Assert.NotEqual(ProjectStore.PathFor(@"C:\A\Office.rvt"), ProjectStore.PathFor(@"C:\B\Office.rvt"));
            Assert.StartsWith("Office_", Path.GetFileName(ProjectStore.PathFor(@"C:\A\Office.rvt")));
            Assert.StartsWith("untitled_", Path.GetFileName(ProjectStore.PathFor("")));
        }

        static PluginSettings Legacy(string layoutKey)
        {
            AnalysisArea a = Layout();
            return new PluginSettings
            {
                AnalysisSettings = new AnalysisSettings { GridSpacingM = 0.75, Occupants = 12 },
                BoundaryRooms = a.BoundaryRooms, WallGroups = a.WallGroups, SavedSpeakerGroups = a.SpeakerGroups,
                LayoutProjectKey = layoutKey, LayoutProjectName = layoutKey == null ? null : Path.GetFileNameWithoutExtension(layoutKey),
                BoundaryLineIds = new List<int> { 1, 2, 3 }
            };
        }

        [Fact]
        public void Migration_TakesTheOldLayoutIntoItsOwnProjectOnly()
        {
            var legacy = Legacy(@"C:\Jobs\Office.rvt");

            var other = ProjectStore.Create(@"C:\Jobs\School.rvt", "School", legacy, out bool usedOther);
            Assert.False(usedOther);
            Assert.Empty(other.ActiveArea.WallGroups);
            Assert.Equal(0.75, other.ActiveArea.Settings.GridSpacingM);   // Room page values still carry over

            var mine = ProjectStore.Create(@"C:\Jobs\Office.rvt", "Office", legacy, out bool usedMine);
            Assert.True(usedMine);
            Assert.Single(mine.Areas);
            Assert.Equal("Area 1", mine.ActiveArea.Name);
            Assert.Single(mine.ActiveArea.WallGroups);
            Assert.Single(mine.ActiveArea.SpeakerGroups);
            Assert.Equal(new[] { 1, 2, 3 }, mine.ActiveArea.BoundaryLineIds);
            Assert.Equal(12, mine.ActiveArea.Settings.Occupants);
            // A copy: editing the project does not change the settings object
            mine.ActiveArea.WallGroups[0].Segments.Clear();
            Assert.Single(legacy.WallGroups[0].Segments);

            legacy.LegacyLayoutMigrated = true;
            ProjectStore.Create(@"C:\Jobs\Office.rvt", "Office", legacy, out bool again);
            Assert.False(again);
        }

        [Fact]
        public void Migration_UntaggedLayoutGoesToTheFirstProject()
        {
            var legacy = Legacy(null);
            ProjectStore.Create(@"C:\Jobs\Any.rvt", "Any", legacy, out bool used);
            Assert.True(used);
            Assert.False(ProjectStore.Create(@"C:\Jobs\Any.rvt", "Any", new PluginSettings(), out _).ActiveArea.WallGroups.Any());
        }

        [Fact]
        public void LoadOrCreate_PrefersTheStoredFile()
        {
            var p = new SoundCalcsProject { ProjectKey = "K" };
            p.Areas.Add(Layout("Stored"));
            ProjectStore.Save(p);
            var q = ProjectStore.LoadOrCreate("K", "K", Legacy("K"), out bool used);
            Assert.False(used);
            Assert.Equal("Stored", q.ActiveArea.Name);
        }

        [Fact]
        public void UnreadableFile_IsKeptAndANewProjectStarts()
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllText(ProjectStore.PathFor("Broken"), "{ not json");
            Assert.Null(ProjectStore.Load("Broken"));
            Assert.True(File.Exists(ProjectStore.PathFor("Broken") + ".bad.json"));
        }

        [Fact]
        public void Fingerprint_FollowsInputsNotNames()
        {
            AnalysisArea a = Layout();
            string f = AreaFingerprint.Compute(a);
            a.Name = "Renamed"; a.Notes = "notes"; a.Settings.UseMinSplThreshold = true;
            Assert.Equal(f, AreaFingerprint.Compute(a));

            AnalysisArea moved = a.DeepCopy();
            moved.SpeakerGroups[0].Instances[0].Position = new Vec3(4.1, 3, 2.8);
            Assert.NotEqual(f, AreaFingerprint.Compute(moved));
            Assert.Equal(f, AreaFingerprint.Compute(a));     // the copy shares nothing

            AnalysisArea rt = a.DeepCopy();
            rt.Settings.RT60ByBand[3] = 1.5;
            Assert.NotEqual(f, AreaFingerprint.Compute(rt));

            AnalysisArea wall = a.DeepCopy();
            wall.WallGroups[0].WallType = WallTypeCatalog.All[0];
            Assert.NotEqual(f, AreaFingerprint.Compute(wall));
        }

        [Fact]
        public void ResultStatus_AndDuplicate()
        {
            AnalysisArea a = Layout();
            Assert.Equal(AreaResultStatus.NotCalculated, a.ResultStatus);
            a.LastJobId = "job_1";
            a.ResultFingerprint = AreaFingerprint.Compute(a);
            Assert.Equal(AreaResultStatus.Current, a.ResultStatus);
            a.Settings.GridSpacingM = 0.25;
            Assert.Equal(AreaResultStatus.Outdated, a.ResultStatus);

            AnalysisArea d = a.CloneAsNew("Copy");
            Assert.NotEqual(a.Id, d.Id);
            Assert.Equal(AreaResultStatus.NotCalculated, d.ResultStatus);
            Assert.Equal(a.WallGroups[0].Segments.Count, d.WallGroups[0].Segments.Count);

            var p = new SoundCalcsProject();
            p.Areas.Add(new AnalysisArea { Name = "Area 1" });
            p.Areas.Add(new AnalysisArea { Name = "Area 3" });
            Assert.Equal("Area 2", p.NextAreaName());
        }
    }
}
