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
    /// Pan: left-drag   Zoom: scroll wheel   Fit: Fit button
    /// </summary>
    public partial class AcousticViewerControl : System.Windows.Controls.UserControl
    {
        // ── Smooth heatmap gradient: red (low/quiet) → green (high/loud) ──
        // Stops are evenly spaced 0..1. SampleGradient() lerps between them.
        static readonly SKColor[] GradientStops =
        {
            new SKColor(0xCC, 0x00, 0x00),  // 0.00 – red        (quietest)
            new SKColor(0xFF, 0x44, 0x00),  // 0.17 – red-orange
            new SKColor(0xFF, 0xAA, 0x00),  // 0.33 – amber
            new SKColor(0xFF, 0xFF, 0x00),  // 0.50 – yellow
            new SKColor(0xAA, 0xFF, 0x00),  // 0.67 – yellow-green
            new SKColor(0x44, 0xDD, 0x00),  // 0.83 – lime
            new SKColor(0x00, 0xAA, 0x00),  // 1.00 – green      (loudest)
        };

        static SKColor SampleGradient(double t)
        {
            t = t < 0.0 ? 0.0 : t > 1.0 ? 1.0 : t;
            double scaled = t * (GradientStops.Length - 1);
            int    lo     = (int)scaled;
            int    hi     = lo + 1 < GradientStops.Length ? lo + 1 : lo;
            double frac   = scaled - lo;
            SKColor a = GradientStops[lo], b = GradientStops[hi];
            return new SKColor(
                (byte)(a.Red   + (b.Red   - a.Red)   * frac),
                (byte)(a.Green + (b.Green - a.Green) * frac),
                (byte)(a.Blue  + (b.Blue  - a.Blue)  * frac),
                210);
        }

        // Legacy discrete palette used only for the legend swatches
        static readonly SKColor[] HeatColors = new SKColor[8];
        static AcousticViewerControl()
        {
            for (int i = 0; i < 8; i++)
            {
                var c = SampleGradient(i / 7.0);
                HeatColors[i] = new SKColor(c.Red, c.Green, c.Blue);
            }
        }

        static readonly SKColor BgColor       = new SKColor(0x1F, 0x1F, 0x1F);
        static readonly SKColor WallColor      = new SKColor(0xAA, 0xBB, 0xCC);
        static readonly SKColor SpeakerFill    = new SKColor(0x40, 0x90, 0xFF);
        static readonly SKColor SpeakerRing    = new SKColor(0x80, 0xB8, 0xFF);
        static readonly SKColor DirColor       = new SKColor(0xFF, 0xFF, 0x60, 200);
        static readonly SKColor LegendBg       = new SKColor(0x18, 0x18, 0x18, 0xCC);
        static readonly SKColor TextBright     = new SKColor(0xEE, 0xEE, 0xEE);
        static readonly SKColor TextDim        = new SKColor(0x66, 0x66, 0x66);

        // ── View transform ─────────────────────────────────────────────────
        float _panX, _panY;
        float _zoom = 50f;          // pixels per world-metre
        bool  _fitPending = true;   // set whenever geometry/results change

        // ── Pan input ──────────────────────────────────────────────────────
        bool              _isPanning;
        System.Windows.Point _lastMouse;

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
        }
        // ── Collection change handler ──────────────────────────────────
        void OnSourceCollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            RebuildGeometry();
            _fitPending = true;
            Refresh();
        }
        // ── Geometry cache ─────────────────────────────────────────────────
        void RebuildGeometry()
        {
            _walls.Clear();
            _speakers.Clear();

            if (WallGroupsSource != null)
                foreach (WallLineGroupViewModel wvm in WallGroupsSource.OfType<WallLineGroupViewModel>())
                    _walls.AddRange(wvm.GetGroup().Segments);

            if (SpeakerGroupsSource != null)
                foreach (SpeakerGroupViewModel svm in SpeakerGroupsSource.OfType<SpeakerGroupViewModel>())
                    _speakers.AddRange(svm.GetGroup().Instances);
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

            if (pts.Count == 0)
            {
                _zoom = 50f;
                _panX = canvasW / 2f;
                _panY = canvasH / 2f;
                _fitPending = false;
                return;
            }

            float xMin = pts.Min(p => p.x), xMax = pts.Max(p => p.x);
            float yMin = pts.Min(p => p.y), yMax = pts.Max(p => p.y);
            float bw = Math.Max(xMax - xMin, 0.1f);
            float bh = Math.Max(yMax - yMin, 0.1f);

            const float pad = 64f;
            _zoom = Math.Min((canvasW - 2f * pad) / bw, (canvasH - 2f * pad) / bh);
            _zoom = Math.Max(_zoom, 1f);

            float cx = (xMin + xMax) * 0.5f;
            float cy = (yMin + yMax) * 0.5f;
            _panX = canvasW * 0.5f - cx * _zoom;
            _panY = canvasH * 0.5f + cy * _zoom;  // Y flipped: screen↓ = world↑
            _fitPending = false;
        }

        // ── SKElement PaintSurface ─────────────────────────────────────────
        void OnPaintSurface(object sender, SKPaintSurfaceEventArgs e)
        {
            SKCanvas canvas = e.Surface.Canvas;
            float w = e.Info.Width;
            float h = e.Info.Height;

            if (_fitPending && w > 0 && h > 0)
                FitView(w, h);

            canvas.Clear(BgColor);

            bool hasContent = _walls.Count > 0 || _speakers.Count > 0 ||
                              (JobOutput != null && JobOutput.Results.Count > 0);

            if (!hasContent)
            {
                DrawEmptyMessage(canvas, w, h);
                return;
            }

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
        }

        // ── Heatmap ────────────────────────────────────────────────────────
        void DrawHeatmap(SKCanvas canvas)
        {
            if (JobOutput == null || JobOutput.Results.Count == 0) return;

            bool isSti     = Mode == VisualizationMode.STI;
            int  bandIdx   = MainViewModel.GetOctaveBandIndex(Mode);
            bool isPerBand = bandIdx >= 0;

            // Rebuild bitmap only when the output or mode changes.
            if (_heatBitmap == null ||
                !ReferenceEquals(_heatBitmapSource, JobOutput) ||
                _heatBitmapMode != Mode)
            {
                var results = JobOutput.Results;
                var vals = new double[results.Count];
                for (int i = 0; i < results.Count; i++)
                    vals[i] = isSti     ? results[i].Sti
                            : isPerBand ? results[i].SplDbByBand[bandIdx]
                            : results[i].SplDb;

                var sorted = (double[])vals.Clone();
                System.Array.Sort(sorted);
                _heatMinVal = sorted[Math.Max(0, (int)(sorted.Length * 0.02))];
                _heatMaxVal = sorted[Math.Min(sorted.Length - 1, (int)(sorted.Length * 0.98))];
                if (_heatMaxVal - _heatMinVal < (isSti ? 0.05 : 3.0))
                {
                    double mid = (_heatMinVal + _heatMaxVal) * 0.5;
                    _heatMinVal = mid - (isSti ? 0.25 : 15.0);
                    _heatMaxVal = mid + (isSti ? 0.25 : 15.0);
                }

                _heatBitmap?.Dispose();
                _heatBitmap       = BuildHeatmapBitmap(results, vals, _heatMinVal, _heatMaxVal - _heatMinVal, GridSpacing, out _heatWorldRect);
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
            if (results.Count == 0) { worldRect = SKRect.Empty; return null; }
            if (spacing <= 0) spacing = 1.0;

            double xMin = double.MaxValue, xMax = double.MinValue;
            double yMin = double.MaxValue, yMax = double.MinValue;
            foreach (var r in results)
            {
                if (r.Position.X < xMin) xMin = r.Position.X;
                if (r.Position.X > xMax) xMax = r.Position.X;
                if (r.Position.Y < yMin) yMin = r.Position.Y;
                if (r.Position.Y > yMax) yMax = r.Position.Y;
            }

            int cols = Math.Max(1, (int)Math.Round((xMax - xMin) / spacing) + 1);
            int rows = Math.Max(1, (int)Math.Round((yMax - yMin) / spacing) + 1);

            var bmp = new SKBitmap(cols, rows, SKColorType.Bgra8888, SKAlphaType.Premul);
            bmp.Erase(SKColors.Transparent);

            for (int i = 0; i < results.Count; i++)
            {
                int col = (int)Math.Round((results[i].Position.X - xMin) / spacing);
                int row = (int)Math.Round((results[i].Position.Y - yMin) / spacing);
                if ((uint)col < (uint)cols && (uint)row < (uint)rows)
                    bmp.SetPixel(col, row, SampleGradient((vals[i] - minVal) / range));
            }

            float hs = (float)(spacing * 0.5);
            worldRect = new SKRect(
                (float)xMin - hs, (float)yMin - hs,
                (float)xMax + hs, (float)yMax + hs);
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

                if (hLen > 0.15f)
                {
                    // Scale the indicator to 2.5× the symbol radius
                    float scale = radius * 2.5f / hLen;
                    canvas.DrawLine(sx, sy, sx + dx * scale, sy + dy * scale, dir);
                }

                canvas.DrawCircle(sx, sy, radius, fill);
                canvas.DrawCircle(sx, sy, radius, ring);
            }
        }

        // ── Colour legend (screen-space) ───────────────────────────────────
        void DrawLegend(SKCanvas canvas, float cw, float ch)
        {
            if (_heatBitmap == null) return;  // nothing rendered yet

            bool isSti    = Mode == VisualizationMode.STI;
            int  bandIdx  = MainViewModel.GetOctaveBandIndex(Mode);
            bool isPerBand = bandIdx >= 0;
            string unit   = isSti ? "" : " dB";

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
            header.Color    = new SKColor(0xAA, 0xAA, 0xAA);

            string modeLabel = isSti ? "STI" : isPerBand
                ? $"SPL {OctaveBands.Labels[bandIdx]} Hz"
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
                string label = isSti
                    ? $"{lo:F2} – {hi:F2}"
                    : $"{lo:F0} – {hi:F0}{unit}";
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
                Color = new SKColor(0xAA, 0xAA, 0xAA),
                StrokeWidth = 1.5f,
                Style = SKPaintStyle.Stroke,
                IsAntialias = true,
            };
            using var textPaint = new SKPaint
            {
                Color      = new SKColor(0xAA, 0xAA, 0xAA),
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
                Color      = TextDim,
                TextSize   = 13f,
                IsAntialias = true,
                Typeface   = SKTypeface.FromFamilyName("Segoe UI") ?? SKTypeface.Default,
                TextAlign  = SKTextAlign.Center,
            };
            using var hint = primary.Clone();
            hint.TextSize = 10f;
            hint.Color    = new SKColor(0x44, 0x44, 0x44);

            canvas.DrawText("Select boundary lines or pick speakers",     cw * 0.5f, ch * 0.5f - 12f, primary);
            canvas.DrawText("to see the 2D scene, then run analysis.",    cw * 0.5f, ch * 0.5f + 6f,  primary);
            canvas.DrawText("Pan: left-drag  ·  Zoom: scroll wheel",      cw * 0.5f, ch * 0.5f + 26f, hint);
        }

        // ── Input: mouse ───────────────────────────────────────────────────
        void Canvas_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                if (_probeMode)
                {
                    var pos = e.GetPosition(SkCanvas);
                    PlaceProbePin((float)pos.X, (float)pos.Y);
                    e.Handled = true;
                    return;
                }

                // Start speaker-rotation drag when clicking near a speaker symbol
                var mpos = e.GetPosition(SkCanvas);
                var spk  = HitTestSpeaker((float)mpos.X, (float)mpos.Y);
                if (spk != null)
                {
                    _rotatingSpk = spk;
                    SkCanvas.CaptureMouse();
                    e.Handled = true;
                    return;
                }

                _isPanning  = true;
                _lastMouse  = mpos;
                SkCanvas.CaptureMouse();
            }
        }

        void Canvas_MouseMove(object sender, MouseEventArgs e)
        {
            var cur = e.GetPosition(SkCanvas);

            if (_rotatingSpk != null)
            {
                float wx = (float)(cur.X - _panX) / _zoom;
                float wy = -(float)(cur.Y - _panY) / _zoom;
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
                _panX += (float)(cur.X - _lastMouse.X);
                _panY += (float)(cur.Y - _lastMouse.Y);
                _lastMouse = cur;
                Refresh();
                return;
            }

            // Hover: change cursor when over a speaker to hint rotation is available
            if (!_probeMode)
            {
                var spk = HitTestSpeaker((float)cur.X, (float)cur.Y);
                SkCanvas.Cursor = spk != null ? Cursors.SizeAll : Cursors.Hand;
            }
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
                return;
            }

            if (!_isPanning) return;
            _isPanning = false;
            SkCanvas.ReleaseMouseCapture();
        }

        void Canvas_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            var pos = e.GetPosition(SkCanvas);
            float cx = (float)pos.X;
            float cy = (float)pos.Y;

            // World coords under the cursor (accounting for Y flip)
            float wx =  (cx - _panX) / _zoom;
            float wy = -(cy - _panY) / _zoom;

            float factor = e.Delta > 0 ? 1.15f : 1f / 1.15f;
            _zoom = Math.Min(Math.Max(_zoom * factor, 2f), 8000f);

            // Keep the world point under the cursor pinned to cursor position
            _panX = cx - wx * _zoom;
            _panY = cy + wy * _zoom;

            Refresh();
        }

        // ── Fit button ─────────────────────────────────────────────────────
        void FitBtn_Click(object sender, RoutedEventArgs e)
        {
            _fitPending = true;
            Refresh();
        }

        // ── Probe button ───────────────────────────────────────────────────
        void ProbeBtn_Click(object sender, RoutedEventArgs e)
        {
            _probeMode = !_probeMode;
            ProbeBtn.Background = _probeMode
                ? new SolidColorBrush(Color.FromArgb(0xAA, 0x40, 0xA0, 0xFF))
                : new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
            ProbeBtn.Foreground = _probeMode
                ? Brushes.White
                : new SolidColorBrush(Color.FromArgb(0xFF, 0xCC, 0xCC, 0xCC));
            SkCanvas.Cursor = _probeMode ? Cursors.Cross : Cursors.Hand;
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
            float hitR = 10f / _zoom;  // 10 screen-pixel hit radius
            SpeakerInstance best  = null;
            double          bestD = hitR;
            foreach (var s in _speakers)
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
