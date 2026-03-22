using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using System.Runtime.InteropServices;

namespace SoundCalcs.Commands
{
    [Transaction(TransactionMode.Manual)]
    public class SoundCalcsCommand : IExternalCommand
    {
        private static UI.MainWindow _window;

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        private const int SW_RESTORE = 9;

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                if (_window != null && _window.IsLoaded)
                {
                    IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(_window).Handle;
                    if (_window.WindowState == System.Windows.WindowState.Minimized)
                        ShowWindow(hwnd, SW_RESTORE);

                    _window.Activate();
                    _window.Focus();
                    SetForegroundWindow(hwnd);
                    return Result.Succeeded;
                }

                UIApplication uiApp = commandData.Application;

                _window = new UI.MainWindow(uiApp);
                IntPtr owner = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
                new System.Windows.Interop.WindowInteropHelper(_window) { Owner = owner };

                _window.Closed += (s, e) => { _window = null; };
                _window.Show();

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }
    }
}
