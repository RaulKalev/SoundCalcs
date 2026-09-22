# Testing SoundCalcs without Revit

Everything here runs headless on Linux or Windows with only the .NET 8 SDK. No Revit, no WPF window, no clicks.

```bash
scripts/selftest.sh              # all four layers below; output in harness-output/
```

| Layer | Command | What it proves |
|---|---|---|
| Unit tests | `dotnet test SoundCalcs.Tests` | Calculator formulas, heatmap colour mapping and grid geometry, job-input helpers |
| XAML checks | `python3 scripts/xamlcheck.py` | The XAML is not compiled on Linux, so this checks what would otherwise fail inside Revit: resource keys (and StaticResource order), styles on the right element types, event handlers and `x:Name` fields, binding paths, both palettes plus high contrast defining every key. It writes the icon names to `SoundCalcs.CompileCheck/XamlIcons.g.cs`, which the compile check then builds against `PackIconKind` |
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
| `wall_mounted_aim` | Aims along the drag line; ≈12 dB front/back at 1 kHz, nearly omni at 125 Hz; directivity continuous across 90° |
| `speaker_rotation` | Rotating a wall-mounted speaker moves its coverage lobe; rotating a ceiling cone changes nothing; only ceiling speakers set the ceiling height |
| `wall_materials` | RT60 estimated per room from the materials. The line style → wall type mapping carries the surface material: concrete reflects more than curtains, "Open (No Wall)" lines neither block, reflect nor enclose, and carpet / acoustic tiles weaken floor / ceiling reflections, and the RT60 estimate follows wall, floor and ceiling materials |
| `reverberant_room` | Full ≥ Draft; longer RT60 raises SPL and lowers STI; noise lowers STI; C80 uses 80 ms (≥ C50); SPL never below the Barron reverberant level; STI between the pure-diffuse-field (−0.03: the lattice's diffuse part builds up after the direct sound) and burst+tail IEC bounds |
| `room_shape` | Shoebox lattice vs the diffuse model: flat 30×24×3 m office falls steadily with distance; without perimeter walls it matches the explicit floor/ceiling reflections (≤ 1.5 dB); a proportionate 10×8×3 m room agrees in mean SPL (±1.5 dB) and STI (±0.1); a 40 m corridor falls steadily (≥ 5 dB over 30 m); an L-shaped room is left to the diffuse model unchanged |
| `screen_diffraction` | Free-standing wall: frequency-dependent shadow via diffraction around its ends, no hard edges; a 1.5 m screen shields a talker but hardly a ceiling speaker |
| `two_rooms` | Boundary split into walled rooms (and merged by a door gap); per-room volume, Barron level and Eyring RT60; a source's reverberant field stays in its room |
| `directivity_data` | Datasheet coverage angles (−6 dB at each half-angle, omni at 180°), polar-table CSV followed at every receiver, fallback when the file is unreadable |
| `measurement_comparison` | The `--measured` tool: zero error for exact data, correct bias and tolerance handling for offset data, CSV parsing, off-grid (wrong unit) detection |
| `sti_reference` | STICalculator end points (SNR ±15, 0 dB), exact agreement (±0.01) with an independent IEC 60268-16:2011 implementation (`IecReference.cs`) for noise, reverberation and both, and the reception threshold for quiet speech |

**Fail** means a broken invariant or a clear bug. **Warn** is available for deviations from a reference model that need an engineering decision rather than an automatic fix; all built-in checks currently pass without warnings.

`PhysicsRegressionTests.cs` in the unit tests pins each model bug the harness found (spurious wall blocking near walls, 2D reflection distances, missing / double-counted reverberant field, C80 split, directivity step behind speakers).

### Your own scenes

```bash
# write a template, edit walls/speakers/probes, run it
dotnet run --project SoundCalcs.Harness -- --example-spec my_room.json
dotnet run --project SoundCalcs.Harness -- --spec my_room.json --modes SPL,STI,SPL_A

# replay a job the plugin actually ran in Revit (saved automatically on every run)
dotnet run --project SoundCalcs.Harness -- --job "%AppData%\RK Tools\SoundCalcs\jobs\job_20260101_120000_input.json"
```

A spec describes what you set up in the plugin UI. Walls are detail lines with an STC value, and if you leave out `Boundary` it is the convex hull of the wall endpoints, as *Select Boundary* does. Speakers have a position, a height above the level, an aim and a profile. You also set grid spacing, receiver height, boundary offset, quality and the environment. `Probes` assert SPL/STI ranges at points.

### Comparing with measurements

Measure SPL, STI, C80 or octave bands at a few points in a finished room. Then replay the plugin's job for that room against your measurements:

```bash
dotnet run --project SoundCalcs.Harness -- --job "<id>_input.json" --measured measured.csv [--tol-spl 3 --tol-sti 0.05]
```

The file is a CSV with a header row:

```
name,x,y,spl,spla,sti,c80,spl125,spl250,spl500,spl1k,spl2k,spl4k,spl8k
```

Any subset of the metric columns works, and empty cells are skipped. A JSON array of `{Name, X, Y, Spl, SplA, Sti, C80, Bands[7]}` is also accepted. `x`/`y` are **metres in the model's coordinates** (the same as the job input). Points that land off the receiver grid are flagged, which usually means feet or a shifted origin.

The report gives, per metric: bias, RMS error, worst point, and how many points fall outside the tolerance. Error means predicted − measured. `<scenario>/measured_vs_predicted.csv` lists every point for a spreadsheet. A consistent bias usually points to a wrong input (speaker level, RT60, noise). A scatter usually points to geometry or directivity.

The `measurement_comparison` built-in scenario tests this tool itself. `docs/examples/measured_example.csv` shows the format.

Every scenario also runs a generic **"no isolated hot spots"** check: a receiver more than 3 dB louder than all four neighbours (away from speakers) is almost always a leak through a wall.

### Adding a built-in scenario

Add a method to `SoundCalcs.Harness/Scenarios.cs` that returns a `Scenario` with a `ScenarioSpec` and a `Checks` lambda, then list it in `BuiltInScenarios.All()`. Derive expectations independently of `SPLCalculator`, for example in closed form (see `Analytic`) or with a reference implementation. Otherwise the check only repeats the code under test.

## Shared code

The viewer (`UI/AcousticViewerControl.xaml.cs`) and `Visualization/FilledRegionRenderer.cs` take their colour range, band assignment, grid placement and strip merging from `Visualization/HeatmapMath.cs`. `MainViewModel` builds jobs through `Domain/JobInputBuilder.cs`. Keep new rendering or job-building logic in those Revit-free classes so the harness keeps covering it.
