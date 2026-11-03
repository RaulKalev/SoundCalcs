# Plan: PDF reports and multiple areas per project

Goal: export calculation results as a PDF report, and organise work the way DIALux does for
lighting: a project holds several **areas** (rooms or zones), each with its own boundary, walls,
speakers, settings and results, and the report dialog lets you tick the rooms to include.

## 1. Where the plugin is today

| Topic | Today | What the plan needs |
|---|---|---|
| Layout storage | One layout per *user* in `%AppData%\RK Tools\SoundCalcs\settings.json`: one boundary, one wall set, one speaker pick, one set of Room-page settings (`IO/SettingsStore.cs`, `PluginSettings`). `LayoutProjectKey` only *warns* when it belongs to another project. | A project file holding N areas, keyed by the Revit document. |
| Results | `JobRunner` writes `jobs/<jobId>_input.json` and `_output.json`. `MainViewModel.LastOutput` is in memory only; "Import latest" reloads the newest file, whichever project it came from. | Each area remembers its last job id; the report reads input + output pairs. |
| Rooms | `JobInputBuilder.BuildRoomsAndReceivers` splits the boundary into rooms (`"Boundary – Room 1"`, "Open area"). `ReceiverPoint.RoomIndex` is set on the **input**, but `ReceiverResult` (output) has no room index, so per-room statistics cannot be computed from an output file alone. | `RoomIndex` on `ReceiverResult`, plus a Revit-free per-room statistics class. |
| Rendering | `Visualization/HeatmapMath.cs` builds the one-pixel-per-cell grid, colour range, legend bands; the viewer paints it with SkiaSharp, the harness with its own `Raster`. | The report heatmap figure reuses `HeatmapMath` exactly, so what is printed is what the viewer shows. |
| PDF capability | SkiaSharp 2.88.8 is already embedded for both targets (net48 + net8). Skia has a vector PDF backend: `SKDocument.CreatePdf(stream, metadata)` → `BeginPage` → ordinary `SKCanvas` drawing. | No new dependency for PDF output. |
| Headless checks | Tests and the harness compile `Domain/`, `Compute/`, `Visualization/HeatmapMath.cs` without Skia. | Report *content and layout data* must stay Skia-free so the harness and tests can cover it. |

## 2. Decisions

### 2.1 PDF engine: SkiaSharp (already shipped)

- `SKDocument.CreatePdf` gives real vector PDF: text stays text (selectable, searchable), walls and
  tables are vectors, the heatmap is an embedded bitmap drawn through the same
  `HeatmapMath.BuildViewerGrid` as the viewer (bilinear-scaled, like the viewer).
- Works on net48 (Revit 2024) and net8 (Revit 2026) with the native `libSkiaSharp` already embedded.
- Fonts: Segoe UI via `SKTypeface.FromFamilyName`, embedded (subset) by Skia automatically.

Alternatives considered and set aside:

- **QuestPDF**: nice fluent layout, but it carries its own SkiaSharp native build; two copies of
  `libSkiaSharp` inside one Revit process is a reliable way to crash.
- **PdfSharp / PdfSharpCore**: fine library, but one more dependency to merge with Costura and a second
  drawing API next to Skia for the same figures.
- **WPF FlowDocument → XPS**: no PDF without a converter, and the heatmap would have to be re-drawn
  with WPF primitives.

### 2.2 Report code lives in a Revit-free `Report/` folder with a tiny display list

```
Report/
  ReportOptions.cs      what to include (areas/rooms, sections, modes, page size, project info)
  RoomStatistics.cs     per-room numbers from one input + output pair (Revit-free, unit tested)
  ReportModel.cs        the content: cover, summary table, per-room sections, tables, figures
  ReportBuilder.cs      ReportOptions + project data + jobs → ReportModel (Revit-free, harness-tested)
  ReportBlocks.cs       display list: Heading, Paragraph, KeyValueTable, DataTable, HeatmapFigure,
                        LegendStrip, PageBreak — no fonts, no Skia, no sizes in pixels
  SkiaReportPainter.cs  paginates the blocks and paints them to any SKCanvas (PDF page or bitmap)
  PdfReportWriter.cs    SKDocument.CreatePdf + painter, one page at a time
```

Why a display list: the painter needs font metrics for pagination (Skia), but the *content* (which
rooms, which numbers, which captions, which colours per legend band) is decided in `ReportBuilder`.
The harness compiles `ReportBuilder` and `RoomStatistics` and can check them like it checks jobs.
The painter also renders pages to `SKBitmap`, which gives a free in-window **preview** later.

### 2.3 Areas live in a per-project JSON file, not in Revit transactions

`%AppData%\RK Tools\SoundCalcs\projects\<hash-of-document-path>.json`:

```
SoundCalcsProject
  ProjectKey, ProjectName                 (document path / title, as LayoutProjectKey today)
  ProjectInfo: Project, Client, Author, Company, Description, Logo path   (report cover)
  Areas[]:
    Id (guid), Name, LevelName, Order, Notes
    Boundary (BoundaryRooms, BoundaryLineIds), WallGroups, WallsDetected, LinkSelection
    SpeakerGroups, PickedSpeakerIds, LineFilter
    RoomSettings (the Room-page values: grid, receiver height, offset, RT60/noise per band,
                  humidity, occupants, floor/ceiling, auto RT60) — per area, with "copy from area…"
    LastJobId, LastRunUtc, LastRunQuality, InputFingerprint (hash of the job input)
  ActiveAreaId
```

- The current autosave (0.6 s after each change, no Save button) stays exactly as it is; it just
  writes the project file instead of `settings.json`. Writing to the Revit document on every
  keystroke would need a transaction each time, which pollutes Revit's undo list and fails on
  read-only or borrowed workshared files. So the document is not the live store.
- `settings.json` keeps only user-wide things (speaker mapping library `SpeakerMappings`,
  speaker category, A/B parameter name). On first start with an existing `settings.json`, its
  layout becomes **Area 1** of the project it was recorded for (`LayoutProjectKey`), so nothing is lost.
- Optional later step (phase 4): **"Store in model"** / **"Load from model"** buttons that put the same
  JSON into a `DataStorage` element via ExtensibleStorage, so a colleague opening the central model
  gets the areas. Explicit, one transaction, user-triggered.

### 2.4 Results stay as job files; the area points at them

- Each run records `LastJobId` and a fingerprint of the job input (sources, walls, rooms, settings,
  quality). The report dialog marks an area **outdated** when the fingerprint of the current inputs
  differs from the one that produced the results, and **not calculated** when there is no job. Same
  idea as DIALux's "calculation out of date" marker.
- Job files get the project key and area id written into `AcousticJobInput` so a jobs folder can
  be tidied per project later (and "Import latest" can be limited to the active area).

## 3. What the report contains

Page size A4 portrait by default (A3 and landscape as options), margins 15 mm, header with project
name and area, footer with page x / y, date, plugin version.

1. **Cover**: project, client, author, company, date, logo (optional), list of included areas.
2. **Summary table** (one row per room across all selected areas): area, room, floor area, volume,
   speakers, SPL mean / min / max (dB and dBA), spread (max − min), STI mean / min, STI class of
   the mean, C80 mean, D50 mean, RT60 at 500 Hz, pass/fail against the criteria.
3. **Per area**:
   - Plan figure for each selected mode (SPL, dBA, STI, C80, any octave band): heatmap, walls,
     speakers with aim, room outlines and names, scale bar, legend with exact band ranges — the same
     colour range the viewer used (`HeatmapMath.ComputeViewerRange`).
   - Inputs: grid spacing, receiver height, boundary offset, quality, temperature, humidity,
     occupants, floor and ceiling finish, background noise per band, RT60 per band (and whether it
     was estimated per room).
   - Walls: line style, wall type, STC, surface, height, segments, total length.
   - Speakers: family / type, count, profile (omni / cone / wall-mounted / datasheet / polar table),
     level, response preset, coverage per band; per instance: id, position, level, height, aim.
   - **Per room**: area, volume, ceiling height, enclosure ratio, RT60 per band used, receiver
     count; statistics table per mode (mean, min, max, spread, percentiles P10/P90, share of
     points ≥ target); STI class distribution (Bad … Excellent, % of points); pass/fail lines.
4. **Criteria** page: the thresholds used (defaults: STI ≥ 0.50 at every point per EN 50849 /
   IEC 60268-16 for voice alarm, SPL spread ≤ 6 dB over the listening area, SPL ≥ background + 10 dB
   and ≥ 65 dBA for voice alarm), editable in the dialog and stored with the project.
5. **Method** page: one page of fixed text describing the model (image sources, Barron / shoebox
   lattice, IEC 60268-16 STI, wall TL contour, diffraction), with the README's wording.

Optional, off by default: value tables (the receiver grid printed as numbers per room, like
DIALux's isolux value charts), per-band plan figures, isolines on the plan.

## 4. Phases

Each phase is shippable on its own and keeps `scripts/selftest.sh` green.

### Phase 0 — Groundwork (small, no UI)

- `ReceiverResult.RoomIndex` (copied from the receiver in `SPLCalculator`/`JobRunner.Compute`);
  `AcousticJobOutput` keeps `Rooms` already.
- `AcousticJobInput`: `ProjectKey`, `AreaId`, `AreaName`, `Settings` snapshot (grid, heights,
  quality, environment is there; add occupants, auto-RT60 flag, speaker line filter).
- `Report/RoomStatistics.cs`: per room and per mode → mean (energy mean for dB, arithmetic for
  STI), min, max, spread, P10/P90, share above a threshold, STI class histogram. Tests in
  `SoundCalcs.Tests/RoomStatisticsTests.cs` with hand-computed values.
- Harness: `report.md` gets the per-room statistics table for every scenario (free coverage).

### Phase 1 — PDF export of the current results (single area)

- `Report/` as in 2.2. `ReportBuilder` takes `(input, output)` pairs + options → `ReportModel`.
- `SkiaReportPainter`: A4/A3, header/footer, headings, paragraphs, tables with column widths from
  measured text, figure with the heatmap bitmap + vector walls/speakers/legend, page breaks.
- `PdfReportWriter.Write(model, path)`.
- UI: **Export report…** secondary button on the Results page next to *Draw in Revit view*,
  enabled when `HasResults`. Opens a small options dialog (Sentinel style, `DynamicResource` only):
  sections (checkboxes), modes (checkboxes, default SPL + STI), page size, project info fields
  (remembered in the project file), criteria values, output path (`SaveFileDialog`, default
  `<model name> – <area> – Acoustic report.pdf` next to the model). Writes on a background thread
  with the existing progress bar; status line offers **Open** when done.
- Headless verification: the harness gets `--report <dir>`; because the painter needs Skia, the
  harness project adds `SkiaSharp` + `SkiaSharp.NativeAssets.Linux` (NuGet restore needed once in
  the sandbox) and writes `report.pdf` plus `report_page_01.png` … per scenario so the pages can
  be looked at with the Read tool. If the Linux native package cannot be restored, the fallback is
  a `RasterReportPainter` in the harness for the PNGs and a structural check of the PDF
  (page count, text extraction with `pdftotext` if present).
- Compile check: new `x:Name`s go into `SoundCalcs.CompileCheck/XamlStubs.cs`; new palette keys
  (if any) into both themes and `BuildHighContrast`.

### Phase 2 — Areas per project

- `Domain/SoundCalcsProject.cs`, `Domain/AnalysisArea.cs`, `IO/ProjectStore.cs` (same atomic
  write as `SettingsStore`), migration from `settings.json` (unit tested with a copy of a real file).
- `MainViewModel` becomes a thin shell over an **active area**: `AreaViewModel` holds what the
  Model / Speakers / Room pages edit today (boundary, walls, speakers, room settings, last
  output). Pages bind to `ActiveArea.*`; the viewer shows the active area. `RunPreflight` runs per
  area (also a project-level item: "Area 2 has no speakers").
- UI: an **Areas** strip at the top of the page column (above the sidebar's *Set up* group):
  dropdown or list of areas with *Add*, *Duplicate*, *Rename*, *Delete* (Undo via the status
  line like other destructive actions), and the area's level. Sidebar counts refer to the active
  area. Run page gets **Run all areas** (sequential jobs, one progress bar, cancel stops the queue).
- Opening a different Revit document switches the project file; the Run page already warns when
  layouts and speakers come from another project, which becomes unnecessary within a project but
  stays for linked models.
- Job files carry area ids; "Import latest" imports the latest job *of the active area*.

### Phase 3 — Report dialog like DIALux

- The dialog from phase 1 grows a left-hand tree: **project → areas → rooms**, each with a
  checkbox, plus a status glyph per area: *calculated (date, quality)*, *outdated*, *not
  calculated*. Outdated or missing areas can be run from the dialog (*Calculate selected*) before
  export.
- Right-hand side: sections, modes, page setup, criteria, project info; a live **page count**.
- **Preview** pane: the painter renders the current page to an `SKBitmap` shown in a
  `SKElement`; previous/next page buttons. Same painter as the PDF, so no drift.
- Export writes one PDF for the selection; option to also write `report.csv` (the summary table)
  and the per-room value tables as CSV.

### Phase 4 — Nice to have, in order of value

1. Store the project in the model (ExtensibleStorage `DataStorage`, explicit buttons).
2. Isolines on the plan figures (marching squares over the receiver grid in `HeatmapMath`, which
   also benefits the viewer and the Revit renderer).
3. Value tables per room (numbers on the grid).
4. Report language switch (English / Estonian strings in a resource dictionary).
5. Per-speaker contribution table (loudest speaker per room, levels at the room centre).

## 5. Verification per phase

- Unit tests: `RoomStatisticsTests`, `ReportBuilderTests` (which rooms/sections appear for a given
  option set; outdated detection from fingerprints), `ProjectStoreTests` (round trip, migration).
- `python3 scripts/xamlcheck.py` after every XAML change, then the compile check.
- Harness `--report`: look at the page PNGs for clipped tables, legend/range mismatch against the
  viewer PNG of the same scenario, wrong orientation of the plan (north up = +Y up, as the viewer).
- Inside Revit (manual, Windows): export from a real project, open in Acrobat and Edge, check text
  is selectable and the file size is sane (a 2 000-point heatmap bitmap is tiny; fonts subset).

## 6. Open questions

1. **Page defaults**: A4 portrait with the plan figure on its own landscape page, or everything
   landscape? Proposed: A4 portrait, plan figures rotated to landscape pages automatically when
   the boundary is wider than tall.
2. **Criteria defaults**: STI ≥ 0.50 everywhere and SPL spread ≤ 6 dB as proposed, or the
   project-specific values you use (e.g. STI ≥ 0.45 min / 0.50 mean for VA, dBA targets per
   room type)?
3. **Cover fields**: project, client, author, company, logo — anything else your deliverables need
   (project number, revision, checked by)?
4. **Area storage**: per-project JSON in `%AppData%` first (as proposed), with "store in model"
   later — fine, or is sharing between colleagues needed from day one?
5. **Language**: English only to start, Estonian as a later switch?
6. **Value tables** (numbers on the grid, DIALux-style): include in phase 3 or leave for later?

## 7. Rough size

| Phase | New / touched code | Estimate |
|---|---|---|
| 0 Groundwork | ~300 lines + tests | small |
| 1 PDF export | ~1 500 lines (painter, builder, dialog) + harness option | medium |
| 2 Areas | ~1 200 lines (project model, store, migration, VM split, strip UI, run-all) | medium–large, most risk (VM refactor) |
| 3 Report dialog | ~800 lines (tree, status, preview) | medium |
| 4 Extras | per item | small each |

Suggested order: 0 → 1 → 2 → 3. Phase 1 gives you a usable PDF after the first step; phase 2 is
the structural change and is easier to do once the report already consumes `(input, output)`
pairs rather than view-model state.
