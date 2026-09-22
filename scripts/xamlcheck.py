#!/usr/bin/env python3
"""Static checks for the plugin XAML, which the Linux compile check cannot compile.

Catches the mistakes that would otherwise only show up as a XamlParseException inside Revit:
  - malformed XML
  - StaticResource / DynamicResource keys that are not defined (or, for StaticResource, defined too late)
  - StaticResource references to palette colours (they would not follow the theme)
  - Style keys applied to the wrong element type
  - event handlers and x:Name fields missing from code-behind / XamlStubs.cs
  - binding paths that are not public properties of the view models / domain types
  - palette keys missing from the light theme or from ThemeManager's high-contrast palette

Icon names (PackIcon Kind="...") are written to SoundCalcs.CompileCheck/XamlIcons.g.cs as PackIconKind
references, so the compile check fails on a name that MaterialDesignThemes does not have.

Usage: scripts/xamlcheck.py      Exit code 1 when a problem is found.
"""
import glob
import os
import re
import sys
import xml.etree.ElementTree as ET

ROOT = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..'))
UI = os.path.join(ROOT, 'UI')
THEMES = os.path.join(UI, 'Themes')
STYLES = os.path.join(THEMES, 'SoundCalcsStyles.xaml')
VIEWS = [(os.path.join(UI, 'MainWindow.xaml'), os.path.join(UI, 'MainWindow.xaml.cs')),
         (os.path.join(UI, 'AcousticViewerControl.xaml'), os.path.join(UI, 'AcousticViewerControl.xaml.cs'))]
STUBS = os.path.join(ROOT, 'SoundCalcs.CompileCheck', 'XamlStubs.cs')
ICONS_OUT = os.path.join(ROOT, 'SoundCalcs.CompileCheck', 'XamlIcons.g.cs')

# Set at runtime by ThemeManager (materials layer), not in the palette files.
RUNTIME_KEYS = {'Shadow.Opacity'}
# Binding path segments that belong to WPF types rather than our view models.
WPF_MEMBERS = {'Count', 'IsCompact', 'ActualWidth', 'ActualHeight', 'IsOpen', 'IsChecked'}
# Elements a style with this TargetType may be applied to.
DERIVED = {'ButtonBase': {'Button', 'ToggleButton', 'RepeatButton', 'RadioButton', 'CheckBox'}}

problems = []


def read(path):
    with open(path, encoding='utf-8') as f:
        return f.read()


def rel(path):
    return os.path.relpath(path, ROOT)


def keys_with_pos(text):
    return [(m.group(1), m.start()) for m in re.finditer(r'x:Key="([^"{]+)"', text)]


def public_members(paths):
    names = set()
    for p in paths:
        names |= set(re.findall(r'public\s+[\w<>\[\],. ?]+\s+(\w+)\s*(?:\{|=>)', read(p)))
    return names


# ---------------------------------------------------------------- well-formed XML
xaml_files = sorted(glob.glob(os.path.join(UI, '**', '*.xaml'), recursive=True))
for f in xaml_files:
    try:
        ET.parse(f)
    except ET.ParseError as e:
        problems.append(f'{rel(f)}: not well-formed XML: {e}')

# ---------------------------------------------------------------- palettes
tokens = {k for k, _ in keys_with_pos(read(os.path.join(THEMES, 'Tokens.xaml')))}
dark = [k for k, _ in keys_with_pos(read(os.path.join(THEMES, 'DarkTheme.xaml')))]
light = [k for k, _ in keys_with_pos(read(os.path.join(THEMES, 'LightTheme.xaml')))]
for k in sorted(set(dark) ^ set(light)):
    problems.append(f'palette key {k} is defined in only one of DarkTheme / LightTheme')
tm = read(os.path.join(THEMES, 'ThemeManager.cs'))
hc_keys = set(re.findall(r'"([A-Za-z][\w.]*)"', tm[tm.index('BuildHighContrast()'):]))
for k in dark:
    if k not in hc_keys:
        problems.append(f'palette key {k} is missing from ThemeManager.BuildHighContrast')
palette = set(dark)

# ---------------------------------------------------------------- styles dictionary
styles_text = read(STYLES)
style_keys = keys_with_pos(styles_text)
for m in re.finditer(r'\{StaticResource ([^}]+)\}', styles_text):
    k = m.group(1).strip()
    if k in palette:
        problems.append(f'{rel(STYLES)}: StaticResource {k} is a palette key (use DynamicResource)')
    elif k not in tokens and not any(kk == k and pos < m.start() for kk, pos in style_keys):
        problems.append(f'{rel(STYLES)}: StaticResource {k} is not defined before it is used')
for m in re.finditer(r'\{DynamicResource ([^}]+)\}', styles_text):
    k = m.group(1).strip()
    if k not in palette | tokens | RUNTIME_KEYS | {kk for kk, _ in style_keys}:
        problems.append(f'{rel(STYLES)}: DynamicResource {k} is not defined')

shared = tokens | palette | RUNTIME_KEYS | {k for k, _ in style_keys}

# Style key -> TargetType (styles dictionary and window resources)
target_types = {}
for path in [STYLES] + [v for v, _ in VIEWS]:
    for m in re.finditer(r'<Style\s([^>]*)>', read(path), re.S):
        attrs = m.group(1)
        k = re.search(r'x:Key="([^"]+)"', attrs)
        t = re.search(r'TargetType="(?:\{x:Type )?([\w:]+)\}?"', attrs)
        if k and t:
            target_types[k.group(1)] = t.group(1).split(':')[-1]

# ---------------------------------------------------------------- views
vm_dir = os.path.join(UI, 'ViewModels')
members = public_members(glob.glob(os.path.join(vm_dir, '*.cs')) + glob.glob(os.path.join(ROOT, 'Domain', '*.cs')))
stubs = read(STUBS)
icons = set()

for xaml, cs in VIEWS:
    text, code = read(xaml), read(cs)
    name = rel(xaml)
    is_user_control = text.lstrip().startswith('<UserControl')
    local = keys_with_pos(text)

    for m in re.finditer(r'\{(StaticResource|DynamicResource) ([^}]+)\}', text):
        kind, k = m.group(1), m.group(2).strip()
        if kind == 'StaticResource' and k in palette:
            problems.append(f'{name}: StaticResource {k} is a palette key (use DynamicResource)')
        if kind == 'StaticResource' and is_user_control:
            problems.append(f'{name}: StaticResource {k} in a UserControl cannot see the window resources')
        if k in shared:
            continue
        if not any(kk == k and (pos < m.start() or kind == 'DynamicResource') for kk, pos in local):
            problems.append(f'{name}: {kind} {k} is not defined')

    for m in re.finditer(r'<([\w:.]+)\s[^<>]*?\bStyle="\{(?:Static|Dynamic)Resource ([^}]+)\}"', text, re.S):
        element, k = m.group(1).split(':')[-1], m.group(2).strip()
        t = target_types.get(k)
        if t is None:
            problems.append(f'{name}: style {k} has no TargetType')
        elif element != t and element not in DERIVED.get(t, set()):
            problems.append(f'{name}: <{element}> uses style {k} whose TargetType is {t}')
    for m in re.finditer(r'(ElementStyle|EditingElementStyle)="\{StaticResource ([^}]+)\}"', text):
        want = 'TextBlock' if m.group(1) == 'ElementStyle' else 'TextBox'
        if target_types.get(m.group(2)) != want:
            problems.append(f'{name}: {m.group(1)} {m.group(2)} must target {want}')

    handler_attrs = r'Click|Checked|Unchecked|MouseDown|MouseMove|MouseUp|MouseWheel|PaintSurface|DragCompleted|MouseLeftButtonDown'
    for m in re.finditer(r'\b(?:' + handler_attrs + r')="(\w+)"', text):
        if not re.search(r'void\s+' + m.group(1) + r'\s*\(', code):
            problems.append(f'{name}: event handler {m.group(1)} is missing from {rel(cs)}')

    for n in set(re.findall(r'x:Name="(\w+)"', text)):
        if re.search(r'\b' + n + r'\b', code) and not re.search(r'\b' + n + r'\b', stubs):
            problems.append(f'{name}: x:Name {n} is used by code-behind but not declared in {rel(STUBS)}')

    for m in re.finditer(r'\{Binding (?:Path=)?([A-Za-z_][\w.]*)(?=[,}\s])', text):
        path = m.group(1)  # "{Binding Converter=...}" does not match: the name must end at , } or space
        for seg in path.split('.'):
            if seg not in WPF_MEMBERS and seg not in members:
                problems.append(f'{name}: binding {path}: {seg} is not a public view-model or domain property')
    for m in re.finditer(r'<Binding (?:Path=)?"?([\w.]+)"', text):
        for seg in m.group(1).split('.'):
            if seg not in WPF_MEMBERS and seg not in members:
                problems.append(f'{name}: binding {m.group(1)}: {seg} is not a public view-model or domain property')

for f in xaml_files:
    text = read(f)
    icons |= set(re.findall(r'<materialDesign:PackIcon\b[^>]*?\bKind="(\w+)"', text, re.S))
    icons |= set(re.findall(r'Property="Kind"\s+Value="(\w+)"', text))

# ---------------------------------------------------------------- icons → compiled by the compile check
lines = ['// <auto-generated> by scripts/xamlcheck.py: every PackIcon Kind used in UI/*.xaml.',
         '// The compile check fails here when MaterialDesignThemes has no icon of that name.',
         'namespace SoundCalcs.CompileCheck',
         '{',
         '    internal static class XamlIcons',
         '    {',
         '        internal static readonly MaterialDesignThemes.Wpf.PackIconKind[] Used =',
         '        {']
lines += [f'            MaterialDesignThemes.Wpf.PackIconKind.{k},' for k in sorted(icons)]
lines += ['        };', '    }', '}', '']
with open(ICONS_OUT, 'w', encoding='utf-8', newline='\n') as f:
    f.write('\n'.join(lines))

if problems:
    print('\n'.join(problems))
    print(f'{len(problems)} problem(s) in the XAML')
    sys.exit(1)
print(f'XAML checks passed ({len(xaml_files)} files, {len(icons)} icon names written to {rel(ICONS_OUT)})')
