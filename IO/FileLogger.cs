using System;
using System.IO;

namespace SoundCalcs.IO
{
    public static class FileLogger
    {
        private static string _logPath;
        private static readonly object Gate = new object();

        /// <summary>The log is started afresh beyond this size (it is written on every run).</summary>
        private const long MaxBytes = 20L * 1024 * 1024;

        public static string LogPath
        {
            get
            {
                if (_logPath == null)
                {
                    string docDir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                    _logPath = Path.Combine(docDir, "SoundCalcs_Debug.log");
                }
                return _logPath;
            }
            // Overridable so headless runs (harness, tests) can log next to their output.
            set => _logPath = value;
        }

        public static void Log(string message)
        {
            try
            {
                string line = $"{DateTime.Now:HH:mm:ss.fff} {message}";
                lock (Gate)   // compute and the IFC preload log from other threads
                {
                    var info = new FileInfo(LogPath);
                    if (info.Exists && info.Length > MaxBytes)
                        File.Copy(LogPath, Path.ChangeExtension(LogPath, ".old.log"), true);
                    if (info.Exists && info.Length > MaxBytes) File.Delete(LogPath);
                    File.AppendAllText(LogPath, line + Environment.NewLine);
                }
            }
            catch 
            {
                // Swallow logging errors to avoid crashing app
            }
            
            // Also write to debug output just in case
            System.Diagnostics.Debug.WriteLine(message);
        }

        public static void Clear()
        {
            try
            {
                if (File.Exists(LogPath))
                    File.Delete(LogPath);
            }
            catch { }
        }
    }
}
