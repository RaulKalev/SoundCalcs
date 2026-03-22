# SoundCalcs

A Revit plugin for room acoustics analysis. Calculates per-receiver SPL and Speech Transmission Index (STI) from speaker layouts and renders interactive heatmaps directly in the active plan view.

## Features

- **SPL heatmap** — broadband Sound Pressure Level, red (quiet) → dark green (loud)
- **Per-octave-band heatmaps** — individual SPL maps at 125 / 250 / 500 / 1k / 2k / 4k / 8k Hz
- **STI heatmap** — IEC 60268-16 Speech Transmission Index with intelligibility labels (Bad → Excellent)
- **Wall transmission loss** — detail lines in the plan view act as wall boundaries; each line style is assigned an STC rating. Sound through walls is attenuated per band using the ASTM E413 STC contour
- **First-order reflections** — image-source method, wall surface absorption considered on both incoming and outgoing legs
- **Reverberant field** — Sabine room acoustics applied per octave band (RT60 configurable)
- **Air absorption** — frequency-dependent attenuation per IEC
- **Speaker directivity** — configurable cone angle or omnidirectional; GLL stub support
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

## Dependencies

| Package | Purpose |
|---------|---------|
| Costura.Fody | Merges all NuGet DLLs into the single output assembly |
| MaterialDesignThemes | WPF UI styling |
| Newtonsoft.Json | Job serialisation |
| ricaun.Revit.UI | Ribbon registration helper |
