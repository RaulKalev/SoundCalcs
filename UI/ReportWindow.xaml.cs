using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SkiaSharp;
using SkiaSharp.Views.Desktop;
using SoundCalcs.UI.ViewModels;

namespace SoundCalcs.UI
{
    /// <summary>
    /// The report dialog. Code-behind handles the file dialogs, the preview drawing and paging; the content and options
    /// live in <see cref="ReportViewModel"/>.
    /// </summary>
    public partial class ReportWindow : Window
    {
        private readonly ReportViewModel _vm;
        private readonly ThemeManager _theme;
        private string _lastPdf;

        public ReportWindow(MainViewModel main)
        {
            InitializeComponent();
            _theme = new ThemeManager(this);
            _theme.ApplyTheme();

            Cursor = Cursors.Wait;
            try
            {
                _vm = new ReportViewModel(main);
            }
            finally
            {
                Cursor = null;
            }
            DataContext = _vm;
            _vm.PreviewChanged += (s, e) => PreviewCanvas.InvalidateVisual();

            // Project fields, criteria and contents are plain stored objects: any edit rebuilds the preview
            AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler((s, e) => OptionEdited()));
            AddHandler(Keyboard.LostKeyboardFocusEvent, new KeyboardFocusChangedEventHandler((s, e) => { if (e.OriginalSource is TextBox) OptionEdited(); }));
            AddHandler(ToggleButton.CheckedEvent, new RoutedEventHandler((s, e) => OptionEdited()));
            AddHandler(ToggleButton.UncheckedEvent, new RoutedEventHandler((s, e) => OptionEdited()));

            PreviewKeyDown += OnPreviewKeyDown;
            Closed += (s, e) => _vm.Dispose();
        }

        /// <summary>Lets the binding write the value first, then rebuilds assessments and the preview.</summary>
        private void OptionEdited() =>
            Dispatcher.BeginInvoke(new Action(_vm.OnCriteriaChanged), DispatcherPriority.Background);

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.OriginalSource is TextBox) return;
            if (e.Key == Key.PageDown) { _vm.PageIndex++; e.Handled = true; }
            else if (e.Key == Key.PageUp) { _vm.PageIndex--; e.Handled = true; }
            else if (e.Key == Key.Home && Keyboard.Modifiers == ModifierKeys.Control) { _vm.PageIndex = 0; e.Handled = true; }
            else if (e.Key == Key.End && Keyboard.Modifiers == ModifierKeys.Control) { _vm.PageIndex = int.MaxValue; e.Handled = true; }
        }

        // ------------------------------------------------------------------ preview

        private void PreviewCanvas_PaintSurface(object sender, SKPaintSurfaceEventArgs e)
        {
            SKCanvas canvas = e.Surface.Canvas;
            canvas.Clear(Sk(TryFindResource("Viewer.CanvasBrush") as SolidColorBrush, new SKColor(0x2B, 0x2B, 0x2F)));
            var painter = _vm.Painter;
            if (painter == null || painter.PageCount == 0) return;

            float pad = 18f * (float)(e.Info.Width / Math.Max(1.0, PreviewCanvas.ActualWidth));
            float scale = Math.Min((e.Info.Width - 2 * pad) / painter.PageWidth, (e.Info.Height - 2 * pad) / painter.PageHeight);
            if (scale <= 0) return;
            float w = painter.PageWidth * scale, h = painter.PageHeight * scale;
            float x = (e.Info.Width - w) / 2, y = (e.Info.Height - h) / 2;

            using (var shadow = new SKPaint { Color = new SKColor(0, 0, 0, 70), ImageFilter = SKImageFilter.CreateBlur(6, 6), IsAntialias = true })
                canvas.DrawRect(x + 2, y + 3, w, h, shadow);
            canvas.Save();
            canvas.ClipRect(SKRect.Create(x, y, w, h));
            canvas.Translate(x, y);
            canvas.Scale(scale);
            try
            {
                painter.PaintPage(canvas, Math.Min(_vm.PageIndex, painter.PageCount - 1));
            }
            catch (Exception ex)
            {
                IO.FileLogger.Log($"Report preview page failed: {ex}");
            }
            canvas.Restore();
        }

        private static SKColor Sk(SolidColorBrush brush, SKColor fallback) =>
            brush == null ? fallback : new SKColor(brush.Color.R, brush.Color.G, brush.Color.B, brush.Color.A);

        private void PreviewCanvas_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            _vm.PageIndex += e.Delta < 0 ? 1 : -1;
            e.Handled = true;
        }

        private void PrevPage_Click(object sender, RoutedEventArgs e) => _vm.PageIndex--;

        private void NextPage_Click(object sender, RoutedEventArgs e) => _vm.PageIndex++;

        // ------------------------------------------------------------------ areas

        private void Calculate_Click(object sender, RoutedEventArgs e) => _vm.CalculateStale();

        // ------------------------------------------------------------------ project

        private void BrowseLogo_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Logo for the report cover",
                Filter = "Images (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg",
                CheckFileExists = true
            };
            string current = _vm.LogoPath;
            if (!string.IsNullOrEmpty(current) && Directory.Exists(Path.GetDirectoryName(current)))
                dialog.InitialDirectory = Path.GetDirectoryName(current);
            if (dialog.ShowDialog(this) == true) _vm.LogoPath = dialog.FileName;
        }

        private void ClearLogo_Click(object sender, RoutedEventArgs e) => _vm.LogoPath = "";

        // ------------------------------------------------------------------ save

        private async void SavePdf_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Save the report",
                Filter = "PDF (*.pdf)|*.pdf",
                DefaultExt = ".pdf",
                InitialDirectory = _vm.DefaultFolder(),
                FileName = _vm.DefaultFileName(),
                AddExtension = true,
                OverwritePrompt = true
            };
            if (dialog.ShowDialog(this) != true) return;

            var button = sender as Button;
            if (button != null) button.IsEnabled = false;
            OpenButton.Visibility = Visibility.Collapsed;
            ExportStatus.Text = "Saving the report…";
            Cursor = Cursors.Wait;
            try
            {
                int pages = await _vm.ExportAsync(dialog.FileName, CsvCheck.IsChecked == true);
                _lastPdf = dialog.FileName;
                ExportStatus.Text = $"Saved {pages} page{(pages == 1 ? "" : "s")}: {dialog.FileName}" +
                    (CsvCheck.IsChecked == true ? " (and CSV tables)" : "");
                OpenButton.Visibility = Visibility.Visible;
            }
            catch (IOException ex)
            {
                ExportStatus.Text = "Could not write the file. Is it open in another program? " + ex.Message;
            }
            catch (UnauthorizedAccessException)
            {
                ExportStatus.Text = "That folder cannot be written to. Choose another one.";
            }
            catch (Exception ex)
            {
                IO.FileLogger.Log($"Report export failed: {ex}");
                ExportStatus.Text = "The report could not be saved: " + ex.Message;
            }
            finally
            {
                Cursor = null;
                if (button != null) button.IsEnabled = _vm.CanExport;
            }
        }

        private void OpenPdf_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_lastPdf) || !File.Exists(_lastPdf)) return;
            try
            {
                Process.Start(new ProcessStartInfo(_lastPdf) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                ExportStatus.Text = "The PDF could not be opened: " + ex.Message;
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();
    }
}
