using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Autodesk.Revit.UI;
using SoundCalcs.UI.ViewModels;

namespace SoundCalcs.UI
{
    /// <summary>The window's pages, in sidebar order.</summary>
    public enum SoundCalcsPage { Model, Speakers, Room, Run, Results }

    /// <summary>
    /// Modeless SoundCalcs window. Code-behind handles chrome only: theme, pages, compact sidebar, caption buttons
    /// and keyboard shortcuts. Behaviour lives in <see cref="MainViewModel"/>.
    /// </summary>
    public partial class MainWindow : Window
    {
        /// <summary>Below this width the sidebar shows icons only (labels stay available as tooltips / names).</summary>
        public const double CompactWidth = 1240;

        public static readonly DependencyProperty IsCompactProperty = DependencyProperty.Register(
            nameof(IsCompact), typeof(bool), typeof(MainWindow), new PropertyMetadata(false));

        private readonly MainViewModel _vm;
        private readonly ThemeManager _theme;
        private SoundCalcsPage _page = SoundCalcsPage.Model;

        public MainWindow(UIApplication uiApp)
        {
            InitializeComponent();
            _theme = new ThemeManager(this);
            _theme.ApplyTheme();
            _theme.ThemeChanged += (s, e) => OnAppearanceChanged();
            UpdateThemeButton();

            var prefs = _theme.Preferences;
            Width = Math.Max(MinWidth, prefs.WindowWidth);
            Height = Math.Max(MinHeight, prefs.WindowHeight);
            Left = Math.Max(SystemParameters.VirtualScreenLeft, Math.Min(SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - Width, prefs.WindowLeft));
            Top = Math.Max(SystemParameters.VirtualScreenTop, Math.Min(SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - Height, prefs.WindowTop));
            PageColumn.Width = new GridLength(Math.Max(PageColumn.MinWidth, prefs.PanelWidth));

            _vm = new MainViewModel(uiApp);
            DataContext = _vm;

            string docTitle = uiApp?.ActiveUIDocument?.Document?.Title;
            if (!string.IsNullOrEmpty(docTitle))
            {
                DocumentTitleText.Text = docTitle;
                DocumentTitleText.ToolTip = docTitle;
            }

            // Wire speaker rotation: viewer drag → ViewModel → Revit ExtensibleStorage
            AcousticViewer.OnSpeakerRotated = (elementId, angleDeg) =>
                _vm.SetSpeakerAimAngle(elementId, angleDeg);

            SoundCalcsPage page;
            if (Enum.TryParse(prefs.LastPage, out page)) ShowPage(page);

            SizeChanged += (s, e) => UpdateLayoutMode();
            StateChanged += (s, e) => UpdateMaximizedState();
            PreviewKeyDown += OnPreviewKeyDown;
            Closing += OnClosing;
        }

        public bool IsCompact
        {
            get => (bool)GetValue(IsCompactProperty);
            private set => SetValue(IsCompactProperty, value);
        }

        // ------------------------------------------------------------------ pages

        private void ShowPage(SoundCalcsPage page)
        {
            _page = page;
            ModelPage.Visibility = page == SoundCalcsPage.Model ? Visibility.Visible : Visibility.Collapsed;
            SpeakersPage.Visibility = page == SoundCalcsPage.Speakers ? Visibility.Visible : Visibility.Collapsed;
            RoomPage.Visibility = page == SoundCalcsPage.Room ? Visibility.Visible : Visibility.Collapsed;
            RunPage.Visibility = page == SoundCalcsPage.Run ? Visibility.Visible : Visibility.Collapsed;
            ResultsPage.Visibility = page == SoundCalcsPage.Results ? Visibility.Visible : Visibility.Collapsed;

            NavButton(page).IsChecked = true;
            PageTitleText.Text = page.ToString();
            PageSubtitleText.Text = PageSubtitle(page);
        }

        private static string PageSubtitle(SoundCalcsPage page)
        {
            switch (page)
            {
                case SoundCalcsPage.Model: return "Linked model, room boundary and walls";
                case SoundCalcsPage.Speakers: return "Speakers and their acoustic data";
                case SoundCalcsPage.Room: return "Receiver grid, room acoustics and surfaces";
                case SoundCalcsPage.Run: return "Calculate the sound field";
                default: return "Heatmaps, figures and the Revit view";
            }
        }

        private RadioButton NavButton(SoundCalcsPage page)
        {
            switch (page)
            {
                case SoundCalcsPage.Model: return NavModel;
                case SoundCalcsPage.Speakers: return NavSpeakers;
                case SoundCalcsPage.Room: return NavRoom;
                case SoundCalcsPage.Run: return NavRun;
                default: return NavResults;
            }
        }

        private void Nav_Checked(object sender, RoutedEventArgs e)
        {
            // Checked fires while InitializeComponent is still creating the pages (NavModel starts checked).
            if (ResultsPage == null) return;
            if (sender == NavModel) ShowPage(SoundCalcsPage.Model);
            else if (sender == NavSpeakers) ShowPage(SoundCalcsPage.Speakers);
            else if (sender == NavRoom) ShowPage(SoundCalcsPage.Room);
            else if (sender == NavRun) ShowPage(SoundCalcsPage.Run);
            else if (sender == NavResults) ShowPage(SoundCalcsPage.Results);
        }

        private void GoToRun_Click(object sender, RoutedEventArgs e) => ShowPage(SoundCalcsPage.Run);

        // ------------------------------------------------------------------ layout

        private void UpdateLayoutMode()
        {
            IsCompact = ActualWidth < CompactWidth;
            SidebarColumn.Width = new GridLength(IsCompact ? 60 : 212);
            PageSubtitleText.Visibility = ActualWidth < 1100 ? Visibility.Collapsed : Visibility.Visible;
        }

        private void UpdateMaximizedState()
        {
            // A maximized WindowChrome window extends past the work area by the resize frame; pad the content back in.
            bool maximized = WindowState == WindowState.Maximized;
            Thickness frame = SystemParameters.WindowResizeBorderThickness;
            RootGrid.Margin = maximized ? new Thickness(frame.Left + 4, frame.Top + 4, frame.Right + 4, frame.Bottom + 4) : new Thickness(0);
            MaximizeIcon.Kind = maximized ? MaterialDesignThemes.Wpf.PackIconKind.WindowRestore : MaterialDesignThemes.Wpf.PackIconKind.WindowMaximize;
            MaximizeButton.ToolTip = maximized ? "Restore down" : "Maximize";
            System.Windows.Automation.AutomationProperties.SetName(MaximizeButton, maximized ? "Restore down" : "Maximize");
        }

        private void Splitter_DragCompleted(object sender, DragCompletedEventArgs e)
            => _theme.Preferences.PanelWidth = PageColumn.ActualWidth;

        // ------------------------------------------------------------------ appearance

        private void OnAppearanceChanged()
        {
            UpdateThemeButton();
            AcousticViewer.ApplyAppearance();
        }

        private void UpdateThemeButton()
        {
            ThemeIcon.Kind = _theme.IsDarkMode ? MaterialDesignThemes.Wpf.PackIconKind.WeatherNight : MaterialDesignThemes.Wpf.PackIconKind.WhiteBalanceSunny;
            ThemeText.Text = _theme.IsDarkMode ? "Dark appearance" : "Light appearance";
        }

        private void ToggleTheme_Click(object sender, RoutedEventArgs e) => _theme.ToggleTheme();

        // ------------------------------------------------------------------ keyboard

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            Key key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (Keyboard.Modifiers != ModifierKeys.Control) return;
            switch (key)
            {
                case Key.D1: case Key.NumPad1: ShowPage(SoundCalcsPage.Model); e.Handled = true; break;
                case Key.D2: case Key.NumPad2: ShowPage(SoundCalcsPage.Speakers); e.Handled = true; break;
                case Key.D3: case Key.NumPad3: ShowPage(SoundCalcsPage.Room); e.Handled = true; break;
                case Key.D4: case Key.NumPad4: ShowPage(SoundCalcsPage.Run); e.Handled = true; break;
                case Key.D5: case Key.NumPad5: ShowPage(SoundCalcsPage.Results); e.Handled = true; break;
                case Key.S: _vm.SaveSettings(); e.Handled = true; break;
            }
        }

        // ------------------------------------------------------------------ chrome

        private void Minimize_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

        private void Maximize_Click(object sender, RoutedEventArgs e)
        {
            if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
            else SystemCommands.MaximizeWindow(this);
        }

        private void Close_Click(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);

        private void OnClosing(object sender, CancelEventArgs e)
        {
            try
            {
                _theme.CaptureWindowPlacement();
                _theme.Preferences.LastPage = _page.ToString();
                _theme.Preferences.PanelWidth = PageColumn.ActualWidth;
                _theme.Save();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("SoundCalcs: saving window preferences failed: " + ex);
            }
        }

        // ============ Model page ============

        private void RefreshLinks_Click(object sender, RoutedEventArgs e)
        {
            _vm.RefreshLinks();
        }

        private void SelectBoundary_Click(object sender, RoutedEventArgs e)
        {
            _vm.SelectBoundary(
                hideWindow: () => this.Hide(),
                showWindow: () => { this.Show(); this.Activate(); });
        }

        private void AutoDetectWalls_Click(object sender, RoutedEventArgs e)
        {
            _vm.AutoDetectWalls();
        }

        private void ClearWalls_Click(object sender, RoutedEventArgs e)
        {
            _vm.ClearWalls();
        }

        // ============ Speakers page ============

        private void PickSpeaker_Click(object sender, RoutedEventArgs e)
        {
            _vm.PickSpeaker(
                hideWindow: () => this.Hide(),
                showWindow: () => { this.Show(); this.Activate(); });
        }

        private void ClearPickedSpeakers_Click(object sender, RoutedEventArgs e)
        {
            _vm.ClearPickedSpeakers();
        }

        private void SaveSettings_Click(object sender, RoutedEventArgs e)
        {
            _vm.SaveSettings();
        }

        // ============ Room page ============

        private void EstimateRT60_Click(object sender, RoutedEventArgs e)
        {
            _vm.EstimateRT60();
        }

        // ============ Run page ============

        private void StartDraft_Click(object sender, RoutedEventArgs e)
        {
            _vm.StartJob(SoundCalcs.Domain.CalculationQuality.Draft);
        }

        private void StartFull_Click(object sender, RoutedEventArgs e)
        {
            _vm.StartJob(SoundCalcs.Domain.CalculationQuality.Full);
        }

        private void CancelJob_Click(object sender, RoutedEventArgs e)
        {
            _vm.CancelJob();
        }

        // ============ Results page ============

        private void Visualize_Click(object sender, RoutedEventArgs e)
        {
            _vm.VisualizeResults();
        }

        private void ClearVisualization_Click(object sender, RoutedEventArgs e)
        {
            _vm.ClearVisualization();
        }

        private void ImportResults_Click(object sender, RoutedEventArgs e)
        {
            _vm.ImportLatestResults();
        }
    }
}
