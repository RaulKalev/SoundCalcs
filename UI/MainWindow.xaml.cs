using System.Windows;
using System.Windows.Input;
using Autodesk.Revit.UI;
using SoundCalcs.UI.ViewModels;

namespace SoundCalcs.UI
{
    public partial class MainWindow : Window
    {
        private readonly MainViewModel _vm;
        private readonly WindowResizer _resizer;

        public MainWindow(UIApplication uiApp)
        {
            InitializeComponent();
            _vm = new MainViewModel(uiApp);
            DataContext = _vm;
            _resizer = new WindowResizer(this);

            // Wire speaker rotation: viewer drag → ViewModel → Revit ExtensibleStorage
            AcousticViewer.OnSpeakerRotated = (elementId, angleDeg) =>
                _vm.SetSpeakerAimAngle(elementId, angleDeg);
        }

        // ============ Resize Handlers ============

        private void ResizeLeft_MouseDown(object sender, MouseButtonEventArgs e)
            => _resizer.StartResizing(e, ResizeDirection.Left);

        private void ResizeRight_MouseDown(object sender, MouseButtonEventArgs e)
            => _resizer.StartResizing(e, ResizeDirection.Right);

        private void ResizeBottom_MouseDown(object sender, MouseButtonEventArgs e)
            => _resizer.StartResizing(e, ResizeDirection.Bottom);

        private void ResizeBottomLeft_MouseDown(object sender, MouseButtonEventArgs e)
            => _resizer.StartResizing(e, ResizeDirection.BottomLeft);

        private void ResizeBottomRight_MouseDown(object sender, MouseButtonEventArgs e)
            => _resizer.StartResizing(e, ResizeDirection.BottomRight);

        private void Window_MouseMove(object sender, MouseEventArgs e)
            => _resizer.ResizeWindow(e);

        private void Window_MouseUp(object sender, MouseButtonEventArgs e)
            => _resizer.StopResizing();

        // ============ Model Tab ============

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

        // ============ Speakers Tab ============

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

        // ============ Run Tab ============

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

        // ============ Results Tab ============

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
