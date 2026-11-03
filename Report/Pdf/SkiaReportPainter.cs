using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using SkiaSharp;
using SoundCalcs.Domain;
using SoundCalcs.UI.ViewModels;
using SoundCalcs.Visualization;

namespace SoundCalcs.Report.Pdf
{
    /// <summary>
    /// Lays a <see cref="ReportDocument"/> out on portrait pages and paints them on any <see cref="SKCanvas"/>: a PDF page
    /// (<see cref="PdfReportWriter"/>) or a bitmap (the report dialog's preview, the harness PNGs). Units are PDF points.
    /// </summary>
    public sealed class SkiaReportPainter : IDisposable
    {
        // ------------------------------------------------------------------ style

        const float Margin = 48f, HeaderY = 30f, ContentTop = 62f, ContentBottomGap = 58f;
        const float BodySize = 9.5f, NoteSize = 8.5f, CaptionSize = 7.8f;

        static readonly SKColor Ink = new SKColor(0x1F, 0x23, 0x28);
        static readonly SKColor Muted = new SKColor(0x5F, 0x66, 0x6D);
        static readonly SKColor Faint = new SKColor(0x8A, 0x91, 0x99);
        static readonly SKColor Rule = new SKColor(0xD5, 0xD9, 0xDE);
        static readonly SKColor RuleStrong = new SKColor(0x9A, 0xA1, 0xA9);
        static readonly SKColor Accent = new SKColor(0x0F, 0x6C, 0xBD);
        static readonly SKColor HeaderFill = new SKColor(0xEE, 0xF1, 0xF5);
        static readonly SKColor GroupFill = new SKColor(0xE2, 0xE7, 0xED);
        static readonly SKColor PassInk = new SKColor(0x10, 0x7C, 0x10);
        static readonly SKColor PassFill = new SKColor(0xE6, 0xF4, 0xE6);
        static readonly SKColor FailInk = new SKColor(0xC4, 0x2B, 0x1C);
        static readonly SKColor FailFill = new SKColor(0xFB, 0xE9, 0xE7);
        static readonly SKColor WarnFill = new SKColor(0xFF, 0xF4, 0xCE);
        static readonly SKColor WarnInk = new SKColor(0x6B, 0x4E, 0x00);
        static readonly SKColor WallInk = new SKColor(0x2B, 0x2F, 0x33);

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        readonly ReportDocument _doc;
        readonly SKTypeface _regular, _bold;
        readonly float _pageW, _pageH;
        readonly List<Page> _pages = new List<Page>();
        readonly Dictionary<string, int> _anchorPages = new Dictionary<string, int>();
        readonly List<HeadingBlock> _contentsEntries = new List<HeadingBlock>();
        bool _laidOut;

        // Flow state while laying out
        Page _page;
        float _y;
        string _section = "";

        class Page
        {
            public readonly List<Action<SKCanvas>> Ops = new List<Action<SKCanvas>>();
            public string Section = "";
            public bool Plain;       // cover: no header or footer
            public bool Empty = true;
        }

        public SkiaReportPainter(ReportDocument doc)
        {
            _doc = doc;
            (_pageW, _pageH) = ReportOptions.PageSizePt(doc.PageFormat);
            _regular = ResolveTypeface(SKFontStyle.Normal);
            _bold = ResolveTypeface(SKFontStyle.Bold);
        }

        public float PageWidth => _pageW;
        public float PageHeight => _pageH;

        public int PageCount
        {
            get { EnsureLayout(); return _pages.Count; }
        }

        /// <summary>Page index (0-based) of each heading anchor, after layout.</summary>
        public IReadOnlyDictionary<string, int> AnchorPages
        {
            get { EnsureLayout(); return _anchorPages; }
        }

        float ContentW => _pageW - 2 * Margin;
        float ContentBottom => _pageH - ContentBottomGap;
        float ContentH => ContentBottom - ContentTop;

        /// <summary>The first installed family of a list that reads well in a report (Segoe UI on Windows).</summary>
        static SKTypeface ResolveTypeface(SKFontStyle style)
        {
            foreach (string family in new[] { "Segoe UI", "Inter", "DejaVu Sans", "Arial", "Liberation Sans", "Helvetica" })
            {
                SKTypeface tf = SKTypeface.FromFamilyName(family, style);
                if (tf != null && string.Equals(tf.FamilyName, family, StringComparison.OrdinalIgnoreCase)) return tf;
                tf?.Dispose();
            }
            return SKTypeface.FromFamilyName(null, style) ?? SKTypeface.Default;
        }

        public void Dispose()
        {
            _regular?.Dispose();
            _bold?.Dispose();
        }

        // ------------------------------------------------------------------ painting

        /// <summary>Paints page <paramref name="index"/> (0-based) in points; scale the canvas for other resolutions.</summary>
        public void PaintPage(SKCanvas canvas, int index)
        {
            EnsureLayout();
            Page page = _pages[index];
            canvas.Save();
            using (var white = new SKPaint { Color = SKColors.White })
                canvas.DrawRect(0, 0, _pageW, _pageH, white);
            foreach (Action<SKCanvas> op in page.Ops) op(canvas);
            if (!page.Plain) PaintHeaderFooter(canvas, page, index);
            canvas.Restore();
        }

        void PaintHeaderFooter(SKCanvas c, Page page, int index)
        {
            using (SKPaint p = Text(_regular, 7.6f, Muted))
            {
                c.DrawText(Fit(_doc.HeaderLeft, p, ContentW * 0.55f), Margin, HeaderY, p);
                string right = Fit(page.Section, p, ContentW * 0.42f);
                c.DrawText(right, _pageW - Margin - p.MeasureText(right), HeaderY, p);
                float fy = _pageH - 30f;
                c.DrawText(Fit(_doc.FooterLeft, p, ContentW * 0.7f), Margin, fy, p);
                string num = string.Format(Inv, _doc.PageNumberFormat, index + 1, _pages.Count);
                c.DrawText(num, _pageW - Margin - p.MeasureText(num), fy, p);
            }
            using (var rule = new SKPaint { Color = Rule, StrokeWidth = 0.6f, IsAntialias = true })
            {
                c.DrawLine(Margin, HeaderY + 6, _pageW - Margin, HeaderY + 6, rule);
                c.DrawLine(Margin, _pageH - 42f, _pageW - Margin, _pageH - 42f, rule);
            }
        }

        // ------------------------------------------------------------------ layout

        void EnsureLayout()
        {
            if (_laidOut) return;
            _laidOut = true;
            _contentsEntries.AddRange(_doc.Blocks.OfType<HeadingBlock>().Where(h => h.Level <= 2));
            for (int i = 0; i < _doc.Blocks.Count; i++)
            {
                ReportBlock block = _doc.Blocks[i];
                ReportBlock next = i + 1 < _doc.Blocks.Count ? _doc.Blocks[i + 1] : null;
                switch (block)
                {
                    case CoverBlock cover: LayoutCover(cover); break;
                    case ContentsBlock _: LayoutContents(); break;
                    case PageBreakBlock _: NewPage(); break;
                    case HeadingBlock h: LayoutHeading(h, next); break;
                    case ParagraphBlock p: LayoutParagraph(p); break;
                    case KeyValueBlock kv: LayoutKeyValue(kv); break;
                    case TableBlock t: LayoutTable(t); break;
                    case DistributionBlock d: LayoutDistribution(d); break;
                    case FigureBlock f: LayoutFigure(f); break;
                }
            }
            if (_pages.Count == 0) NewPage();
        }

        void NewPage()
        {
            _page = new Page { Section = _section };
            _pages.Add(_page);
            _y = ContentTop;
        }

        Page CurrentPage()
        {
            if (_page == null || _page.Plain) NewPage();
            return _page;
        }

        /// <summary>Starts a new page unless <paramref name="height"/> still fits on this one.</summary>
        void EnsureSpace(float height)
        {
            Page p = CurrentPage();
            if (!p.Empty && _y + height > ContentBottom) NewPage();
        }

        void Emit(Action<SKCanvas> op)
        {
            Page p = CurrentPage();
            p.Ops.Add(op);
            p.Empty = false;
        }

        // ------------------------------------------------------------------ cover

        void LayoutCover(CoverBlock cover)
        {
            var page = new Page { Plain = true, Empty = false };
            _pages.Add(page);
            _page = page;
            float x = Margin + 8, w = ContentW - 16;

            page.Ops.Add(c =>
            {
                using (var band = new SKPaint { Color = Accent })
                    c.DrawRect(0, 0, 10, _pageH, band);

                float logoBottom = Margin + 10;
                if (!string.IsNullOrEmpty(cover.LogoPath) && File.Exists(cover.LogoPath))
                {
                    try
                    {
                        using (SKBitmap logo = SKBitmap.Decode(cover.LogoPath))
                        {
                            if (logo != null && logo.Width > 0 && logo.Height > 0)
                            {
                                float s = Math.Min(170f / logo.Width, 64f / logo.Height);
                                var rect = SKRect.Create(x, Margin + 10, logo.Width * s, logo.Height * s);
                                using (var paint = new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = true })
                                    c.DrawBitmap(logo, rect, paint);
                                logoBottom = rect.Bottom;
                            }
                        }
                    }
                    catch { /* an unreadable logo is left out */ }
                }

                float y = Math.Max(logoBottom + 90, _pageH * 0.30f);
                using (SKPaint p = Text(_regular, 13f, Muted))
                    c.DrawText(cover.Title, x, y, p);
                y += 14;
                using (SKPaint p = Text(_bold, 26f, Ink))
                    foreach (string line in Wrap(cover.Subtitle, p, w))
                    {
                        y += 31;
                        c.DrawText(line, x, y, p);
                    }
                y += 16;
                using (var bar = new SKPaint { Color = Accent })
                    c.DrawRect(x, y, 56, 3, bar);
                y += 30;

                using (SKPaint k = Text(_regular, 9.5f, Muted))
                using (SKPaint v = Text(_bold, 10f, Ink))
                {
                    foreach (var f in cover.Fields)
                    {
                        c.DrawText(f.Key, x, y, k);
                        float vy = y;
                        foreach (string line in Wrap(f.Value, v, w - 130))
                        {
                            c.DrawText(line, x + 130, vy, v);
                            vy += 14;
                        }
                        y = Math.Max(y + 18, vy + 4);
                    }
                }

                if (cover.ListItems.Count > 0)
                {
                    y += 16;
                    using (SKPaint h = Text(_bold, 10f, Ink))
                        c.DrawText(cover.ListTitle, x, y, h);
                    y += 16;
                    using (SKPaint p = Text(_regular, 9.5f, Ink))
                        foreach (string item in cover.ListItems.Take(24))
                        {
                            c.DrawText("•  " + Fit(item, p, w - 10), x, y, p);
                            y += 14;
                        }
                }

                using (SKPaint p = Text(_regular, 8f, Faint))
                {
                    var lines = Wrap(cover.Note, p, w);
                    float ny = _pageH - Margin - lines.Count * 11;
                    foreach (string line in lines)
                    {
                        c.DrawText(line, x, ny, p);
                        ny += 11;
                    }
                }
            });
        }

        // ------------------------------------------------------------------ contents

        void LayoutContents()
        {
            if (_contentsEntries.Count == 0) return;
            NewPage();
            float titleH = 30;
            Emit(c => { using (SKPaint p = Text(_bold, 18f, Ink)) c.DrawText(_doc.ContentsTitle, Margin, ContentTop + 18, p); });
            _y += titleH;
            foreach (HeadingBlock h in _contentsEntries)
            {
                float lh = h.Level == 1 ? 17f : 14.5f;
                if (_y + lh > ContentBottom) NewPage();
                float y = _y + lh - 4;
                HeadingBlock entry = h;
                Emit(c =>
                {
                    float indent = entry.Level == 1 ? 0 : 14;
                    using (SKPaint p = Text(entry.Level == 1 ? _bold : _regular, entry.Level == 1 ? 10f : 9.2f, Ink))
                    using (SKPaint dots = Text(_regular, 9f, Faint))
                    {
                        string num = _anchorPages.TryGetValue(entry.Anchor, out int pg) ? (pg + 1).ToString(Inv) : "";
                        float numW = p.MeasureText(num);
                        string text = Fit(entry.Text, p, ContentW - indent - numW - 30);
                        float tx = Margin + indent, tw = p.MeasureText(text);
                        c.DrawText(text, tx, y, p);
                        c.DrawText(num, _pageW - Margin - numW, y, p);
                        // Dotted leader
                        float dotW = dots.MeasureText(". ");
                        for (float dx = tx + tw + 6; dx < _pageW - Margin - numW - 6 - dotW; dx += dotW)
                            c.DrawText(".", dx, y, dots);
                        c.DrawLinkDestinationAnnotation(SKRect.Create(tx, y - 11, ContentW - indent, 14), entry.Anchor);
                    }
                });
                _y += lh;
            }
        }

        // ------------------------------------------------------------------ headings and text

        static float HeadingSize(int level) => level == 1 ? 17f : level == 2 ? 13f : 10.8f;
        static float HeadingBefore(int level) => level == 1 ? 6f : level == 2 ? 14f : 10f;
        static float HeadingAfter(int level) => level == 1 ? 12f : level == 2 ? 6f : 4f;

        float HeadingHeight(HeadingBlock h)
        {
            using (SKPaint p = Text(_bold, HeadingSize(h.Level), Ink))
                return HeadingBefore(h.Level) + Wrap(h.Text, p, ContentW).Count * HeadingSize(h.Level) * 1.25f + HeadingAfter(h.Level);
        }

        void LayoutHeading(HeadingBlock h, ReportBlock next)
        {
            if (h.PageBreakBefore && (_page == null || _page.Plain || !_page.Empty)) NewPage();
            float height = HeadingHeight(h);
            EnsureSpace(height + MinHeight(next));
            Page page = CurrentPage();
            if (h.Level == 1)
            {
                _section = h.Text;
                page.Section = h.Text;
            }
            _anchorPages[h.Anchor] = _pages.IndexOf(page);
            bool atTop = page.Empty;
            float y = _y + (atTop ? 0 : HeadingBefore(h.Level));
            float size = HeadingSize(h.Level);
            HeadingBlock hb = h;
            Emit(c =>
            {
                using (SKPaint p = Text(_bold, size, Ink))
                {
                    float ly = y;
                    foreach (string line in Wrap(hb.Text, p, ContentW))
                    {
                        ly += size * 1.25f;
                        c.DrawText(line, Margin, ly - size * 0.25f, p);
                    }
                    c.DrawNamedDestinationAnnotation(new SKPoint(Margin, y), hb.Anchor);
                    if (hb.Level == 1)
                        using (var bar = new SKPaint { Color = Accent })
                            c.DrawRect(Margin, ly + 3, 40, 2.5f, bar);
                }
            });
            _y = y + height - HeadingBefore(h.Level);
        }

        /// <summary>The least of a block that has to follow a heading on the same page.</summary>
        float MinHeight(ReportBlock next)
        {
            switch (next)
            {
                case null: return 0;
                case ParagraphBlock p: return ParagraphLineHeight(p.Style) * 2 + 6;
                case KeyValueBlock _: return 30;
                case TableBlock t: return TableHeaderHeight(t) + 18 * t.TextScale + (string.IsNullOrEmpty(t.Caption) ? 0 : 16);
                case FigureBlock f: return FigureHeight(f, out _, out _, out _, out _);
                case DistributionBlock d: return DistributionHeight(d);
                case HeadingBlock h: return HeadingHeight(h) + 30;
                default: return 0;
            }
        }

        static float ParagraphSize(ParagraphStyle s) => s == ParagraphStyle.Body ? BodySize : s == ParagraphStyle.Caption ? CaptionSize : s == ParagraphStyle.Warning ? 9f : NoteSize;
        static float ParagraphLineHeight(ParagraphStyle s) => ParagraphSize(s) * 1.38f;

        void LayoutParagraph(ParagraphBlock para)
        {
            if (string.IsNullOrWhiteSpace(para.Text)) return;
            float size = ParagraphSize(para.Style), lh = ParagraphLineHeight(para.Style);
            bool box = para.Style == ParagraphStyle.Warning;
            float pad = box ? 6f : 0f;
            SKColor color = para.Style == ParagraphStyle.Body ? Ink : box ? WarnInk : Muted;
            List<string> lines;
            using (SKPaint p = Text(_regular, size, color)) lines = Wrap(para.Text, p, ContentW - 2 * pad);
            float gapBefore = para.Style == ParagraphStyle.Caption ? 2f : 4f;
            _y += CurrentPage().Empty ? 0 : gapBefore;

            int i = 0;
            while (i < lines.Count)
            {
                if (_y + lh + 2 * pad > ContentBottom && !CurrentPage().Empty) NewPage();
                int n = Math.Max(1, (int)Math.Floor((ContentBottom - _y - 2 * pad) / lh));
                n = Math.Min(n, lines.Count - i);
                var chunk = lines.GetRange(i, n);
                float y0 = _y;
                Emit(c =>
                {
                    if (box)
                        using (var fill = new SKPaint { Color = WarnFill, IsAntialias = true })
                            c.DrawRoundRect(SKRect.Create(Margin, y0, ContentW, chunk.Count * lh + 2 * pad), 3, 3, fill);
                    using (SKPaint p = Text(_regular, size, color))
                    {
                        float y = y0 + pad;
                        foreach (string line in chunk)
                        {
                            y += lh;
                            c.DrawText(line, Margin + pad, y - lh * 0.28f, p);
                        }
                    }
                });
                _y += chunk.Count * lh + 2 * pad;
                i += n;
            }
            _y += 3;
        }

        // ------------------------------------------------------------------ key/value

        void LayoutKeyValue(KeyValueBlock kv)
        {
            if (kv.Items.Count == 0) return;
            int cols = Math.Max(1, Math.Min(2, kv.Columns));
            float colW = ContentW / cols, keyW = colW * 0.42f, valW = colW - keyW - 10;
            float size = 9f, lh = size * 1.38f;
            _y += CurrentPage().Empty ? 0 : 4;
            using (SKPaint kp = Text(_regular, size, Muted))
            using (SKPaint vp = Text(_bold, size, Ink))
            {
                for (int r = 0; r * cols < kv.Items.Count; r++)
                {
                    var cells = kv.Items.Skip(r * cols).Take(cols).ToList();
                    var wrapped = cells.Select(cell => (Wrap(cell.Key, kp, keyW - 4), Wrap(cell.Value, vp, valW))).ToList();
                    int lines = wrapped.Max(w => Math.Max(w.Item1.Count, w.Item2.Count));
                    float h = lines * lh + 3;
                    EnsureSpace(h);
                    float y0 = _y;
                    Emit(c =>
                    {
                        using (SKPaint k = Text(_regular, size, Muted))
                        using (SKPaint v = Text(_bold, size, Ink))
                        using (var rule = new SKPaint { Color = Rule, StrokeWidth = 0.5f })
                        {
                            for (int i = 0; i < wrapped.Count; i++)
                            {
                                float x = Margin + i * colW;
                                float y = y0;
                                foreach (string line in wrapped[i].Item1) { y += lh; c.DrawText(line, x, y - lh * 0.28f, k); }
                                y = y0;
                                foreach (string line in wrapped[i].Item2) { y += lh; c.DrawText(line, x + keyW, y - lh * 0.28f, v); }
                                c.DrawLine(x, y0 + h - 0.5f, x + colW - 10, y0 + h - 0.5f, rule);
                            }
                        }
                    });
                    _y += h;
                }
            }
            _y += 6;
        }

        // ------------------------------------------------------------------ tables

        /// <summary>
        /// Column widths by weight, widened where a header word would not fit (so headers wrap between words only);
        /// the extra width comes from the columns that have room to spare.
        /// </summary>
        float[] ColumnWidths(TableBlock t)
        {
            float total = t.Columns.Sum(c => Math.Max(0.01f, c.Weight));
            float[] w = t.Columns.Select(c => ContentW * Math.Max(0.01f, c.Weight) / total).ToArray();
            float[] min;
            using (SKPaint p = Text(_bold, 8.6f * t.TextScale, Ink))
                min = t.Columns.Select(c => (c.Header ?? "").Split(' ').Select(word => p.MeasureText(word)).DefaultIfEmpty(0).Max() + 2 * CellPadX + 1).ToArray();
            for (int pass = 0; pass < 4; pass++)
            {
                float need = 0, spare = 0;
                for (int i = 0; i < w.Length; i++)
                {
                    if (w[i] < min[i]) need += min[i] - w[i];
                    else spare += w[i] - min[i];
                }
                if (need <= 0.01f || spare <= 0.01f) break;
                float k = Math.Min(1f, need / spare);
                for (int i = 0; i < w.Length; i++)
                    w[i] = w[i] < min[i] ? min[i] : w[i] - (w[i] - min[i]) * k;
            }
            return w;
        }

        const float CellPadX = 4f, CellPadY = 3f;

        float TableHeaderHeight(TableBlock t)
        {
            float size = 8.6f * t.TextScale, lh = size * 1.3f;
            float[] w = ColumnWidths(t);
            using (SKPaint p = Text(_bold, size, Ink))
                return t.Columns.Select((c, i) => Wrap(c.Header, p, w[i] - 2 * CellPadX).Count).DefaultIfEmpty(1).Max() * lh + 2 * CellPadY;
        }

        void LayoutTable(TableBlock t)
        {
            if (t.Columns.Count == 0) return;
            float size = 9f * t.TextScale, headSize = 8.6f * t.TextScale;
            float lh = size * 1.3f, headLh = headSize * 1.3f;
            float[] w = ColumnWidths(t);
            float headerH = TableHeaderHeight(t);
            float captionH = string.IsNullOrEmpty(t.Caption) ? 0 : 15f;

            // Wrap every row once
            var rows = new List<(TableRow Row, List<string>[] Lines, float H)>();
            using (SKPaint rp = Text(_regular, size, Ink))
            using (SKPaint bp = Text(_bold, size, Ink))
            {
                foreach (TableRow row in t.Rows)
                {
                    var lines = new List<string>[t.Columns.Count];
                    if (row.IsGroup)
                    {
                        lines[0] = Wrap(row.Cells.Length > 0 ? row.Cells[0] : "", bp, ContentW - 2 * CellPadX);
                        for (int i = 1; i < lines.Length; i++) lines[i] = new List<string>();
                    }
                    else
                        for (int i = 0; i < t.Columns.Count; i++)
                        {
                            string text = i < row.Cells.Length ? row.Cells[i] ?? "" : "";
                            bool bold = row.Bold || (row.Status != null && i < row.Status.Length && (row.Status[i] == CellStatus.Pass || row.Status[i] == CellStatus.Fail));
                            lines[i] = Wrap(text, bold ? bp : rp, w[i] - 2 * CellPadX);
                        }
                    rows.Add((row, lines, lines.Max(l => Math.Max(1, l.Count)) * lh + 2 * CellPadY));
                }
            }

            _y += CurrentPage().Empty ? 0 : 6;
            int index = 0;
            bool first = true;
            while (index < rows.Count || first)
            {
                float firstRowH = index < rows.Count ? rows[index].H : 0;
                EnsureSpace(captionH + headerH + firstRowH);
                float y0 = _y;
                float y = y0 + captionH + headerH;
                int start = index;
                while (index < rows.Count && (y + rows[index].H <= ContentBottom || index == start))
                {
                    y += rows[index].H;
                    index++;
                }
                var slice = rows.GetRange(start, index - start);
                bool isFirst = first;
                Emit(c => PaintTable(c, t, w, y0, captionH, headerH, headSize, headLh, size, lh, slice, isFirst));
                _y = y;
                first = false;
                if (index < rows.Count) NewPage();
            }
            _y += 8;
        }

        void PaintTable(SKCanvas c, TableBlock t, float[] w, float y0, float captionH, float headerH, float headSize,
            float headLh, float size, float lh, List<(TableRow Row, List<string>[] Lines, float H)> rows, bool first)
        {
            float x0 = Margin;
            if (captionH > 0)
                using (SKPaint p = Text(_bold, 9f, Ink))
                    c.DrawText(t.Caption, x0, y0 + 10, p);
            float y = y0 + captionH;

            using (var fill = new SKPaint { Color = HeaderFill })
                c.DrawRect(x0, y, ContentW, headerH, fill);
            using (SKPaint p = Text(_bold, headSize, Ink))
            {
                float x = x0;
                for (int i = 0; i < t.Columns.Count; i++)
                {
                    float ly = y + CellPadY;
                    foreach (string line in Wrap(t.Columns[i].Header, p, w[i] - 2 * CellPadX))
                    {
                        ly += headLh;
                        DrawAligned(c, line, p, x, w[i], ly - headLh * 0.26f, t.Columns[i].Align);
                    }
                    x += w[i];
                }
            }
            using (var strong = new SKPaint { Color = RuleStrong, StrokeWidth = 0.8f })
                c.DrawLine(x0, y, x0 + ContentW, y, strong);
            y += headerH;

            using (SKPaint rp = Text(_regular, size, Ink))
            using (SKPaint bp = Text(_bold, size, Ink))
            using (var rule = new SKPaint { Color = Rule, StrokeWidth = 0.5f })
            {
                foreach (var (row, lines, h) in rows)
                {
                    if (row.IsGroup)
                    {
                        using (var fill = new SKPaint { Color = GroupFill })
                            c.DrawRect(x0, y, ContentW, h, fill);
                        float ly = y + CellPadY;
                        foreach (string line in lines[0]) { ly += lh; c.DrawText(line, x0 + CellPadX, ly - lh * 0.26f, bp); }
                    }
                    else
                    {
                        float x = x0;
                        for (int i = 0; i < t.Columns.Count; i++)
                        {
                            CellStatus st = row.Status != null && i < row.Status.Length ? row.Status[i] : CellStatus.None;
                            SKPaint p = row.Bold || st == CellStatus.Pass || st == CellStatus.Fail ? bp : rp;
                            SKColor ink = st == CellStatus.Pass ? PassInk : st == CellStatus.Fail ? FailInk : st == CellStatus.Muted ? Muted : Ink;
                            if (st == CellStatus.Pass || st == CellStatus.Fail)
                                using (var fill = new SKPaint { Color = st == CellStatus.Pass ? PassFill : FailFill })
                                    c.DrawRect(x + 1, y + 1, w[i] - 2, h - 2, fill);
                            p.Color = ink;
                            float ly = y + CellPadY;
                            foreach (string line in lines[i])
                            {
                                ly += lh;
                                DrawAligned(c, line, p, x, w[i], ly - lh * 0.26f, t.Columns[i].Align);
                            }
                            p.Color = Ink;
                            x += w[i];
                        }
                    }
                    y += h;
                    c.DrawLine(x0, y, x0 + ContentW, y, rule);
                }
            }
        }

        static void DrawAligned(SKCanvas c, string text, SKPaint p, float x, float w, float y, CellAlign align)
        {
            float tw = p.MeasureText(text);
            float tx = align == CellAlign.Right ? x + w - CellPadX - tw : align == CellAlign.Center ? x + (w - tw) / 2 : x + CellPadX;
            c.DrawText(text, tx, y, p);
        }

        // ------------------------------------------------------------------ distribution bar

        float DistributionHeight(DistributionBlock d) => 14 + 12 + 6 + 12 * Math.Max(1, (int)Math.Ceiling(d.Segments.Count / 5.0)) + 8;

        void LayoutDistribution(DistributionBlock d)
        {
            float h = DistributionHeight(d);
            _y += CurrentPage().Empty ? 0 : 4;
            EnsureSpace(h);
            float y0 = _y;
            Emit(c =>
            {
                using (SKPaint p = Text(_bold, 9f, Ink)) c.DrawText(d.Caption, Margin, y0 + 10, p);
                float x = Margin, by = y0 + 14;
                using (var fill = new SKPaint { IsAntialias = true })
                {
                    foreach (DistributionSegment s in d.Segments)
                    {
                        float sw = (float)(ContentW * Math.Max(0, s.Fraction));
                        if (sw <= 0) continue;
                        fill.Color = Sk(s.Color);
                        c.DrawRect(x, by, sw, 12, fill);
                        x += sw;
                    }
                }
                using (var border = new SKPaint { Color = RuleStrong, Style = SKPaintStyle.Stroke, StrokeWidth = 0.5f })
                    c.DrawRect(Margin, by, ContentW, 12, border);
                float lx = Margin, ly = by + 12 + 6 + 9;
                using (SKPaint p = Text(_regular, 8f, Ink))
                using (var sw = new SKPaint())
                {
                    float itemW = ContentW / 5;
                    for (int i = 0; i < d.Segments.Count; i++)
                    {
                        float ix = lx + (i % 5) * itemW, iy = ly + (i / 5) * 12;
                        sw.Color = Sk(d.Segments[i].Color);
                        c.DrawRect(ix, iy - 7.5f, 8, 8, sw);
                        c.DrawText(d.Segments[i].Label, ix + 11, iy, p);
                    }
                }
            });
            _y += h;
        }

        // ------------------------------------------------------------------ figures

        const float LegendW = 96f, LegendGap = 10f, ScaleBarH = 20f;

        /// <summary>Height of a figure block and where its plan goes (scale in points per metre).</summary>
        float FigureHeight(FigureBlock f, out float planW, out float planH, out float scale, out List<string> caption)
        {
            PlanFigure fig = f.Figure;
            float maxW = ContentW - LegendW - LegendGap;
            float maxH = ContentH * f.MaxHeightFraction;
            double wm = Math.Max(0.1, fig.WidthM), hm = Math.Max(0.1, fig.HeightM);
            scale = (float)Math.Min(maxW / wm, maxH / hm);
            planW = (float)(wm * scale);
            planH = (float)(hm * scale);
            float legendH = 16 + fig.Legend.Count * 14 + 6;
            string text = f.Caption;
            if (!string.IsNullOrEmpty(f.CaptionWhenThinned) && fig.Values.Count > 0)
            {
                int step = ValueStep(fig, scale);
                if (step > 1) text = string.Format(Inv, f.CaptionWhenThinned, step);
            }
            using (SKPaint p = Text(_regular, CaptionSize, Muted)) caption = Wrap(text, p, ContentW);
            return Math.Max(planH, legendH) + ScaleBarH + caption.Count * CaptionSize * 1.38f + 8;
        }

        /// <summary>Every how many grid points a value is printed so the numbers do not overlap.</summary>
        int ValueStep(PlanFigure fig, float scale)
        {
            if (fig.Grid == null || fig.Values.Count == 0) return 1;
            float cell = (float)(fig.Grid.Spacing * scale);
            using (SKPaint p = Text(_regular, ValueTextSize, Ink))
            {
                float needW = fig.Values.Max(v => p.MeasureText(v.Text)) + 3f;
                float needH = ValueTextSize * 1.25f;
                return Math.Max(1, (int)Math.Ceiling(Math.Max(needW / cell, needH / cell) - 1e-3));
            }
        }

        const float ValueTextSize = 6.2f;

        void LayoutFigure(FigureBlock f)
        {
            if (f.Figure == null) return;
            float h = FigureHeight(f, out float planW, out float planH, out float scale, out List<string> caption);
            _y += CurrentPage().Empty ? 0 : 6;
            EnsureSpace(h);
            float y0 = _y;
            int step = ValueStep(f.Figure, scale);
            Emit(c => PaintFigure(c, f.Figure, Margin, y0, planW, planH, scale, step, caption));
            _y += h;
        }

        void PaintFigure(SKCanvas c, PlanFigure fig, float x0, float y0, float planW, float planH, float s, int step, List<string> caption)
        {
            SKPoint P(Vec2 v) => new SKPoint(x0 + (float)((v.X - fig.MinX) * s), y0 + (float)((fig.MaxY - v.Y) * s));
            var planRect = SKRect.Create(x0, y0, planW, planH);

            using (var frame = new SKPaint { Color = new SKColor(0xFA, 0xFB, 0xFC) })
                c.DrawRect(planRect, frame);

            c.Save();
            c.ClipRect(planRect);

            // Heatmap: one layer per room, each clipped to its outline
            using (var paint = new SKPaint { FilterQuality = SKFilterQuality.Medium, Color = SKColors.White.WithAlpha((byte)Math.Round(255 * Math.Max(0, Math.Min(1, fig.HeatmapOpacity)))) })
            {
                foreach (HeatmapLayer layer in fig.Layers)
                {
                    HeatmapGrid g = layer.Grid;
                    if (g == null) continue;
                    SKPoint tl = P(new Vec2(g.WorldLeft, g.WorldTop));
                    SKPoint br = P(new Vec2(g.WorldRight, g.WorldBottom));
                    var dest = new SKRect(tl.X, tl.Y, br.X, br.Y);
                    // About 3 pixels per point (≈ 216 dpi), never more than 2400 across
                    int pw = (int)Math.Max(2, Math.Min(2400, Math.Round(dest.Width * 3)));
                    int ph = (int)Math.Max(2, Math.Min(2400, Math.Round(dest.Height * 3)));
                    Rgba[] px = HeatmapMath.ResampleBilinear(g, pw, ph);
                    using (SKBitmap bmp = ToBitmap(px, pw, ph))
                    {
                        c.Save();
                        if (layer.ClipRings != null && layer.ClipRings.Count > 0)
                            using (SKPath clip = RingsPath(layer.ClipRings, P))
                                c.ClipPath(clip, SKClipOperation.Intersect, true);
                        c.DrawBitmap(bmp, dest, paint);
                        c.Restore();
                    }
                }
            }

            // Room outlines
            using (var outline = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = 0.6f, Color = Muted.WithAlpha(150), IsAntialias = true })
            using (var strong = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = 1.6f, Color = Accent, IsAntialias = true })
                foreach (FigureRoom r in fig.Rooms)
                    using (SKPath path = RingsPath(r.Rings, P))
                        c.DrawPath(path, r.Highlight ? strong : outline);

            // Walls
            using (var wall = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, Color = WallInk, IsAntialias = true, StrokeCap = SKStrokeCap.Round })
            using (var dash = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = 0.8f, Color = Faint, IsAntialias = true, PathEffect = SKPathEffect.CreateDash(new[] { 3f, 2f }, 0) })
                foreach (FigureWall w in fig.Walls)
                    c.DrawLine(P(w.A), P(w.B), w.IsOpening ? dash : wall);

            // Places labels must keep clear of: speakers and room names (contour labels go around them)
            var occupied = new List<SKRect>();
            bool singleRoom = fig.Rooms.Any(r => r.Highlight);
            foreach (FigureSpeaker sp in fig.Speakers)
            {
                SKPoint at = P(sp.Position);
                occupied.Add(new SKRect(at.X - 5, at.Y - 10, at.X + 16, at.Y + 5));
            }
            if (fig.Values.Count == 0)
                using (SKPaint p = Text(_bold, 7f, Ink))
                    foreach (FigureRoom r in fig.Rooms)
                    {
                        if (singleRoom && !r.Highlight) continue;
                        SKPoint at = P(r.LabelAt);
                        float tw = p.MeasureText(Fit(r.Label, p, Math.Max(40, planW * 0.4f)));
                        occupied.Add(new SKRect(at.X - tw / 2 - 2, at.Y - 8, at.X + tw / 2 + 2, at.Y + 3));
                    }

            // Contour lines and their labels
            if (fig.Isolines.Count > 0)
            {
                using (var line = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = 0.7f, Color = new SKColor(0x10, 0x10, 0x10, 170), IsAntialias = true })
                using (SKPaint label = Text(_bold, 6.4f, Ink))
                using (SKPaint halo = Text(_bold, 6.4f, SKColors.White))
                {
                    halo.Style = SKPaintStyle.Stroke;
                    halo.StrokeWidth = 2.2f;
                    foreach (FigureIsoline iso in fig.Isolines)
                    {
                        foreach (List<Vec2> pl in iso.Lines)
                        {
                            if (pl.Count < 2) continue;
                            using (var path = new SKPath())
                            {
                                path.MoveTo(P(pl[0]));
                                for (int i = 1; i < pl.Count; i++) path.LineTo(P(pl[i]));
                                c.DrawPath(path, line);
                            }
                        }
                        // Label on the longest line, at the first spot along it that is inside the plan and free
                        List<Vec2> longest = iso.Lines.OrderByDescending(PolylineLength).FirstOrDefault();
                        if (longest != null && PolylineLength(longest) * s > 40)
                        {
                            float tw = label.MeasureText(iso.Label);
                            foreach (double t in new[] { 0.5, 0.35, 0.65, 0.2, 0.8, 0.1, 0.9 })
                            {
                                SKPoint at = P(PointAlong(longest, t));
                                var rect = new SKRect(at.X - tw / 2 - 1.5f, at.Y - 4.5f, at.X + tw / 2 + 1.5f, at.Y + 4f);
                                if (!planRect.Contains(rect) || occupied.Any(o => o.IntersectsWith(rect))) continue;
                                occupied.Add(rect);
                                c.DrawText(iso.Label, at.X - tw / 2, at.Y + 2.2f, halo);
                                c.DrawText(iso.Label, at.X - tw / 2, at.Y + 2.2f, label);
                                break;
                            }
                        }
                    }
                }
            }

            // Grid values (value charts)
            if (fig.Values.Count > 0)
            {
                int c0 = fig.Values.Min(v => v.Col), r0 = fig.Values.Min(v => v.Row);
                using (SKPaint p = Text(_regular, ValueTextSize, Ink))
                using (SKPaint b = Text(_bold, ValueTextSize, Ink))
                using (var box = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = 0.6f, Color = Ink, IsAntialias = true })
                using (var back = new SKPaint { Color = SKColors.White.WithAlpha(220) })
                {
                    foreach (FigureValue v in fig.Values)
                    {
                        bool marked = v.IsMin || v.IsMax;
                        if (!marked && ((v.Col - c0) % step != 0 || (v.Row - r0) % step != 0)) continue;
                        SKPoint at = P(v.Position);
                        SKPaint tp = marked ? b : p;
                        float tw = tp.MeasureText(v.Text);
                        if (marked)
                        {
                            var r = SKRect.Create(at.X - tw / 2 - 1.5f, at.Y - ValueTextSize * 0.55f - 1.5f, tw + 3, ValueTextSize * 1.1f + 3);
                            c.DrawRect(r, back);
                            c.DrawRect(r, box);
                        }
                        c.DrawText(v.Text, at.X - tw / 2, at.Y + ValueTextSize * 0.36f, tp);
                    }
                }
            }

            // Room names (figures of a single room show only that room's name)
            bool single = singleRoom;
            if (fig.Values.Count == 0)
            {
                using (SKPaint p = Text(_bold, 7f, Ink))
                using (SKPaint halo = Text(_bold, 7f, SKColors.White))
                {
                    halo.Style = SKPaintStyle.Stroke;
                    halo.StrokeWidth = 2.4f;
                    foreach (FigureRoom r in fig.Rooms)
                    {
                        if (single && !r.Highlight) continue;
                        SKPoint at = P(r.LabelAt);
                        string text = Fit(r.Label, p, Math.Max(40, planW * 0.4f));
                        float tw = p.MeasureText(text);
                        c.DrawText(text, at.X - tw / 2, at.Y, halo);
                        c.DrawText(text, at.X - tw / 2, at.Y, p);
                    }
                }
            }

            // Speakers
            bool labels = fig.Speakers.Count <= 60;
            using (var fill = new SKPaint { Color = Accent, IsAntialias = true })
            using (var ring = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Stroke, StrokeWidth = 0.8f, IsAntialias = true })
            using (var aim = new SKPaint { Color = Accent, StrokeWidth = 1.1f, IsAntialias = true, StrokeCap = SKStrokeCap.Round })
            using (SKPaint lp = Text(_bold, 5.8f, Accent))
            using (SKPaint lh = Text(_bold, 5.8f, SKColors.White))
            {
                lh.Style = SKPaintStyle.Stroke;
                lh.StrokeWidth = 2f;
                foreach (FigureSpeaker sp in fig.Speakers)
                {
                    SKPoint at = P(sp.Position);
                    if (sp.Aim.HasValue)
                        c.DrawLine(at, new SKPoint(at.X + (float)sp.Aim.Value.X * 9, at.Y - (float)sp.Aim.Value.Y * 9), aim);
                    c.DrawCircle(at, 3.1f, fill);
                    c.DrawCircle(at, 3.1f, ring);
                    if (labels)
                    {
                        c.DrawText(sp.Label, at.X + 4f, at.Y - 3.5f, lh);
                        c.DrawText(sp.Label, at.X + 4f, at.Y - 3.5f, lp);
                    }
                }
            }
            c.Restore();

            using (var border = new SKPaint { Color = RuleStrong, Style = SKPaintStyle.Stroke, StrokeWidth = 0.8f })
                c.DrawRect(planRect, border);

            // Legend
            float lx = x0 + planW + LegendGap, ly = y0;
            using (SKPaint head = Text(_bold, 8f, Ink))
                c.DrawText(fig.LegendTitle, lx, ly + 8, head);
            using (SKPaint p = Text(_regular, 7.2f, Ink))
            using (var sw = new SKPaint())
            using (var swb = new SKPaint { Color = Rule, Style = SKPaintStyle.Stroke, StrokeWidth = 0.4f })
            {
                float sy = ly + 16;
                foreach (LegendEntry e in fig.Legend)
                {
                    sw.Color = Sk(e.Color);
                    c.DrawRect(lx, sy, 11, 11, sw);
                    c.DrawRect(lx, sy, 11, 11, swb);
                    c.DrawText(Fit(e.Label, p, LegendW - 16), lx + 15, sy + 8.4f, p);
                    sy += 14;
                }
            }

            // Scale bar
            double[] nice = { 0.5, 1, 2, 5, 10, 20, 50, 100, 200 };
            double len = nice.LastOrDefault(n => n * s <= Math.Max(30, planW * 0.28));
            if (len <= 0) len = nice[0];
            float bx = x0, by = y0 + planH + 7, bw = (float)(len * s);
            using (var dark = new SKPaint { Color = Ink })
            using (var light = new SKPaint { Color = SKColors.White })
            using (var border = new SKPaint { Color = Ink, Style = SKPaintStyle.Stroke, StrokeWidth = 0.5f })
            using (SKPaint p = Text(_regular, 7f, Ink))
            {
                c.DrawRect(bx, by, bw / 2, 3.5f, dark);
                c.DrawRect(bx + bw / 2, by, bw / 2, 3.5f, light);
                c.DrawRect(bx, by, bw, 3.5f, border);
                c.DrawText("0", bx, by + 11.5f, p);
                string l = len.ToString("0.#", Inv) + " m";
                c.DrawText(l, bx + bw - p.MeasureText(l) / 2, by + 11.5f, p);
            }

            // Caption
            using (SKPaint p = Text(_regular, CaptionSize, Muted))
            {
                float cy = y0 + Math.Max(planH, 16 + fig.Legend.Count * 14 + 6) + ScaleBarH;
                foreach (string line in caption)
                {
                    cy += CaptionSize * 1.38f;
                    c.DrawText(line, x0, cy - CaptionSize * 0.35f, p);
                }
            }
        }

        static double PolylineLength(List<Vec2> pl)
        {
            double l = 0;
            for (int i = 1; i < pl.Count; i++) l += Vec2.Distance(pl[i - 1], pl[i]);
            return l;
        }

        static Vec2 PointAlong(List<Vec2> pl, double t)
        {
            double target = PolylineLength(pl) * t, acc = 0;
            for (int i = 1; i < pl.Count; i++)
            {
                double d = Vec2.Distance(pl[i - 1], pl[i]);
                if (acc + d >= target && d > 0)
                {
                    double f = (target - acc) / d;
                    return new Vec2(pl[i - 1].X + (pl[i].X - pl[i - 1].X) * f, pl[i - 1].Y + (pl[i].Y - pl[i - 1].Y) * f);
                }
                acc += d;
            }
            return pl[pl.Count - 1];
        }

        static SKPath RingsPath(List<List<Vec2>> rings, Func<Vec2, SKPoint> P)
        {
            var path = new SKPath { FillType = SKPathFillType.EvenOdd };
            foreach (List<Vec2> ring in rings)
            {
                if (ring.Count < 2) continue;
                path.MoveTo(P(ring[0]));
                for (int i = 1; i < ring.Count; i++) path.LineTo(P(ring[i]));
                path.Close();
            }
            return path;
        }

        static SKBitmap ToBitmap(Rgba[] px, int w, int h)
        {
            var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul));
            var bytes = new byte[w * h * 4];
            for (int i = 0; i < px.Length; i++)
            {
                bytes[i * 4] = px[i].R;
                bytes[i * 4 + 1] = px[i].G;
                bytes[i * 4 + 2] = px[i].B;
                bytes[i * 4 + 3] = px[i].A;
            }
            Marshal.Copy(bytes, 0, bmp.GetPixels(), bytes.Length);
            bmp.NotifyPixelsChanged();
            return bmp;
        }

        // ------------------------------------------------------------------ text helpers

        static SKColor Sk(Rgba c) => new SKColor(c.R, c.G, c.B, 255);

        SKPaint Text(SKTypeface tf, float size, SKColor color) =>
            new SKPaint { Typeface = tf, TextSize = size, Color = color, IsAntialias = true, SubpixelText = true };

        /// <summary>Cuts <paramref name="text"/> with an ellipsis so it fits <paramref name="width"/>.</summary>
        static string Fit(string text, SKPaint p, float width)
        {
            if (string.IsNullOrEmpty(text) || p.MeasureText(text) <= width) return text ?? "";
            const string ell = "…";
            int lo = 0, hi = text.Length;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (p.MeasureText(text.Substring(0, mid) + ell) <= width) lo = mid; else hi = mid - 1;
            }
            return text.Substring(0, lo).TrimEnd() + ell;
        }

        /// <summary>Word-wraps <paramref name="text"/> to <paramref name="width"/>; newlines break lines, long words are split.</summary>
        static List<string> Wrap(string text, SKPaint p, float width)
        {
            var lines = new List<string>();
            if (string.IsNullOrEmpty(text)) { lines.Add(""); return lines; }
            foreach (string para in text.Replace("\r", "").Split('\n'))
            {
                string line = "";
                foreach (string word in para.Split(' '))
                {
                    string candidate = line.Length == 0 ? word : line + " " + word;
                    if (p.MeasureText(candidate) <= width) { line = candidate; continue; }
                    if (line.Length > 0) lines.Add(line);
                    line = word;
                    while (line.Length > 1 && p.MeasureText(line) > width)
                    {
                        int n = line.Length - 1;
                        while (n > 1 && p.MeasureText(line.Substring(0, n)) > width) n--;
                        lines.Add(line.Substring(0, n));
                        line = line.Substring(n);
                    }
                }
                lines.Add(line);
            }
            return lines;
        }
    }
}
