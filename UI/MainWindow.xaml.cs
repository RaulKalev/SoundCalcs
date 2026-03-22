using System.Windows;
using Autodesk.Revit.UI;
using SoundCalcs.UI.ViewModels;

namespace SoundCalcs.UI
{
    public partial class MainWindow : Window
    {
        private readonly MainViewModel _vm;

        public MainWindow(UIApplication uiApp)
        {
            InitializeComponent();
            _vm = new MainViewModel(uiApp);
            DataContext = _vm;
        }

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

        private void StartJob_Click(object sender, RoutedEventArgs e)
        {
            _vm.StartJob();
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
