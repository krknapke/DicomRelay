using System;
using System.Diagnostics;
using Microsoft.Win32;

namespace DicomRelay
{
    /// <summary>
    /// Adds/removes a Run-key entry so the app starts automatically when
    /// the user logs in. Uses HKCU so it does not require admin rights.
    /// </summary>
    internal static class StartupHelper
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName  = "DicomRelay";

        /// <summary>
        /// Checks whether the application is currently registered in HKCU Run key.
        /// </summary>
        public static bool IsEnabled()
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) != null;
        }

        /// <summary>
        /// Registers or unregisters the current executable with the HKCU Run key.
        /// </summary>
        public static void SetEnabled(bool enabled)
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);

            if (enabled)
            {
                var exePath = Process.GetCurrentProcess().MainModule?.FileName ?? "";
                key.SetValue(ValueName, $"\"{exePath}\"");
                Logger.Write("info", $"Startup registered: {exePath}");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                Logger.Write("info", "Startup entry removed.");
            }
        }
    }
}
