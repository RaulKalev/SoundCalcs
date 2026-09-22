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
  "Transparency effects" off (solid materials, no shadows), "Animation effects" off (fade only), and "Text size"
  (Accessibility): the sidebar, title, pages and status line scale with it, layout included. The dark/light
  choice, window placement, last page and page width are remembered in `%LocalAppData%\RK Tools\SoundCalcs\ui.json`.
- **Motion**: press feedback is instant (fill + 0.97 scale on pointer-down); hover fades in over 100 ms and out over
  150 ms on its own layer, so the palette's hover colour is reached exactly. Pages slide 8 DIP in the direction of
  travel in the sidebar (down the list: up from below; up the list: down from above), fading only with reduced motion.
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
- **Status line** under the content: the last result with a glyph, and the progress while running. After a
  destructive action it offers **Undo** (or *Draw again* after *Remove from view*) for 15 s or until the next
  message; Ctrl+Z does the same outside text fields. Destructive actions never ask first.

## Pages

- Each page has one primary (filled) button for its next step: *Select boundary*, *Pick speakers*,
  *Continue to Run*, *Run analysis*, *Draw in Revit view*. Other actions are secondary; destructive ones (*Clear*,
  *Remove from view*) are red plain buttons set apart on the right.
- There is no Save button: every change is written to `settings.json` 0.6 s after it is made (and on close).
- Settings are grouped cards with the label (and a short hint) on the left and the value on the right. Numbers are
  right aligned with the unit after the field. RT60 and background noise share one octave-band table.
- **Validation is inline**: a value out of range or not a number turns the field red and replaces the hint under
  its label with what to enter (`FieldHint`); an octave-band table shows the first bad band on a line under it.
  The typed value stays as typed, and the Run checklist blocks until it is fixed.
- The Speakers table shows each type's response preset and coverage model; the card under it edits the selected
  type's response (dB) and datasheet coverage (°) per band, one field per band. The category list uses Revit's
  names (*Data Devices*, …).
- The Run page opens with **Before you run**: the boundary and speakers the run will use, and warnings when they
  do not belong together (speakers outside the boundary, on another level, or picked in another project; boundary
  from another project) with a *Go to …* button. Blockers (no boundary, no speakers, invalid fields) disable Run,
  and the reason is shown under the buttons. The sidebar's Run item counts the open items. The checks are
  `Domain/RunPreflight.cs` (unit tested).
- The speaker line filter (All / A line / B line) is a segmented control.
- Empty states say what to do: *No walls yet*, *No speakers yet*, *No results yet* (with *Go to Run* and
  *Import latest*).
- The results summary shows ranges (min – max) for SPL, dBA and STI.
- The plan viewer follows the appearance (canvas, walls, legend and text colours come from the `Viewer.*` keys).
  Its tools (Fit, Probe, Clear pins) are icon buttons on one floating bar, top left, clear of the legend; Probe
  shows the accent fill while it is on.
- **Plan viewer motion**: dragging tracks the pointer 1:1 at any display scaling (mouse DIPs are converted to the
  Skia surface's pixels) and a flick glides on and slows down; the wheel zoom settles smoothly around the cursor
  in steps proportional to the wheel delta (precision touchpads zoom finely); *Fit* and new content animate to
  the fitted view (0.32 s ease-out, zoom in log space). Any new input stops a motion where it is. With reduced
  motion the view jumps and does not glide. Hovering an aimable speaker shows a halo and *Drag to aim*; while
  dragging, the live angle.

## Verification

`scripts/selftest.sh` runs `scripts/xamlcheck.py` (XAML keys, styles, handlers, bindings, icon names) and the compile
check for net48 and net8.0-windows. On Windows the window can also be rendered offscreen without Revit (every page,
dark and light, error and undo states) by a small WPF host that loads the Release DLL with stand-in `RevitAPI` /
`RevitAPIUI` assemblies; that was used to check the checklist, inline errors, band editor and undo. The plan viewer's
pointer motion (glide, smooth zoom, animated fit) has not been exercised inside Revit.
