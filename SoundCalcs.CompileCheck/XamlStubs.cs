// XAML-generated members (InitializeComponent + x:Name fields), stubbed so the plugin
// C# compiles on Linux where the WPF markup compiler is unavailable. Keep in sync with
// the x:Name attributes in UI/*.xaml that code-behind references.
using System.Windows.Controls;
using MaterialDesignThemes.Wpf;

namespace SoundCalcs.UI
{
    public partial class AcousticViewerControl
    {
        internal SkiaSharp.Views.WPF.SKElement SkCanvas = null;
        internal Button ClearPinsBtn = null, ProbeBtn = null, FitBtn = null;
        public void InitializeComponent() { }
    }
    public partial class MainWindow
    {
        internal AcousticViewerControl AcousticViewer = null;
        internal ListBox LinksListBox = null;
        internal ComboBox CategoryCombo = null;
        internal Grid RootGrid = null, SpeakersPage = null;
        internal ColumnDefinition SidebarColumn = null, PageColumn = null;
        internal TextBlock DocumentTitleText = null, ThemeText = null, PageTitleText = null, PageSubtitleText = null;
        internal PackIcon ThemeIcon = null, MaximizeIcon = null;
        internal Button ThemeButton = null, MaximizeButton = null;
        internal RadioButton NavModel = null, NavSpeakers = null, NavRoom = null, NavRun = null, NavResults = null;
        internal ScrollViewer ModelPage = null, RoomPage = null, RunPage = null, ResultsPage = null;
        internal Border ViewerFrame = null;
        public void InitializeComponent() { }
    }
}
