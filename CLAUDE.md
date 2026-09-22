# SoundCalcs — notes for Claude

Revit plugin (net48 + net8.0-windows, WPF). Acoustic math is Revit-free in `Compute/` and `Domain/`. The visualization math shared by the viewer and the Revit renderer is in `Visualization/HeatmapMath.cs`.

## Verifying changes (no Revit / Windows needed)

Run `scripts/selftest.sh`. See TESTING.md for details.

- `dotnet test SoundCalcs.Tests` runs the unit tests.
- `dotnet build SoundCalcs.CompileCheck -p:EnableWindowsTargeting=true` compiles all plugin C# for both targets. Run it after touching anything under `UI/`, `Revit/`, `Visualization/` or `Commands/`. If you add an `x:Name` that code-behind uses, add it to `SoundCalcs.CompileCheck/XamlStubs.cs`.
- `dotnet run -c Release --project SoundCalcs.Harness -- --out harness-output` runs the scenario checks and writes `report.md` and PNGs. **Look at the PNGs** (Read tool) after changing calculations or rendering, because some bugs only show up visually (streaks, blocky patches, wrong orientation).

Before changing a calculation, run the harness first to get a baseline. If you fix a known FAIL/WARN, check that it turns green and that nothing else regresses.

## Conventions

- Plugin code must stay C# compatible with net48 (no records/init-only setters).
- The main `SoundCalcs.csproj` globs `**/*.cs`. New side projects must be excluded there, as `SoundCalcs.Tests`, `SoundCalcs.Harness` and `SoundCalcs.CompileCheck` are.
- Keep new job-building logic in `Domain/JobInputBuilder.cs` and rendering decisions in `HeatmapMath` so the harness exercises them.
