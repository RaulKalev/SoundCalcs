using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using SoundCalcs.Domain;
using SoundCalcs.UI.ViewModels;
using SoundCalcs.Visualization;

namespace SoundCalcs.Report
{
    /// <summary>Whether an area's results match its current inputs.</summary>
    public enum AreaResultStatus
    {
        Current,
        Outdated,
        NotCalculated
    }

    /// <summary>One area of the project as the report sees it: its last job and the layout it was set up with.</summary>
    public class ReportAreaData
    {
        public string AreaId { get; set; } = "";
        public string AreaName { get; set; } = "";
        public string LevelName { get; set; } = "";
        public string Notes { get; set; } = "";
        public AcousticJobInput Input { get; set; }
        public AcousticJobOutput Output { get; set; }
        public List<WallLineGroup> WallGroups { get; set; } = new List<WallLineGroup>();
        public List<SpeakerTypeGroup> SpeakerGroups { get; set; } = new List<SpeakerTypeGroup>();
        public AreaResultStatus Status { get; set; } = AreaResultStatus.Current;

        public bool HasResults => Input != null && Output != null && Output.Results != null && Output.Results.Count > 0;
    }

    public class ReportRequest
    {
        public ProjectInfo Project { get; set; } = new ProjectInfo();
        public ReportCriteria Criteria { get; set; } = new ReportCriteria();
        public ReportOptions Options { get; set; } = new ReportOptions();
        public List<ReportAreaData> Areas { get; set; } = new List<ReportAreaData>();
        public string ModelName { get; set; } = "";
        public string PluginVersion { get; set; } = "";
        public DateTime Date { get; set; } = DateTime.Now;

        /// <summary>
        /// Speaker contributions already computed, by job id (they take one model run per speaker). Build fills it, so
        /// a dialog that rebuilds the report for its preview computes them once per job.
        /// </summary>
        public Dictionary<string, Dictionary<int, RoomContributions>> ContributionCache { get; set; } =
            new Dictionary<string, Dictionary<int, RoomContributions>>();
    }

    /// <summary>The numbers behind one area's part of the report.</summary>
    public class AreaAnalysis
    {
        public ReportAreaData Area { get; set; }

        /// <summary>The rooms in the report (excluded rooms left out), in the job's order.</summary>
        public List<RoomStatistics> Rooms { get; set; } = new List<RoomStatistics>();
        public List<RoomAssessment> Assessments { get; set; } = new List<RoomAssessment>();
        public Dictionary<int, RoomContributions> Contributions { get; set; } = new Dictionary<int, RoomContributions>();

        public int PassCount => Assessments.Count(a => a.Outcome == RoomOutcome.Pass);
        public int FailCount => Assessments.Count(a => a.Outcome == RoomOutcome.Fail);
        public int NotAssessedCount => Assessments.Count(a => a.Outcome == RoomOutcome.NotAssessed);
    }

    public class ReportBuildResult
    {
        public ReportDocument Document { get; set; }
        public List<AreaAnalysis> Areas { get; set; } = new List<AreaAnalysis>();
    }

    /// <summary>
    /// Turns a project's areas, criteria and options into a <see cref="ReportDocument"/>. Revit-free: the harness builds
    /// reports from its scenarios with it, and the plugin paints the result to PDF.
    /// </summary>
    public static class ReportBuilder
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// <summary>The areas the options include that have results, in request order.</summary>
        public static List<ReportAreaData> IncludedAreas(ReportRequest req) =>
            req.Areas.Where(a => a.HasResults &&
                (req.Options.ExcludedAreaIds == null || !req.Options.ExcludedAreaIds.Contains(a.AreaId))).ToList();

        /// <summary>Statistics and criteria of every included area and room (no speaker contributions: those are slow).</summary>
        public static List<AreaAnalysis> Analyse(ReportRequest req)
        {
            var t = new ReportText(req.Options.Language);
            var list = new List<AreaAnalysis>();
            foreach (ReportAreaData area in IncludedAreas(req))
            {
                var a = new AreaAnalysis { Area = area };
                foreach (RoomStatistics room in RoomStatisticsCalculator.Compute(area.Input, area.Output, t.OutsideRooms))
                {
                    if (req.Options.ExcludedRooms != null &&
                        req.Options.ExcludedRooms.Contains(ReportOptions.RoomKey(area.AreaId, room.Name))) continue;
                    a.Rooms.Add(room);
                    a.Assessments.Add(CriteriaEvaluator.Assess(room, area.Output, req.Criteria, t));
                }
                list.Add(a);
            }
            return list;
        }

        public static ReportBuildResult Build(ReportRequest req, CancellationToken token = default(CancellationToken))
        {
            var t = new ReportText(req.Options.Language);
            ReportOptions o = req.Options;
            ProjectInfo p = req.Project ?? new ProjectInfo();
            List<AreaAnalysis> areas = Analyse(req);
            if (o.IncludeSpeakerContributions)
                foreach (AreaAnalysis a in areas)
                {
                    token.ThrowIfCancellationRequested();
                    string key = a.Area.Output.JobId ?? "";
                    var cache = req.ContributionCache ?? new Dictionary<string, Dictionary<int, RoomContributions>>();
                    if (key.Length == 0 || !cache.TryGetValue(key, out Dictionary<int, RoomContributions> c))
                    {
                        // Every room of the job (not only the included ones), so the cache does not depend on the selection
                        var all = RoomStatisticsCalculator.Compute(a.Area.Input, a.Area.Output, t.OutsideRooms);
                        c = SpeakerContributionCalculator.Compute(a.Area.Input, a.Area.Output, all, token);
                        if (key.Length > 0) lock (cache) cache[key] = c;
                    }
                    a.Contributions = c;
                }

            string projectTitle = FirstNonEmpty(p.ProjectName, req.ModelName, t.ReportTitle);
            var doc = new ReportDocument
            {
                Title = t.ReportTitle + " – " + projectTitle,
                Subject = t.ReportSubtitle,
                Author = FirstNonEmpty(p.Author, p.Company),
                Created = req.Date,
                PageFormat = o.PageFormat,
                HeaderLeft = Join(" · ", projectTitle, p.ProjectNumber),
                FooterLeft = Join(" · ", t.ReportTitle, string.IsNullOrEmpty(p.Revision) ? "" : t.Revision + " " + p.Revision,
                    req.Date.ToString("yyyy-MM-dd", Inv)),
                PageNumberFormat = t.PageOf,
                ContentsTitle = t.Contents
            };
            var b = new Blocks(doc);

            if (o.IncludeCover) AddCover(b, req, t, areas, projectTitle);
            if (o.IncludeContents) b.Add(new ContentsBlock());
            if (o.IncludeSummary) AddSummary(b, areas, o, t, o.IncludeContents && !o.IncludeCover);
            foreach (AreaAnalysis a in areas)
            {
                token.ThrowIfCancellationRequested();
                AddArea(b, a, o, t);
            }
            if (o.IncludeCriteria) AddCriteria(b, req.Criteria, t);
            if (o.IncludeMethod) AddMethod(b, t);
            return new ReportBuildResult { Document = doc, Areas = areas };
        }

        // ------------------------------------------------------------------ cover / summary

        static void AddCover(Blocks b, ReportRequest req, ReportText t, List<AreaAnalysis> areas, string projectTitle)
        {
            ProjectInfo p = req.Project ?? new ProjectInfo();
            var cover = new CoverBlock
            {
                Title = t.ReportTitle,
                Subtitle = projectTitle,
                LogoPath = p.LogoPath ?? "",
                ListTitle = t.AreasInReport,
                Note = Join("\n", p.Description, t.CoverNote + (string.IsNullOrEmpty(req.PluginVersion) ? "" : " (SoundCalcs " + req.PluginVersion + ")"))
            };
            void F(string k, string v) { if (!string.IsNullOrWhiteSpace(v)) cover.Fields.Add(new KeyValuePair<string, string>(k, v)); }
            F(t.ProjectNumber, p.ProjectNumber);
            F(t.Client, p.Client);
            F(t.Company, p.Company);
            F(t.Author, p.Author);
            F(t.CheckedBy, p.CheckedBy);
            F(t.Revision, p.Revision);
            F(t.Date, req.Date.ToString("yyyy-MM-dd", Inv));
            F(t.Model, req.ModelName);
            foreach (AreaAnalysis a in areas)
                cover.ListItems.Add(a.Area.AreaName + " – " + a.Rooms.Count.ToString(Inv) + " " + t.RoomsCount.ToLowerInvariant());
            b.Add(cover);
        }

        /// <summary>A summary column: header, width, alignment and the cell of a room.</summary>
        class SummaryColumn
        {
            public TableColumn Column;
            public Func<RoomStatistics, RoomAssessment, string> Cell;
            public bool IsResult;
        }

        /// <summary>The summary's columns for the quantities the options put in the tables.</summary>
        static List<SummaryColumn> SummaryColumns(ReportOptions o, ReportText t)
        {
            string mean = t.Mean.ToLowerInvariant();
            var cols = new List<SummaryColumn>
            {
                new SummaryColumn { Column = new TableColumn(t.Room, 2.6f), Cell = (r, a) => PlanFigureBuilder.RoomDisplayName(r.Name) },
                new SummaryColumn { Column = new TableColumn(t.FloorArea, 0.9f, CellAlign.Right), Cell = (r, a) => r.AreaM2 > 0 ? r.AreaM2.ToString("0.0", Inv) : "–" },
                new SummaryColumn { Column = new TableColumn(t.Speakers, 0.8f, CellAlign.Right), Cell = (r, a) => r.SpeakerCount.ToString(Inv) }
            };
            string Range(ModeStatistics m) => F1(m.Mean) + " (" + F1(m.Min) + "–" + F1(m.Max) + ")";
            if (o.InTables(VisualizationMode.SPL))
                cols.Add(new SummaryColumn { Column = new TableColumn("SPL dB " + mean + " (min–max)", 2.0f, CellAlign.Right), Cell = (r, a) => Range(r[VisualizationMode.SPL]) });
            if (o.InTables(VisualizationMode.SPL_A))
                cols.Add(new SummaryColumn { Column = new TableColumn("dBA " + mean + " (min–max)", 2.0f, CellAlign.Right), Cell = (r, a) => Range(r[VisualizationMode.SPL_A]) });
            if (o.InTables(VisualizationMode.SPL) || o.InTables(VisualizationMode.SPL_A))
                cols.Add(new SummaryColumn { Column = new TableColumn(t.Spread + " dB", 0.9f, CellAlign.Right), Cell = (r, a) => F1(r[VisualizationMode.SPL].Spread) });
            if (o.InTables(VisualizationMode.STI))
            {
                cols.Add(new SummaryColumn { Column = new TableColumn("STI " + mean + " (min)", 1.4f, CellAlign.Right), Cell = (r, a) => F2(r[VisualizationMode.STI].Mean) + " (" + F2(r[VisualizationMode.STI].Min) + ")" });
                cols.Add(new SummaryColumn { Column = new TableColumn(t.StiClass, 1.1f), Cell = (r, a) => t.StiCategory(r[VisualizationMode.STI].Mean) });
            }
            if (o.InTables(VisualizationMode.C80))
                cols.Add(new SummaryColumn { Column = new TableColumn("C80 dB " + mean, 1.0f, CellAlign.Right), Cell = (r, a) => F1(r[VisualizationMode.C80].Mean) });
            cols.Add(new SummaryColumn { Column = new TableColumn(t.Rt60At500, 0.9f, CellAlign.Right), Cell = (r, a) => r.Rt60ByBand[2].ToString("0.00", Inv) });
            cols.Add(new SummaryColumn { Column = new TableColumn(t.Result, 1.1f), Cell = (r, a) => CriteriaEvaluator.OutcomeText(a.Outcome, t), IsResult = true });
            return cols;
        }

        static void AddSummary(Blocks b, List<AreaAnalysis> areas, ReportOptions o, ReportText t, bool samePage)
        {
            b.Heading(1, t.Summary, !samePage);
            b.Add(new ParagraphBlock { Text = t.SummaryIntro, Style = ParagraphStyle.Note });
            List<SummaryColumn> cols = SummaryColumns(o, t);
            var table = new TableBlock { TextScale = 0.86f };
            table.Columns.AddRange(cols.Select(c => c.Column));
            foreach (AreaAnalysis a in areas)
            {
                if (areas.Count > 1)
                    table.Rows.Add(new TableRow(new[] { a.Area.AreaName }.Concat(Enumerable.Repeat("", cols.Count - 1)).ToArray()) { IsGroup = true });
                for (int i = 0; i < a.Rooms.Count; i++)
                {
                    RoomStatistics r = a.Rooms[i];
                    RoomAssessment ra = a.Assessments[i];
                    var row = table.AddRow(cols.Select(c => c.Cell(r, ra)).ToArray());
                    row.Status = cols.Select(c => c.IsResult ? CriteriaEvaluator.OutcomeStatus(ra.Outcome) : CellStatus.None).ToArray();
                }
            }
            b.Add(table);
            foreach (AreaAnalysis a in areas)
                b.Add(new ParagraphBlock
                {
                    Style = ParagraphStyle.Note,
                    Text = (areas.Count > 1 ? a.Area.AreaName + ": " : "") +
                           string.Format(Inv, a.Rooms.Count == 1 ? t.AreaSummaryLineOne : t.AreaSummaryLine,
                               a.Rooms.Count, a.PassCount, a.FailCount, a.NotAssessedCount)
                });
        }

        // ------------------------------------------------------------------ area

        static void AddArea(Blocks b, AreaAnalysis a, ReportOptions o, ReportText t)
        {
            ReportAreaData area = a.Area;
            AcousticJobInput input = area.Input;
            AcousticJobOutput output = area.Output;
            b.Heading(1, string.Format(t.AreaHeading, area.AreaName), true);
            if (area.Status == AreaResultStatus.Outdated)
                b.Add(new ParagraphBlock { Text = t.Outdated, Style = ParagraphStyle.Warning });

            var kv = new KeyValueBlock();
            void KV(string k, string v) { if (!string.IsNullOrWhiteSpace(v)) kv.Items.Add(new KeyValuePair<string, string>(k, v)); }
            KV(t.Level, FirstNonEmpty(area.LevelName, LevelOf(area)));
            KV(t.Calculated, output.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm", Inv));
            KV(t.Quality, output.Quality == CalculationQuality.Draft ? t.QualityDraft : t.QualityFull);
            KV(t.Receivers, output.Results.Count.ToString(Inv));
            KV(t.Speakers, input.Sources.Count.ToString(Inv));
            KV(t.RoomsCount, a.Rooms.Count.ToString(Inv));
            KV(t.SpeakerLine, input.SpeakerLineFilter == SpeakerLineFilterType.Both ? t.LineBoth
                : input.SpeakerLineFilter == SpeakerLineFilterType.ALine ? "A" : "B");
            b.Add(kv);
            if (!string.IsNullOrWhiteSpace(area.Notes))
                b.Add(new ParagraphBlock { Text = t.Notes + ": " + area.Notes, Style = ParagraphStyle.Note });

            double listenerH = input.Settings?.ReceiverHeightM ?? (input.Receivers.Count > 0 ? input.Receivers[0].Position.Z : 1.2);
            var ranges = new Dictionary<VisualizationMode, (double, double)>();
            foreach (VisualizationMode mode in (o.PlanModes ?? new List<VisualizationMode>()).Distinct())
            {
                PlanFigure fig = PlanFigureBuilder.ForArea(input, output, mode, o.ShowIsolines);
                ranges[mode] = (fig.MinVal, fig.MaxVal);
                string caption = string.Format(Inv, t.PlanCaption, t.ModeName(mode), listenerH);
                if (fig.Isolines.Count > 0)
                    caption += string.Format(Inv, t.IsolinesCaption, string.Join(", ", fig.Isolines.Select(l => l.Label)));
                b.Add(new FigureBlock { Figure = fig, Caption = caption });
            }

            if (o.IncludeInputs) AddInputs(b, input, HeatmapMath.ViewerGridSpacing(output.Results), t);
            if (o.IncludeWalls) AddWalls(b, area, t);
            if (o.IncludeSpeakers) AddSpeakers(b, area, o, t);

            if (o.IncludeRoomDetails || o.IncludeValueCharts)
            {
                b.Heading(2, t.RoomResults, false);
                for (int i = 0; i < a.Rooms.Count; i++)
                    AddRoom(b, a, i, o, t, ranges);
            }
        }

        static void AddInputs(Blocks b, AcousticJobInput input, double spacing, ReportText t)
        {
            b.Heading(2, t.Inputs, false);
            AnalysisSettings s = input.Settings;
            EnvironmentSettings e = input.Environment ?? new EnvironmentSettings();
            var kv = new KeyValueBlock();
            void KV(string k, string v) => kv.Items.Add(new KeyValuePair<string, string>(k, v));
            KV(t.GridSpacing, (s != null ? s.GridSpacingM : spacing).ToString("0.0#", Inv) + " m");
            KV(t.ReceiverHeight, s != null ? s.ReceiverHeightM.ToString("0.0#", Inv) + " m" : "–");
            KV(t.BoundaryOffset, s != null ? s.BoundaryOffsetM.ToString("0.0#", Inv) + " m" : "–");
            KV(t.Temperature, e.TemperatureC.ToString("0.#", Inv) + " °C");
            KV(t.Humidity, e.RelativeHumidityPct.ToString("0", Inv) + " %");
            KV(t.Occupants, s != null ? s.Occupants.ToString(Inv) : "–");
            KV(t.FloorFinish, SurfaceMaterialCatalog.Find(SurfaceMaterialCatalog.FloorOptions, e.FloorSurface)?.DisplayName ?? e.FloorSurface.ToString());
            KV(t.CeilingFinish, SurfaceMaterialCatalog.Find(SurfaceMaterialCatalog.CeilingOptions, e.CeilingSurface)?.DisplayName ?? e.CeilingSurface.ToString());
            bool perRoom = s?.AutoRt60PerRoom ?? (input.Rooms != null && input.Rooms.Any(r => r.RT60ByBand != null));
            KV(t.Rt60Mode, perRoom ? t.Rt60PerRoom : t.Rt60Global);
            KV(t.SpeechSpectrum, e.SpeechWeightType == SpeechWeightType.Female ? t.SpeechFemale : t.SpeechMale);
            KV(t.RoomShapeModel, e.UseRoomShapeModel ? t.On : t.Off);
            b.Add(kv);

            var table = new TableBlock { Caption = t.BandsCaption, TextScale = 0.9f };
            table.Columns.Add(new TableColumn(t.Band, 2.4f));
            foreach (string l in OctaveBands.Labels) table.Columns.Add(new TableColumn(l + " Hz", 1f, CellAlign.Right));
            table.AddRow(new[] { t.BackgroundNoise }.Concat((e.BackgroundNoiseByBand ?? new double[7]).Select(v => v.ToString("0", Inv))).ToArray());
            table.AddRow(new[] { t.Rt60 + (perRoom ? " (" + t.Entered + ")" : "") }.Concat((e.RT60ByBand ?? new double[7]).Select(v => v.ToString("0.00", Inv))).ToArray());
            b.Add(table);
        }

        static void AddWalls(Blocks b, ReportAreaData area, ReportText t)
        {
            b.Heading(2, t.Walls, false);
            if (area.WallGroups == null || area.WallGroups.Count == 0)
            {
                b.Add(new ParagraphBlock { Text = t.NoWalls, Style = ParagraphStyle.Note });
                return;
            }
            var table = new TableBlock
            {
                TextScale = 0.9f,
                Columns =
                {
                    new TableColumn(t.LineStyle, 2.2f), new TableColumn(t.WallType, 2.6f), new TableColumn(t.Stc, 0.6f, CellAlign.Right),
                    new TableColumn(t.Surface, 1.3f), new TableColumn(t.Height, 0.9f, CellAlign.Right),
                    new TableColumn(t.Segments, 0.8f, CellAlign.Right), new TableColumn(t.Length, 0.9f, CellAlign.Right)
                }
            };
            foreach (WallLineGroup g in area.WallGroups)
            {
                WallTypeInfo wt = g.WallType ?? WallTypeCatalog.Default;
                table.AddRow(g.LineStyleName + (g.IsObstacle ? " (" + t.Obstacle + ")" : ""), wt.DisplayName, wt.StcRating.ToString(Inv),
                    HeatmapMath.WallSurfaceLabel(wt.Surface), g.HeightM > 0 ? g.HeightM.ToString("0.0#", Inv) + " m" : t.FullHeight,
                    g.SegmentCount.ToString(Inv), g.TotalLengthM.ToString("0.0", Inv) + " m");
            }
            b.Add(table);
        }

        static void AddSpeakers(Blocks b, ReportAreaData area, ReportOptions o, ReportText t)
        {
            AcousticJobInput input = area.Input;
            b.Heading(2, t.SpeakerTypes, false);
            if (input.Sources.Count == 0)
            {
                b.Add(new ParagraphBlock { Text = t.NoSpeakers, Style = ParagraphStyle.Note });
                return;
            }

            var types = new TableBlock
            {
                TextScale = 0.86f,
                Columns =
                {
                    new TableColumn(t.FamilyType, 3.0f), new TableColumn(t.Count, 0.7f, CellAlign.Right), new TableColumn(t.Profile, 1.6f),
                    new TableColumn(t.OnAxisLevel, 1.0f, CellAlign.Right), new TableColumn(t.Coverage, 1.6f), new TableColumn(t.Response, 1.6f)
                }
            };
            // Types: the area's speaker groups, else the distinct profiles of the job's sources
            var groups = area.SpeakerGroups ?? new List<SpeakerTypeGroup>();
            var rows = new List<(string Name, SpeakerProfileMapping Map, int Count)>();
            foreach (SpeakerTypeGroup g in groups)
            {
                string name = g.FamilyName + " : " + g.TypeName;
                int n = input.Sources.Count(s => s.TypeName == name);
                if (n == 0 && input.Sources.All(s => string.IsNullOrEmpty(s.TypeName))) n = g.Instances.Count;
                if (n > 0) rows.Add((name, g.Mapping, n));
            }
            if (rows.Count == 0)
                foreach (var grp in input.Sources.GroupBy(s => FirstNonEmpty(s.TypeName, s.Profile?.TypeKey, "–")))
                    rows.Add((grp.Key, grp.First().Profile, grp.Count()));
            foreach (var r in rows)
            {
                SpeakerProfileMapping m = r.Map ?? new SpeakerProfileMapping();
                types.AddRow(r.Name, r.Count.ToString(Inv), ProfileText(m, t), m.OnAxisSplDb.ToString("0.#", Inv) + " dB",
                    CoverageText(m, t), SpeakerResponsePresets.NameFor(m.SpectrumShapeByBand));
            }
            b.Add(types);

            if (!o.IncludeSpeakerInstances) return;
            b.Heading(3, t.SpeakerPositions, false);
            var instances = groups.SelectMany(g => g.Instances).GroupBy(i => i.ElementId).ToDictionary(g => g.Key, g => g.First());
            var pos = new TableBlock
            {
                TextScale = 0.82f,
                Columns =
                {
                    new TableColumn("#", 0.5f), new TableColumn(t.Id, 0.9f, CellAlign.Right), new TableColumn(t.Type, 3.0f),
                    new TableColumn("X m", 0.9f, CellAlign.Right), new TableColumn("Y m", 0.9f, CellAlign.Right),
                    new TableColumn(t.Height, 0.9f, CellAlign.Right), new TableColumn(t.Level, 1.4f),
                    new TableColumn(t.Aim, 0.8f, CellAlign.Right), new TableColumn(t.Line, 0.6f)
                }
            };
            for (int i = 0; i < input.Sources.Count; i++)
            {
                ComputeSource s = input.Sources[i];
                instances.TryGetValue(s.ElementId, out SpeakerInstance inst);
                double hx = s.FacingDirection.X, hy = s.FacingDirection.Y;
                string aim = Math.Sqrt(hx * hx + hy * hy) > 0.5
                    ? ((Math.Atan2(hy, hx) * 180 / Math.PI + 360) % 360).ToString("0", Inv) + "°" : t.Down;
                pos.AddRow(PlanFigureBuilder.SpeakerLabel(i), s.ElementId > 0 ? s.ElementId.ToString(Inv) : "–",
                    FirstNonEmpty(s.TypeName, s.Profile?.TypeKey, "–"),
                    s.Position.X.ToString("0.00", Inv), s.Position.Y.ToString("0.00", Inv),
                    (inst != null ? inst.ElevationFromLevelM : s.Position.Z).ToString("0.00", Inv),
                    inst?.LevelName ?? "", aim, inst?.AbLine ?? "");
            }
            b.Add(pos);
            b.Add(new ParagraphBlock { Text = t.SpeakerTableNote, Style = ParagraphStyle.Caption });
        }

        static string ProfileText(SpeakerProfileMapping m, ReportText t)
        {
            string s;
            switch (m.ProfileSource)
            {
                case ProfileSourceType.SimpleConical: s = t.ProfileCone; break;
                case ProfileSourceType.WallMounted: s = t.ProfileWall; break;
                case ProfileSourceType.GllFile: s = t.ProfileGll; break;
                default: s = t.ProfileOmni; break;
            }
            if (!string.IsNullOrEmpty(m.DirectivityFilePath)) s += ", " + t.ProfilePolar;
            return s;
        }

        static string CoverageText(SpeakerProfileMapping m, ReportText t)
        {
            if (m.ProfileSource == ProfileSourceType.SimpleOmni) return "360°";
            if (!string.IsNullOrEmpty(m.DirectivityFilePath)) return t.ProfilePolar;
            if (m.CoverageAngleByBandDeg != null && m.CoverageAngleByBandDeg.Length >= 4)
                return t.Datasheet + ", 1 kHz " + m.CoverageAngleByBandDeg[3].ToString("0", Inv) + "°";
            return Math.Min(360, 2 * m.ConeHalfAngleDeg).ToString("0", Inv) + "°";
        }

        // ------------------------------------------------------------------ room

        static void AddRoom(Blocks b, AreaAnalysis a, int index, ReportOptions o, ReportText t,
            Dictionary<VisualizationMode, (double, double)> ranges)
        {
            RoomStatistics r = a.Rooms[index];
            RoomAssessment ra = a.Assessments[index];
            AcousticJobOutput output = a.Area.Output;
            b.Heading(3, PlanFigureBuilder.RoomDisplayName(r.Name), false);

            if (o.IncludeRoomDetails)
            {
                var kv = new KeyValueBlock();
                void KV(string k, string v) => kv.Items.Add(new KeyValuePair<string, string>(k, v));
                KV(t.FloorArea.Replace(" m²", ""), r.AreaM2.ToString("0.0", Inv) + " m²");
                KV(t.Volume.Replace(" m³", ""), r.VolumeM3.ToString("0", Inv) + " m³");
                KV(t.Ceiling, r.CeilingHeightM.ToString("0.0#", Inv) + " m");
                KV(t.Enclosure, (r.EnclosureRatio * 100).ToString("0", Inv) + " %");
                KV(t.Points, r.ReceiverCount.ToString(Inv));
                KV(t.Speakers, r.SpeakerCount.ToString(Inv));
                KV(t.Rt60Used, r.Rt60ByBand[2].ToString("0.00", Inv) + " s (" + (r.Rt60Estimated ? t.Estimated : t.Entered) + ")");
                KV(t.Result, CriteriaEvaluator.OutcomeText(ra.Outcome, t));
                b.Add(kv);

                var stats = new TableBlock
                {
                    TextScale = 0.86f,
                    Columns =
                    {
                        new TableColumn(t.Quantity, 2.2f), new TableColumn(t.Mean, 1f, CellAlign.Right), new TableColumn(t.Min, 1f, CellAlign.Right),
                        new TableColumn(t.Max, 1f, CellAlign.Right), new TableColumn(t.Spread, 1f, CellAlign.Right),
                        new TableColumn(t.P10, 1f, CellAlign.Right), new TableColumn(t.P90, 1f, CellAlign.Right)
                    }
                };
                foreach (VisualizationMode m in RoomStatisticsCalculator.SummaryModes)
                {
                    ModeStatistics s = r[m];
                    if (s == null || s.Count == 0 || !o.InTables(m)) continue;
                    var row = stats.AddRow(ReportText.ModeShort(m), ReportText.Format(m, s.Mean), ReportText.Format(m, s.Min),
                        ReportText.Format(m, s.Max), ReportText.Format(m, s.Spread), ReportText.Format(m, s.P10), ReportText.Format(m, s.P90));
                    row.Bold = m == VisualizationMode.SPL_A || m == VisualizationMode.STI;
                }
                if (stats.Rows.Count > 0) b.Add(stats);

                var dist = new DistributionBlock { Caption = t.StiDistribution };
                Rgba[] stiColors = StiCategoryColors;
                for (int k = 0; k < 5; k++)
                {
                    double share = r.ReceiverCount > 0 ? r.StiCategoryCounts[k] / (double)r.ReceiverCount : 0;
                    dist.Segments.Add(new DistributionSegment
                    {
                        Label = t.StiCategoryName(k) + " " + (share * 100).ToString("0", Inv) + " %",
                        Fraction = share,
                        Color = stiColors[k]
                    });
                }
                if (o.InTables(VisualizationMode.STI)) b.Add(dist);

                if (ra.Results.Count > 0)
                {
                    var crit = new TableBlock
                    {
                        Caption = t.CriteriaHeading,
                        TextScale = 0.86f,
                        Columns =
                        {
                            new TableColumn(t.Criterion, 3.2f), new TableColumn(t.Target, 1.2f, CellAlign.Right),
                            new TableColumn(t.Value, 1.4f, CellAlign.Right), new TableColumn(t.Result, 1.1f)
                        }
                    };
                    foreach (CriterionResult c in ra.Results)
                    {
                        var row = crit.AddRow(c.Name, c.Target, c.Value, c.Passed ? t.Pass : t.Fail);
                        row.Status = new[] { CellStatus.None, CellStatus.None, CellStatus.None, c.Passed ? CellStatus.Pass : CellStatus.Fail };
                    }
                    b.Add(crit);
                }
            }

            if (o.IncludeValueCharts)
            {
                double spacing = HeatmapMath.ViewerGridSpacing(output.Results);
                foreach (VisualizationMode m in (o.ValueChartModes ?? new List<VisualizationMode>()).Distinct())
                {
                    (double, double) range = ranges.TryGetValue(m, out var rr) ? rr : PlanFigureBuilder.Range(output, m);
                    PlanFigure fig = PlanFigureBuilder.ForRoom(a.Area.Input, output, r, m, range, true, false);
                    b.Add(new FigureBlock
                    {
                        Figure = fig,
                        MaxHeightFraction = 0.5f,
                        Caption = string.Format(Inv, t.ValueChart, t.ModeName(m)) + ". " +
                                  string.Format(Inv, t.ValueChartCaptionAll, ReportText.ModeShort(m), spacing),
                        CaptionWhenThinned = string.Format(Inv, t.ValueChart, t.ModeName(m)) + ". " +
                                  string.Format(Inv, t.ValueChartCaption, ReportText.ModeShort(m), spacing, "{0}")
                    });
                }
            }

            if (o.IncludeSpeakerContributions)
            {
                if (a.Area.Input.Sources.Count > SpeakerContributionCalculator.MaxSources)
                {
                    if (index == 0)
                        b.Add(new ParagraphBlock { Style = ParagraphStyle.Note, Text = string.Format(Inv, t.ContributionsSkipped, SpeakerContributionCalculator.MaxSources) });
                }
                else if (a.Contributions.TryGetValue(r.RoomIndex, out RoomContributions rc) && rc.Items.Count > 0)
                {
                    var table = new TableBlock
                    {
                        Caption = t.Contributions,
                        TextScale = 0.86f,
                        Columns =
                        {
                            new TableColumn(t.Speaker, 0.7f), new TableColumn(t.Type, 3.0f), new TableColumn(t.Distance, 1f, CellAlign.Right),
                            new TableColumn("dBA", 0.9f, CellAlign.Right), new TableColumn(t.Share, 0.9f, CellAlign.Right)
                        }
                    };
                    foreach (SpeakerContribution c in rc.Items.Take(8))
                        table.AddRow(c.Label, FirstNonEmpty(c.TypeName, "–"), c.DistanceM.ToString("0.0", Inv) + " m",
                            c.LevelDbA.ToString("0.0", Inv), (c.Share * 100).ToString("0", Inv) + " %");
                    b.Add(table);
                    b.Add(new ParagraphBlock
                    {
                        Style = ParagraphStyle.Caption,
                        Text = string.Format(Inv, t.ContributionsIntro, rc.Point.X, rc.Point.Y, rc.TotalDbA)
                    });
                }
            }
        }

        /// <summary>Colours of the STI categories Bad … Excellent (red → green, as the viewer's gradient).</summary>
        public static readonly Rgba[] StiCategoryColors =
        {
            new Rgba(0xCC, 0x00, 0x00), new Rgba(0xFF, 0x77, 0x00), new Rgba(0xFF, 0xCC, 0x00),
            new Rgba(0x88, 0xDD, 0x00), new Rgba(0x00, 0xAA, 0x00)
        };

        // ------------------------------------------------------------------ criteria / method

        static void AddCriteria(Blocks b, ReportCriteria c, ReportText t)
        {
            b.Heading(1, t.Criteria, true);
            b.Add(new ParagraphBlock { Text = t.CriteriaIntro });
            var table = new TableBlock
            {
                Columns = { new TableColumn(t.Criterion, 3.4f), new TableColumn(t.Target, 1.4f, CellAlign.Right) }
            };
            if (c.CheckStiMin) table.AddRow(t.CritStiMin, "≥ " + c.StiMin.ToString("0.00", Inv));
            if (c.CheckStiMean) table.AddRow(t.CritStiMean, "≥ " + c.StiMeanMin.ToString("0.00", Inv));
            if (c.CheckSplSpread) table.AddRow(t.CritSplSpread, "≤ " + c.SplSpreadMaxDb.ToString("0.#", Inv) + " dB");
            if (c.CheckSplMinA) table.AddRow(t.CritSplMinA, "≥ " + c.SplMinDbA.ToString("0.#", Inv) + " dBA");
            if (c.CheckSignalToNoise) table.AddRow(t.CritSnr, "≥ " + c.SignalToNoiseMinDb.ToString("0.#", Inv) + " dB");
            if (table.Rows.Count == 0) table.AddRow(t.NoCriteria, "–");
            b.Add(table);
            if (c.PointCoverage < 1.0)
                b.Add(new ParagraphBlock { Style = ParagraphStyle.Note, Text = string.Format(Inv, t.CritCoverage, c.PointCoverage * 100, (1 - c.PointCoverage) * 100) });
            if (c.SkipRoomsWithoutSpeakers)
                b.Add(new ParagraphBlock { Style = ParagraphStyle.Note, Text = t.CritSkipNoSpeakers });
            b.Add(new ParagraphBlock { Style = ParagraphStyle.Note, Text = t.CritStandard });

            var scale = new TableBlock
            {
                Caption = t.StiScale,
                Columns = { new TableColumn(t.StiClass, 2f), new TableColumn("STI", 2f, CellAlign.Right) }
            };
            string[] ranges = { "< 0.30", "0.30 – 0.45", "0.45 – 0.60", "0.60 – 0.75", "≥ 0.75" };
            for (int k = 0; k < 5; k++) scale.AddRow(t.StiCategoryName(k), ranges[k]);
            b.Add(scale);
        }

        static void AddMethod(Blocks b, ReportText t)
        {
            b.Heading(1, t.Method, true);
            foreach (string para in t.MethodParagraphs)
                b.Add(new ParagraphBlock { Text = para });
        }

        // ------------------------------------------------------------------ CSV

        /// <summary>
        /// The summary as CSV, one row per room, with the quantities the options put in the tables (all when
        /// <paramref name="options"/> is null), in <paramref name="culture"/>'s number format and list separator (so a
        /// spreadsheet in that locale opens it in columns).
        /// </summary>
        public static string SummaryCsv(List<AreaAnalysis> areas, ReportText t, CultureInfo culture, ReportOptions options = null)
        {
            ReportOptions o = options ?? new ReportOptions();
            string sep = Separator(culture);
            var sb = new StringBuilder();
            string N(double v, string fmt) => double.IsNaN(v) ? "" : v.ToString(fmt, culture);
            string Q(string s) => s == null ? "" : (s.Contains(sep) || s.Contains("\"") || s.Contains("\n")) ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

            var head = new List<string> { t.Area, t.Room, t.FloorArea, t.Volume, t.Speakers, t.Points };
            var cells = new List<Func<RoomStatistics, string>>();
            void Mode(VisualizationMode m, string name, string fmt)
            {
                if (!o.InTables(m)) return;
                head.AddRange(new[] { name + " mean", name + " min", name + " max" });
                cells.Add(r => N(r[m].Mean, fmt));
                cells.Add(r => N(r[m].Min, fmt));
                cells.Add(r => N(r[m].Max, fmt));
            }
            Mode(VisualizationMode.SPL, "SPL dB", "0.0");
            Mode(VisualizationMode.SPL_A, "SPL dBA", "0.0");
            Mode(VisualizationMode.STI, "STI", "0.00");
            Mode(VisualizationMode.C80, "C80 dB", "0.0");
            head.Add("RT60 500 Hz s");
            head.Add(t.Result);
            sb.AppendLine(string.Join(sep, head.Select(Q)));
            foreach (AreaAnalysis a in areas)
                for (int i = 0; i < a.Rooms.Count; i++)
                {
                    RoomStatistics r = a.Rooms[i];
                    var row = new List<string>
                    {
                        Q(a.Area.AreaName), Q(PlanFigureBuilder.RoomDisplayName(r.Name)), N(r.AreaM2, "0.0"), N(r.VolumeM3, "0"),
                        r.SpeakerCount.ToString(culture), r.ReceiverCount.ToString(culture)
                    };
                    row.AddRange(cells.Select(c => c(r)));
                    row.Add(N(r.Rt60ByBand[2], "0.00"));
                    row.Add(Q(CriteriaEvaluator.OutcomeText(a.Assessments[i].Outcome, t)));
                    sb.AppendLine(string.Join(sep, row));
                }
            return sb.ToString();
        }

        /// <summary>Every grid point of the included rooms as CSV (area, room, x, y and the chosen quantities).</summary>
        public static string ValuesCsv(List<AreaAnalysis> areas, ReportText t, CultureInfo culture, ReportOptions options = null)
        {
            ReportOptions o = options ?? new ReportOptions();
            string sep = Separator(culture);
            var cols = new List<(string Name, Func<ReceiverResult, string> Cell)>();
            if (o.InTables(VisualizationMode.SPL)) cols.Add(("SPL dB", x => x.SplDb.ToString("0.0", culture)));
            if (o.InTables(VisualizationMode.SPL_A)) cols.Add(("SPL dBA", x => x.SplDbA.ToString("0.0", culture)));
            if (o.InTables(VisualizationMode.STI)) cols.Add(("STI", x => x.Sti.ToString("0.00", culture)));
            if (o.InTables(VisualizationMode.C80))
            {
                cols.Add(("C80 dB", x => x.C80Db.ToString("0.0", culture)));
                cols.Add(("D50", x => x.D50.ToString("0.00", culture)));
            }
            if (o.IncludeOctaveBands)
                for (int k = 0; k < OctaveBands.Count; k++)
                {
                    int band = k;
                    cols.Add(("SPL " + OctaveBands.Labels[k] + " Hz",
                        x => (x.SplDbByBand != null && x.SplDbByBand.Length > band ? x.SplDbByBand[band] : double.NaN).ToString("0.0", culture)));
                }

            var sb = new StringBuilder();
            sb.AppendLine(string.Join(sep, new[] { t.Area, t.Room, "X m", "Y m" }.Concat(cols.Select(c => c.Name))));
            foreach (AreaAnalysis a in areas)
                foreach (RoomStatistics r in a.Rooms)
                    foreach (int i in r.ResultIndices)
                    {
                        ReceiverResult x = a.Area.Output.Results[i];
                        var cells = new List<string>
                        {
                            a.Area.AreaName.Replace(sep, " "), PlanFigureBuilder.RoomDisplayName(r.Name).Replace(sep, " "),
                            x.Position.X.ToString("0.00", culture), x.Position.Y.ToString("0.00", culture)
                        };
                        cells.AddRange(cols.Select(c => c.Cell(x)));
                        sb.AppendLine(string.Join(sep, cells));
                    }
            return sb.ToString();
        }

        /// <summary>The culture's list separator, or ';' when it clashes with the decimal separator.</summary>
        static string Separator(CultureInfo culture)
        {
            string sep = culture.TextInfo.ListSeparator;
            return string.IsNullOrEmpty(sep) || sep == culture.NumberFormat.NumberDecimalSeparator ? ";" : sep;
        }

        // ------------------------------------------------------------------ helpers

        static string LevelOf(ReportAreaData area) =>
            area.SpeakerGroups?.SelectMany(g => g.Instances).Select(i => i.LevelName).FirstOrDefault(n => !string.IsNullOrEmpty(n)) ?? "";

        static string F1(double v) => double.IsNaN(v) ? "–" : v.ToString("0.0", Inv);
        static string F2(double v) => double.IsNaN(v) ? "–" : v.ToString("0.00", Inv);

        static string FirstNonEmpty(params string[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";

        static string Join(string sep, params string[] parts) => string.Join(sep, parts.Where(x => !string.IsNullOrWhiteSpace(x)));

        /// <summary>Adds blocks and gives headings their anchors.</summary>
        class Blocks
        {
            readonly ReportDocument _doc;
            int _anchor;

            public Blocks(ReportDocument doc) { _doc = doc; }

            public void Add(ReportBlock block) => _doc.Blocks.Add(block);

            public void Heading(int level, string text, bool pageBreak) =>
                _doc.Blocks.Add(new HeadingBlock { Level = level, Text = text, PageBreakBefore = pageBreak, Anchor = "h" + (++_anchor).ToString(Inv) });
        }
    }
}
