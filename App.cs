using Autodesk.Revit.UI;
using ricaun.Revit.UI;
using System;
using System.IO;
using System.Runtime.InteropServices;
using SoundCalcs.Commands;

namespace SoundCalcs
{
    [AppLoader]
    public class App : IExternalApplication
    {
        private RibbonPanel ribbonPanel;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr LoadLibrary(string dllToLoad);

        public Result OnStartup(UIControlledApplication application)
        {
            // Pre-load native SkiaSharp library from the x64 subfolder next to this DLL.
            // Required because Revit's working directory isn't the plugin folder.
            PreloadNativeSkia();

            string tabName = "RK Tools";

            try
            {
                application.CreateRibbonTab(tabName);
            }
            catch
            {
                // Tab already exists
            }

            ribbonPanel = application.CreateOrSelectPanel(tabName, "Tools");

            var button = ribbonPanel.CreatePushButton<SoundCalcsCommand>()
                .SetLargeImage("pack://application:,,,/SoundCalcs;component/Assets/SoundCalcs.tiff")
                .SetText("Sound\r\nCalcs")
                .SetToolTip("Acoustic analysis for Revit models.")
                .SetLongDescription("SoundCalcs performs SPL analysis on rooms using speaker placements and linked architectural geometry.");

            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            ribbonPanel?.Remove();
            return Result.Succeeded;
        }

        private static void PreloadNativeSkia()
        {
            try
            {
                string assemblyDir = Path.GetDirectoryName(
                    typeof(App).Assembly.Location);
                string nativePath = Path.Combine(assemblyDir, "x64", "libSkiaSharp.dll");
                if (File.Exists(nativePath))
                {
                    LoadLibrary(nativePath);
                }
                else
                {
                    // Fallback: check runtimes folder structure (NuGet layout)
                    nativePath = Path.Combine(assemblyDir, "runtimes", "win-x64", "native", "libSkiaSharp.dll");
                    if (File.Exists(nativePath))
                        LoadLibrary(nativePath);
                }
            }
            catch
            {
                // Non-fatal — SkiaSharp will try its own resolution
            }
        }
    }
}
