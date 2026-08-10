using System;
using System.IO;

namespace GTAZ.Core
{
    public static class Logger
    {
        private static string _logPath;

        public static void Initialize(string path = "./scripts/GTAZ.log")
        {
            _logPath = path;
            ClearLog();
        }

        public static void ClearLog()
        {
            try
            {
                if (_logPath is not null && File.Exists(_logPath))
                    File.Delete(_logPath);
            }
            catch
            {
                // Logger should never throw
            }
        }

        public static void Log(string message)
        {
            try
            {
                if (_logPath is null) return;
                string line = $"[{DateTime.Now:HH:mm:ss}] {message}";
                File.AppendAllText(_logPath, line + Environment.NewLine);
            }
            catch
            {
                // Logger should never throw
            }
        }

        public static void Info(string message) => Log($"[INFO] {message}");
        public static void Warn(string message) => Log($"[WARN] {message}");
        public static void Error(string message) => Log($"[ERROR] {message}");
    }
}
