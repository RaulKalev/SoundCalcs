using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using SkiaSharp;
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
        // ── Colour palette (matches FilledRegionRenderer) ──────────────────
        static readonly SKColor[] HeatColors =
        {
            new SKColor(0xD2, 0x00, 0x00),  // 0 – coldest / quietest
            new SKColor(0xFF, 0x3C, 0x00),
            new SKColor(0xFF, 0x96, 0x00),
            new SKColor(0xFF, 0xD2, 0x00),
            new SKColor(0xC8, 0xDC, 0x00),
            new SKColor(0x8C, 0xDC, 0x1E),
            new SKColor(0x3C, 0xC8, 0x1E),
            new SKColor(0x00, 0xA0, 0x00),  // 7 – hottest / loudest
        };

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

        // ── LOD state ──────────────────────────────────────────────────────
        // lodStep = 4 → sample every 4th receiver, draw 4× blocks (coarse)
        // lodStep = 2 → every 2nd receiver, 2× blocks
        // lodStep = 1 → all receivers, exact grid spacing (full HD)
        int             _lodStep = 1;
        DispatcherTimer _lodTimer;

        // ── Flattened geometry caches ──────────────────────────────────────
        readonly List<WallSegment2D>   _walls    = new List<WallSegment2D>();
        readonly List<SpeakerInstance> _speakers = new List<SpeakerInstance>();

        // ── Dependency Properties ──────────────────────────────────────────

        public static readonly DependencyProperty JobOutputProperty =
            DependencyProperty.Register(nameof(JobOutput), typeof(AcousticJobOutput),
                typeof(AcousticViewerControl),
                new PropertyMetadata(null, (d, _) =>
                {
                    var c = (AcousticViewerControl)d;
                    c._fitPending = true;
                    c.StartLodProgression();
                }));

        public static readonly DependencyProperty WallGroupsSourceProperty =
            DependencyProperty.Register(nameof(WallGroupsSource), typeof(IEnumerable),
                typeof(AcousticViewerControl),
                new PropertyMetadata(null, (d, _) =>
                {
                    var c = (AcousticViewerControl)d;
                    c.RebuildGeometry();
                    c._fitPending = true;
                    c.Refresh();
                }));

        public static readonly DependencyProperty SpeakerGroupsSourceProperty =
            DependencyProperty.Register(nameof(SpeakerGroupsSource), typeof(IEnumerable),
                typeof(AcousticViewerControl),
                new PropertyMetadata(null, (d, _) =>
                {
                    var c = (AcousticViewerControl)d;
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

            canvas.Restore();

            // ── Screen-space overlays ─────────────────────────────────────
            DrawLegend(canvas, w, h);
            DrawLodBadge(canvas, w, h);
            DrawScaleBar(canvas, w, h);
        }

        // ── Heatmap ────────────────────────────────────────────────────────
        void DrawHeatmap(SKCanvas canvas)
        {
            if (JobOutput == null || JobOutput.Results.Count == 0) return;

            // Determine value range for colour mapping
            bool isSti    = Mode == VisualizationMode.STI;
            int  bandIdx  = MainViewModel.GetOctaveBandIndex(Mode);
            bool isPerBand = bandIdx >= 0;

            double minVal, maxVal;
            if (isSti)
            {
                minVal = JobOutput.MinSti;
                maxVal = JobOutput.MaxSti;
                if (maxVal - minVal < 0.01) { minVal = 0.0; maxVal = 1.0; }
            }
            else if (isPerBand)
            {
                minVal = JobOutput.MinSplDbByBand[bandIdx];
                maxVal = JobOutput.MaxSplDbByBand[bandIdx];
                if (maxVal - minVal < 1.0) { minVal -= 10.0; maxVal += 10.0; }
            }
            else  // broadband SPL
            {
                minVal = JobOutput.MinSplDb;
                maxVal = JobOutput.MaxSplDb;
                if (maxVal - minVal < 1.0) { minVal -= 10.0; maxVal += 10.0; }
            }

            double range = maxVal - minVal;

            // Each cell is drawn as a square of half-size = gridSpacing × lodStep / 2
            float cellHalf = (float)(GridSpacing * _lodStep * 0.5);
            int   step     = _lodStep;

            using var paint = new SKPaint
            {
                Style       = SKPaintStyle.Fill,
                IsAntialias = false,
            };

            var results = JobOutput.Results;
            int count   = results.Count;

            for (int i = 0; i < count; i += step)
            {
                ReceiverResult r   = results[i];
                double val = isSti    ? r.Sti
                           : isPerBand ? r.SplDbByBand[bandIdx]
                           : r.SplDb;

                double t  = (val - minVal) / range;
                t = t < 0.0 ? 0.0 : t > 1.0 ? 1.0 : t;
                int ci = (int)(t * (HeatColors.Length - 1));

                paint.Color = HeatColors[ci].WithAlpha(210);

                float x = (float)r.Position.X;
                float y = (float)r.Position.Y;
                canvas.DrawRect(x - cellHalf, y - cellHalf, cellHalf * 2f, cellHalf * 2f, paint);
            }
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
                float hLen = MathF.Sqrt(dx * dx + dy * dy);

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
            if (JobOutput == null || JobOutput.Results.Count == 0) return;

            bool isSti    = Mode == VisualizationMode.STI;
            int  bandIdx  = MainViewModel.GetOctaveBandIndex(Mode);
            bool isPerBand = bandIdx >= 0;

            double minVal, maxVal;
            string unit;

            if (isSti)
            {
                minVal = JobOutput.MinSti;  maxVal = JobOutput.MaxSti;
                if (maxVal - minVal < 0.01) { minVal = 0.0; maxVal = 1.0; }
                unit = "";
            }
            else if (isPerBand)
            {
                minVal = JobOutput.MinSplDbByBand[bandIdx];
                maxVal = JobOutput.MaxSplDbByBand[bandIdx];
                if (maxVal - minVal < 1.0) { minVal -= 10.0; maxVal += 10.0; }
                unit = " dB";
            }
            else
            {
                minVal = JobOutput.MinSplDb;  maxVal = JobOutput.MaxSplDb;
                if (maxVal - minVal < 1.0) { minVal -= 10.0; maxVal += 10.0; }
                unit = " dB";
            }

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
                _isPanning  = true;
                _lastMouse  = e.GetPosition(SkCanvas);
                SkCanvas.CaptureMouse();
            }
        }

        void Canvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isPanning) return;
            var cur = e.GetPosition(SkCanvas);
            _panX += (float)(cur.X - _lastMouse.X);
            _panY += (float)(cur.Y - _lastMouse.Y);
            _lastMouse = cur;
            Refresh();
        }

        void Canvas_MouseUp(object sender, MouseButtonEventArgs e)
        {
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
            _zoom = Math.Clamp(_zoom * factor, 2f, 8000f);

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
    }
}
