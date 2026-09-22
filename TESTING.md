# Testing SoundCalcs without Revit

Everything here runs headless on Linux or Windows with only the .NET 8 SDK. No Revit, no WPF window, no clicks.

```bash
scripts/selftest.sh              # all three layers below; output in harness-output/
```

| Layer | Command | What it proves |
|---|---|---|
| Unit tests | `dotnet test SoundCalcs.Tests` | Calculator formulas, heatmap colour mapping and grid geometry, job-input helpers |
| Compile check | `dotnet build SoundCalcs.CompileCheck -p:EnableWindowsTargeting=true` | All plugin C# (Revit + WPF code included) compiles for net48 **and** net8.0-windows, using NuGet reference assemblies; XAML-generated members are stubbed in `XamlStubs.cs` |
| Scenario harness | `dotnet run -c Release --project SoundCalcs.Harness -- --out harness-output` | Whole scenes go through the real pipeline: job built like `MainViewModel`, computed by `JobRunner.Compute`, drawn by the same `HeatmapMath` code as the viewer and the Revit renderer. Results are checked against physics and rendering invariants, and PNGs are written |

## The harness

`SoundCalcs.Harness` compiles the Revit-free plugin sources directly (`Domain/`, `Compute/`, `Visualization/HeatmapMath.cs`). Nothing is re-implemented, so a test result is a statement about plugin code.

For every scenario it writes, under `harness-output/`:

- `report.md` has a summary table plus per-scenario checks (✅ pass, ⚠️ warn, ❌ fail) and the images inline. `summary.json` has the same data in machine-readable form.
- `<scenario>/viewer_<MODE>.png` shows what the in-panel viewer draws. It uses the same P2–P98 colour range, one-pixel-per-cell bitmap, gradient, bilinear upscaling, wall/speaker symbols and legend.
- `<scenario>/revit_<MODE>.png` shows what `FilledRegionRenderer` creates in the plan view: the merged rectangles per colour band, with the panel legend.
- `<scenario>/input.json` and `output.json` hold the exact job input and results.
- `compute.log` is the calculator's diagnostic log. Normally it goes to `Documents\SoundCalcs_Debug.log`.

The exit code is 0 when there are no failures. Warnings are allowed.

### Checks

Generic checks run on every scenario, and on every one of the 11 visualization modes where relevant:

- One finite result per receiver, in order. STI and D50 are within [0, 1]. Broadband dB and dBA equal the energy sums of the bands.
- **Viewer:** every receiver gets its own bitmap pixel with no collisions or out-of-bounds, and each pixel has exactly the gradient colour of its value. The largest value is drawn green and the smallest red.
- **Revit:** filled-region strips tile every receiver cell exactly once (the total area equals N·spacing²), and each strip has the band of the receivers under it. Legend bands are contiguous, highest first, and span the rendered min/max.

Built-in physics scenarios (`--list`):

| Scenario | Expectation |
|---|---|
| `free_field_omni` | SPL = 90 − 20·log r − air absorption at every receiver; 6 dB per doubling; loudest at the speaker; no late energy → D50 = 1 |
| `two_sources_sum` | Incoherent energy sum; +3.01 dB on the bisector |
| `wall_partition` | Source side untouched by walls; shadow side carries exactly one partition's per-band TL |
| `cone_ceiling` | 0 dB on axis, −6 dB at the rated half-angle, never below the off-axis floor, narrower beam at high frequencies |
| `wall_mounted_aim` | Aims along the drag line; front ≈12 dB over back; directivity continuous |
| `reverberant_room` | Full ≥ Draft (reflections only add energy); more RT60 or noise lowers STI; C80/D50; Sabine diffuse level; STI vs a reference that counts reverberation once |
| `sti_reference` | STICalculator end points (SNR ±15, 0 dB) and comparison with an independent IEC 60268-16:2011 implementation (`IecReference.cs`) |

**Fail** means a broken invariant or a clear bug. **Warn** means the result deviates from a reference model (IEC / Sabine) or has a known UX weakness, and needs an engineering decision rather than an automatic fix.

### Your own scenes

```bash
# write a template, edit walls/speakers/probes, run it
dotnet run --project SoundCalcs.Harness -- --example-spec my_room.json
dotnet run --project SoundCalcs.Harness -- --spec my_room.json --modes SPL,STI,SPL_A

# replay a job the plugin actually ran in Revit (saved automatically on every run)
dotnet run --project SoundCalcs.Harness -- --job "%AppData%\RK Tools\SoundCalcs\jobs\job_20260101_120000_input.json"
```

A spec describes what you set up in the plugin UI. Walls are detail lines with an STC value, and if you leave out `Boundary` it is the convex hull of the wall endpoints, as *Select Boundary* does. Speakers have a position, a height above the level, an aim and a profile. You also set grid spacing, receiver height, boundary offset, quality and the environment. `Probes` assert SPL/STI ranges at points.

### Adding a built-in scenario

Add a method to `SoundCalcs.Harness/Scenarios.cs` that returns a `Scenario` with a `ScenarioSpec` and a `Checks` lambda, then list it in `BuiltInScenarios.All()`. Derive expectations independently of `SPLCalculator`, for example in closed form (see `Analytic`) or with a reference implementation. Otherwise the check only repeats the code under test.

## Shared code

The viewer (`UI/AcousticViewerControl.xaml.cs`) and `Visualization/FilledRegionRenderer.cs` take their colour range, band assignment, grid placement and strip merging from `Visualization/HeatmapMath.cs`. `MainViewModel` builds jobs through `Domain/JobInputBuilder.cs`. Keep new rendering or job-building logic in those Revit-free classes so the harness keeps covering it.
