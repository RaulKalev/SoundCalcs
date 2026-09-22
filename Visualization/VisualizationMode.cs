// Kept in the original UI.ViewModels namespace so existing XAML bindings and
// converters are unaffected; lives here so Revit/WPF-free code (HeatmapMath,
// the headless harness and tests) can reference it.
namespace SoundCalcs.UI.ViewModels
{
    public enum VisualizationMode
    {
        SPL,
        SPL_A,
        STI,
        C80,
        SPL_125,
        SPL_250,
        SPL_500,
        SPL_1k,
        SPL_2k,
        SPL_4k,
        SPL_8k
    }
}
