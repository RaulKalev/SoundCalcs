using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using Newtonsoft.Json;

namespace SoundCalcs.UI
{
    /// <summary>Per-user UI preferences (theme, window placement, last page). Project data is never stored here.</summary>
    public class UiPreferences
    {
        public bool IsDarkMode { get; set; } = true;
        public double WindowWidth { get; set; } = 1380;
        public double WindowHeight { get; set; } = 820;
        public double WindowLeft { get; set; } = 80;
        public double WindowTop { get; set; } = 60;
        public string LastPage { get; set; } = "Model";
        /// <summary>Width of the page column; the plan viewer takes the rest.</summary>
        public double PanelWidth { get; set; } = 520;
    }

    /// <summary>
    /// Applies the appearance to a window: Dark/Light palette, plus the Windows accessibility preferences
    ///  - High contrast       → palette built from the system high-contrast colours,
    ///  - Transparency off    → solid materials (no gradients/shadows),
    ///  - Animations off      → <see cref="ReducedMotion"/> (Motion helper uses opacity only / no motion).
    /// Only the palette dictionary is swapped; shared styles stay merged. Same behaviour as Sentinel's ThemeManager. Preferences persist to
    /// %LocalAppData%\RK Tools\SoundCalcs\ui.json.
    /// </summary>
    public class ThemeManager
    {
        /// <summary>Preferences file. Overridable so test harnesses never touch the user's real preferences.</summary>
        public static string PrefsPath { get; set; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RK Tools", "SoundCalcs", "ui.json");

        /// <summary>Overrides for tests (null = follow Windows).</summary>
        public static bool? ForceHighContrast { get; set; }
        public static bool? ForceSolidMaterials { get; set; }
        public static bool? ForceReducedMotion { get; set; }

        private readonly Window _window;
        private readonly List<KeyValuePair<ResourceDictionary, ResourceDictionary[]>> _applied =
            new List<KeyValuePair<ResourceDictionary, ResourceDictionary[]>>();

        public UiPreferences Preferences { get; private set; } = new UiPreferences();
        public bool IsDarkMode => Preferences.IsDarkMode;

        public event EventHandler ThemeChanged;

        public ThemeManager(Window window)
        {
            _window = window;
            Load();
            SystemParameters.StaticPropertyChanged += OnSystemParametersChanged;
            window.Closed += (s, e) => SystemParameters.StaticPropertyChanged -= OnSystemParametersChanged;
        }

        // ------------------------------------------------------------------ accessibility preferences

        public static bool HighContrast => ForceHighContrast ?? SystemParameters.HighContrast;

        /// <summary>Windows "Transparency effects" (Settings → Personalization → Colors) switched off.</summary>
        public static bool SolidMaterials
        {
            get
            {
                if (ForceSolidMaterials.HasValue) return ForceSolidMaterials.Value;
                if (SystemParameters.HighContrast) return true;
                try
                {
                    using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                    {
                        var v = key?.GetValue("EnableTransparency");
                        return v is int && (int)v == 0;
                    }
                }
                catch
                {
                    return false;
                }
            }
        }

        /// <summary>Windows "Animation effects" switched off.</summary>
        public static bool ReducedMotion => ForceReducedMotion ?? !SystemParameters.ClientAreaAnimation;

        private void OnSystemParametersChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(SystemParameters.HighContrast) || e.PropertyName == nameof(SystemParameters.ClientAreaAnimation))
            {
                _window.Dispatcher.BeginInvoke(new Action(() =>
                {
                    ApplyTheme();
                    ThemeChanged?.Invoke(this, EventArgs.Empty);
                }));
            }
        }

        // ------------------------------------------------------------------ apply

        public void ToggleTheme()
        {
            Preferences.IsDarkMode = !Preferences.IsDarkMode;
            ApplyTheme();
            Save();
            ThemeChanged?.Invoke(this, EventArgs.Empty);
        }

        public void ApplyTheme() => ApplyTheme(_window.Resources);

        /// <summary>Applies the current appearance to any resource dictionary (window or sheet).</summary>
        public void ApplyTheme(ResourceDictionary target)
        {
            try
            {
                var dicts = BuildDictionaries();

                // Remove what we applied before, plus palette dictionaries merged from XAML at parse time.
                var entry = _applied.FirstOrDefault(kv => kv.Key == target);
                if (entry.Key != null)
                {
                    foreach (var d in entry.Value) target.MergedDictionaries.Remove(d);
                    _applied.Remove(entry);
                }
                foreach (var d in target.MergedDictionaries.Where(IsPaletteSource).ToList()) target.MergedDictionaries.Remove(d);

                for (int i = 0; i < dicts.Length; i++) target.MergedDictionaries.Insert(i, dicts[i]);
                _applied.Add(new KeyValuePair<ResourceDictionary, ResourceDictionary[]>(target, dicts));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("SoundCalcs: applying theme failed: " + ex);
            }
        }

        private static bool IsPaletteSource(ResourceDictionary d)
        {
            var s = d.Source?.OriginalString;
            return s != null && s.Contains("/UI/Themes/") && (s.EndsWith("DarkTheme.xaml") || s.EndsWith("LightTheme.xaml"));
        }

        private ResourceDictionary[] BuildDictionaries()
        {
            if (HighContrast) return new[] { BuildHighContrast() };

            var palette = new ResourceDictionary
            {
                Source = new Uri(Preferences.IsDarkMode
                    ? "pack://application:,,,/SoundCalcs;component/UI/Themes/DarkTheme.xaml"
                    : "pack://application:,,,/SoundCalcs;component/UI/Themes/LightTheme.xaml", UriKind.Absolute)
            };
            var materials = SolidMaterials ? BuildSolidMaterials(palette) : new ResourceDictionary { ["Shadow.Opacity"] = Preferences.IsDarkMode ? 0.45 : 0.16 };
            return new[] { palette, materials };
        }

        /// <summary>Reduced transparency: materials become solid surfaces with a defined edge, no shadows.</summary>
        private static ResourceDictionary BuildSolidMaterials(ResourceDictionary palette)
        {
            var raised = palette["Surface.Raised"] as Brush;
            var stroke = palette["Surface.Stroke"] as Brush;
            var content = palette["Surface.Content"] as Brush;
            return new ResourceDictionary
            {
                ["Sidebar.Material"] = palette["Surface.Inset"],
                ["Toolbar.Material"] = palette["Window.Background"],
                ["Floating.Material"] = raised ?? content,
                ["Floating.Edge"] = stroke,
                ["Sidebar.Edge"] = stroke,
                ["Shadow.Opacity"] = 0.0
            };
        }

        /// <summary>High contrast: every brush maps to a Windows system colour so the user's HC scheme is honoured.</summary>
        private static ResourceDictionary BuildHighContrast()
        {
            Func<Color, SolidColorBrush> b = c => { var br = new SolidColorBrush(c); br.Freeze(); return br; };
            var window = b(SystemColors.WindowColor);
            var text = b(SystemColors.WindowTextColor);
            var gray = b(SystemColors.GrayTextColor);
            var highlight = b(SystemColors.HighlightColor);
            var highlightText = b(SystemColors.HighlightTextColor);
            var hot = b(SystemColors.HotTrackColor);
            var btn = b(SystemColors.ControlColor);
            var d = new ResourceDictionary();
            foreach (var k in new[] { "Window.Background", "Sidebar.Material", "Toolbar.Material", "Floating.Material", "Surface.Content",
                                      "Surface.Raised", "Surface.Inset", "Input.Fill", "Control.Fill",
                                      "Row.Alternate", "Status.Ok.Subtle", "Status.Info.Subtle", "Status.Warning.Subtle",
                                      "Status.Error.Subtle", "Status.Neutral.Subtle", "Accent.Subtle" })
                d[k] = window;
            foreach (var k in new[] { "Window.Stroke", "Sidebar.Edge", "Floating.Edge", "Surface.Stroke", "Separator", "Control.Stroke",
                                      "Input.Stroke", "Input.StrokeHover", "Text.Primary", "Text.Secondary",
                                      "Status.Ok", "Status.Info", "Status.Warning", "Status.Error", "Status.Neutral" })
                d[k] = text;
            d["Text.Tertiary"] = text;
            d["Text.Disabled"] = gray;
            foreach (var k in new[] { "Accent", "Accent.Hover", "Accent.Pressed", "Row.Selected", "Row.SelectedInactive", "Control.FillPressed" })
                d[k] = highlight;
            d["Text.OnAccent"] = highlightText;
            d["Accent.Text"] = hot;
            d["Focus.Ring"] = hot;
            d["Control.FillHover"] = btn;
            d["Control.Plain.Hover"] = btn;
            d["Control.Plain.Pressed"] = highlight;
            d["Row.Hover"] = btn;
            d["Viewer.CanvasBrush"] = window;
            d["Viewer.Canvas"] = SystemColors.WindowColor;
            d["Viewer.Panel"] = SystemColors.WindowColor;
            d["Viewer.Wall"] = SystemColors.WindowTextColor;
            d["Viewer.Text"] = SystemColors.WindowTextColor;
            d["Viewer.TextSecondary"] = SystemColors.WindowTextColor;
            d["Viewer.TextTertiary"] = SystemColors.GrayTextColor;
            d["Scrim"] = b(Color.FromArgb(0x80, 0, 0, 0));
            d["Shadow.Color"] = Colors.Black;
            d["Shadow.Opacity"] = 0.0;
            return d;
        }

        // ------------------------------------------------------------------ preferences

        public void CaptureWindowPlacement()
        {
            if (_window.WindowState != WindowState.Normal) return;
            Preferences.WindowWidth = _window.Width;
            Preferences.WindowHeight = _window.Height;
            Preferences.WindowLeft = _window.Left;
            Preferences.WindowTop = _window.Top;
        }

        public void Load()
        {
            try
            {
                if (!File.Exists(PrefsPath)) return;
                var prefs = JsonConvert.DeserializeObject<UiPreferences>(File.ReadAllText(PrefsPath));
                if (prefs != null) Preferences = prefs;
            }
            catch
            {
                Preferences = new UiPreferences();
            }
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(PrefsPath));
                File.WriteAllText(PrefsPath, JsonConvert.SerializeObject(Preferences, Formatting.Indented));
            }
            catch
            {
                // preferences are best effort
            }
        }
    }
}
