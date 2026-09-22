# SoundCalcs UI design

SoundCalcs uses the same design language as the Sentinel plugin (`RaulKalev/Revit.Sentinel`, `docs/design/README.md`):
Apple-style clarity and hierarchy, built from native WPF so it behaves like a Windows window.

## Design system

- **Tokens** (`UI/Themes/Tokens.xaml`, identical to Sentinel): Segoe UI Variable. Type sizes: caption 11, secondary 12,
  body 13, emphasis 14, section 15, title 20. Controls are 32 DIP (28 compact), grid rows 36. Spacing 4/8/12/16/24.
  Radii: 4 small, 6 control, 10 card, 12 floating.
- **Palettes** (`DarkTheme.xaml`, `LightTheme.xaml`) hold semantic keys only (`Surface.*`, `Control.*`, `Text.*`,
  `Accent.*`, `Row.*`, `Status.*`, `Viewer.*`). Both define every key; the window and pages contain no colours.
- **ThemeManager** swaps the palette at runtime and follows Windows: high contrast (every key mapped to `SystemColors`),
  "Transparency effects" off (solid materials, no shadows), "Animation effects" off (fade only). The dark/light
  choice, window placement, last page and page width are remembered in `%LocalAppData%\RK Tools\SoundCalcs\ui.json`.
- **Styles** (`SoundCalcsStyles.xaml`): Sentinel's buttons (primary / secondary / plain / destructive / icon),
  inputs, segmented control, sidebar items, lists, grid, menus, tooltips and scrollbars, plus SoundCalcs additions:
  numeric fields with units, the octave-band table, editable grid cells, a thin progress bar, settings rows and
  the floating viewer tools.

## Layout

- **Sidebar** in workflow order: *Set up* (Model, Speakers, Room) then *Analysis* (Run, Results). Ctrl+1…5 switch
  pages. The Speakers item counts the speaker types; Run shows the progress while an analysis runs. Below 1240 px
  the sidebar is icon-only. The appearance toggle sits at the bottom.
- **Title area** shows the page name and a one-line description, with the standard Windows caption buttons.
  The window uses WindowChrome, so Snap, Win+arrows, native resize borders and the DWM shadow work.
- **Body**: the page on the left, the live plan on the right in a rounded surface. The splitter between them shows
  an accent line on hover; its position is remembered.
- **Status line** under the content: the last result with a glyph, and the progress while running.

## Pages

- Each page has one primary (filled) button for its next step: *Select boundary*, *Pick speakers*,
  *Save settings*, *Run analysis*, *Draw in Revit view*. Other actions are secondary; destructive ones (*Clear*,
  *Remove from view*) are red plain buttons set apart on the right.
- Settings are grouped cards with the label (and a short hint) on the left and the value on the right. Numbers are
  right aligned with the unit after the field. RT60 and background noise share one octave-band table.
- The speaker line filter (All / A line / B line) is a segmented control.
- Empty states say what to do: *No walls yet*, *No speakers yet*, *No results yet* (with *Go to Run* and
  *Import latest*).
- The results summary shows ranges (min – max) for SPL, dBA and STI.
- The plan viewer follows the appearance (canvas, walls, legend and text colours come from the `Viewer.*` keys).
  Its tools (Fit, Probe, Clear pins) are icon buttons on one floating bar, top left, clear of the legend; Probe
  shows the accent fill while it is on.

## Verification

`scripts/selftest.sh` runs `scripts/xamlcheck.py` (XAML keys, styles, handlers, bindings, icon names) and the compile
check for net48 and net8.0-windows. The window has not been rendered or run inside Revit from this environment.
