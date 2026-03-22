using Autodesk.Revit.UI;
using ricaun.Revit.UI;
using System;
using SoundCalcs.Commands;

namespace SoundCalcs
{
    [AppLoader]
    public class App : IExternalApplication
    {
        private RibbonPanel ribbonPanel;

        public Result OnStartup(UIControlledApplication application)
        {
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
    }
}
