using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SkiaSharp;
using SkiaSharp.Views.Desktop;
using SkiaSharp.Views.WPF;
using SoundCalcs.Domain;
using SoundCalcs.UI.ViewModels;
using SoundCalcs.Visualization;

namespace SoundCalcs.UI
{
    /// <summary>
    /// Real-time 2D acoustic results viewer.
    ///
    /// Renders boundary walls, speaker symbols and a SPL/STI heatmap onto a
    /// SkiaSharp surface that WPF GPU-composites via DirectX.
    ///
    /// LOD progression: when new results arrive the heatmap is first drawn at
    /// 4× block size (coarse, instant), then sharpened to 2× after ~350 ms,
    /// and to native grid resolution after ~700 ms — giving a "focus-in" feel.
    ///
    /// Pan: left-drag (glides on after a flick)   Zoom: scroll wheel (smoothed, anchored at the cursor)
    /// Fit: Fit button (animates to the fitted view). Every motion starts from the current view and stops the
    /// moment new input arrives; with reduced motion the view jumps instead.
    ///
    /// Skia draws in device pixels while WPF reports the mouse in DIPs: <see cref="ToCanvas"/> converts, so the
    /// plan stays under the cursor at any display scaling.
    /// </summary>
    public partial class AcousticViewerControl : System.Windows.Controls.UserControl
    {
        // ── Heatmap colours: red (low/quiet) → green (high/loud) ──
        // Gradient stops and interpolation live in HeatmapMath so the headless
        // harness renders exactly the same colours.
        // Legacy discrete palette used only for the legend swatches
        static readonly SKColor[] HeatColors = HeatmapMath.ViewerLegendColors()
            .Select(c => new SKColor(c.R, c.G, c.B))
            .ToArray();

        static readonly SKColor SpeakerFill    = new SKColor(0x40, 0x90, 0xFF);
        static readonly SKColor SpeakerRing    = new SKColor(0x80, 0xB8, 0xFF);
        static readonly SKColor DirColor       = new SKColor(0xFF, 0xFF, 0x60, 200);

        // ── Canvas colours: follow the window's appearance (Viewer.* keys in the palettes) ──
        SKColor BgColor      = new SKColor(0x1A, 0x1A, 0x1C);
        SKColor WallColor    = new SKColor(0xAE, 0xB9, 0xC6);
        SKColor LegendBg     = new SKColor(0x24, 0x24, 0x27, 0xE0);
        SKColor TextBright   = new SKColor(0xF2, 0xF2, 0xF5);
        SKColor TextMid      = new SKColor(0xA8, 0xA8, 0xB0);
        SKColor TextDim      = new SKColor(0x6A, 0x6A, 0x72);

        // ── View transform ─────────────────────────────────────────────────
        float _panX, _panY;
        float _zoom = 50f;          // pixels per world-metre
        bool  _fitPending = true;   // set whenever geometry/results change

        // ── Pan input ──────────────────────────────────────────────────────
        bool              _isPanning;
        SKPoint           _lastMouse;   // device pixels
        // Recent pointer samples (time s, x, y) for the release velocity
        readonly List<(double T, float X, float Y)> _panHistory = new List<(double, float, float)>();

        // ── Motion: animated fit, smoothed zoom, glide ─────────────────────
        static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
        static double Now => Clock.Elapsed.TotalSeconds;

        const double FitDuration   = 0.32;   // s, ease-out
        const float  ZoomTau       = 0.06f;  // s, time constant of the zoom smoothing
        const double GlideDecel    = 0.995;  // velocity kept per millisecond (Apple's scroll projection uses 0.998)
        const float  MinGlideSpeed = 150f;   // px/s at release to start a glide
        const float  StopSpeed     = 20f;    // px/s where a glide ends

        float  _canvasW, _canvasH;           // last painted size, device pixels
        bool   _hasView;                      // content has been shown (later fits animate)
        bool   _animateNextFit;
        bool   _ticking;
        double _lastFrame;

        bool   _fitAnimating;
        double _fitT0;
        float  _fromCx, _fromCy, _fromZoom, _toCx, _toCy, _toZoom;

        bool    _zoomAnimating;
        float   _zoomTarget;
        SKPoint _zoomAnchor;                  // screen point that keeps its world point during a zoom
        float   _anchorWx, _anchorWy;

        bool  _gliding;
        float _velX, _velY;                   // px/s

        SpeakerInstance _hoverSpk;            // aimable speaker under the cursor

        // ── Probe ──────────────────────────────────────────────────────────
        bool                    _probeMode;
        readonly List<ProbePin> _probePins = new List<ProbePin>();

        // ── Speaker rotation drag ──────────────────────────────────────────
        SpeakerInstance _rotatingSpk;

        /// <summary>
        /// Called when the user finishes dragging a speaker's aim direction.
        /// Arguments: (ElementId, newAngleDegrees).
        /// </summary>
        public Action<int, double> OnSpeakerRotated { get; set; }

        // ── LOD state ──────────────────────────────────────────────────────
        // lodStep = 4 → sample every 4th receiver, draw 4× blocks (coarse)
        // lodStep = 2 → every 2nd receiver, 2× blocks
        // lodStep = 1 → all receivers, exact grid spacing (full HD)
        int             _lodStep = 1;
        DispatcherTimer _lodTimer;

        // ── Flattened geometry caches ──────────────────────────────────────
        readonly List<WallSegment2D>   _walls    = new List<WallSegment2D>();
        readonly List<SpeakerInstance> _speakers = new List<SpeakerInstance>();

        // Speakers whose horizontal aim affects the calculation (wall-mounted only):
        // only these show an aim line and can be rotated by dragging.
        readonly HashSet<SpeakerInstance> _aimableSpeakers = new HashSet<SpeakerInstance>();
        readonly List<SpeakerGroupViewModel> _speakerGroupSubscriptions = new List<SpeakerGroupViewModel>();

        // ── Heatmap bitmap cache ───────────────────────────────────────────
        SKBitmap          _heatBitmap;
        AcousticJobOutput _heatBitmapSource;
        VisualizationMode _heatBitmapMode;
        SKRect            _heatWorldRect;
        double            _heatMinVal, _heatMaxVal;



        struct ProbePin
        {
            public float  WorldX, WorldY;
            public double SplDb, Sti;
            public double[] SplDbByBand;
            public int    Index;
        }

        // ── Dependency Properties ──────────────────────────────────────────

        public static readonly DependencyProperty JobOutputProperty =
            DependencyProperty.Register(nameof(JobOutput), typeof(AcousticJobOutput),
                typeof(AcousticViewerControl),
                new PropertyMetadata(null, (d, _) =>
                {
                    var c = (AcousticViewerControl)d;
                    c._fitPending = true;
                    c._animateNextFit = true;
                    c.RefreshPinValues();
                    c.StartLodProgression();
                }));

        public static readonly DependencyProperty WallGroupsSourceProperty =
            DependencyProperty.Register(nameof(WallGroupsSource), typeof(IEnumerable),
                typeof(AcousticViewerControl),
                new PropertyMetadata(null, (d, e) =>
                {
                    var c = (AcousticViewerControl)d;
                    if (e.OldValue is INotifyCollectionChanged oldCol)
                        oldCol.CollectionChanged -= c.OnSourceCollectionChanged;
                    if (e.NewValue is INotifyCollectionChanged newCol)
                        newCol.CollectionChanged += c.OnSourceCollectionChanged;
                    c.RebuildGeometry();
                    c._fitPending = true;
                    c.Refresh();
                }));

        public static readonly DependencyProperty SpeakerGroupsSourceProperty =
            DependencyProperty.Register(nameof(SpeakerGroupsSource), typeof(IEnumerable),
                typeof(AcousticViewerControl),
                new PropertyMetadata(null, (d, e) =>
                {
                    var c = (AcousticViewerControl)d;
                    if (e.OldValue is INotifyCollectionChanged oldCol)
                        oldCol.CollectionChanged -= c.OnSourceCollectionChanged;
                    if (e.NewValue is INotifyCollectionChanged newCol)
                        newCol.CollectionChanged += c.OnSourceCollectionChanged;
                    c.RebuildGeometry();
                    c._fitPending = true;
                    c.Refresh();
                }));

        public static readonly DependencyProperty ModeProperty =
            DependencyProperty.Register(nameof(Mode), typeof(VisualizationMode),
                typeof(AcousticViewerControl),
                new PropertyMetadata(VisualizationMode.SPL,
                    (d, _) => ((AcousticViewerControl)d).Refresh()));

        public static readonly DependencyProperty GridSpacingProperty =
            DependencyProperty.Register(nameof(GridSpacing), typeof(double),
                typeof(AcousticViewerControl),
                new PropertyMetadata(1.0,
                    (d, _) => ((AcousticViewerControl)d).Refresh()));

        // CLR wrappers
        public AcousticJobOutput JobOutput
        {
            get => (AcousticJobOutput)GetValue(JobOutputProperty);
            set => SetValue(JobOutputProperty, value);
        }
        public IEnumerable WallGroupsSource
        {
            get => (IEnumerable)GetValue(WallGroupsSourceProperty);
            set => SetValue(WallGroupsSourceProperty, value);
        }
        public IEnumerable SpeakerGroupsSource
        {
            get => (IEnumerable)GetValue(SpeakerGroupsSourceProperty);
            set => SetValue(SpeakerGroupsSourceProperty, value);
        }
        public VisualizationMode Mode
        {
            get => (VisualizationMode)GetValue(ModeProperty);
            set => SetValue(ModeProperty, value);
        }
        public double GridSpacing
        {
            get => (double)GetValue(GridSpacingProperty);
            set => SetValue(GridSpacingProperty, value);
        }

        // ── Constructor ────────────────────────────────────────────────────
        public AcousticViewerControl()
        {
            InitializeComponent();
            Loaded += (s, e) => ApplyAppearance();
            Unloaded += (s, e) => StopMotion();
        }

        /// <summary>
        /// Re-reads the canvas colours from the active palette (dark, light or high contrast) and redraws.
        /// Call after the window's theme changes; the WPF parts follow on their own through DynamicResource.
        /// </summary>
        public void ApplyAppearance()
        {
            BgColor    = ResourceColor("Viewer.Canvas", BgColor);
            WallColor  = ResourceColor("Viewer.Wall", WallColor);
            LegendBg   = ResourceColor("Viewer.Panel", LegendBg);
            TextBright = ResourceColor("Viewer.Text", TextBright);
            TextMid    = ResourceColor("Viewer.TextSecondary", TextMid);
            TextDim    = ResourceColor("Viewer.TextTertiary", TextDim);
            UpdateProbeBtn();
            Refresh();
        }

        SKColor ResourceColor(string key, SKColor fallback)
        {
            object res = TryFindResource(key);
            if (res is Color c) return new SKColor(c.R, c.G, c.B, c.A);
            if (res is SolidColorBrush b) return new SKColor(b.Color.R, b.Color.G, b.Color.B, b.Color.A);
            return fallback;
        }
        // ── Collection change handler ──────────────────────────────────
        void OnSourceCollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            RebuildGeometry();
            _fitPending = true;
            _animateNextFit = true;   // show where the new content is
            Refresh();
        }
        // ── Geometry cache ─────────────────────────────────────────────────
        void RebuildGeometry()
        {
            _walls.Clear();
            _speakers.Clear();
            _aimableSpeakers.Clear();
            foreach (var svm in _speakerGroupSubscriptions)
                svm.PropertyChanged -= OnSpeakerGroupPropertyChanged;
            _speakerGroupSubscriptions.Clear();

            if (WallGroupsSource != null)
                foreach (WallLineGroupViewModel wvm in WallGroupsSource.OfType<WallLineGroupViewModel>())
                    _walls.AddRange(wvm.GetGroup().Segments);

            if (SpeakerGroupsSource != null)
                foreach (SpeakerGroupViewModel svm in SpeakerGroupsSource.OfType<SpeakerGroupViewModel>())
                {
                    _speakers.AddRange(svm.GetGroup().Instances);
                    if (JobInputBuilder.IsAimAdjustable(svm.ProfileSource))
                        foreach (var inst in svm.GetGroup().Instances)
                            _aimableSpeakers.Add(inst);
                    svm.PropertyChanged += OnSpeakerGroupPropertyChanged;
                    _speakerGroupSubscriptions.Add(svm);
                }
        }

        // Profile changes (e.g. Conical → Wall Mounted) change which speakers can be aimed
        void OnSpeakerGroupPropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(SpeakerGroupViewModel.ProfileSource)) return;
            RebuildGeometry();
            Refresh();
        }

        // ── LOD progression ────────────────────────────────────────────────
        void StartLodProgression()
        {
            _lodTimer?.Stop();
            _lodStep = 4;
            Refresh();

            int phase = 0;
            _lodTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
            _lodTimer.Tick += (_, __) =>
            {
                phase++;
                if (phase == 1)
                {
                    _lodStep = 2;
                    Refresh();
                }
                else
                {
                    _lodStep = 1;
                    Refresh();
                    _lodTimer.Stop();
                }
            };
            _lodTimer.Start();
        }

        void Refresh() => SkCanvas.InvalidateVisual();

        // ── Fit-to-content ─────────────────────────────────────────────────
        void FitView(float canvasW, float canvasH)
        {
            if (!ComputeFit(canvasW, canvasH, out float cx, out float cy, out float zoom))
            {
                _zoom = 50f;
                _panX = canvasW / 2f;
                _panY = canvasH / 2f;
                _fitPending = false;
                return;
            }
            SetView(cx, cy, zoom);
            _fitPending = false;
        }

        // Animates from the current view to the fitted one (the user sees where the content went).
        void StartFitAnimation(float canvasW, float canvasH)
        {
            _fitPending = false;
            if (!ComputeFit(canvasW, canvasH, out _toCx, out _toCy, out _toZoom)) return;
            _gliding = false;
            _zoomAnimating = false;
            CurrentCenter(out _fromCx, out _fromCy);
            _fromZoom = _zoom;
            _fitT0 = Now;
            _fitAnimating = true;
            EnsureTicking();
        }

        // World point at the canvas centre.
        void CurrentCenter(out float cx, out float cy)
        {
            cx = (_canvasW * 0.5f - _panX) / _zoom;
            cy = (_panY - _canvasH * 0.5f) / _zoom;
        }

        // Centres the world point (cx, cy) at the given zoom.
        void SetView(float cx, float cy, float zoom)
        {
            _zoom = zoom;
            _panX = _canvasW * 0.5f - cx * zoom;
            _panY = _canvasH * 0.5f + cy * zoom;   // Y flipped: screen↓ = world↑
        }

        bool ComputeFit(float canvasW, float canvasH, out float cx, out float cy, out float zoom)
        {
            cx = cy = 0f;
            zoom = 50f;
            var pts = new List<(float x, float y)>();

            foreach (var w in _walls)
            {
                pts.Add(((float)w.Start.X, (float)w.Start.Y));
                pts.Add(((float)w.End.X,   (float)w.End.Y));
            }
            foreach (var s in _speakers)
                pts.Add(((float)s.Position.X, (float)s.Position.Y));

            if (JobOutput != null)
                foreach (var r in JobOutput.Results)
                    pts.Add(((float)r.Position.X, (float)r.Position.Y));

            if (pts.Count == 0) return false;

            float xMin = pts.Min(p => p.x), xMax = pts.Max(p => p.x);
            float yMin = pts.Min(p => p.y), yMax = pts.Max(p => p.y);
            float bw = Math.Max(xMax - xMin, 0.1f);
            float bh = Math.Max(yMax - yMin, 0.1f);

            const float pad = 64f;
            zoom = Math.Min((canvasW - 2f * pad) / bw, (canvasH - 2f * pad) / bh);
            zoom = Math.Max(zoom, 1f);

            cx = (xMin + xMax) * 0.5f;
            cy = (yMin + yMax) * 0.5f;
            return true;
        }

        // ── Motion loop: runs only while something moves ──────────────────
        void EnsureTicking()
        {
            if (_ticking) return;
            _ticking = true;
            _lastFrame = Now;
            CompositionTarget.Rendering += OnFrame;
        }

        void StopTickingIfIdle()
        {
            if (!_ticking || _fitAnimating || _zoomAnimating || _gliding) return;
            CompositionTarget.Rendering -= OnFrame;
            _ticking = false;
        }

        /// <summary>Stops every motion where it is (new input takes over from the current view).</summary>
        void StopMotion()
        {
            _fitAnimating = _zoomAnimating = _gliding = false;
            StopTickingIfIdle();
        }

        void OnFrame(object sender, EventArgs e)
        {
            double now = Now;
            float dt = (float)Math.Min(0.05, Math.Max(0, now - _lastFrame));
            _lastFrame = now;

            if (_fitAnimating)
            {
                double p = Math.Min(1.0, (now - _fitT0) / FitDuration);
                float k = (float)(1 - Math.Pow(1 - p, 3));                    // ease-out: fast start, gentle landing
                float z = (float)Math.Exp(Lerp((float)Math.Log(_fromZoom), (float)Math.Log(_toZoom), k)); // zoom in log space
                SetView(Lerp(_fromCx, _toCx, k), Lerp(_fromCy, _toCy, k), z);
                if (p >= 1.0) _fitAnimating = false;
            }

            if (_zoomAnimating)
            {
                float a = 1f - (float)Math.Exp(-dt / ZoomTau);
                float lz = (float)Math.Log(_zoom);
                float lt = (float)Math.Log(_zoomTarget);
                if (Math.Abs(lt - lz) < 0.002f) { _zoom = _zoomTarget; _zoomAnimating = false; }
                else _zoom = (float)Math.Exp(lz + (lt - lz) * a);
                _panX = _zoomAnchor.X - _anchorWx * _zoom;
                _panY = _zoomAnchor.Y + _anchorWy * _zoom;
            }

            if (_gliding)
            {
                _panX += _velX * dt;
                _panY += _velY * dt;
                float keep = (float)Math.Pow(GlideDecel, dt * 1000.0);
                _velX *= keep;
                _velY *= keep;
                if (Math.Sqrt(_velX * _velX + _velY * _velY) < StopSpeed) _gliding = false;
            }

            Refresh();
            StopTickingIfIdle();
        }

        static float Lerp(float a, float b, float t) => a + (b - a) * t;

        // Mouse position (DIPs) → canvas device pixels.
        SKPoint ToCanvas(System.Windows.Point p)
        {
            float s = PixelScale;
            return new SKPoint((float)p.X * s, (float)p.Y * s);
        }

        // Device pixels per DIP, as the Skia surface was last sized (1 at 100 % display scaling).
        float PixelScale => SkCanvas.ActualWidth > 0 && _canvasW > 0 ? (float)(_canvasW / SkCanvas.ActualWidth) : 1f;

        // ── SKElement PaintSurface ─────────────────────────────────────────
        void OnPaintSurface(object sender, SKPaintSurfaceEventArgs e)
        {
            SKCanvas canvas = e.Surface.Canvas;
            float w = e.Info.Width;
            float h = e.Info.Height;
            _canvasW = w;
            _canvasH = h;

            bool hasContent = _walls.Count > 0 || _speakers.Count > 0 ||
                              (JobOutput != null && JobOutput.Results.Count > 0);

            if (_fitPending && w > 0 && h > 0)
            {
                // The first view appears in place; later fits move there so the change is easy to follow.
                if (_animateNextFit && _hasView && hasContent && !ThemeManager.ReducedMotion) StartFitAnimation(w, h);
                else FitView(w, h);
                _animateNextFit = false;
            }

            canvas.Clear(BgColor);

            if (!hasContent)
            {
                DrawEmptyMessage(canvas, w, h);
                return;
            }
            _hasView = true;

            // ── World-space layer ─────────────────────────────────────────
            // Transform: screenX = worldX * _zoom + _panX
            //            screenY = -worldY * _zoom + _panY   (Y axis flipped)
            canvas.Save();
            canvas.Translate(_panX, _panY);
            canvas.Scale(_zoom, -_zoom);

            DrawHeatmap(canvas);
            DrawWalls(canvas);
            DrawSpeakers(canvas);
            DrawPinMarkers(canvas);

            canvas.Restore();

            // ── Screen-space overlays ─────────────────────────────────────
            DrawLegend(canvas, w, h);
            DrawLodBadge(canvas, w, h);
            DrawScaleBar(canvas, w, h);
            DrawPinLabels(canvas, w, h);
            DrawAimHint(canvas);
        }

        // ── Heatmap ────────────────────────────────────────────────────────
        void DrawHeatmap(SKCanvas canvas)
        {
            if (JobOutput == null || JobOutput.Results.Count == 0) return;

            // Rebuild bitmap only when the output or mode changes.
            if (_heatBitmap == null ||
                !ReferenceEquals(_heatBitmapSource, JobOutput) ||
                _heatBitmapMode != Mode)
            {
                var results = JobOutput.Results;
                var vals = new double[results.Count];
                for (int i = 0; i < results.Count; i++)
                    vals[i] = HeatmapMath.GetValue(results[i], Mode);

                (_heatMinVal, _heatMaxVal) = HeatmapMath.ComputeViewerRange(vals, Mode);

                _heatBitmap?.Dispose();
                // Spacing comes from the results themselves, not the live Grid Spacing field,
                // so editing that field after a run can't scramble the bitmap.
                double spacing = HeatmapMath.ViewerGridSpacing(results);
                _heatBitmap       = BuildHeatmapBitmap(results, vals, _heatMinVal, _heatMaxVal - _heatMinVal, spacing, out _heatWorldRect);
                _heatBitmapSource = JobOutput;
                _heatBitmapMode   = Mode;
            }

            if (_heatBitmap == null) return;

            // Bilinear filtering gives smooth zoom-independent interpolation
            // between grid cells — no blur needed, no zoom-dependent artefacts.
            using var paint = new SKPaint { FilterQuality = SKFilterQuality.Medium };
            canvas.DrawBitmap(_heatBitmap, _heatWorldRect, paint);
        }

        static SKBitmap BuildHeatmapBitmap(
            List<ReceiverResult> results, double[] vals,
            double minVal, double range, double spacing, out SKRect worldRect)
        {
            HeatmapGrid grid = HeatmapMath.BuildViewerGrid(results, vals, minVal, range, spacing);
            if (grid == null) { worldRect = SKRect.Empty; return null; }

            var bmp = new SKBitmap(grid.Cols, grid.Rows, SKColorType.Bgra8888, SKAlphaType.Premul);
            bmp.Erase(SKColors.Transparent);

            for (int row = 0; row < grid.Rows; row++)
            {
                for (int col = 0; col < grid.Cols; col++)
                {
                    int idx = row * grid.Cols + col;
                    if (!grid.Filled[idx]) continue;
                    Rgba c = grid.Pixels[idx];
                    bmp.SetPixel(col, row, new SKColor(c.R, c.G, c.B, c.A));
                }
            }

            worldRect = new SKRect(
                (float)grid.WorldLeft, (float)grid.WorldBottom,
                (float)grid.WorldRight, (float)grid.WorldTop);
            return bmp;
        }

        // ── Walls ──────────────────────────────────────────────────────────
        void DrawWalls(SKCanvas canvas)
        {
            if (_walls.Count == 0) return;

            // Keep stroke at a constant 2 screen-pixels regardless of zoom
            float sw = 2f / _zoom;

            using var paint = new SKPaint
            {
                Color       = WallColor,
                StrokeWidth = sw,
                Style       = SKPaintStyle.Stroke,
                IsAntialias = true,
                StrokeCap   = SKStrokeCap.Round,
            };

            foreach (var w in _walls)
                canvas.DrawLine(
                    (float)w.Start.X, (float)w.Start.Y,
                    (float)w.End.X,   (float)w.End.Y,
                    paint);
        }

        // ── Speaker symbols ────────────────────────────────────────────────
        void DrawSpeakers(SKCanvas canvas)
        {
            if (_speakers.Count == 0) return;

            // Radius: 8 screen-pixels, minimum 0.25 m in world space
            float radius = Math.Max(0.25f, 8f / _zoom);
            float sw     = 1.5f / _zoom;

            using var fill   = new SKPaint { Color = SpeakerFill.WithAlpha(200), Style = SKPaintStyle.Fill, IsAntialias = true };
            using var ring   = new SKPaint { Color = SpeakerRing,   StrokeWidth = sw, Style = SKPaintStyle.Stroke, IsAntialias = true };
            using var dir    = new SKPaint { Color = DirColor,       StrokeWidth = sw, Style = SKPaintStyle.Stroke, IsAntialias = true, StrokeCap = SKStrokeCap.Round };

            foreach (var s in _speakers)
            {
                float sx = (float)s.Position.X;
                float sy = (float)s.Position.Y;

                // Project FacingDirection to XY plane for direction indicator
                float dx = (float)s.FacingDirection.X;
                float dy = (float)s.FacingDirection.Y;
                float hLen = (float)Math.Sqrt(dx * dx + dy * dy);

                if (hLen > 0.15f && _aimableSpeakers.Contains(s))
                {
                    // Scale the indicator to 2.5× the symbol radius
                    float scale = radius * 2.5f / hLen;
                    canvas.DrawLine(sx, sy, sx + dx * scale, sy + dy * scale, dir);
                }

                canvas.DrawCircle(sx, sy, radius, fill);
                canvas.DrawCircle(sx, sy, radius, ring);

                // Aimable speaker under the cursor or being dragged: a halo says it can be grabbed
                if (s == _hoverSpk || s == _rotatingSpk)
                {
                    using var halo = new SKPaint { Color = DirColor, StrokeWidth = 2f * sw, Style = SKPaintStyle.Stroke, IsAntialias = true };
                    canvas.DrawCircle(sx, sy, radius * 1.7f, halo);
                }

                // Draw A/B line label centered on the speaker icon
                if (!string.IsNullOrEmpty(s.AbLine))
                {
                    canvas.Save();
                    canvas.Translate(sx, sy);
                    // Undo the world-space zoom and Y-flip so text renders upright at screen-pixel size
                    canvas.Scale(1f / _zoom, -1f / _zoom);
                    float fontSize = 9f;
                    using var labelPaint = new SKPaint
                    {
                        Color       = SKColors.White,
                        TextSize    = fontSize,
                        IsAntialias = true,
                        TextAlign   = SKTextAlign.Center,
                        Typeface    = SKTypeface.FromFamilyName("Segoe UI") ?? SKTypeface.Default,
                        FakeBoldText = true
                    };
                    canvas.DrawText(s.AbLine, 0f, fontSize * 0.35f, labelPaint);
                    canvas.Restore();
                }
            }
        }

        // ── Aim hint (screen-space): "Drag to aim" on hover, the live angle while dragging ──
        void DrawAimHint(SKCanvas canvas)
        {
            SpeakerInstance s = _rotatingSpk ?? _hoverSpk;
            if (s == null) return;

            string text = _rotatingSpk != null
                ? $"{(Math.Atan2(s.FacingDirection.Y, s.FacingDirection.X) * 180.0 / Math.PI + 360.0) % 360.0:F0}°"
                : "Drag to aim";
            float sx =  (float)s.Position.X * _zoom + _panX;
            float sy = -(float)s.Position.Y * _zoom + _panY;
            float r  = Math.Max(0.25f * _zoom, 8f) * 1.7f;

            using var tf = new SKPaint
            {
                Color       = TextBright,
                TextSize    = 11f * PixelScale,
                IsAntialias = true,
                Typeface    = SKTypeface.FromFamilyName("Segoe UI") ?? SKTypeface.Default,
            };
            using var bg = new SKPaint { Color = LegendBg, Style = SKPaintStyle.Fill, IsAntialias = true };
            float tw = tf.MeasureText(text);
            float padX = 6f * PixelScale, h = 18f * PixelScale;
            float bx = sx + r + 4f * PixelScale;
            float by = sy - h * 0.5f;
            canvas.DrawRoundRect(bx, by, tw + 2 * padX, h, h * 0.5f, h * 0.5f, bg);
            canvas.DrawText(text, bx + padX, by + h * 0.5f + tf.TextSize * 0.35f, tf);
        }

        // ── Colour legend (screen-space) ───────────────────────────────────
        void DrawLegend(SKCanvas canvas, float cw, float ch)
        {
            if (_heatBitmap == null) return;  // nothing rendered yet

            bool isSti    = Mode == VisualizationMode.STI;
            bool isSplA   = Mode == VisualizationMode.SPL_A;
            bool isC80Leg = Mode == VisualizationMode.C80;
            int  bandIdx  = MainViewModel.GetOctaveBandIndex(Mode);
            bool isPerBand = bandIdx >= 0;

            // Use the same range that was used to build the heatmap bitmap
            double minVal = _heatMinVal;
            double maxVal = _heatMaxVal;

            const float swW  = 16f;
            const float swH  = 18f;
            const float gap  = 2f;
            const float pad  = 8f;
            const float txtX = swW + 6f;
            float totalH  = (swH + gap) * HeatColors.Length + pad;
            float panelW  = 108f;
            float ox      = cw - panelW - 6f;
            float oy      = 10f;

            using var bg = new SKPaint { Color = LegendBg, Style = SKPaintStyle.Fill };
            canvas.DrawRoundRect(ox - pad, oy - pad, panelW + pad * 2f, totalH + pad, 4f, 4f, bg);

            using var swatch = new SKPaint { Style = SKPaintStyle.Fill };
            using var tf = new SKPaint
            {
                Color      = TextBright,
                TextSize   = 10f,
                IsAntialias = true,
                Typeface   = SKTypeface.FromFamilyName("Segoe UI") ?? SKTypeface.Default,
            };
            using var header = tf.Clone();
            header.TextSize = 10f;
            header.Color    = TextMid;

            string modeLabel = isSti ? "STI"
                : isSplA   ? "dBA"
                : isC80Leg ? "C80"
                : isPerBand ? $"SPL {OctaveBands.Labels[bandIdx]} Hz"
                : "SPL";
            canvas.DrawText(modeLabel, ox, oy + 1f, header);

            float rowStart = oy + 14f;
            // Draw bands from high (top) to low (bottom) so the legend reads max→min top-down
            for (int i = HeatColors.Length - 1; i >= 0; i--)
            {
                int row = HeatColors.Length - 1 - i;
                float sy = rowStart + row * (swH + gap);

                swatch.Color = HeatColors[i];
                canvas.DrawRect(ox, sy, swW, swH, swatch);

                double lo = minVal + (maxVal - minVal) * i       / HeatColors.Length;
                double hi = minVal + (maxVal - minVal) * (i + 1) / HeatColors.Length;
                string label = HeatmapMath.ViewerLegendLabel(lo, hi, Mode);
                canvas.DrawText(label, ox + txtX, sy + swH - 4f, tf);
            }
        }

        // ── LOD quality badge ──────────────────────────────────────────────
        void DrawLodBadge(SKCanvas canvas, float cw, float ch)
        {
            if (JobOutput == null || JobOutput.Results.Count == 0) return;

            string label = _lodStep == 1 ? "HD" : _lodStep == 2 ? "▲HD" : "...";
            var color = _lodStep == 1
                ? new SKColor(0x40, 0xCC, 0x60)
                : new SKColor(0xFF, 0xA0, 0x40);

            using var paint = new SKPaint
            {
                Color      = color,
                TextSize   = 10f,
                IsAntialias = true,
                Typeface   = SKTypeface.FromFamilyName("Segoe UI") ?? SKTypeface.Default,
            };
            canvas.DrawText(label, 10f, ch - 8f, paint);
        }

        // ── Scale bar ──────────────────────────────────────────────────────
        void DrawScaleBar(SKCanvas canvas, float cw, float ch)
        {
            // Target bar: roughly 80 px wide → find nearest nice world distance
            float targetPx = 80f;
            float worldDist = targetPx / _zoom;

            // Round to a nice number
            float[] nice = { 0.1f, 0.25f, 0.5f, 1f, 2f, 5f, 10f, 20f, 50f, 100f };
            float barWorld = nice.OrderBy(n => Math.Abs(n - worldDist)).First();
            float barPx    = barWorld * _zoom;

            float bx = 10f;
            float by = ch - 22f;

            using var linePaint = new SKPaint
            {
                Color = TextMid,
                StrokeWidth = 1.5f,
                Style = SKPaintStyle.Stroke,
                IsAntialias = true,
            };
            using var textPaint = new SKPaint
            {
                Color      = TextMid,
                TextSize   = 9f,
                IsAntialias = true,
                Typeface   = SKTypeface.FromFamilyName("Segoe UI") ?? SKTypeface.Default,
            };

            // Horizontal line with end ticks
            canvas.DrawLine(bx, by, bx + barPx, by, linePaint);
            canvas.DrawLine(bx, by - 3f, bx, by + 3f, linePaint);
            canvas.DrawLine(bx + barPx, by - 3f, bx + barPx, by + 3f, linePaint);

            string label = barWorld >= 1f ? $"{barWorld:F0} m" : $"{barWorld * 100f:F0} cm";
            canvas.DrawText(label, bx + barPx * 0.5f - 12f, by - 5f, textPaint);
        }

        // ── Empty state message ────────────────────────────────────────────
        void DrawEmptyMessage(SKCanvas canvas, float cw, float ch)
        {
            using var primary = new SKPaint
            {
                Color      = TextMid,
                TextSize   = 13f,
                IsAntialias = true,
                Typeface   = SKTypeface.FromFamilyName("Segoe UI") ?? SKTypeface.Default,
                TextAlign  = SKTextAlign.Center,
            };
            using var hint = primary.Clone();
            hint.TextSize = 10f;
            hint.Color    = TextDim;

            canvas.DrawText("Select boundary lines or pick speakers",     cw * 0.5f, ch * 0.5f - 12f, primary);
            canvas.DrawText("to see the 2D scene, then run analysis.",    cw * 0.5f, ch * 0.5f + 6f,  primary);
            canvas.DrawText("Pan: left-drag  ·  Zoom: scroll wheel",      cw * 0.5f, ch * 0.5f + 26f, hint);
        }

        // ── Input: mouse ───────────────────────────────────────────────────
        void Canvas_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                // Grabbing the plan stops any glide or animated fit exactly where it is.
                StopMotion();
                var mpos = ToCanvas(e.GetPosition(SkCanvas));

                if (_probeMode)
                {
                    PlaceProbePin(mpos.X, mpos.Y);
                    e.Handled = true;
                    return;
                }

                // Start speaker-rotation drag when clicking near a speaker symbol
                var spk  = HitTestSpeaker(mpos.X, mpos.Y);
                if (spk != null)
                {
                    _rotatingSpk = spk;
                    SkCanvas.CaptureMouse();
                    Refresh();
                    e.Handled = true;
                    return;
                }

                _isPanning  = true;
                _lastMouse  = mpos;
                _panHistory.Clear();
                _panHistory.Add((Now, mpos.X, mpos.Y));
                SkCanvas.CaptureMouse();
            }
        }

        void Canvas_MouseMove(object sender, MouseEventArgs e)
        {
            var cur = ToCanvas(e.GetPosition(SkCanvas));

            if (_rotatingSpk != null)
            {
                float wx = (cur.X - _panX) / _zoom;
                float wy = -(cur.Y - _panY) / _zoom;
                double dx   = wx - _rotatingSpk.Position.X;
                double dy   = wy - _rotatingSpk.Position.Y;
                double dist = Math.Sqrt(dx * dx + dy * dy);
                if (dist > 0.05)   // ignore jitter when cursor is right on top
                {
                    double fz   = _rotatingSpk.FacingDirection.Z;
                    double hLen = Math.Sqrt(Math.Max(0.0, 1.0 - fz * fz));
                    if (hLen < 1e-6) hLen = 1.0;
                    _rotatingSpk.FacingDirection = new Vec3(
                        dx / dist * hLen, dy / dist * hLen, fz);
                }
                Refresh();
                return;
            }

            if (_isPanning)
            {
                // 1:1 with the pointer: the point grabbed stays under it
                _panX += cur.X - _lastMouse.X;
                _panY += cur.Y - _lastMouse.Y;
                _lastMouse = cur;
                double now = Now;
                _panHistory.Add((now, cur.X, cur.Y));
                _panHistory.RemoveAll(s => now - s.T > 0.1);
                Refresh();
                return;
            }

            // Hover: highlight an aimable speaker and label it ("Drag to aim"); the canvas keeps its pan cursor.
            if (!_probeMode)
            {
                var spk = HitTestSpeaker(cur.X, cur.Y);
                if (spk != _hoverSpk)
                {
                    _hoverSpk = spk;
                    Refresh();
                }
            }
        }

        void Canvas_MouseLeave(object sender, MouseEventArgs e)
        {
            if (_hoverSpk == null || _rotatingSpk != null) return;
            _hoverSpk = null;
            Refresh();
        }

        void Canvas_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_rotatingSpk != null)
            {
                double angleDeg = Math.Atan2(
                    _rotatingSpk.FacingDirection.Y,
                    _rotatingSpk.FacingDirection.X) * 180.0 / Math.PI;
                OnSpeakerRotated?.Invoke(_rotatingSpk.ElementId, angleDeg);
                _rotatingSpk = null;
                SkCanvas.ReleaseMouseCapture();
                Refresh();
                return;
            }

            if (!_isPanning) return;
            _isPanning = false;
            SkCanvas.ReleaseMouseCapture();
            StartGlide();
        }

        // Continues a flick at the release velocity and lets it slow down (no glide after a pause).
        void StartGlide()
        {
            if (ThemeManager.ReducedMotion || _panHistory.Count < 2) return;
            var first = _panHistory[0];
            var last = _panHistory[_panHistory.Count - 1];
            double span = last.T - first.T;
            if (span < 0.01 || Now - last.T > 0.05) return;

            _velX = (float)((last.X - first.X) / span);
            _velY = (float)((last.Y - first.Y) / span);
            float speed = (float)Math.Sqrt(_velX * _velX + _velY * _velY);
            if (speed < MinGlideSpeed) return;
            const float maxSpeed = 6000f;
            if (speed > maxSpeed)
            {
                _velX *= maxSpeed / speed;
                _velY *= maxSpeed / speed;
            }
            _gliding = true;
            EnsureTicking();
        }

        void Canvas_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            var pos = ToCanvas(e.GetPosition(SkCanvas));
            _fitAnimating = false;
            _gliding = false;

            // World point under the cursor now; it stays under the cursor while the zoom settles.
            _zoomAnchor = pos;
            _anchorWx =  (pos.X - _panX) / _zoom;
            _anchorWy = -(pos.Y - _panY) / _zoom;

            // Proportional to the wheel delta, so precision touchpads zoom in small steps
            float factor = (float)Math.Pow(1.15, e.Delta / 120.0);
            float from = _zoomAnimating ? _zoomTarget : _zoom;
            _zoomTarget = Math.Min(Math.Max(from * factor, 2f), 8000f);

            if (ThemeManager.ReducedMotion)
            {
                _zoom = _zoomTarget;
                _panX = pos.X - _anchorWx * _zoom;
                _panY = pos.Y + _anchorWy * _zoom;
                Refresh();
                return;
            }
            _zoomAnimating = true;
            EnsureTicking();
        }

        // ── Fit button ─────────────────────────────────────────────────────
        void FitBtn_Click(object sender, RoutedEventArgs e)
        {
            _fitPending = true;
            _animateNextFit = true;
            Refresh();
        }

        // ── Probe button ───────────────────────────────────────────────────
        void ProbeBtn_Click(object sender, RoutedEventArgs e)
        {
            _probeMode = !_probeMode;
            UpdateProbeBtn();
            SkCanvas.Cursor = _probeMode ? Cursors.Cross : Cursors.Hand;
        }

        // The active tool gets the accent fill, like a selected toolbar item.
        void UpdateProbeBtn()
        {
            if (TryFindResource(_probeMode ? "ViewerTool.Active" : "ViewerTool") is Style style)
                ProbeBtn.Style = style;
            ProbeBtn.ToolTip = _probeMode
                ? "Probe is on: click the plan to pin a reading. Click here to turn it off."
                : "Probe: click the plan to pin a reading";
        }

        void ClearPinsBtn_Click(object sender, RoutedEventArgs e)
        {
            _probePins.Clear();
            UpdateClearPinsBtn();
            Refresh();
        }

        void UpdateClearPinsBtn()
            => ClearPinsBtn.Visibility = _probePins.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        // ── Probe placement ────────────────────────────────────────────────
        void PlaceProbePin(float screenX, float screenY)
        {
            if (JobOutput == null || JobOutput.Results.Count == 0) return;
            float wx =  (screenX - _panX) / _zoom;
            float wy = -(screenY - _panY) / _zoom;
            var r = FindNearestResult(wx, wy);
            if (r == null) return;
            _probePins.Add(new ProbePin
            {
                WorldX      = (float)r.Position.X,
                WorldY      = (float)r.Position.Y,
                SplDb       = r.SplDb,
                Sti         = r.Sti,
                SplDbByBand = r.SplDbByBand,
                Index       = _probePins.Count + 1,
            });
            UpdateClearPinsBtn();
            Refresh();
        }

        // Hit-test: returns the speaker within 8 screen pixels of (screenX, screenY), or null.
        SpeakerInstance HitTestSpeaker(float screenX, float screenY)
        {
            if (_speakers.Count == 0) return null;
            float wx   = (screenX - _panX) / _zoom;
            float wy   = -(screenY - _panY) / _zoom;
            float hitR = 10f * PixelScale / _zoom;  // 10 DIP hit radius
            SpeakerInstance best  = null;
            double          bestD = hitR;
            foreach (var s in _aimableSpeakers)
            {
                double dx = s.Position.X - wx;
                double dy = s.Position.Y - wy;
                double d  = Math.Sqrt(dx * dx + dy * dy);
                if (d < bestD) { bestD = d; best = s; }
            }
            return best;
        }

        ReceiverResult FindNearestResult(float wx, float wy)
        {
            if (JobOutput == null) return null;
            ReceiverResult best  = null;
            double         bestD = double.MaxValue;
            foreach (var r in JobOutput.Results)
            {
                double dx = r.Position.X - wx;
                double dy = r.Position.Y - wy;
                double d2 = dx * dx + dy * dy;
                if (d2 < bestD) { bestD = d2; best = r; }
            }
            return best;
        }

        // Re-sample all placed pins against the current JobOutput results.
        // Called whenever a new analysis result arrives.
        void RefreshPinValues()
        {
            if (JobOutput == null || JobOutput.Results.Count == 0 || _probePins.Count == 0) return;
            for (int i = 0; i < _probePins.Count; i++)
            {
                var pin = _probePins[i];
                var r   = FindNearestResult(pin.WorldX, pin.WorldY);
                if (r == null) continue;
                pin.SplDb       = r.SplDb;
                pin.Sti         = r.Sti;
                pin.SplDbByBand = r.SplDbByBand;
                _probePins[i]   = pin;
            }
        }

        // ── Pin markers (world-space) ──────────────────────────────────────
        void DrawPinMarkers(SKCanvas canvas)
        {
            if (_probePins.Count == 0) return;
            float r  = 5f / _zoom;
            float sw = 1.5f / _zoom;
            using var fill = new SKPaint { Color = new SKColor(0xFF, 0xE0, 0x40, 230), Style = SKPaintStyle.Fill, IsAntialias = true };
            using var ring = new SKPaint { Color = new SKColor(0x22, 0x22, 0x22, 220), StrokeWidth = sw, Style = SKPaintStyle.Stroke, IsAntialias = true };
            foreach (var p in _probePins)
            {
                canvas.DrawCircle(p.WorldX, p.WorldY, r, fill);
                canvas.DrawCircle(p.WorldX, p.WorldY, r, ring);
            }
        }

        // ── Pin callout labels (screen-space) ──────────────────────────────
        void DrawPinLabels(SKCanvas canvas, float cw, float ch)
        {
            if (_probePins.Count == 0) return;
            using var typeface = SKTypeface.FromFamilyName("Segoe UI") ?? SKTypeface.Default;
            using var bgPaint   = new SKPaint { Color = new SKColor(0x12, 0x12, 0x12, 0xEE), Style = SKPaintStyle.Fill };
            using var brdPaint  = new SKPaint { Color = new SKColor(0xFF, 0xE0, 0x40, 0xB0), StrokeWidth = 1f, Style = SKPaintStyle.Stroke, IsAntialias = true };
            using var stemPaint = new SKPaint { Color = new SKColor(0xFF, 0xE0, 0x40, 0x80), StrokeWidth = 1f, Style = SKPaintStyle.Stroke, IsAntialias = true };
            using var hdrPaint  = new SKPaint { Color = new SKColor(0xFF, 0xE0, 0x40), TextSize = 10f, Typeface = typeface, IsAntialias = true };
            using var valPaint  = new SKPaint { Color = new SKColor(0xCC, 0xCC, 0xCC), TextSize = 9.5f, Typeface = typeface, IsAntialias = true };

            const float lineH = 13f;
            const float padX  = 6f;
            const float padY  = 4f;

            foreach (var p in _probePins)
            {
                float sx =  p.WorldX * _zoom + _panX;
                float sy = -p.WorldY * _zoom + _panY;

                string header = $"Pin {p.Index}";
                string l1     = $"SPL  {p.SplDb:F1} dB";
                string l2     = $"STI  {p.Sti:F2}";

                float tw   = Math.Max(hdrPaint.MeasureText(header),
                             Math.Max(valPaint.MeasureText(l1), valPaint.MeasureText(l2)));
                float boxW = tw + 2f * padX;
                float boxH = 3f * lineH + 2f * padY;

                float bx = sx + 10f;
                float by = sy - boxH - 10f;
                if (bx + boxW > cw - 4f) bx = sx - boxW - 10f;
                if (by < 4f)              by = sy + 10f;

                canvas.DrawLine(sx, sy, bx + boxW * 0.5f, by + boxH, stemPaint);
                canvas.DrawRoundRect(bx, by, boxW, boxH, 3f, 3f, bgPaint);
                canvas.DrawRoundRect(bx, by, boxW, boxH, 3f, 3f, brdPaint);
                canvas.DrawText(header, bx + padX, by + padY + lineH,        hdrPaint);
                canvas.DrawText(l1,     bx + padX, by + padY + lineH * 2f,   valPaint);
                canvas.DrawText(l2,     bx + padX, by + padY + lineH * 3f,   valPaint);
            }
        }
    }
}
