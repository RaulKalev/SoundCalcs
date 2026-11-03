using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SoundCalcs.Domain;
using SoundCalcs.IO;
using SoundCalcs.Report;
using SoundCalcs.Report.Pdf;

namespace SoundCalcs.UI.ViewModels
{
    /// <summary>A room in the report dialog's tree.</summary>
    public class ReportRoomNode : INotifyPropertyChanged
    {
        private readonly ReportAreaNode _area;
        private bool _isChecked = true;

        public ReportRoomNode(ReportAreaNode area, string roomName)
        {
            _area = area;
            RoomName = roomName;
            Name = PlanFigureBuilder.RoomDisplayName(roomName);
        }

        /// <summary>The room's full name in the job (the exclusion key).</summary>
        public string RoomName { get; }
        public string Name { get; }

        /// <summary>"48 m² · STI 0.62".</summary>
        public string Summary { get; private set; } = "";

        /// <summary>Pass / Fail / Not assessed against the project's criteria.</summary>
        public string Outcome { get; private set; } = "";
        public bool Passed { get; private set; }
        public bool Failed { get; private set; }

        public bool IsChecked
        {
            get => _isChecked;
            set
            {
                if (_isChecked == value) return;
                _isChecked = value;
                OnPropertyChanged(nameof(IsChecked));
                _area.OnRoomChecked();
            }
        }

        internal void SetChecked(bool value)
        {
            _isChecked = value;
            OnPropertyChanged(nameof(IsChecked));
        }

        internal void Update(RoomStatistics stats, RoomAssessment assessment, ReportText t)
        {
            ModeStatistics sti = stats[SoundCalcs.UI.ViewModels.VisualizationMode.STI];
            Summary = $"{stats.AreaM2:F0} m² · STI {sti?.Mean ?? double.NaN:F2}";
            Outcome = assessment == null ? "" : CriteriaEvaluator.OutcomeText(assessment.Outcome, t);
            Passed = assessment?.Outcome == RoomOutcome.Pass;
            Failed = assessment?.Outcome == RoomOutcome.Fail;
            foreach (string p in new[] { nameof(Summary), nameof(Outcome), nameof(Passed), nameof(Failed) }) OnPropertyChanged(p);
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>An area in the report dialog's tree: whether it is in the report and its rooms.</summary>
    public class ReportAreaNode : INotifyPropertyChanged
    {
        private readonly ReportViewModel _owner;
        private bool _isChecked;

        public ReportAreaNode(ReportViewModel owner, ReportAreaData data, bool isChecked)
        {
            _owner = owner;
            Data = data;
            _isChecked = isChecked && data.HasResults;
        }

        public ReportAreaData Data { get; private set; }
        public string Id => Data.AreaId;
        public string Name => Data.AreaName;
        public bool HasResults => Data.HasResults;
        public bool IsOutdated => Data.HasResults && Data.Status == AreaResultStatus.Outdated;
        public bool IsCurrent => Data.HasResults && Data.Status == AreaResultStatus.Current;
        public bool NeedsCalculation => !IsCurrent;

        public string StatusText =>
            !Data.HasResults ? "Not calculated"
            : Data.Status == AreaResultStatus.Outdated ? "Changed since calculated"
            : "Calculated " + Data.Output.Timestamp.ToLocalTime().ToString("d MMM HH:mm", CultureInfo.CurrentCulture) + ", " + Data.Output.Quality;

        public ObservableCollection<ReportRoomNode> Rooms { get; } = new ObservableCollection<ReportRoomNode>();

        /// <summary>In the report (only areas with results can be).</summary>
        public bool IsChecked
        {
            get => _isChecked;
            set
            {
                bool v = value && HasResults;
                if (_isChecked == v) return;
                _isChecked = v;
                OnPropertyChanged(nameof(IsChecked));
                // Ticking an area with every room unticked brings its rooms back
                if (v && Rooms.All(r => !r.IsChecked)) foreach (ReportRoomNode r in Rooms) r.SetChecked(true);
                _owner.OnSelectionChanged();
            }
        }

        internal void OnRoomChecked()
        {
            bool any = Rooms.Any(r => r.IsChecked);
            if (_isChecked != any && HasResults)
            {
                _isChecked = any;
                OnPropertyChanged(nameof(IsChecked));
            }
            _owner.OnSelectionChanged();
        }

        internal void Reload(ReportAreaData data, IEnumerable<string> excludedRooms)
        {
            Data = data;
            var excluded = new HashSet<string>(excludedRooms);
            Rooms.Clear();
            if (data.HasResults)
                foreach (RoomStatistics r in RoomStatisticsCalculator.Compute(data.Input, data.Output))
                {
                    var node = new ReportRoomNode(this, r.Name);
                    node.SetChecked(!excluded.Contains(ReportOptions.RoomKey(Id, r.Name)));
                    Rooms.Add(node);
                }
            if (!data.HasResults) _isChecked = false;
            foreach (string p in new[] { nameof(Name), nameof(HasResults), nameof(IsOutdated), nameof(IsCurrent), nameof(NeedsCalculation), nameof(StatusText), nameof(IsChecked) })
                OnPropertyChanged(p);
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>
    /// The report dialog: which areas and rooms, what to include, criteria and project info (stored with the project),
    /// a preview of the pages and the export. The report is rebuilt in the background a moment after each change.
    /// </summary>
    public class ReportViewModel : INotifyPropertyChanged, IDisposable
    {
        private readonly MainViewModel _main;
        private readonly SoundCalcsProject _project;
        private readonly System.Windows.Threading.DispatcherTimer _previewTimer;
        private readonly Dictionary<string, Dictionary<int, RoomContributions>> _contributions =
            new Dictionary<string, Dictionary<int, RoomContributions>>();
        private CancellationTokenSource _previewCts;
        private bool _loading = true;

        public ReportViewModel(MainViewModel main)
        {
            _main = main;
            _project = main.Project;
            _previewTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _previewTimer.Tick += (s, e) => { _previewTimer.Stop(); BuildPreview(); };
            _main.PropertyChanged += OnMainChanged;

            ReloadAreas();
            _loading = false;
            RefreshAssessments();
            RequestPreview();
        }

        public MainViewModel Main => _main;
        public ProjectInfo Info => _project.ProjectInfo;
        public ReportCriteria Criteria => _project.Criteria;
        public ReportOptions Options => _project.ReportOptions;

        public ObservableCollection<ReportAreaNode> Areas { get; } = new ObservableCollection<ReportAreaNode>();

        /// <summary>The cover logo (empty = none).</summary>
        public string LogoPath
        {
            get => Info.LogoPath ?? "";
            set
            {
                Info.LogoPath = value ?? "";
                OnPropertyChanged(nameof(LogoPath));
                OnOptionChanged();
            }
        }

        // ------------------------------------------------------------------ tree

        /// <summary>Reads every area's last job again (after a calculation) and rebuilds the tree.</summary>
        public void ReloadAreas()
        {
            List<ReportAreaData> data = _main.BuildReportAreas();
            var excludedAreas = new HashSet<string>(Options.ExcludedAreaIds ?? new List<string>());
            var byId = Areas.ToDictionary(a => a.Id);
            Areas.Clear();
            foreach (ReportAreaData d in data)
            {
                ReportAreaNode node = byId.TryGetValue(d.AreaId, out ReportAreaNode old) ? old : new ReportAreaNode(this, d, !excludedAreas.Contains(d.AreaId));
                node.Reload(d, Options.ExcludedRooms ?? new List<string>());
                if (d.HasResults && !byId.ContainsKey(d.AreaId)) node.IsChecked = !excludedAreas.Contains(d.AreaId);
                Areas.Add(node);
            }
            OnPropertyChanged(nameof(CanCalculate));
            OnPropertyChanged(nameof(HasAnyResults));
            OnPropertyChanged(nameof(CalculateLabel));
        }

        internal void OnSelectionChanged()
        {
            if (_loading) return;
            Options.ExcludedAreaIds = Areas.Where(a => a.HasResults && !a.IsChecked).Select(a => a.Id).ToList();
            // Exclusions of rooms in areas that are not shown any more are kept (the area may come back)
            var shownAreas = new HashSet<string>(Areas.Select(a => a.Id));
            Options.ExcludedRooms = (Options.ExcludedRooms ?? new List<string>())
                .Where(k => !shownAreas.Contains(k.Split('|')[0]))
                .Concat(Areas.SelectMany(a => a.Rooms.Where(r => !r.IsChecked).Select(r => ReportOptions.RoomKey(a.Id, r.RoomName))))
                .Distinct().ToList();
            OnPropertyChanged(nameof(CanExport));
            OnOptionChanged();
        }

        public bool HasAnyResults => Areas.Any(a => a.HasResults);

        public bool CanExport => !_main.IsRunningAll && Areas.Any(a => a.IsChecked && a.Rooms.Any(r => r.IsChecked));

        // ------------------------------------------------------------------ calculate from the dialog

        /// <summary>Areas that are not calculated or changed since: what "Calculate" runs.</summary>
        private List<ReportAreaNode> Stale => Areas.Where(a => a.NeedsCalculation).ToList();

        public bool CanCalculate => !_main.IsRunning && !_main.IsRunningAll && Stale.Count > 0;

        public string CalculateLabel => Stale.Count == 1 ? "Calculate 1 area" : $"Calculate {Stale.Count} areas";

        public void CalculateStale()
        {
            if (!CanCalculate) return;
            _main.RunAreas(Stale.Select(a => a.Id), CalculationQuality.Full);
            OnPropertyChanged(nameof(CanCalculate));
            OnPropertyChanged(nameof(CanExport));
        }

        private void OnMainChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MainViewModel.IsRunningAll) || e.PropertyName == nameof(MainViewModel.IsRunning))
            {
                OnPropertyChanged(nameof(CanCalculate));
                OnPropertyChanged(nameof(CanExport));
                if (!_main.IsRunningAll && !_main.IsRunning)
                {
                    _loading = true;
                    ReloadAreas();
                    _loading = false;
                    RefreshAssessments();
                    RequestPreview();
                }
            }
        }

        // ------------------------------------------------------------------ options as toggles

        private bool Has(List<VisualizationMode> list, VisualizationMode m) => list != null && list.Contains(m);

        private void Set(List<VisualizationMode> list, VisualizationMode m, bool on, string property)
        {
            if (on && !list.Contains(m)) list.Add(m);
            if (!on) list.Remove(m);
            // Keep the order of the quantities fixed (plans and charts appear in this order)
            var order = new[] { VisualizationMode.SPL_A, VisualizationMode.SPL, VisualizationMode.STI, VisualizationMode.C80 };
            list.Sort((a, b) => Array.IndexOf(order, a).CompareTo(Array.IndexOf(order, b)));
            OnPropertyChanged(property);
            OnOptionChanged();
        }

        public bool PlanSplA { get => Has(Options.PlanModes, VisualizationMode.SPL_A); set => Set(Options.PlanModes, VisualizationMode.SPL_A, value, nameof(PlanSplA)); }
        public bool PlanSpl { get => Has(Options.PlanModes, VisualizationMode.SPL); set => Set(Options.PlanModes, VisualizationMode.SPL, value, nameof(PlanSpl)); }
        public bool PlanSti { get => Has(Options.PlanModes, VisualizationMode.STI); set => Set(Options.PlanModes, VisualizationMode.STI, value, nameof(PlanSti)); }
        public bool PlanC80 { get => Has(Options.PlanModes, VisualizationMode.C80); set => Set(Options.PlanModes, VisualizationMode.C80, value, nameof(PlanC80)); }
        public bool ChartSplA { get => Has(Options.ValueChartModes, VisualizationMode.SPL_A); set => Set(Options.ValueChartModes, VisualizationMode.SPL_A, value, nameof(ChartSplA)); }
        public bool ChartSpl { get => Has(Options.ValueChartModes, VisualizationMode.SPL); set => Set(Options.ValueChartModes, VisualizationMode.SPL, value, nameof(ChartSpl)); }
        public bool ChartSti { get => Has(Options.ValueChartModes, VisualizationMode.STI); set => Set(Options.ValueChartModes, VisualizationMode.STI, value, nameof(ChartSti)); }
        public bool ChartC80 { get => Has(Options.ValueChartModes, VisualizationMode.C80); set => Set(Options.ValueChartModes, VisualizationMode.C80, value, nameof(ChartC80)); }

        public bool LanguageEnglish
        {
            get => Options.Language == ReportLanguage.English;
            set { if (value) { Options.Language = ReportLanguage.English; LanguageChanged(); } }
        }

        public bool LanguageEstonian
        {
            get => Options.Language == ReportLanguage.Estonian;
            set { if (value) { Options.Language = ReportLanguage.Estonian; LanguageChanged(); } }
        }

        private void LanguageChanged()
        {
            OnPropertyChanged(nameof(LanguageEnglish));
            OnPropertyChanged(nameof(LanguageEstonian));
            RefreshAssessments();
            OnOptionChanged();
        }

        public bool PageA4
        {
            get => Options.PageFormat == ReportPageFormat.A4;
            set { if (value) { Options.PageFormat = ReportPageFormat.A4; OnPropertyChanged(nameof(PageA4)); OnPropertyChanged(nameof(PageA3)); OnOptionChanged(); } }
        }

        public bool PageA3
        {
            get => Options.PageFormat == ReportPageFormat.A3;
            set { if (value) { Options.PageFormat = ReportPageFormat.A3; OnPropertyChanged(nameof(PageA4)); OnPropertyChanged(nameof(PageA3)); OnOptionChanged(); } }
        }

        /// <summary>Point coverage as a percentage for the field (the criteria keep a share 0..1).</summary>
        public double PointCoveragePercent
        {
            get => Math.Round(Criteria.PointCoverage * 100, 1);
            set { Criteria.PointCoverage = Math.Max(50, Math.Min(100, value)) / 100.0; OnPropertyChanged(nameof(PointCoveragePercent)); OnCriteriaChanged(); }
        }

        /// <summary>Called by the dialog when any bound option, criterion or project field changed.</summary>
        public void OnOptionChanged()
        {
            if (_loading) return;
            _main.SaveProject();
            RequestPreview();
        }

        public void OnCriteriaChanged()
        {
            if (_loading) return;
            RefreshAssessments();
            OnOptionChanged();
        }

        /// <summary>Pass / fail per room in the tree, against the current criteria.</summary>
        public void RefreshAssessments()
        {
            var req = Request();
            req.Options = Options.Clone();
            req.Options.ExcludedAreaIds = new List<string>();
            req.Options.ExcludedRooms = new List<string>();
            var t = new ReportText(Options.Language);
            foreach (AreaAnalysis a in ReportBuilder.Analyse(req))
            {
                ReportAreaNode node = Areas.FirstOrDefault(n => n.Id == a.Area.AreaId);
                if (node == null) continue;
                for (int i = 0; i < a.Rooms.Count; i++)
                    node.Rooms.FirstOrDefault(r => r.RoomName == a.Rooms[i].Name)?.Update(a.Rooms[i], a.Assessments[i], t);
            }
        }

        private ReportRequest Request() => new ReportRequest
        {
            Project = Info,
            Criteria = Criteria,
            Options = Options,
            Areas = Areas.Select(a => a.Data).ToList(),
            ModelName = _project.ProjectName,
            PluginVersion = typeof(ReportViewModel).Assembly.GetName().Version?.ToString(3) ?? "",
            Date = DateTime.Now,
            ContributionCache = _contributions
        };

        // ------------------------------------------------------------------ preview

        private SkiaReportPainter _painter;
        private int _pageIndex;
        private bool _isBuilding;
        private string _previewError = "";

        /// <summary>The laid-out report shown in the preview; null until the first build.</summary>
        public SkiaReportPainter Painter => _painter;

        public int PageIndex
        {
            get => _pageIndex;
            set
            {
                int n = _painter?.PageCount ?? 0;
                int v = Math.Max(0, Math.Min(Math.Max(0, n - 1), value));
                if (v == _pageIndex) return;
                _pageIndex = v;
                OnPropertyChanged(nameof(PageIndex));
                OnPropertyChanged(nameof(PageLabel));
                PreviewChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public string PageLabel => _painter == null ? "" : $"Page {_pageIndex + 1} of {_painter.PageCount}";

        public bool IsBuilding
        {
            get => _isBuilding;
            private set { _isBuilding = value; OnPropertyChanged(nameof(IsBuilding)); }
        }

        public string PreviewError
        {
            get => _previewError;
            private set { _previewError = value ?? ""; OnPropertyChanged(nameof(PreviewError)); }
        }

        /// <summary>Raised on the UI thread when the preview page or report changed.</summary>
        public event EventHandler PreviewChanged;

        public void RequestPreview()
        {
            _previewTimer.Stop();
            _previewTimer.Start();
        }

        private void BuildPreview()
        {
            _previewCts?.Cancel();
            var cts = new CancellationTokenSource();
            _previewCts = cts;
            ReportRequest req = Request();
            req.Options = Options.Clone();
            req.Project = Info.Clone();
            req.Criteria = Criteria.Clone();
            if (ReportBuilder.IncludedAreas(req).Count == 0)
            {
                ReplacePainter(null);
                PreviewError = HasAnyResults ? "Tick at least one area." : "No area has results yet: calculate an area first.";
                return;
            }
            IsBuilding = true;
            var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            Task.Run(() =>
            {
                ReportDocument doc = ReportBuilder.Build(req, cts.Token).Document;
                var painter = new SkiaReportPainter(doc);
                int pages = painter.PageCount;   // lays out here, off the UI thread
                return painter;
            }, cts.Token).ContinueWith(task =>
            {
                dispatcher.BeginInvoke(new Action(() =>
                {
                    if (cts != _previewCts) { if (task.Status == TaskStatus.RanToCompletion) task.Result.Dispose(); return; }
                    IsBuilding = false;
                    if (task.Status == TaskStatus.RanToCompletion)
                    {
                        PreviewError = "";
                        ReplacePainter(task.Result);
                    }
                    else if (task.IsFaulted)
                    {
                        FileLogger.Log($"Report preview failed: {task.Exception}");
                        PreviewError = "The preview could not be built: " + task.Exception?.GetBaseException().Message;
                    }
                }));
            });
        }

        private void ReplacePainter(SkiaReportPainter painter)
        {
            _painter?.Dispose();
            _painter = painter;
            _pageIndex = Math.Min(_pageIndex, Math.Max(0, (painter?.PageCount ?? 1) - 1));
            OnPropertyChanged(nameof(Painter));
            OnPropertyChanged(nameof(PageIndex));
            OnPropertyChanged(nameof(PageLabel));
            PreviewChanged?.Invoke(this, EventArgs.Empty);
        }

        // ------------------------------------------------------------------ export

        /// <summary>Default file name: "Model – Acoustic report – 2026-11-03.pdf".</summary>
        public string DefaultFileName()
        {
            string stem = string.IsNullOrWhiteSpace(Info.ProjectName) ? _project.ProjectName : Info.ProjectName;
            string title = new ReportText(Options.Language).ReportTitle;
            string name = $"{stem} – {title} – {DateTime.Now:yyyy-MM-dd}.pdf";
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name;
        }

        /// <summary>The folder offered for saving: the last one used, else the model's folder.</summary>
        public string DefaultFolder()
        {
            if (!string.IsNullOrEmpty(_project.LastReportFolder) && Directory.Exists(_project.LastReportFolder)) return _project.LastReportFolder;
            try
            {
                string dir = Path.GetDirectoryName(_project.ProjectKey ?? "");
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) return dir;
            }
            catch (ArgumentException) { }
            return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        }

        /// <summary>
        /// Writes the PDF (and, with <paramref name="csv"/>, the room summary and grid values as CSV next to it).
        /// Runs on a background thread; returns the page count.
        /// </summary>
        public Task<int> ExportAsync(string pdfPath, bool csv)
        {
            ReportRequest req = Request();
            req.Options = Options.Clone();
            req.Project = Info.Clone();
            req.Criteria = Criteria.Clone();
            _project.LastReportFolder = Path.GetDirectoryName(pdfPath) ?? "";
            _main.SaveProject();
            CultureInfo culture = CultureInfo.CurrentCulture;
            return Task.Run(() =>
            {
                ReportBuildResult built = ReportBuilder.Build(req);
                int pages = PdfReportWriter.Write(built.Document, pdfPath);
                if (csv)
                {
                    var t = new ReportText(req.Options.Language);
                    string stem = Path.Combine(Path.GetDirectoryName(pdfPath) ?? "", Path.GetFileNameWithoutExtension(pdfPath));
                    // UTF-8 with a byte order mark, so a spreadsheet reads ä, õ and ² correctly
                    File.WriteAllText(stem + " – rooms.csv", ReportBuilder.SummaryCsv(built.Areas, t, culture), new System.Text.UTF8Encoding(true));
                    File.WriteAllText(stem + " – grid values.csv", ReportBuilder.ValuesCsv(built.Areas, t, culture), new System.Text.UTF8Encoding(true));
                }
                return pages;
            });
        }

        public void Dispose()
        {
            _previewTimer.Stop();
            _previewCts?.Cancel();
            _main.PropertyChanged -= OnMainChanged;
            _painter?.Dispose();
            _painter = null;
            _main.SaveProject();
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
