using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using SoundCalcs.Domain;
using SoundCalcs.Report;
using SoundCalcs.Report.Pdf;
using SoundCalcs.UI.ViewModels;
using SoundCalcs.Visualization;

namespace SoundCalcs.Harness
{
    /// <summary>Checks of the report inputs: room bookkeeping on the results and the per-room statistics.</summary>
    public static class ReportChecks
    {
        /// <summary>Every result carries its receiver's room, and the room statistics account for every receiver once.</summary>
        public static void RoomIndices(AcousticJobInput input, AcousticJobOutput output, CheckContext ctx)
        {
            if (output.Results.Count == 0) return;
            var byIndex = input.Receivers.ToDictionary(r => r.Index, r => r.RoomIndex);
            int wrong = output.Results.Count(r => !byIndex.TryGetValue(r.ReceiverIndex, out int ri) || ri != r.RoomIndex);
            ctx.Assert("report: each result carries its receiver's room", wrong == 0, $"{wrong} result(s) with another room index");

            List<RoomStatistics> rooms = RoomStatisticsCalculator.Compute(input, output);
            int counted = rooms.Sum(r => r.ReceiverCount);
            int distinct = rooms.SelectMany(r => r.ResultIndices).Distinct().Count();
            ctx.Assert("report: room statistics cover every receiver once",
                counted == output.Results.Count && distinct == counted,
                $"{counted} counted, {distinct} distinct, {output.Results.Count} results");
        }
    
        /// <summary>The scenario as a report area, with the wall and speaker groups the plugin would keep.</summary>
        public static ReportAreaData Area(ScenarioRun run) => new ReportAreaData
        {
            AreaId = run.Spec.Name,
            AreaName = run.Spec.Name,
            LevelName = "Level 1",
            Notes = run.Spec.Description,
            Input = run.Input,
            Output = run.Output,
            WallGroups = run.Spec.WallGroups(),
            SpeakerGroups = run.Spec.SpeakerGroups()
        };

        public static ReportRequest Request(IEnumerable<ReportAreaData> areas, ReportLanguage lang) => new ReportRequest
        {
            Project = new ProjectInfo
            {
                ProjectName = "SoundCalcs harness", ProjectNumber = "H-001", Client = "Test client", Company = "RK Tools",
                Author = "Harness", CheckedBy = "Reviewer", Revision = "A", Description = "Headless report built from the harness scenarios."
            },
            Options = new ReportOptions { Language = lang },
            Areas = areas.ToList(),
            ModelName = "harness.rvt",
            PluginVersion = "harness",
            Date = new DateTime(2026, 1, 15, 10, 0, 0)
        };

        /// <summary>
        /// Builds and paints the scenario's report: report.pdf and one PNG per page in <paramref name="dir"/>, with
        /// checks of the content (rooms, figures, value charts, speaker contributions) and of the PDF.
        /// </summary>
        public static void WriteReport(ScenarioRun run, string dir, ReportLanguage lang, CheckContext ctx, List<string> images, string outDir)
        {
            ReportRequest req = Request(new[] { Area(run) }, lang);
            ReportBuildResult built = ReportBuilder.Build(req);
            ReportDocument doc = built.Document;
            AreaAnalysis area = built.Areas.Single();

            // Plan figures use the viewer's colour range
            foreach (FigureBlock f in doc.Blocks.OfType<FigureBlock>().Where(f => f.Figure.Values.Count == 0))
            {
                double[] vals = run.Output.Results.Select(r => HeatmapMath.GetValue(r, f.Figure.Mode)).ToArray();
                var (lo, hi) = HeatmapMath.ComputeViewerRange(vals, f.Figure.Mode);
                ctx.Assert($"report: {f.Figure.Mode} plan uses the viewer's range", Math.Abs(lo - f.Figure.MinVal) < 1e-9 && Math.Abs(hi - f.Figure.MaxVal) < 1e-9,
                    $"figure {f.Figure.MinVal:0.###}–{f.Figure.MaxVal:0.###}, viewer {lo:0.###}–{hi:0.###}");
            }

            // Value charts: one number per receiver of the room, one min and one max
            var charts = doc.Blocks.OfType<FigureBlock>().Where(f => f.Figure.Values.Count > 0).ToList();
            int expectedCharts = area.Rooms.Count * req.Options.ValueChartModes.Distinct().Count();
            ctx.Assert("report: a value chart per room and quantity", charts.Count == expectedCharts, $"{charts.Count} charts, expected {expectedCharts}");
            int bad = 0;
            for (int i = 0; i < charts.Count && i < expectedCharts; i++)
            {
                RoomStatistics room = area.Rooms[i / req.Options.ValueChartModes.Distinct().Count()];
                PlanFigure fig = charts[i].Figure;
                if (fig.Values.Count != room.ReceiverCount || fig.Values.Count(v => v.IsMin) != 1 ||
                    fig.Values.Count(v => v.IsMax) != (room.ReceiverCount > 1 ? 1 : 0)) bad++;
            }
            ctx.Assert("report: value charts list every point of their room, with one min and one max", bad == 0, $"{bad} chart(s) wrong");

            // Speaker contributions add up to the job's level (the model is additive in energy over speakers)
            foreach (var rc in area.Contributions.Values)
            {
                if (double.IsNaN(rc.SumDbA)) continue;
                string name = area.Rooms.First(r => r.RoomIndex == rc.RoomIndex).Name;
                ctx.Assert($"report: speaker contributions in '{name}' sum to the level there", Math.Abs(rc.SumDbA - rc.TotalDbA) <= 0.5,
                    $"sum {rc.SumDbA:0.00} dBA, job {rc.TotalDbA:0.00} dBA", CheckSeverity.Warn);
            }

            // Paint: every heading gets a page; the PDF is a PDF; pages as PNG to look at
            string pdf = Path.Combine(dir, "report.pdf");
            int pages = PdfReportWriter.Write(doc, pdf);
            using (var painter = new SkiaReportPainter(doc))
            {
                var headings = doc.Blocks.OfType<HeadingBlock>().ToList();
                ctx.Assert("report: every heading is placed on a page", headings.All(h => painter.AnchorPages.ContainsKey(h.Anchor)),
                    $"{headings.Count(h => !painter.AnchorPages.ContainsKey(h.Anchor))} missing");
                var order = headings.Select(h => painter.AnchorPages[h.Anchor]).ToList();
                ctx.Assert("report: headings appear in order", order.Zip(order.Skip(1), (a, b) => a <= b).All(x => x), string.Join(",", order));
                for (int i = 0; i < painter.PageCount; i++)
                {
                    string png = Path.Combine(dir, $"report_p{i + 1:00}.png");
                    PdfReportWriter.SavePagePng(painter, i, 110, png);
                    images.Add(Path.GetRelativePath(outDir, png));
                }
            }
            byte[] head = File.ReadAllBytes(pdf).Take(5).ToArray();
            ctx.Assert("report: PDF written", pages >= 3 && System.Text.Encoding.ASCII.GetString(head) == "%PDF-", $"{pages} pages");

            string text = PdfText(pdf);
            if (text != null)
            {
                var missing = area.Rooms.Select(r => PlanFigureBuilder.RoomDisplayName(r.Name)).Where(n => !text.Contains(n)).ToList();
                ctx.Assert("report: PDF text holds every room name (text is selectable)", missing.Count == 0, string.Join(", ", missing));
            }
        }

        /// <summary>The PDF's text by pdftotext, or null when the tool is not installed.</summary>
        static string PdfText(string pdf)
        {
            try
            {
                var psi = new ProcessStartInfo("pdftotext", $"-layout \"{pdf}\" -") { RedirectStandardOutput = true, UseShellExecute = false };
                using (var p = Process.Start(psi))
                {
                    string s = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(20000);
                    return p.ExitCode == 0 ? s : null;
                }
            }
            catch { return null; }
        }
    }
}
