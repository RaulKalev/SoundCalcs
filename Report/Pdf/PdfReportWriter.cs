using System;
using System.IO;
using SkiaSharp;

namespace SoundCalcs.Report.Pdf
{
    /// <summary>Writes a <see cref="ReportDocument"/> as a vector PDF (text stays text) and renders page previews.</summary>
    public static class PdfReportWriter
    {
        /// <summary>Writes the PDF to <paramref name="path"/> (through a temporary file); returns the page count.</summary>
        public static int Write(ReportDocument doc, string path)
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string temp = path + ".tmp";
            int pages;
            using (var fs = File.Create(temp))
                pages = Write(doc, fs);
            if (File.Exists(path)) File.Delete(path);
            File.Move(temp, path);
            return pages;
        }

        public static int Write(ReportDocument doc, Stream stream)
        {
            var meta = new SKDocumentPdfMetadata
            {
                Title = doc.Title ?? "",
                Subject = doc.Subject ?? "",
                Author = doc.Author ?? "",
                Creator = "SoundCalcs",
                Producer = "SoundCalcs (SkiaSharp)",
                Creation = doc.Created,
                Modified = doc.Created,
                RasterDpi = 216
            };
            using (var painter = new SkiaReportPainter(doc))
            using (var wstream = new SKManagedWStream(stream))
            using (SKDocument pdf = SKDocument.CreatePdf(wstream, meta))
            {
                if (pdf == null) throw new InvalidOperationException("PDF output is not available.");
                int n = painter.PageCount;
                for (int i = 0; i < n; i++)
                {
                    SKCanvas canvas = pdf.BeginPage(painter.PageWidth, painter.PageHeight);
                    painter.PaintPage(canvas, i);
                    pdf.EndPage();
                }
                pdf.Close();
                return n;
            }
        }

        /// <summary>Renders one page at <paramref name="dpi"/> (72 = one pixel per point).</summary>
        public static SKBitmap RenderPage(SkiaReportPainter painter, int index, float dpi)
        {
            float k = dpi / 72f;
            int w = (int)Math.Ceiling(painter.PageWidth * k), h = (int)Math.Ceiling(painter.PageHeight * k);
            var bmp = new SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
            using (var canvas = new SKCanvas(bmp))
            {
                canvas.Clear(SKColors.White);
                canvas.Scale(k);
                painter.PaintPage(canvas, index);
            }
            return bmp;
        }

        /// <summary>Renders one page to a PNG file.</summary>
        public static void SavePagePng(SkiaReportPainter painter, int index, float dpi, string path)
        {
            using (SKBitmap bmp = RenderPage(painter, index, dpi))
            using (SKImage img = SKImage.FromBitmap(bmp))
            using (SKData data = img.Encode(SKEncodedImageFormat.Png, 90))
            using (var fs = File.Create(path))
                data.SaveTo(fs);
        }
    }
}
