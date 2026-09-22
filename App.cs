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
            // Pre-load the native SkiaSharp library (embedded, or next to this DLL).
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

        private const string EmbeddedSkiaResource = "SoundCalcs.Native.libSkiaSharp.dll";

        private static void PreloadNativeSkia()
        {
            try
            {
                // 1. Embedded in this DLL (net8.0-windows). Loading it once by full path makes
                //    SkiaSharp's P/Invokes to "libSkiaSharp" bind to the already-loaded module.
                string extracted = ExtractEmbeddedSkia();
                if (extracted != null) { LoadLibrary(extracted); return; }

                // Location is empty when a loader (e.g. ricaun AppLoader) loads the DLL from bytes.
                string location = typeof(App).Assembly.Location;
                if (string.IsNullOrEmpty(location)) return;
                string assemblyDir = Path.GetDirectoryName(location);

                // 2. Flat next to the DLL
                string nativePath = Path.Combine(assemblyDir, "libSkiaSharp.dll");
                if (File.Exists(nativePath)) { LoadLibrary(nativePath); return; }

                // 3. x64 subfolder
                nativePath = Path.Combine(assemblyDir, "x64", "libSkiaSharp.dll");
                if (File.Exists(nativePath)) { LoadLibrary(nativePath); return; }

                // 4. runtimes folder structure (NuGet layout)
                nativePath = Path.Combine(assemblyDir, "runtimes", "win-x64", "native", "libSkiaSharp.dll");
                if (File.Exists(nativePath))
                    LoadLibrary(nativePath);
            }
            catch
            {
                // Non-fatal — SkiaSharp will try its own resolution
            }
        }

        /// <summary>
        /// Writes the embedded libSkiaSharp.dll to %LocalAppData%\RK Tools\SoundCalcs\native\&lt;size&gt;\
        /// (reused while it matches) and returns its path, or null when this build has none embedded
        /// (net48, where Costura handles it). The file keeps its name so P/Invoke resolves it.
        /// </summary>
        private static string ExtractEmbeddedSkia()
        {
            using (Stream resource = typeof(App).Assembly.GetManifestResourceStream(EmbeddedSkiaResource))
            {
                if (resource == null) return null;

                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "RK Tools", "SoundCalcs", "native", resource.Length.ToString());
                string path = Path.Combine(dir, "libSkiaSharp.dll");
                if (File.Exists(path) && new FileInfo(path).Length == resource.Length)
                    return path;

                // Write to a temp file and move into place, so a concurrent Revit never sees a partial DLL.
                Directory.CreateDirectory(dir);
                string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                using (FileStream file = File.Create(temp))
                    resource.CopyTo(file);
                try
                {
                    File.Move(temp, path);
                }
                catch (IOException)
                {
                    // Another Revit instance placed it first; use theirs.
                    File.Delete(temp);
                }
                return path;
            }
        }
    }
}
