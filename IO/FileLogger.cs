using System;
using System.IO;

namespace SoundCalcs.IO
{
    public static class FileLogger
    {
        private static string _logPath;

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
        }

        public static void Log(string message)
        {
            try
            {
                string line = $"{DateTime.Now:HH:mm:ss.fff} {message}";
                File.AppendAllText(LogPath, line + Environment.NewLine);
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
