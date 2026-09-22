# SoundCalcs

A Revit plugin for room acoustics analysis. Calculates per-receiver SPL and Speech Transmission Index (STI) from speaker layouts and renders interactive heatmaps directly in the active plan view.

## Features

- **SPL heatmap** — broadband Sound Pressure Level, red (quiet) → dark green (loud)
- **Per-octave-band heatmaps** — individual SPL maps at 125 / 250 / 500 / 1k / 2k / 4k / 8k Hz
- **STI heatmap** — IEC 60268-16:2011 Speech Transmission Index (indirect method: MTF of the computed impulse response incl. arrival times, male/female α/β weighting, auditory masking and reception threshold) with IEC intelligibility categories (Bad < 0.30 … Excellent ≥ 0.75)
- **Wall types** — detail lines in the plan view act as wall boundaries; each line style is assigned a wall type with an STC rating (sound through the wall, per band via the ASTM E413 contour) and a surface material (concrete, brick, drywall, glass, wood, curtain, open) used for reflections and the RT60 estimate 
- **Floor & ceiling finishes** — chosen in the Grid tab (hard floor, wood, carpet; plasterboard, concrete, wood, acoustic tiles, acoustic panels), used for floor/ceiling reflections and the RT60 estimate
- **Reflections** — image-source method: first-order wall reflections (plus second-order and floor/ceiling in Full quality), 3D path lengths, transmission loss on every leg
- **Reverberant field** — Barron's revised theory per octave band (RT60 configurable): the Sabine diffuse level decaying with distance, minus the energy the explicit reflections already carry; included in SPL and as the reverberant tail for STI, C80 and D50
- **Room-shape-aware reverberation** — in Full quality, rooms that are (near-)rectangular (≥ 90 % of their fitted box) use a shoebox image-source lattice instead of the diffuse model: the specular reflections of all six surfaces up to 150 ms, with each wall side's own material (uncovered parts count as open), calibrated so the room still decays at its RT60, plus 20 % scattering per reflection into a diffuse field that starts with the direct sound. In flat rooms and corridors the level therefore follows the room's shape (reflective walls, channelling along a corridor) instead of a single diffuse level; L-shaped and irregular rooms, the open remainder and Draft quality keep Barron's model
- **Clarity** — C80 (80 ms split) and D50 (50 ms split) over the 500 Hz / 1 kHz bands
- **Speaker frequency response** — per-type preset (flat, typical ceiling / column / horn) or custom per-band values; normalised so the SPL column stays the broadband level
- **STI test signal** — IEC 60268-16 standard male/female speech spectrum played through each speaker's response
- **RT60 estimate** — Eyring from the actual boundary perimeter, wall lines (internal partitions count both faces, open boundary absorbs fully), floor/ceiling/wall materials, occupancy and air absorption
- **Air absorption** — frequency-dependent attenuation per IEC
- **Speaker directivity** — cone angle, datasheet coverage angles per octave band, or a measured polar table CSV (see `docs/examples/polar_table_example.csv`); omnidirectional; GLL stub
- **Wall height & diffraction** — per line style height (screens, low partitions); thin-screen diffraction (Kurze–Anderson / Maekawa) around free wall ends and over partial walls, with no hard shadow edges
- **Rooms** — the boundary is split into the rooms its walls form plus the open remainder; each has its own volume and reverberant field, optionally its own RT60 estimated from its geometry ("Per room")
- **Linked IFC support** — wall boundaries are drawn as detail lines (not Revit wall elements), which works reliably with linked IFC models
- **Configurable environment** — temperature, per-band RT60, per-band background noise
- **Live legend** — always reflects the exact rendered min/max range

## Revit Compatibility

| Target | Framework | Revit version |
|--------|-----------|---------------|
| net48  | .NET 4.8  | Revit 2024    |
| net8.0-windows | .NET 8 | Revit 2026 |

The output DLL is signed with a code-signing certificate.

## How It Works

1. **Draw detail lines** in your plan view to represent wall boundaries. Assign each line style to a wall type (e.g. *200 mm Concrete — STC 55*) in the plugin panel.
2. **Place speaker families** in the model. The plugin picks up their positions and facing directions.
3. **Define a receiver grid** (spacing, coverage area).
4. **Run the analysis** — the plugin computes SPL and STI at every grid point on a background thread with a live progress bar.
5. **Switch visualization modes** — choose broadband SPL, any octave band, or STI. The heatmap updates in-place without re-running the analysis.
6. **Read the legend** — each colour band shows the exact dB (or STI) range it covers.

## Project Structure

```
Commands/          Revit IExternalCommand entry point
Compute/           Pure-C# acoustic math (SPLCalculator, STICalculator, JobRunner)
Domain/            Data models (AcousticJobInput/Output, ComputeWall, ReceiverPoint, …)
IO/                JSON job serialisation, file logger, settings store
Revit/             Revit API helpers (data collector, dispatcher, compat shims)
UI/                WPF panel (MainWindow, MainViewModel, converters, themes)
Visualization/     FilledRegionRenderer — row-strip heatmap drawing
Assets/            Ribbon icon
Properties/        AssemblyInfo, application settings
```

## Building

Requirements:
- Visual Studio 2022 or `dotnet` SDK 8+
- Revit 2024 installed at `E:\Autodesk\Revit 2024\` (for net48 references)
- Revit 2026 installed at `E:\Revit 2026\` (for net8.0-windows references)

```powershell
dotnet build SoundCalcs.csproj -c Release
```

Output lands in `C:\Users\<you>\OneDrive\Desktop\DevDlls\SoundCalcs\`.  
Copy `SoundCalcs.dll` (net48 build) to your Revit add-ins folder and register it with a `.addin` manifest.

## Testing

The calculations and both heatmap renderers can be tested headless, without Revit:

```bash
scripts/selftest.sh   # unit tests + plugin compile check + scenario harness with PNG heatmaps
```

See [TESTING.md](TESTING.md) for the scenario harness, custom scene specs and replaying saved jobs.

## Dependencies

| Package | Purpose |
|---------|---------|
| Costura.Fody | Merges all NuGet DLLs into the single output assembly |
| MaterialDesignThemes | WPF UI styling |
| Newtonsoft.Json | Job serialisation |
| ricaun.Revit.UI | Ribbon registration helper |
