using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using SoundCalcs.Report;

namespace SoundCalcs.Domain
{
    /// <summary>
    /// One area of a project (a room, a floor, a zone): its own boundary, walls, speakers, room settings and the last
    /// calculation. Areas are what the report lists, like rooms in a lighting calculation.
    /// </summary>
    public class AnalysisArea
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "Area 1";
        public string Notes { get; set; } = "";

        public LinkSelection LinkSelection { get; set; } = new LinkSelection();

        /// <summary>The Room page values (grid, listener height, RT60 and noise per band, surfaces, ...).</summary>
        public AnalysisSettings Settings { get; set; } = new AnalysisSettings();

        public List<WallLineGroup> WallGroups { get; set; } = new List<WallLineGroup>();
        public List<RoomPolygon> BoundaryRooms { get; set; } = new List<RoomPolygon>();
        public List<int> BoundaryLineIds { get; set; } = new List<int>();
        public bool WallsDetected { get; set; }

        public List<SpeakerTypeGroup> SpeakerGroups { get; set; } = new List<SpeakerTypeGroup>();
        public SpeakerLineFilterType SpeakerLineFilter { get; set; } = SpeakerLineFilterType.Both;

        // Where the layout and the speakers were read from (document path, else title)
        public string LayoutProjectKey { get; set; }
        public string LayoutProjectName { get; set; }
        public string SpeakersProjectKey { get; set; }
        public string SpeakersProjectName { get; set; }

        /// <summary>The last completed calculation of this area (job files under %AppData%\...\jobs); empty = none.</summary>
        public string LastJobId { get; set; } = "";
        public DateTime? LastRunUtc { get; set; }
        public CalculationQuality? LastRunQuality { get; set; }

        /// <summary><see cref="AreaFingerprint"/> of the inputs the last calculation used.</summary>
        public string ResultFingerprint { get; set; } = "";

        public bool HasResults => !string.IsNullOrEmpty(LastJobId);

        /// <summary>Whether the last results still match the inputs (none, current or outdated).</summary>
        public AreaResultStatus ResultStatus =>
            !HasResults ? AreaResultStatus.NotCalculated
            : ResultFingerprint == AreaFingerprint.Compute(this) ? AreaResultStatus.Current : AreaResultStatus.Outdated;

        /// <summary>A deep copy with a new id and no results (Duplicate).</summary>
        public AnalysisArea CloneAsNew(string name)
        {
            AnalysisArea c = DeepCopy();
            c.Id = Guid.NewGuid().ToString("N");
            c.Name = name;
            c.LastJobId = "";
            c.LastRunUtc = null;
            c.LastRunQuality = null;
            c.ResultFingerprint = "";
            return c;
        }

        /// <summary>A copy that shares nothing mutable with this area.</summary>
        public AnalysisArea DeepCopy()
        {
            var c = (AnalysisArea)MemberwiseClone();
            c.LinkSelection = LinkSelection == null ? new LinkSelection() : (LinkSelection)LinkSelection.Clone();
            c.Settings = Settings == null ? new AnalysisSettings() : Settings.Clone();
            c.WallGroups = (WallGroups ?? new List<WallLineGroup>()).Select(g => g.Clone()).ToList();
            c.BoundaryRooms = (BoundaryRooms ?? new List<RoomPolygon>()).Select(r => r.Clone()).ToList();
            c.BoundaryLineIds = new List<int>(BoundaryLineIds ?? new List<int>());
            c.SpeakerGroups = (SpeakerGroups ?? new List<SpeakerTypeGroup>()).Select(g => g.Clone()).ToList();
            return c;
        }
    }

    /// <summary>A Revit project's SoundCalcs data: its areas and its report settings. Stored per user and document.</summary>
    public class SoundCalcsProject
    {
        public int Version { get; set; } = 1;

        /// <summary>Document path (title while unsaved) and title.</summary>
        public string ProjectKey { get; set; } = "";
        public string ProjectName { get; set; } = "";

        public List<AnalysisArea> Areas { get; set; } = new List<AnalysisArea>();
        public string ActiveAreaId { get; set; } = "";

        public ProjectInfo ProjectInfo { get; set; } = new ProjectInfo();
        public ReportCriteria Criteria { get; set; } = new ReportCriteria();
        public ReportOptions ReportOptions { get; set; } = new ReportOptions();

        /// <summary>Folder the last report was saved to.</summary>
        public string LastReportFolder { get; set; } = "";

        public AnalysisArea ActiveArea =>
            Areas.FirstOrDefault(a => a.Id == ActiveAreaId) ?? Areas.FirstOrDefault();

        public AnalysisArea Find(string id) => Areas.FirstOrDefault(a => a.Id == id);

        /// <summary>"Area N" with the lowest N not in use.</summary>
        public string NextAreaName(string stem = "Area")
        {
            for (int n = 1; ; n++)
            {
                string name = stem + " " + n.ToString(CultureInfo.InvariantCulture);
                if (Areas.All(a => !string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase))) return name;
            }
        }

        /// <summary>Makes the project usable after loading: at least one area, a valid active area, settings present.</summary>
        public void Normalise()
        {
            if (Areas == null) Areas = new List<AnalysisArea>();
            if (Areas.Count == 0) Areas.Add(new AnalysisArea { Name = "Area 1" });
            foreach (AnalysisArea a in Areas)
            {
                if (string.IsNullOrEmpty(a.Id)) a.Id = Guid.NewGuid().ToString("N");
                if (a.Settings == null) a.Settings = new AnalysisSettings();
                if (a.LinkSelection == null) a.LinkSelection = new LinkSelection();
                if (a.WallGroups == null) a.WallGroups = new List<WallLineGroup>();
                if (a.BoundaryRooms == null) a.BoundaryRooms = new List<RoomPolygon>();
                if (a.BoundaryLineIds == null) a.BoundaryLineIds = new List<int>();
                if (a.SpeakerGroups == null) a.SpeakerGroups = new List<SpeakerTypeGroup>();
                foreach (WallLineGroup g in a.WallGroups)
                    g.WallType = WallTypeCatalog.FindByKey(g.WallType?.Key ?? "");
            }
            if (Find(ActiveAreaId) == null) ActiveAreaId = Areas[0].Id;
            if (ProjectInfo == null) ProjectInfo = new ProjectInfo();
            if (Criteria == null) Criteria = new ReportCriteria();
            if (ReportOptions == null) ReportOptions = new ReportOptions();
        }
    }

    /// <summary>
    /// A hash of everything in an area that changes its results (geometry, walls, speakers, room settings), so a report
    /// can tell when an area's results no longer match its inputs. Names, notes and display settings are left out.
    /// </summary>
    public static class AreaFingerprint
    {
        public static string Compute(AnalysisArea a)
        {
            var sb = new StringBuilder();
            void N(double v) => sb.Append(v.ToString("R", CultureInfo.InvariantCulture)).Append(';');
            void S(string v) => sb.Append(v ?? "").Append('|');
            void B(double[] v) { foreach (double x in v ?? new double[0]) N(x); sb.Append('/'); }
            void P(Vec2 v) { N(v.X); N(v.Y); }
            void P3(Vec3 v) { N(v.X); N(v.Y); N(v.Z); }

            AnalysisSettings s = a.Settings ?? new AnalysisSettings();
            N(s.GridSpacingM); N(s.ReceiverHeightM); N(s.BoundaryOffsetM); N(s.RelativeHumidityPct); N(s.Occupants);
            S(s.AutoRt60PerRoom.ToString()); S(s.FloorSurface.ToString()); S(s.CeilingSurface.ToString());
            B(s.RT60ByBand); B(s.BackgroundNoiseByBand);
            S(a.SpeakerLineFilter.ToString());

            foreach (RoomPolygon r in a.BoundaryRooms ?? new List<RoomPolygon>())
            {
                sb.Append("R:"); N(r.FloorElevationM);
                foreach (Vec2 v in r.Vertices) P(v);
                foreach (List<Vec2> h in r.Holes ?? new List<List<Vec2>>()) { sb.Append('h'); foreach (Vec2 v in h) P(v); }
            }
            foreach (WallLineGroup g in a.WallGroups ?? new List<WallLineGroup>())
            {
                sb.Append("W:"); S(g.WallType?.Key); N(g.HeightM); S(g.IsObstacle.ToString());
                foreach (WallSegment2D seg in g.Segments) { P(seg.Start); P(seg.End); N(seg.ThicknessM); N(seg.HeightM); N(seg.BaseElevationM); }
            }
            foreach (SpeakerTypeGroup g in a.SpeakerGroups ?? new List<SpeakerTypeGroup>())
            {
                sb.Append("S:"); S(g.TypeKey);
                SpeakerProfileMapping m = g.Mapping ?? new SpeakerProfileMapping();
                S(m.ProfileSource.ToString()); S(m.GllFilePath); S(m.DirectivityFilePath);
                N(m.OnAxisSplDb); N(m.ConeHalfAngleDeg); N(m.OffAxisAttenuationDb);
                B(m.SpectrumShapeByBand); B(m.CoverageAngleByBandDeg);
                foreach (SpeakerInstance i in g.Instances)
                {
                    sb.Append(i.ElementId).Append(':'); P3(i.Position); P3(i.FacingDirection); N(i.LevelElevationM); S(i.AbLine);
                }
            }

            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
                return string.Concat(hash.Take(12).Select(b => b.ToString("x2")));
            }
        }
    }
}
