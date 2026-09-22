using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using SoundCalcs.Domain;
using SoundCalcs.IO;
using SoundCalcs.UI.ViewModels;
using SoundCalcs.Visualization;

namespace SoundCalcs.Harness
{
    public static class Program
    {
        const string Usage = @"SoundCalcs headless harness

Runs acoustic scenarios through the plugin's real compute engine and heatmap code,
checks physics and rendering invariants, and writes PNG images plus a report.

Usage:
  dotnet run -c Release --project SoundCalcs.Harness -- [options]

Options:
  --out <dir>             Output directory (default: harness-output)
  --scenario <names>      Comma-separated built-in scenarios to run (default: all)
  --spec <file.json>      Run scenario(s) from a JSON ScenarioSpec file (object or array)
  --job <input.json>      Replay a job input saved by the plugin
                          (%AppData%\RK Tools\SoundCalcs\jobs\<id>_input.json)
  --modes <list>          Viewer image modes for --spec/--job runs (default: SPL,STI)
  --measured <file>       Compare --spec/--job runs with measurements (CSV or JSON; x,y in
                          model metres; columns spl, spla, sti, c80, spl125 … spl8k)
  --tol-spl <dB>          Tolerance for SPL comparisons (default 3)
  --tol-sti <value>       Tolerance for STI comparisons (default 0.05)
  --no-images             Skip PNG rendering
  --list                  List built-in scenarios and exit
  --example-spec <file>   Write an example ScenarioSpec JSON and exit
  --help                  Show this help

Exit code: 0 = all checks passed (warnings allowed), 1 = a check failed, 2 = usage error.";

        static readonly JsonSerializerSettings Json = new JsonSerializerSettings
        {
            Formatting = Formatting.Indented,
            Converters = { new StringEnumConverter() },
            NullValueHandling = NullValueHandling.Ignore
        };

        public static int Main(string[] args)
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;

            string outDir = "harness-output";
            var only = new List<string>();
            var specFiles = new List<string>();
            var jobFiles = new List<string>();
            var modes = new[] { VisualizationMode.SPL, VisualizationMode.STI };
            bool images = true;
            List<MeasuredPoint> measured = null;
            var tolerances = new MeasurementTolerances();

            try
            {
                for (int i = 0; i < args.Length; i++)
                {
                    switch (args[i])
                    {
                        case "--out": outDir = args[++i]; break;
                        case "--scenario": only.AddRange(args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries)); break;
                        case "--spec": specFiles.Add(args[++i]); break;
                        case "--job": jobFiles.Add(args[++i]); break;
                        case "--modes":
                            modes = args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries)
                                .Select(m => (VisualizationMode)Enum.Parse(typeof(VisualizationMode), m.Trim(), true)).ToArray();
                            break;
                        case "--no-images": images = false; break;
                        case "--measured": measured = Measurements.Load(args[++i]); break;
                        case "--tol-spl": tolerances.SplDb = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
                        case "--tol-sti": tolerances.Sti = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
                        case "--list":
                            foreach (var s in BuiltInScenarios.All())
                                Console.WriteLine($"{s.Spec.Name,-20} {s.Spec.Description}");
                            return 0;
                        case "--example-spec":
                            File.WriteAllText(args[++i], JsonConvert.SerializeObject(ExampleSpec(), Json));
                            Console.WriteLine($"Wrote {args[i]}");
                            return 0;
                        case "--help": case "-h":
                            Console.WriteLine(Usage);
                            return 0;
                        default:
                            Console.Error.WriteLine($"Unknown argument '{args[i]}'.\n\n{Usage}");
                            return 2;
                    }
                }
            }
            catch (Exception ex) when (ex is IndexOutOfRangeException || ex is ArgumentException ||
                                       ex is IOException || ex is FormatException || ex is JsonException ||
                                       ex is InvalidOperationException)
            {
                Console.Error.WriteLine($"Bad arguments: {ex.Message}\n\n{Usage}");
                return 2;
            }

            Directory.CreateDirectory(outDir);
            FileLogger.LogPath = Path.Combine(outDir, "compute.log");
            FileLogger.Clear();

            var scenarios = new List<Scenario>();
            bool custom = specFiles.Count > 0 || jobFiles.Count > 0;
            if (!custom || only.Count > 0)
            {
                var all = BuiltInScenarios.All();
                var unknown = only.Where(n => all.All(s => s.Spec.Name != n)).ToList();
                if (unknown.Count > 0)
                {
                    Console.Error.WriteLine($"Unknown scenario(s): {string.Join(", ", unknown)}. Use --list.");
                    return 2;
                }
                scenarios.AddRange(only.Count == 0 ? all : all.Where(s => only.Contains(s.Spec.Name)));
            }
            foreach (string f in specFiles)
                scenarios.AddRange(LoadSpecs(f).Select(s => new Scenario
                {
                    Spec = s, ViewerModes = modes, RevitModes = modes,
                    Checks = (run, ctx) =>
                    {
                        ProbeChecks(run, ctx);
                        if (measured != null) Measurements.Compare(run, measured, tolerances, ctx, outDir);
                    }
                }));

            var reports = new List<ScenarioReport>();
            foreach (var sc in scenarios)
                reports.Add(RunScenario(sc, null, outDir, images));
            foreach (string f in jobFiles)
                reports.Add(RunJobFile(f, modes, outDir, images, measured, tolerances));

            WriteReports(reports, outDir);

            int fails = reports.Sum(r => r.Checks.Count(c => c.Status == "FAIL"));
            int warns = reports.Sum(r => r.Checks.Count(c => c.Status == "WARN"));
            int passes = reports.Sum(r => r.Checks.Count(c => c.Status == "PASS"));
            Console.WriteLine();
            Console.WriteLine($"{passes} passed, {warns} warnings, {fails} failed. Report: {Path.Combine(outDir, "report.md")}");
            return fails > 0 ? 1 : 0;
        }

        // ------------------------------------------------------------------
        // Running
        // ------------------------------------------------------------------

        class ScenarioReport
        {
            public string Name { get; set; }
            public string Description { get; set; }
            public int Receivers { get; set; }
            public int Sources { get; set; }
            public int Walls { get; set; }
            public string Quality { get; set; }
            public double ComputeSeconds { get; set; }
            public double[] SplRange { get; set; }
            public double[] StiRange { get; set; }
            public double[] C80Range { get; set; }
            public List<CheckResult> Checks { get; set; } = new List<CheckResult>();
            public List<string> Images { get; set; } = new List<string>();
            public string Error { get; set; }
        }

        static ScenarioReport RunScenario(Scenario sc, ScenarioRun preRun, string outDir, bool images)
        {
            var ctx = new CheckContext();
            var report = new ScenarioReport { Name = sc.Spec.Name, Description = sc.Spec.Description };
            Console.WriteLine($"== {sc.Spec.Name}");

            try
            {
                ScenarioRun run = preRun;
                if (run == null && sc.HasScene)
                {
                    var sw = Stopwatch.StartNew();
                    run = ScenarioRun.Execute(sc.Spec);
                    report.ComputeSeconds = sw.Elapsed.TotalSeconds;
                }

                if (run != null)
                {
                    Summarise(run, report);
                    VisualChecks.Run(run.Input, run.Output, HeatmapMath.ViewerGridSpacing(run.Output.Results), ctx);
                }
                sc.Checks?.Invoke(run, ctx);

                if (run != null && run.Output.Results.Count > 0)
                {
                    string dir = Path.Combine(outDir, sc.Spec.Name);
                    Directory.CreateDirectory(dir);
                    File.WriteAllText(Path.Combine(dir, "input.json"), JsonConvert.SerializeObject(run.Input, Json));
                    File.WriteAllText(Path.Combine(dir, "output.json"), JsonConvert.SerializeObject(run.Output, Json));

                    if (images)
                    {
                        foreach (var mode in sc.ViewerModes)
                        {
                            string file = Path.Combine(dir, $"viewer_{mode}.png");
                            ViewerImage.Render(run.Input, run.Output, mode, HeatmapMath.ViewerGridSpacing(run.Output.Results), sc.Spec.Name, file);
                            report.Images.Add(Rel(outDir, file));
                        }
                        foreach (var mode in sc.RevitModes)
                        {
                            string file = Path.Combine(dir, $"revit_{mode}.png");
                            RevitImage.Render(run.Input, run.Output, mode, null, sc.Spec.Name, file);
                            report.Images.Add(Rel(outDir, file));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                report.Error = ex.ToString();
                ctx.Assert("scenario ran without exceptions", false, ex.GetType().Name + ": " + ex.Message);
            }

            report.Checks = ctx.Results;
            foreach (var c in ctx.Results)
                Console.WriteLine($"   [{c.Status}] {c.Name}" + (c.Passed ? "" : $" — {c.Detail}"));
            return report;
        }

        static ScenarioReport RunJobFile(string path, VisualizationMode[] modes, string outDir, bool images,
            List<MeasuredPoint> measured, MeasurementTolerances tolerances)
        {
            var input = JsonConvert.DeserializeObject<AcousticJobInput>(File.ReadAllText(path));
            var output = SoundCalcs.Compute.JobRunner.Compute(input, System.Threading.CancellationToken.None, null);
            double spacing = HeatmapMath.EstimateGridSpacing(output.Results);
            var spec = new ScenarioSpec
            {
                Name = "job_" + Path.GetFileNameWithoutExtension(path),
                Description = $"Replay of plugin job input {Path.GetFileName(path)} " +
                              $"(grid spacing estimated as {spacing:0.###} m).",
                GridSpacingM = spacing,
                Quality = input.Quality
            };
            var run = new ScenarioRun { Spec = spec, Input = input, Output = output };
            return RunScenario(new Scenario
            {
                Spec = spec, ViewerModes = modes, RevitModes = modes,
                Checks = measured == null ? null : (Action<ScenarioRun, CheckContext>)((r, ctx) =>
                    Measurements.Compare(r, measured, tolerances, ctx, outDir))
            }, run, outDir, images);
        }

        static void ProbeChecks(ScenarioRun run, CheckContext ctx)
        {
            foreach (var p in run.Spec.Probes)
            {
                var r = run.Nearest(p.X, p.Y);
                string name = string.IsNullOrEmpty(p.Name) ? $"probe ({p.X}, {p.Y})" : p.Name;
                if (p.MinSplDb.HasValue || p.MaxSplDb.HasValue)
                    ctx.InRange($"{name}: SPL", r.SplDb, p.MinSplDb ?? double.MinValue, p.MaxSplDb ?? double.MaxValue, " dB");
                if (p.MinSti.HasValue || p.MaxSti.HasValue)
                    ctx.InRange($"{name}: STI", r.Sti, p.MinSti ?? 0, p.MaxSti ?? 1);
            }
        }

        static void Summarise(ScenarioRun run, ScenarioReport report)
        {
            var res = run.Output.Results;
            report.Receivers = res.Count;
            report.Sources = run.Input.Sources.Count;
            report.Walls = run.Input.Walls.Count;
            report.Quality = run.Input.Quality.ToString();
            if (res.Count == 0) return;
            report.SplRange = new[] { res.Min(r => r.SplDb), res.Max(r => r.SplDb) };
            report.StiRange = new[] { res.Min(r => r.Sti), res.Max(r => r.Sti) };
            report.C80Range = new[] { res.Min(r => r.C80Db), res.Max(r => r.C80Db) };
        }

        static List<ScenarioSpec> LoadSpecs(string path)
        {
            string text = File.ReadAllText(path).TrimStart();
            return text.StartsWith("[")
                ? JsonConvert.DeserializeObject<List<ScenarioSpec>>(text, Json)
                : new List<ScenarioSpec> { JsonConvert.DeserializeObject<ScenarioSpec>(text, Json) };
        }

        static ScenarioSpec ExampleSpec()
        {
            var s = new ScenarioSpec
            {
                Name = "my_classroom",
                Description = "9×7 m classroom, two ceiling speakers.",
                Walls = ScenarioSpec.RectangleWalls(0, 0, 9, 7, 45),
                Speakers =
                {
                    new SpeakerSpec { X = 3, Y = 3.5, HeightM = 2.8, Profile = ScenarioSpec.Cone(88, 60, -12) },
                    new SpeakerSpec { X = 6, Y = 3.5, HeightM = 2.8, Profile = ScenarioSpec.Cone(88, 60, -12) },
                },
                Probes =
                {
                    new ProbeSpec { Name = "front row", X = 1.5, Y = 3.5, MinSplDb = 70, MinSti = 0.5 },
                    new ProbeSpec { Name = "back corner", X = 8.5, Y = 6.5, MinSplDb = 65 },
                }
            };
            return s;
        }

        // ------------------------------------------------------------------
        // Reports
        // ------------------------------------------------------------------

        static void WriteReports(List<ScenarioReport> reports, string outDir)
        {
            File.WriteAllText(Path.Combine(outDir, "summary.json"), JsonConvert.SerializeObject(reports, Json));

            var md = new StringBuilder();
            md.AppendLine("# SoundCalcs harness report");
            md.AppendLine();
            md.AppendLine("| Scenario | Receivers | SPL range (dB) | STI range | Pass | Warn | Fail |");
            md.AppendLine("|---|---:|---|---|---:|---:|---:|");
            foreach (var r in reports)
            {
                md.AppendLine($"| [{r.Name}](#{r.Name.Replace('_', '-')}) | {r.Receivers} | {Range(r.SplRange, "F1")} | " +
                              $"{Range(r.StiRange, "F2")} | {r.Checks.Count(c => c.Status == "PASS")} | " +
                              $"{r.Checks.Count(c => c.Status == "WARN")} | {r.Checks.Count(c => c.Status == "FAIL")} |");
            }

            foreach (var r in reports)
            {
                md.AppendLine();
                md.AppendLine($"## {r.Name}");
                md.AppendLine();
                if (!string.IsNullOrEmpty(r.Description)) { md.AppendLine(r.Description); md.AppendLine(); }
                if (r.Receivers > 0)
                {
                    md.AppendLine($"{r.Sources} source(s), {r.Walls} wall segment(s), {r.Receivers} receivers, " +
                                  $"{r.Quality} quality, computed in {r.ComputeSeconds:F2} s. " +
                                  $"C80 range {Range(r.C80Range, "F1")} dB.");
                    md.AppendLine();
                }
                md.AppendLine("| Status | Check | Detail |");
                md.AppendLine("|---|---|---|");
                foreach (var c in r.Checks)
                    md.AppendLine($"| {(c.Status == "PASS" ? "✅" : c.Status == "WARN" ? "⚠️" : "❌")} {c.Status} | " +
                                  $"{Esc(c.Name)} | {Esc(c.Detail)} |");
                if (r.Images.Count > 0)
                {
                    md.AppendLine();
                    foreach (var img in r.Images)
                        md.AppendLine($"![{img}]({img.Replace('\\', '/')})");
                }
                if (r.Error != null)
                {
                    md.AppendLine();
                    md.AppendLine("```");
                    md.AppendLine(r.Error);
                    md.AppendLine("```");
                }
            }
            File.WriteAllText(Path.Combine(outDir, "report.md"), md.ToString());
        }

        static string Range(double[] r, string fmt) => r == null ? "-" : $"{r[0].ToString(fmt)} – {r[1].ToString(fmt)}";

        static string Esc(string s) => (s ?? "").Replace("|", "\\|").Replace("\n", " ");

        static string Rel(string root, string path) => Path.GetRelativePath(root, path);
    }
}
