using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using SoundCalcs.Compute;
using SoundCalcs.Domain;
using SoundCalcs.IO;
using SoundCalcs.Revit;
using SoundCalcs.Visualization;

namespace SoundCalcs.UI.ViewModels
{
    public enum VisualizationMode
    {
        SPL,
        SPL_A,
        STI,
        C80,
        SPL_125,
        SPL_250,
        SPL_500,
        SPL_1k,
        SPL_2k,
        SPL_4k,
        SPL_8k
    }
    public class MainViewModel : INotifyPropertyChanged
    {
        private readonly UIApplication _uiApp;
        private readonly RevitApiDispatcher _dispatcher;
        private readonly JobRunner _jobRunner;
        private readonly FilledRegionRenderer _renderer;

        public MainViewModel(UIApplication uiApp)
        {
            _uiApp = uiApp;
            _dispatcher = new RevitApiDispatcher();
            _jobRunner = new JobRunner();
            _renderer = new FilledRegionRenderer();

            _jobRunner.JobCompleted += OnJobCompleted;

            // Load persisted settings
            PluginSettings settings = SettingsStore.Load();
            _selectedLink = settings.LinkSelection;
            GridSpacing = settings.AnalysisSettings.GridSpacingM;
            ReceiverHeight = settings.AnalysisSettings.ReceiverHeightM;
            BoundaryOffset = settings.AnalysisSettings.BoundaryOffsetM;
            _speakerCategoryName = settings.AnalysisSettings.SpeakerCategoryName;
            UseMinSplThreshold = settings.AnalysisSettings.UseMinSplThreshold;
            MinSplThreshold = settings.AnalysisSettings.MinSplThresholdDb;
            LoadOctaveBandSettings(settings.AnalysisSettings);
            _savedMappings = settings.SpeakerMappings;

            // Restore saved speaker groups (instances + mappings)
            if (settings.SavedSpeakerGroups != null && settings.SavedSpeakerGroups.Count > 0)
            {
                foreach (var grp in settings.SavedSpeakerGroups)
                {
                    var vm = new SpeakerGroupViewModel(grp);
                    // Prefer the mapping embedded in the group; fall back to legacy SpeakerMappings list
                    SpeakerProfileMapping legacy = _savedMappings
                        .FirstOrDefault(m => m.TypeKey == grp.TypeKey);
                    if (legacy != null) vm.ApplyMapping(legacy);
                    _speakerGroups.Add(vm);
                    foreach (var inst in grp.Instances)
                        if (!_pickedSpeakerIds.Contains(inst.ElementId))
                            _pickedSpeakerIds.Add(inst.ElementId);
                }
                OnPropertyChanged(nameof(PickedSpeakerSummary));
            }

            // Restore saved wall groups — normalise WallType to catalog reference so ComboBox binding works
            foreach (var grp in settings.WallGroups)
            {
                grp.WallType = WallTypeCatalog.FindByKey(grp.WallType?.Key ?? "");
                WallLineGroups.Add(new WallLineGroupViewModel(grp));
            }
            foreach (var room in settings.BoundaryRooms)
                DetectedRooms.Add(room);
        }

        // ========================= MODEL TAB =========================

        private ObservableCollection<LinkSelection> _availableLinks = new ObservableCollection<LinkSelection>();
        public ObservableCollection<LinkSelection> AvailableLinks
        {
            get => _availableLinks;
            set { _availableLinks = value; OnPropertyChanged(nameof(AvailableLinks)); }
        }

        private LinkSelection _selectedLink = new LinkSelection();
        public LinkSelection SelectedLink
        {
            get => _selectedLink;
            set { _selectedLink = value ?? new LinkSelection(); OnPropertyChanged(nameof(SelectedLink)); }
        }

        private string _speakerCategoryName = "OST_DataDevices";
        public string SpeakerCategoryName
        {
            get => _speakerCategoryName;
            set { _speakerCategoryName = value; OnPropertyChanged(nameof(SpeakerCategoryName)); }
        }

        private ObservableCollection<RoomPolygon> _detectedRooms = new ObservableCollection<RoomPolygon>();
        public ObservableCollection<RoomPolygon> DetectedRooms
        {
            get => _detectedRooms;
            set { _detectedRooms = value; OnPropertyChanged(nameof(DetectedRooms)); }
        }

        private ObservableCollection<WallLineGroupViewModel> _wallLineGroups = new ObservableCollection<WallLineGroupViewModel>();
        public ObservableCollection<WallLineGroupViewModel> WallLineGroups
        {
            get => _wallLineGroups;
            set { _wallLineGroups = value; OnPropertyChanged(nameof(WallLineGroups)); }
        }

        /// <summary>
        /// Let the user pick detail lines in Revit that form a closed boundary.
        /// The lines are stitched into a polygon and used as the analysis area.
        /// </summary>
        public void SelectBoundary(Action hideWindow, Action showWindow)
        {
            Document doc = _uiApp.ActiveUIDocument?.Document;
            if (doc == null) { StatusMessage = "No active document."; return; }

            IList<Autodesk.Revit.DB.Reference> pickedRefs = null;
            try
            {
                hideWindow();
                pickedRefs = _uiApp.ActiveUIDocument.Selection.PickObjects(
                    Autodesk.Revit.UI.Selection.ObjectType.Element,
                    new DetailLineSelectionFilter(),
                    "Select detail lines representing walls. Press Finish when done.");
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                showWindow();
                StatusMessage = "Boundary selection cancelled.";
                return;
            }
            catch (Exception ex)
            {
                showWindow();
                StatusMessage = $"Pick failed: {ex.Message}";
                return;
            }

            showWindow();

            if (pickedRefs == null || pickedRefs.Count < 3)
            {
                StatusMessage = "Need at least 3 lines to form walls.";
                return;
            }

            // Group line segments by their Revit line style
            var groupDict = new Dictionary<string, WallLineGroup>();
            double lineZ = 0;

            foreach (Autodesk.Revit.DB.Reference r in pickedRefs)
            {
                Element elem = doc.GetElement(r.ElementId);
                if (elem == null) continue;

                CurveElement curveElem = elem as CurveElement;
                if (curveElem == null) continue;

                Curve curve = curveElem.GeometryCurve;
                if (curve == null) continue;

                // Get line style name
                string styleName = "Unknown";
                try
                {
                    GraphicsStyle gs = curveElem.LineStyle as GraphicsStyle;
                    if (gs != null) styleName = gs.Name;
                }
                catch { }

                if (!groupDict.ContainsKey(styleName))
                {
                    groupDict[styleName] = new WallLineGroup
                    {
                        LineStyleName = styleName
                    };
                }

                WallLineGroup grp = groupDict[styleName];

                IList<XYZ> pts = curve.Tessellate();
                for (int i = 0; i < pts.Count - 1; i++)
                {
                    XYZ p0 = pts[i];
                    XYZ p1 = pts[i + 1];
                    lineZ = UnitConversion.FtToM(p0.Z);

                    var seg = new WallSegment2D
                    {
                        Start = new Vec2(UnitConversion.FtToM(p0.X), UnitConversion.FtToM(p0.Y)),
                        End = new Vec2(UnitConversion.FtToM(p1.X), UnitConversion.FtToM(p1.Y)),
                        BaseElevationM = lineZ,
                        HeightM = 3.0,
                        ThicknessM = 0.1
                    };
                    grp.Segments.Add(seg);
                    grp.SegmentCount++;
                    grp.TotalLengthM += seg.Length;
                }
            }

            if (groupDict.Count == 0)
            {
                StatusMessage = "No wall segments extracted.";
                return;
            }

            // Populate the WallLineGroups collection for display
            WallLineGroups.Clear();
            foreach (var grp in groupDict.Values)
                WallLineGroups.Add(new WallLineGroupViewModel(grp));

            // Gather all segment endpoints
            var allSegments = new List<WallSegment2D>();
            var allPoints = new List<Vec2>();
            foreach (var grp in groupDict.Values)
            {
                foreach (var seg in grp.Segments)
                {
                    allSegments.Add(seg);
                    allPoints.Add(seg.Start);
                    allPoints.Add(seg.End);
                }
            }

            // Floor elevation: prefer speaker level elevation if available
            double elevM = lineZ;
            var allSpeakers = SpeakerGroups.SelectMany(g => g.GetGroup().Instances).ToList();
            var speakersWithLevel = allSpeakers.Where(s => !string.IsNullOrEmpty(s.LevelName)).ToList();
            if (speakersWithLevel.Count > 0)
            {
                elevM = speakersWithLevel
                    .Select(s => s.LevelElevationM)
                    .GroupBy(e => e)
                    .OrderByDescending(g => g.Count())
                    .First().Key;
            }

            // Build a single boundary polygon from the convex hull of all line endpoints
            List<Vec2> hull = ConvexHull(allPoints);
            var boundary = new RoomPolygon
            {
                Vertices = hull,
                FloorElevationM = elevM,
                Name = "Boundary"
            };

            FileLogger.Log($"SelectBoundary: {allSegments.Count} wall segments, hull={hull.Count} pts, " +
                $"area={boundary.Area:F1}m², floorElev={elevM:F3}m");

            DetectedRooms.Clear();
            DetectedRooms.Add(boundary);

            StatusMessage = $"{WallLineGroups.Count} line style(s), {allSegments.Count} segments, " +
                $"area={boundary.Area:F1} m²";

            // Auto-persist so walls survive window close/reopen
            SaveSettings();
            StatusMessage = $"{WallLineGroups.Count} line style(s), {allSegments.Count} segments, " +
                $"area={boundary.Area:F1} m²";
        }

        /// <summary>
        /// Auto-collect Wall elements from the host document and the selected linked model,
        /// group them by wall type, estimate STC from thickness and type name, and populate
        /// WallLineGroups. The user can then review and override the STC assignments.
        /// </summary>
        public void AutoDetectWalls()
        {
            Document doc = _uiApp.ActiveUIDocument?.Document;
            if (doc == null) { StatusMessage = "No active document."; return; }

            var collector = new RevitDataCollector(doc);
            var allGroups = new List<WallLineGroup>();

            // Host document walls
            var hostGroups = collector.GetHostWallGroups();
            allGroups.AddRange(hostGroups);

            // Linked model walls (if a link is selected)
            if (SelectedLink != null && SelectedLink.IsValid)
            {
                var linkGroups = collector.GetLinkWallGroups(SelectedLink.LinkInstanceId);
                allGroups.AddRange(linkGroups);
            }

            if (allGroups.Count == 0)
            {
                StatusMessage = "No wall elements found in the model.";
                return;
            }

            WallLineGroups.Clear();
            foreach (var grp in allGroups)
                WallLineGroups.Add(new WallLineGroupViewModel(grp));

            int totalSegs = allGroups.Sum(g => g.SegmentCount);
            FileLogger.Log($"AutoDetectWalls: {WallLineGroups.Count} types, {totalSegs} segments");
            StatusMessage = $"Detected {WallLineGroups.Count} wall type(s), {totalSegs} segment(s). " +
                "Review STC ratings in the table and re-run the analysis.";

            // Auto-persist so walls survive window close/reopen
            SaveSettings();
            StatusMessage = $"Detected {WallLineGroups.Count} wall type(s), {totalSegs} segment(s). " +
                "Review STC ratings in the table and re-run the analysis.";
        }

        /// <summary>
        /// Clear all selected walls and the boundary polygon so the user can pick fresh ones.
        /// </summary>
        public void ClearWalls()
        {
            WallLineGroups.Clear();
            DetectedRooms.Clear();
            SaveSettings();
            StatusMessage = "Walls cleared. Use \"Select Boundary\" or \"Auto-Detect Walls\" to pick new walls.";
        }

        /// <summary>
        /// Computes the convex hull of a set of 2D points (Andrew's monotone chain).
        /// Returns vertices in CCW order.
        /// </summary>
        private static List<Vec2> ConvexHull(List<Vec2> points)
        {
            if (points.Count < 3)
                return new List<Vec2>(points);

            // Sort by X, then Y
            var sorted = points.OrderBy(p => p.X).ThenBy(p => p.Y).ToList();

            // Remove duplicates
            var unique = new List<Vec2> { sorted[0] };
            for (int i = 1; i < sorted.Count; i++)
            {
                if (Vec2.Distance(sorted[i], sorted[i - 1]) > 1e-6)
                    unique.Add(sorted[i]);
            }
            if (unique.Count < 3)
                return unique;

            int n = unique.Count;
            var hull = new Vec2[2 * n];
            int k = 0;

            // Lower hull
            for (int i = 0; i < n; i++)
            {
                while (k >= 2 && Vec2.Cross(hull[k - 1] - hull[k - 2], unique[i] - hull[k - 2]) <= 0)
                    k--;
                hull[k++] = unique[i];
            }

            // Upper hull
            int lower = k + 1;
            for (int i = n - 2; i >= 0; i--)
            {
                while (k >= lower && Vec2.Cross(hull[k - 1] - hull[k - 2], unique[i] - hull[k - 2]) <= 0)
                    k--;
                hull[k++] = unique[i];
            }

            var result = new List<Vec2>(k - 1);
            for (int i = 0; i < k - 1; i++)
                result.Add(hull[i]);
            return result;
        }

        // ========================= SPEAKERS TAB =========================

        private ObservableCollection<SpeakerGroupViewModel> _speakerGroups = new ObservableCollection<SpeakerGroupViewModel>();
        public ObservableCollection<SpeakerGroupViewModel> SpeakerGroups
        {
            get => _speakerGroups;
            set { _speakerGroups = value; OnPropertyChanged(nameof(SpeakerGroups)); }
        }

        private List<SpeakerProfileMapping> _savedMappings = new List<SpeakerProfileMapping>();

        /// <summary>
        /// ElementIds of speakers the user has picked in the Revit viewport.
        /// When non-empty, only these speakers are used for the computation.
        /// </summary>
        private ObservableCollection<int> _pickedSpeakerIds = new ObservableCollection<int>();
        public ObservableCollection<int> PickedSpeakerIds
        {
            get => _pickedSpeakerIds;
            set { _pickedSpeakerIds = value; OnPropertyChanged(nameof(PickedSpeakerIds)); OnPropertyChanged(nameof(PickedSpeakerSummary)); }
        }

        public string PickedSpeakerSummary =>
            _pickedSpeakerIds.Count == 0
                ? "No speakers selected"
                : $"{_pickedSpeakerIds.Count} speaker(s) selected";

        /// <summary>
        /// Let the user pick a speaker element in the Revit viewport.
        /// The window must be hidden during PickObject so the user can interact with Revit.
        /// </summary>
        public void PickSpeaker(Action hideWindow, Action showWindow)
        {
            Document doc = _uiApp.ActiveUIDocument?.Document;
            if (doc == null) { StatusMessage = "No active document."; return; }

            IList<Autodesk.Revit.DB.Reference> pickedRefs = null;
            try
            {
                hideWindow();
                BuiltInCategory pickCat = RevitDataCollector.ParseCategory(SpeakerCategoryName);
                pickedRefs = _uiApp.ActiveUIDocument.Selection.PickObjects(
                    Autodesk.Revit.UI.Selection.ObjectType.Element,
                    new CategorySelectionFilter(pickCat),
                    "Select speaker elements in the model. Press Finish when done.");
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                showWindow();
                StatusMessage = "Speaker pick cancelled.";
                return;
            }
            catch (Exception ex)
            {
                showWindow();
                StatusMessage = $"Pick failed: {ex.Message}";
                return;
            }

            showWindow();

            if (pickedRefs == null || pickedRefs.Count == 0)
            {
                StatusMessage = "No elements selected.";
                return;
            }

            // Collect / refresh all speakers so SpeakerGroups is up-to-date
            RefreshSpeakers();

            // Build a lookup of all known speaker element IDs
            var knownIds = new HashSet<int>();
            foreach (SpeakerGroupViewModel gvm in SpeakerGroups)
                foreach (SpeakerInstance inst in gvm.GetGroup().Instances)
                    knownIds.Add(inst.ElementId);

            int added = 0;
            int skipped = 0;
            foreach (Autodesk.Revit.DB.Reference r in pickedRefs)
            {
                int pickedId = RevitCompat.GetIdValue(r.ElementId);
                if (!knownIds.Contains(pickedId))
                {
                    skipped++;
                    continue;
                }
                if (!_pickedSpeakerIds.Contains(pickedId))
                {
                    _pickedSpeakerIds.Add(pickedId);
                    added++;
                }
            }

            OnPropertyChanged(nameof(PickedSpeakerSummary));

            // Filter SpeakerGroups to only show picked speakers
            var pickedSet = new HashSet<int>(_pickedSpeakerIds);
            foreach (var gvm in SpeakerGroups.ToList())
            {
                gvm.GetGroup().Instances.RemoveAll(inst => !pickedSet.Contains(inst.ElementId));
                if (gvm.GetGroup().Instances.Count == 0)
                    SpeakerGroups.Remove(gvm);
            }

            string msg = $"{added} speaker(s) added.";
            if (skipped > 0) msg += $" {skipped} non-speaker element(s) ignored.";
            msg += $" {PickedSpeakerSummary}";
            SaveSettingsCore();
            StatusMessage = msg;
        }

        /// <summary>
        /// Clear the picked-speaker filter and re-collect speakers from Revit
        /// so positions reflect any changes made since last collection.
        /// </summary>
        public void ClearPickedSpeakers()
        {
            _pickedSpeakerIds.Clear();
            SpeakerGroups.Clear();
            OnPropertyChanged(nameof(PickedSpeakerSummary));
            SaveSettingsCore();
            StatusMessage = "Speaker selection cleared.";
        }

        // ========================= GRID TAB =========================

        private double _gridSpacing = 1.0;
        public double GridSpacing
        {
            get => _gridSpacing;
            set { _gridSpacing = value; OnPropertyChanged(nameof(GridSpacing)); }
        }

        private double _receiverHeight = 1.2;
        public double ReceiverHeight
        {
            get => _receiverHeight;
            set { _receiverHeight = value; OnPropertyChanged(nameof(ReceiverHeight)); }
        }

        private double _boundaryOffset = 0.3;
        public double BoundaryOffset
        {
            get => _boundaryOffset;
            set { _boundaryOffset = value; OnPropertyChanged(nameof(BoundaryOffset)); }
        }

        private bool _useMinSplThreshold;
        public bool UseMinSplThreshold
        {
            get => _useMinSplThreshold;
            set { _useMinSplThreshold = value; OnPropertyChanged(nameof(UseMinSplThreshold)); }
        }

        private double _minSplThreshold = 65.0;
        public double MinSplThreshold
        {
            get => _minSplThreshold;
            set { _minSplThreshold = value; OnPropertyChanged(nameof(MinSplThreshold)); }
        }

        private double _backgroundNoiseDb = 35.0;
        public double BackgroundNoiseDb
        {
            get => _backgroundNoiseDb;
            set { _backgroundNoiseDb = value; OnPropertyChanged(nameof(BackgroundNoiseDb)); }
        }

        private double _humidity = 50.0;
        public double Humidity
        {
            get => _humidity;
            set { _humidity = value; OnPropertyChanged(nameof(Humidity)); }
        }

        // --- Per-octave-band RT60 properties ---
        private double _rt60_125 = OctaveBands.DefaultRT60[0];
        public double RT60_125 { get => _rt60_125; set { _rt60_125 = value; OnPropertyChanged(nameof(RT60_125)); } }
        private double _rt60_250 = OctaveBands.DefaultRT60[1];
        public double RT60_250 { get => _rt60_250; set { _rt60_250 = value; OnPropertyChanged(nameof(RT60_250)); } }
        private double _rt60_500 = OctaveBands.DefaultRT60[2];
        public double RT60_500 { get => _rt60_500; set { _rt60_500 = value; OnPropertyChanged(nameof(RT60_500)); } }
        private double _rt60_1k = OctaveBands.DefaultRT60[3];
        public double RT60_1k { get => _rt60_1k; set { _rt60_1k = value; OnPropertyChanged(nameof(RT60_1k)); } }
        private double _rt60_2k = OctaveBands.DefaultRT60[4];
        public double RT60_2k { get => _rt60_2k; set { _rt60_2k = value; OnPropertyChanged(nameof(RT60_2k)); } }
        private double _rt60_4k = OctaveBands.DefaultRT60[5];
        public double RT60_4k { get => _rt60_4k; set { _rt60_4k = value; OnPropertyChanged(nameof(RT60_4k)); } }
        private double _rt60_8k = OctaveBands.DefaultRT60[6];
        public double RT60_8k { get => _rt60_8k; set { _rt60_8k = value; OnPropertyChanged(nameof(RT60_8k)); } }

        // --- Per-octave-band background noise properties ---
        private double _noise_125 = OctaveBands.DefaultBackgroundNoise[0];
        public double Noise_125 { get => _noise_125; set { _noise_125 = value; OnPropertyChanged(nameof(Noise_125)); } }
        private double _noise_250 = OctaveBands.DefaultBackgroundNoise[1];
        public double Noise_250 { get => _noise_250; set { _noise_250 = value; OnPropertyChanged(nameof(Noise_250)); } }
        private double _noise_500 = OctaveBands.DefaultBackgroundNoise[2];
        public double Noise_500 { get => _noise_500; set { _noise_500 = value; OnPropertyChanged(nameof(Noise_500)); } }
        private double _noise_1k = OctaveBands.DefaultBackgroundNoise[3];
        public double Noise_1k { get => _noise_1k; set { _noise_1k = value; OnPropertyChanged(nameof(Noise_1k)); } }
        private double _noise_2k = OctaveBands.DefaultBackgroundNoise[4];
        public double Noise_2k { get => _noise_2k; set { _noise_2k = value; OnPropertyChanged(nameof(Noise_2k)); } }
        private double _noise_4k = OctaveBands.DefaultBackgroundNoise[5];
        public double Noise_4k { get => _noise_4k; set { _noise_4k = value; OnPropertyChanged(nameof(Noise_4k)); } }
        private double _noise_8k = OctaveBands.DefaultBackgroundNoise[6];
        public double Noise_8k { get => _noise_8k; set { _noise_8k = value; OnPropertyChanged(nameof(Noise_8k)); } }

        private double[] GetRT60Array() => new[] { _rt60_125, _rt60_250, _rt60_500, _rt60_1k, _rt60_2k, _rt60_4k, _rt60_8k };
        private double[] GetNoiseArray() => new[] { _noise_125, _noise_250, _noise_500, _noise_1k, _noise_2k, _noise_4k, _noise_8k };

        /// <summary>
        /// Auto-estimate RT60 from detected room geometry and wall absorption preset
        /// using the Eyring-Norris formula. Populates the 7 per-band RT60 fields.
        /// </summary>
        public void EstimateRT60()
        {
            if (DetectedRooms.Count == 0)
            {
                StatusMessage = "No boundary defined. Click 'Select Boundary' first.";
                return;
            }

            // Aggregate floor area and ceiling height across all rooms
            double totalFloorArea = 0;
            double avgCeilingH = 0;
            int roomCount = 0;
            foreach (var room in DetectedRooms)
            {
                totalFloorArea += room.Area;
                if (room.CeilingHeightM > 0.5)
                {
                    avgCeilingH += room.CeilingHeightM;
                    roomCount++;
                }
            }
            // Default ceiling height if not available from speaker elevations
            if (roomCount == 0 || avgCeilingH <= 0)
                avgCeilingH = 3.0;
            else
                avgCeilingH /= roomCount;

            double volumeM3 = totalFloorArea * avgCeilingH;
            double surfaceAreaM2 = Compute.RoomAcoustics.EstimateSurfaceArea(totalFloorArea, avgCeilingH);

            // Get wall absorption preset (already selected by user in Grid tab)
            double[] absorption = OctaveBands.AbsorptionPresets.ContainsKey(WallAbsorptionPreset.Drywall)
                ? OctaveBands.AbsorptionPresets[WallAbsorptionPreset.Drywall]
                : new double[] { 0.10, 0.10, 0.10, 0.10, 0.10, 0.10, 0.10 };

            double[] rt60 = Compute.RoomAcoustics.EstimateEyringRt60(volumeM3, surfaceAreaM2, absorption);

            RT60_125 = rt60[0]; RT60_250 = rt60[1]; RT60_500 = rt60[2]; RT60_1k = rt60[3];
            RT60_2k  = rt60[4]; RT60_4k  = rt60[5]; RT60_8k  = rt60[6];

            StatusMessage = $"RT60 estimated (Eyring): room {totalFloorArea:F0} m² × {avgCeilingH:F1} m = {volumeM3:F0} m³. " +
                $"500 Hz RT60 = {rt60[2]:F2} s";
        }

        private void LoadOctaveBandSettings(AnalysisSettings s)
        {
            double[] rt = s.RT60ByBand ?? OctaveBands.DefaultRT60;
            if (rt.Length >= OctaveBands.Count)
            {
                RT60_125 = rt[0]; RT60_250 = rt[1]; RT60_500 = rt[2]; RT60_1k = rt[3];
                RT60_2k = rt[4]; RT60_4k = rt[5]; RT60_8k = rt[6];
            }
            double[] n = s.BackgroundNoiseByBand ?? OctaveBands.DefaultBackgroundNoise;
            if (n.Length >= OctaveBands.Count)
            {
                Noise_125 = n[0]; Noise_250 = n[1]; Noise_500 = n[2]; Noise_1k = n[3];
                Noise_2k = n[4]; Noise_4k = n[5]; Noise_8k = n[6];
            }
        }

        // ========================= RUN TAB =========================

        private bool _isRunning;
        public bool IsRunning
        {
            get => _isRunning;
            set { _isRunning = value; OnPropertyChanged(nameof(IsRunning)); OnPropertyChanged(nameof(CanRun)); }
        }

        public bool CanRun => !_isRunning;

        private double _progress;
        public double Progress
        {
            get => _progress;
            set { _progress = value; OnPropertyChanged(nameof(Progress)); OnPropertyChanged(nameof(ProgressPercent)); }
        }

        public string ProgressPercent => $"{_progress * 100:F0}%";

        private string _statusMessage = "Ready";
        public string StatusMessage
        {
            get => _statusMessage;
            set { _statusMessage = value; OnPropertyChanged(nameof(StatusMessage)); }
        }

        private string _lastRunSummary = "";
        public string LastRunSummary
        {
            get => _lastRunSummary;
            set { _lastRunSummary = value; OnPropertyChanged(nameof(LastRunSummary)); }
        }

        // ========================= RESULTS TAB =========================

        private AcousticJobOutput _lastOutput;
        public AcousticJobOutput LastOutput
        {
            get => _lastOutput;
            set
            {
                _lastOutput = value;
                OnPropertyChanged(nameof(LastOutput));
                OnPropertyChanged(nameof(HasResults));
                OnPropertyChanged(nameof(LegendItems));
            }
        }

        public bool HasResults => _lastOutput != null && _lastOutput.Results.Count > 0;

        private VisualizationMode _selectedVisualizationMode = VisualizationMode.SPL;
        public VisualizationMode SelectedVisualizationMode
        {
            get => _selectedVisualizationMode;
            set
            {
                _selectedVisualizationMode = value;
                OnPropertyChanged(nameof(SelectedVisualizationMode));
                OnPropertyChanged(nameof(LegendItems));
                OnPropertyChanged(nameof(VisualizationModeTitle));
                OnPropertyChanged(nameof(VisualizationModeDescription));
            }
        }

        /// <summary>Short title for the currently selected visualization mode.</summary>
        public string VisualizationModeTitle
        {
            get
            {
                switch (_selectedVisualizationMode)
                {
                    case VisualizationMode.SPL:     return "SPL — Sound Pressure Level";
                    case VisualizationMode.SPL_A:   return "SPL (A-weighted) — dBA";
                    case VisualizationMode.STI:     return "STI — Speech Transmission Index";
                    case VisualizationMode.C80:     return "C80 — Clarity";
                    case VisualizationMode.SPL_125: return "SPL at 125 Hz";
                    case VisualizationMode.SPL_250: return "SPL at 250 Hz";
                    case VisualizationMode.SPL_500: return "SPL at 500 Hz";
                    case VisualizationMode.SPL_1k:  return "SPL at 1 kHz";
                    case VisualizationMode.SPL_2k:  return "SPL at 2 kHz";
                    case VisualizationMode.SPL_4k:  return "SPL at 4 kHz";
                    case VisualizationMode.SPL_8k:  return "SPL at 8 kHz";
                    default: return _selectedVisualizationMode.ToString();
                }
            }
        }

        /// <summary>
        /// Plain-language description of what the current visualization mode means
        /// and how to interpret its values. Shown to the right of the legend.
        /// </summary>
        public string VisualizationModeDescription
        {
            get
            {
                switch (_selectedVisualizationMode)
                {
                    case VisualizationMode.SPL:
                        return "Total broadband sound level at each point (dB SPL). " +
                               "Higher = louder. Typical PA/VA target: 70–85 dB. " +
                               "Coverage uniformity goal: ≤ 6 dB variation across the listening area.";
                    case VisualizationMode.SPL_A:
                        return "A-weighted SPL in dBA (IEC 61672-1). Weights frequencies to match " +
                               "human hearing sensitivity — mid-range counts more, very low and very high less. " +
                               "Used for noise exposure regulations. Typical occupational limit: 85 dBA (8 hr).";
                    case VisualizationMode.STI:
                        return "Speech Transmission Index per IEC 60268-16. Scale: " +
                               "Bad < 0.30 | Poor 0.30–0.45 | Fair 0.45–0.60 | Good 0.60–0.75 | Excellent > 0.75. " +
                               "Target for PA/VA systems: ≥ 0.50. Critical venues (emergency, courtrooms): ≥ 0.65.";
                    case VisualizationMode.C80:
                        return "Clarity C80 (dB): ratio of sound arriving in the first 80 ms to sound arriving later. " +
                               "Positive = more direct/early energy reaching the listener. " +
                               "Target for speech intelligibility: C80 > 0 dB. Computed from 500 Hz + 1 kHz bands.";
                    case VisualizationMode.SPL_125:
                    case VisualizationMode.SPL_250:
                        return "Low-frequency SPL. Useful for diagnosing bass build-up in corners, " +
                               "room resonances, and boom from subwoofers. High values here can mask speech.";
                    case VisualizationMode.SPL_500:
                    case VisualizationMode.SPL_1k:
                    case VisualizationMode.SPL_2k:
                        return "Mid-frequency SPL — the most important range for speech clarity and music presence. " +
                               "Uniformity in these bands has the greatest impact on intelligibility.";
                    case VisualizationMode.SPL_4k:
                    case VisualizationMode.SPL_8k:
                        return "High-frequency SPL. Shows where air absorption, distance, or obstructions " +
                               "reduce treble projection. Low values here result in muffled sound at the back of the room.";
                    default:
                        return "";
                }
            }
        }

        public VisualizationMode[] AvailableVisualizationModes { get; } =
            (VisualizationMode[])Enum.GetValues(typeof(VisualizationMode));

        /// <summary>
        /// Legend items for the color band display in the Results tab.
        /// </summary>
        public ObservableCollection<LegendItem> LegendItems
        {
            get
            {
                var items = new ObservableCollection<LegendItem>();
                if (_lastOutput == null || _lastOutput.Results.Count == 0)
                    return items;

                List<(int Band, string ColorHex, string Label)> bands;
                int bandIndex = GetOctaveBandIndex(_selectedVisualizationMode);

                // Use the exact range the renderer used (written back after render),
                // but only when the rendered mode matches the current selection.
                // This prevents stale SPL ranges from appearing in the STI legend.
                bool hasRenderedRange = _lastOutput.RenderedMaxVal > _lastOutput.RenderedMinVal
                    && string.Equals(_lastOutput.RenderedMode,
                        _selectedVisualizationMode.ToString(),
                        StringComparison.Ordinal);

                if (_selectedVisualizationMode == VisualizationMode.STI)
                {
                    double lo = hasRenderedRange ? _lastOutput.RenderedMinVal : _lastOutput.MinSti;
                    double hi = hasRenderedRange ? _lastOutput.RenderedMaxVal : _lastOutput.MaxSti;
                    bands = FilledRegionRenderer.GetStiLegendBands(lo, hi);
                }
                else if (_selectedVisualizationMode == VisualizationMode.SPL_A)
                {
                    double lo = hasRenderedRange ? _lastOutput.RenderedMinVal : _lastOutput.MinSplDbA;
                    double hi = hasRenderedRange ? _lastOutput.RenderedMaxVal : _lastOutput.MaxSplDbA;
                    bands = FilledRegionRenderer.GetLegendBands(lo, hi, " dBA");
                }
                else if (_selectedVisualizationMode == VisualizationMode.C80)
                {
                    double lo = hasRenderedRange ? _lastOutput.RenderedMinVal : _lastOutput.MinC80Db;
                    double hi = hasRenderedRange ? _lastOutput.RenderedMaxVal : _lastOutput.MaxC80Db;
                    bands = FilledRegionRenderer.GetLegendBands(lo, hi, " dB C80");
                }
                else if (bandIndex >= 0
                    && _lastOutput.MinSplDbByBand != null
                    && _lastOutput.MaxSplDbByBand != null)
                {
                    string freq = OctaveBands.Labels[bandIndex];
                    double lo = hasRenderedRange ? _lastOutput.RenderedMinVal : _lastOutput.MinSplDbByBand[bandIndex];
                    double hi = hasRenderedRange ? _lastOutput.RenderedMaxVal : _lastOutput.MaxSplDbByBand[bandIndex];
                    bands = FilledRegionRenderer.GetLegendBands(lo, hi, $" dB @ {freq} Hz");
                }
                else
                {
                    double lo = hasRenderedRange ? _lastOutput.RenderedMinVal
                        : (UseMinSplThreshold ? MinSplThreshold : _lastOutput.MinSplDb);
                    double hi = hasRenderedRange ? _lastOutput.RenderedMaxVal : _lastOutput.MaxSplDb;
                    bands = FilledRegionRenderer.GetLegendBands(lo, hi);
                }

                foreach (var (_, hex, label) in bands)
                    items.Add(new LegendItem { ColorHex = hex, Label = label });

                return items;
            }
        }

        /// <summary>
        /// Maps per-band VisualizationMode values to their OctaveBands index (0-6).
        /// Returns -1 for SPL and STI (non-band modes).
        /// </summary>
        internal static int GetOctaveBandIndex(VisualizationMode mode)
        {
            switch (mode)
            {
                case VisualizationMode.SPL_125: return 0;
                case VisualizationMode.SPL_250: return 1;
                case VisualizationMode.SPL_500: return 2;
                case VisualizationMode.SPL_1k:  return 3;
                case VisualizationMode.SPL_2k:  return 4;
                case VisualizationMode.SPL_4k:  return 5;
                case VisualizationMode.SPL_8k:  return 6;
                default: return -1;
            }
        }

        // ========================= COMMANDS =========================

        /// <summary>
        /// Refresh linked models list. Must be called from Revit context.
        /// </summary>
        public void RefreshLinks()
        {
            StatusMessage = "Collecting linked models...";
            Document doc = _uiApp.ActiveUIDocument?.Document;
            if (doc == null) { StatusMessage = "No active document."; return; }

            var collector = new RevitDataCollector(doc);
            List<LinkSelection> links = collector.GetAvailableLinks();

            AvailableLinks.Clear();
            foreach (LinkSelection link in links)
                AvailableLinks.Add(link);

            StatusMessage = $"Found {links.Count} linked model(s).";
        }

        /// <summary>
        /// Refresh speaker instances. Must be called from Revit context.
        /// </summary>
        public void RefreshSpeakers()
        {
            StatusMessage = "Collecting speakers...";
            Document doc = _uiApp.ActiveUIDocument?.Document;
            if (doc == null) { StatusMessage = "No active document."; return; }

            BuiltInCategory cat = RevitDataCollector.ParseCategory(SpeakerCategoryName);
            var collector = new RevitDataCollector(doc);
            List<SpeakerInstance> speakers = collector.CollectSpeakers(cat);
            List<SpeakerTypeGroup> groups = collector.GroupSpeakers(speakers);

            SpeakerGroups.Clear();
            foreach (SpeakerTypeGroup group in groups)
            {
                var vm = new SpeakerGroupViewModel(group);

                // Restore saved mapping if available
                SpeakerProfileMapping saved = _savedMappings
                    .FirstOrDefault(m => m.TypeKey == group.TypeKey);
                if (saved != null)
                    vm.ApplyMapping(saved);

                SpeakerGroups.Add(vm);
            }

            StatusMessage = $"Found {speakers.Count} speaker(s) in {groups.Count} type(s).";
        }

        /// <summary>Save settings to disk and update the status bar.</summary>
        public void SaveSettings() => SaveSettingsCore(updateStatus: true);

        void SaveSettingsCore(bool updateStatus = false)
        {
            var settings = new PluginSettings
            {
                LinkSelection = SelectedLink,
                AnalysisSettings = new AnalysisSettings
                {
                    GridSpacingM = GridSpacing,
                    ReceiverHeightM = ReceiverHeight,
                    BoundaryOffsetM = BoundaryOffset,
                    SpeakerCategoryName = SpeakerCategoryName,
                    UseMinSplThreshold = UseMinSplThreshold,
                    MinSplThresholdDb = MinSplThreshold,
                    BackgroundNoiseDb = BackgroundNoiseDb,
                    RT60ByBand = GetRT60Array(),
                    BackgroundNoiseByBand = GetNoiseArray()
                },
                SpeakerMappings    = SpeakerGroups.Select(g => g.GetMapping()).ToList(),
                WallGroups         = WallLineGroups.Select(vm => vm.GetGroup()).ToList(),
                BoundaryRooms      = DetectedRooms.ToList(),
                SavedSpeakerGroups = SpeakerGroups.Select(vm => vm.GetGroup()).ToList()
            };

            SettingsStore.Save(settings);
            if (updateStatus) StatusMessage = "Settings saved.";
        }

        /// <summary>
        /// Store the user-adjusted aim angle for a speaker in Revit ExtensibleStorage
        /// and persist the updated FacingDirection to the JSON settings.
        /// Must be called after the viewer has already updated FacingDirection in-memory.
        /// </summary>
        public void SetSpeakerAimAngle(int elementId, double angleDeg)
        {
            _dispatcher.Enqueue(uiApp =>
            {
                Document doc = uiApp.ActiveUIDocument?.Document;
                if (doc == null) return;
                SpeakerRotationStorage.Write(doc, elementId, angleDeg);
                FileLogger.Log($"SetSpeakerAimAngle: id={elementId}, angle={angleDeg:F1}°");
            });
            SaveSettingsCore();
        }

        /// <summary>
        /// Start the SPL computation job.
        /// </summary>
        public void StartJob(CalculationQuality quality = CalculationQuality.Full)
        {
            if (IsRunning) return;

            Document doc = _uiApp.ActiveUIDocument?.Document;
            if (doc == null) { StatusMessage = "No active document."; return; }

            if (SpeakerGroups.Count == 0)
            {
                StatusMessage = "No speakers selected. Pick speakers first.";
                return;
            }

            StatusMessage = "Building compute input...";
            IsRunning = true;
            Progress = 0;

            try
            {
                // Build sources
                var sources = new List<ComputeSource>();

                foreach (SpeakerGroupViewModel gvm in SpeakerGroups)
                {
                    SpeakerProfileMapping mapping = gvm.GetMapping();
                    foreach (SpeakerInstance inst in gvm.GetGroup().Instances)
                    {
                        Vec3 facing;

                        if (mapping.ProfileSource == ProfileSourceType.WallMounted)
                        {
                            // Wall-mounted: horizontal direction from per-instance drag line
                            double hx = inst.FacingDirection.X;
                            double hy = inst.FacingDirection.Y;
                            double hLen = System.Math.Sqrt(hx * hx + hy * hy);
                            if (hLen < 1e-6) { hx = 1.0; hy = 0.0; hLen = 1.0; }
                            facing = new Vec3(hx / hLen, hy / hLen, 0);
                        }
                        else
                        {
                            // Omni / Conical: straight down
                            facing = new Vec3(0, 0, -1);
                        }

                        sources.Add(new ComputeSource
                        {
                            Position = inst.Position,
                            FacingDirection = facing,
                            Profile = mapping
                        });
                        FileLogger.Log($"Source: Id={inst.ElementId}, Pos={inst.Position}, " +
                            $"Level='{inst.LevelName}', LevelElev={inst.LevelElevationM:F3}m, " +
                            $"HeightAboveLevel={inst.ElevationFromLevelM:F3}m");
                    }
                }

                // Build receivers
                var collector = new RevitDataCollector(doc);
                var analysisSettings = new AnalysisSettings
                {
                    GridSpacingM = GridSpacing,
                    ReceiverHeightM = ReceiverHeight,
                    BoundaryOffsetM = BoundaryOffset
                };

                var receivers = new List<ReceiverPoint>();
                var surfaces = new List<Domain.Polygon>();
                var analysisRooms = new List<RoomPolygon>();
                int globalIndex = 0;

                // Use the boundary polygon from SelectBoundary
                if (DetectedRooms.Count == 0)
                {
                    StatusMessage = "No boundary defined. Click 'Select Boundary' first.";
                    IsRunning = false;
                    return;
                }

                analysisRooms = DetectedRooms.ToList();

                // Compute enclosure ratio for each room polygon using original wall segments.
                // This determines how much reverberant energy is applied per room.
                var allWallSegs = new List<WallSegment2D>();
                foreach (var wvm in WallLineGroups)
                    allWallSegs.AddRange(wvm.GetGroup().Segments);
                RoomDetector.ComputeEnclosureRatios(analysisRooms, allWallSegs);

                StatusMessage = $"Generating grid in boundary with {sources.Count} speakers...";
                for (int roomIdx = 0; roomIdx < analysisRooms.Count; roomIdx++)
                {
                    RoomPolygon boundary = analysisRooms[roomIdx];
                    List<ReceiverPoint> pts = ReceiverGrid.GenerateForPolygon(
                        boundary, analysisSettings, globalIndex, roomIdx);
                    globalIndex += pts.Count;
                    receivers.AddRange(pts);
                    double recvZ = boundary.FloorElevationM + analysisSettings.ReceiverHeightM;
                    FileLogger.Log($"'{boundary.Name}': {pts.Count} grid pts, " +
                        $"floorElev={boundary.FloorElevationM:F3}m, receiverZ={recvZ:F3}m (area={boundary.Area:F1}m²)");
                }

                FileLogger.Log($"Analysis: {analysisRooms.Count} boundary region(s), " +
                    $"{sources.Count} speakers, {receivers.Count} receiver points");

                // Derive ceiling height per room from the tallest speaker in each room.
                // Speakers are typically ceiling-mounted, so their elevation ≈ ceiling.
                foreach (RoomPolygon room in analysisRooms)
                {
                    double maxElevation = 0;
                    foreach (SpeakerGroupViewModel gvm in SpeakerGroups)
                    {
                        foreach (SpeakerInstance inst in gvm.GetGroup().Instances)
                        {
                            if (room.ContainsSpeakerPosition(inst.Position))
                            {
                                double h = inst.ElevationFromLevelM;
                                if (h > maxElevation) maxElevation = h;
                            }
                        }
                    }
                    if (maxElevation > 0.5)
                        room.CeilingHeightM = maxElevation;
                }

                if (SelectedLink.IsValid)
                    surfaces = collector.ExtractSurfacesFromLink(SelectedLink.LinkInstanceId);

                if (receivers.Count == 0)
                {
                    StatusMessage = "No receiver points generated. Check grid settings.";
                    IsRunning = false;
                    return;
                }

                // Build job input
                string jobId = JobSerializer.NewJobId();

                // Collect wall segments with STC ratings from the assigned wall types
                var computeWalls = new List<ComputeWall>();
                foreach (var wvm in WallLineGroups)
                {
                    WallLineGroup grp = wvm.GetGroup();
                    int stc = grp.WallType?.StcRating ?? 0;
                    FileLogger.Log($"  WallGroup '{grp.LineStyleName}': {grp.SegmentCount} segs, " +
                        $"type='{grp.WallType?.DisplayName}', STC={stc}");
                    foreach (WallSegment2D seg in grp.Segments)
                    {
                        // Extend each wall segment by 0.10m at each end to
                        // bridge small gaps at corners and T-junctions where
                        // detail line endpoints don't connect perfectly.
                        Vec2 dir = (seg.End - seg.Start);
                        double len = dir.Length;
                        const double ext = 0.10; // meters
                        Vec2 norm = len > 1e-6 ? dir * (1.0 / len) : Vec2.Zero;
                        Vec2 extStart = seg.Start - norm * ext;
                        Vec2 extEnd   = seg.End   + norm * ext;

                        computeWalls.Add(new ComputeWall
                        {
                            Start = extStart,
                            End = extEnd,
                            StcRating = stc,
                            HalfThicknessM = Math.Max(seg.ThicknessM * 0.5, 0.05)
                        });
                    }
                }
                FileLogger.Log($"Wall segments for calc: {computeWalls.Count} " +
                    $"(groups: {WallLineGroups.Count})");

                var input = new AcousticJobInput
                {
                    JobId = jobId,
                    Sources = sources,
                    Surfaces = surfaces,
                    Receivers = receivers,
                    Rooms = analysisRooms,
                    Walls = computeWalls,
                    Quality = quality,
                    Environment = new EnvironmentSettings
                    {
                        BackgroundNoiseDb = BackgroundNoiseDb,
                        RT60ByBand = GetRT60Array(),
                        BackgroundNoiseByBand = GetNoiseArray(),
                        RelativeHumidityPct = Humidity
                    }
                };

                // Log elevation summary for debugging
                if (sources.Count > 0 && receivers.Count > 0)
                {
                    double srcMinZ = sources.Min(s => s.Position.Z);
                    double srcMaxZ = sources.Max(s => s.Position.Z);
                    double rcvMinZ = receivers.Min(r => r.Position.Z);
                    double rcvMaxZ = receivers.Max(r => r.Position.Z);
                    FileLogger.Log($"Elevation summary: " +
                        $"Source Z=[{srcMinZ:F3}..{srcMaxZ:F3}]m, " +
                        $"Receiver Z=[{rcvMinZ:F3}..{rcvMaxZ:F3}]m");
                }

                StatusMessage = $"Job started ({quality}): {sources.Count} sources, {receivers.Count} receivers...";

                // Save settings before running
                SaveSettings();

                // Run on background thread — capture dispatcher so progress
                // updates marshal correctly even when SynchronizationContext is
                // not the WPF DispatcherSynchronizationContext (Revit 2024 / net48).
                var dispatcher = System.Windows.Application.Current?.Dispatcher
                    ?? System.Windows.Threading.Dispatcher.CurrentDispatcher;
                var progress = new Progress<double>(p =>
                {
                    if (dispatcher.CheckAccess())
                    {
                        Progress = p;
                        StatusMessage = $"Computing... {p * 100:F0}%";
                    }
                    else
                    {
                        dispatcher.BeginInvoke(new Action(() =>
                        {
                            Progress = p;
                            StatusMessage = $"Computing... {p * 100:F0}%";
                        }));
                    }
                });

                _jobRunner.Start(input, progress);
            }
            catch (Exception ex)
            {
                StatusMessage = $"Job start failed: {ex.Message}";
                IsRunning = false;
                FileLogger.Log($"StartJob error: {ex}");
            }
        }

        /// <summary>
        /// Cancel the running job.
        /// </summary>
        public void CancelJob()
        {
            _jobRunner.Cancel();
            StatusMessage = "Cancelling...";
        }

        private void OnJobCompleted(AcousticJobOutput output)
        {
            // JobCompleted fires on the thread-pool; marshal to the UI thread
            // so bound properties update reliably on both net48 and net8.
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
            {
                dispatcher.BeginInvoke(new Action(() => OnJobCompleted(output)));
                return;
            }

            LastOutput = output;
            IsRunning = false;

            if (output.WasCanceled)
            {
                StatusMessage = "Job canceled.";
                LastRunSummary = $"Canceled after {output.ComputeTimeSeconds:F1}s";
            }
            else
            {
                StatusMessage = $"Results ready: {DateTime.Now:HH:mm:ss}";
                LastRunSummary = $"{output.ReceiverCount} points | " +
                    $"{output.Quality} | " +
                    $"SPL: {output.MinSplDb:F1}–{output.MaxSplDb:F1} dB | " +
                    $"dBA: {output.MinSplDbA:F1}–{output.MaxSplDbA:F1} | " +
                    $"STI: {output.MinSti:F2}–{output.MaxSti:F2} | " +
                    $"C80 avg: {output.AvgC80Db:F1} dB | D50 avg: {output.AvgD50:F2} | " +
                    $"Time: {output.ComputeTimeSeconds:F1}s";
            }
        }

        /// <summary>
        /// Visualize results on the active view. Uses ExternalEvent.
        /// Works in plan view, 3D view, or section view.
        /// </summary>
        public void VisualizeResults()
        {
            if (LastOutput == null || LastOutput.Results.Count == 0)
            {
                StatusMessage = "No results to visualize.";
                return;
            }

            StatusMessage = "Rendering heatmap...";
            AcousticJobOutput output = LastOutput;

            _dispatcher.Enqueue(uiApp =>
            {
                try
                {
                    Document doc = uiApp.ActiveUIDocument?.Document;
                    View view = doc?.ActiveView;
                    if (doc == null || view == null)
                    {
                        SetStatusFromRevitThread("No active view found.");
                        return;
                    }

                    _renderer.Render(doc, view, output,
                        _selectedVisualizationMode,
                        UseMinSplThreshold ? MinSplThreshold : (double?)null);
                    int bi = GetOctaveBandIndex(_selectedVisualizationMode);
                    string modeLabel = _selectedVisualizationMode == VisualizationMode.STI ? "STI"
                        : _selectedVisualizationMode == VisualizationMode.SPL_A ? "SPL (dBA)"
                        : _selectedVisualizationMode == VisualizationMode.C80   ? "C80 (Clarity)"
                        : bi >= 0 ? $"SPL @{OctaveBands.Labels[bi]} Hz"
                        : "SPL";
                    SetStatusFromRevitThread($"{modeLabel} heatmap rendered: {output.Results.Count} points on '{view.Name}'");
                    // Refresh legend so it shows the actual rendered min/max range
                    System.Windows.Application.Current?.Dispatcher?.Invoke(
                        () => OnPropertyChanged(nameof(LegendItems)));
                }
                catch (Exception ex)
                {
                    // Build a detailed message including inner exceptions
                    string detail = ex.Message;
                    if (ex.InnerException != null)
                        detail += " → " + ex.InnerException.Message;

                    // Include the first line of stack trace to identify the failing method
                    string trace = ex.StackTrace;
                    if (!string.IsNullOrEmpty(trace))
                    {
                        string firstLine = trace.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                        if (firstLine != null)
                            detail += " | " + firstLine.Trim();
                    }

                    FileLogger.Log($"Visualize failed: {ex}");
                    SetStatusFromRevitThread($"Heatmap failed: {detail}");
                }
            });
        }

        /// <summary>
        /// Clear visualization from active view. Uses ExternalEvent.
        /// </summary>
        public void ClearVisualization()
        {
            _dispatcher.Enqueue(uiApp =>
            {
                try
                {
                    Document doc = uiApp.ActiveUIDocument?.Document;
                    View view = doc?.ActiveView;
                    if (doc == null || view == null)
                    {
                        SetStatusFromRevitThread("No active view found.");
                        return;
                    }

                    _renderer.Clear(doc, view);
                    SetStatusFromRevitThread("Visualization cleared.");
                }
                catch (Exception ex)
                {
                    FileLogger.Log($"Clear failed: {ex}");
                    SetStatusFromRevitThread($"Clear failed: {ex.Message}");
                }
            });

            StatusMessage = "Clearing...";
        }

        /// <summary>
        /// Update StatusMessage from the Revit API thread by marshaling to the WPF dispatcher.
        /// </summary>
        private void SetStatusFromRevitThread(string message)
        {
            if (System.Windows.Application.Current?.Dispatcher != null)
            {
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    StatusMessage = message;
                });
            }
            else
            {
                StatusMessage = message;
            }
        }

        /// <summary>
        /// Load the latest result file from disk.
        /// </summary>
        public void ImportLatestResults()
        {
            try
            {
                AcousticJobOutput output = JobSerializer.LoadLatestOutput();
                if (output == null)
                {
                    StatusMessage = "No result files found.";
                    return;
                }

                LastOutput = output;
                LastRunSummary = $"{output.ReceiverCount} points | " +
                    $"SPL: {output.MinSplDb:F1} - {output.MaxSplDb:F1} dB | " +
                    $"STI: {output.MinSti:F2} - {output.MaxSti:F2} | " +
                    $"Imported from {output.JobId}";
                StatusMessage = "Results imported.";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Import failed: {ex.Message}";
            }
        }

        // ========================= INotifyPropertyChanged =========================

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged(string name)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    public class DetailLineSelectionFilter : Autodesk.Revit.UI.Selection.ISelectionFilter
    {
        public bool AllowElement(Element elem)
        {
            return elem is CurveElement ce && ce.CurveElementType == CurveElementType.DetailCurve;
        }

        public bool AllowReference(Autodesk.Revit.DB.Reference reference, XYZ position)
        {
            return false;
        }
    }

    public class CategorySelectionFilter : Autodesk.Revit.UI.Selection.ISelectionFilter
    {
        private readonly BuiltInCategory _category;

        public CategorySelectionFilter(BuiltInCategory category)
        {
            _category = category;
        }

        public bool AllowElement(Element elem)
        {
            return elem?.Category != null
                && RevitCompat.GetIdValue(elem.Category.Id) == (int)_category;
        }

        public bool AllowReference(Autodesk.Revit.DB.Reference reference, XYZ position)
        {
            return false;
        }
    }

    public class WallLineGroupViewModel : INotifyPropertyChanged
    {
        private readonly WallLineGroup _group;

        public WallLineGroupViewModel(WallLineGroup group)
        {
            _group = group;
        }

        public string LineStyleName => _group.LineStyleName;
        public int SegmentCount => _group.SegmentCount;
        public string TotalLength => $"{_group.TotalLengthM:F1}m";

        /// <summary>Available wall types for the ComboBox.</summary>
        public List<WallTypeInfo> AvailableWallTypes => WallTypeCatalog.All;

        public WallTypeInfo WallType
        {
            get => _group.WallType;
            set
            {
                if (_group.WallType != value)
                {
                    _group.WallType = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(WallType)));
                }
            }
        }

        public WallLineGroup GetGroup() => _group;

        public event PropertyChangedEventHandler PropertyChanged;
    }

    public class LegendItem
    {
        public string ColorHex { get; set; }
        public string Label { get; set; }

        public System.Windows.Media.Color ColorValue
        {
            get
            {
                try
                {
                    var c = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(ColorHex);
                    return c;
                }
                catch
                {
                    return System.Windows.Media.Colors.Gray;
                }
            }
        }
    }
}
