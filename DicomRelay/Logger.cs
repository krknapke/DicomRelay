using System;
using System.IO;
using System.Reflection;

namespace DicomRelay
{
    /// <summary>
    /// Thread-safe file logger. Writes timestamped entries to dicom_relay.log in the application directory.
    /// </summary>
    internal static class Logger
    {
        private static readonly string LogPath = Path.Combine(
            Path.GetDirectoryName(System.AppContext.BaseDirectory) ?? ".",
            "dicom_relay.log");

        private static readonly object _fileLock = new();

        /// <summary>
        /// Appends a log line to the log file in the format: yyyy-MM-dd HH:mm:ss [LEVEL  ] Message.
        /// </summary>
        public static void Write(string level, string message)
        {
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level.ToUpper(),-7}] {message}";
            lock (_fileLock)
            {
                try { File.AppendAllText(LogPath, line + Environment.NewLine); }
                catch { }
            }
        }

        /// <summary>Full path to the current log file.</summary>
        public static string LogFilePath => LogPath;
    }
}
