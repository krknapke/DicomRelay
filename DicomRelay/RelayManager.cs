using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace DicomRelay
{
    /// <summary>
    /// Manages the DCMTK receiver process (storescp.exe) and the generation of batch scripts
    /// for automatic or manual DICOM study forwarding via storescu.exe.
    /// Captures process standard output/error, tracks uptime, and notifies subscribers of state transitions.
    /// </summary>
    internal class RelayManager : IDisposable
    {
        private Process?  _process;
        private bool      _running;
        private DateTime? _startTime;
        private readonly object _lock = new();

        /// <summary>Fired when the running state changes (started or stopped).</summary>
        public event Action<bool>?   StatusChanged;

        /// <summary>Fired when a new log message is produced (level, message).</summary>
        public event Action<string, string>? LogMessage;

        /// <summary>Indicates whether the storescp process is currently active.</summary>
        public bool    IsRunning => _running;

        /// <summary>Formatted uptime duration string (e.g. "2h 15m", "45s").</summary>
        public string  Uptime
        {
            get
            {
                if (_startTime is null) return "—";
                var s = (int)(DateTime.Now - _startTime.Value).TotalSeconds;
                var h = s / 3600; var m = (s % 3600) / 60; var sec = s % 60;
                return h > 0 ? $"{h}h {m}m" : m > 0 ? $"{m}m {sec}s" : $"{sec}s";
            }
        }

        // ── Validation ───────────────────────────────────────────────────────

        /// <summary>
        /// Validates that required DCMTK binaries exist and network ports are valid integers.
        /// </summary>
        public (bool ok, string? error) Validate(Config cfg)
        {
            var storescp = Path.Combine(cfg.DcmtkBin, "storescp.exe");
            var storescu = Path.Combine(cfg.DcmtkBin, "storescu.exe");

            if (!File.Exists(storescp))
                return (false, $"storescp.exe not found in:\n{cfg.DcmtkBin}");
            if (!File.Exists(storescu))
                return (false, $"storescu.exe not found in:\n{cfg.DcmtkBin}");
            if (!int.TryParse(cfg.RcvPort, out _))
                return (false, "Receive port must be a number.");
            if (!int.TryParse(cfg.FwdPort, out _))
                return (false, "Forward port must be a number.");

            return (true, null);
        }

        // ── Build command ────────────────────────────────────────────────────

        /// <summary>
        /// Assembles the command-line arguments array representing the storescp command.
        /// </summary>
        public string[] BuildCommand(Config cfg)
        {
            var storescp = Path.Combine(cfg.DcmtkBin, "storescp.exe");
            var storescu = Path.Combine(cfg.DcmtkBin, "storescu.exe");

            // Ensure storage directory exists
            Directory.CreateDirectory(cfg.RcvDir);

            // storescu forward command — #p is replaced by storescp with study folder path
            var fwdCmd = new StringBuilder();
            fwdCmd.Append($"\"{storescu}\"");
            fwdCmd.Append($" -aet {cfg.FwdMyAet}");
            fwdCmd.Append($" -aec {cfg.FwdAet}");
            fwdCmd.Append($" {cfg.FwdHost}");
            fwdCmd.Append($" {cfg.FwdPort}");
            fwdCmd.Append(" \"#p\"");

            if (cfg.DeleteAfterFwd)
                fwdCmd = new StringBuilder($"cmd /c ({fwdCmd}) && rmdir /s /q \"#p\"");

            // storescp command parts
            var args = new System.Collections.Generic.List<string>
            {
                "-v",
                "-aet", cfg.RcvAet,
                "-od",  $"\"{cfg.RcvDir}\"",
                "--sort-conc-studies", "study",
                "--eostudy-timeout",   cfg.EosTimeout,
            };

            if (cfg.AcceptAll)
                args.Add("+xa");

            if (cfg.AutoForward)
            {
                args.Add("--exec-on-eostudy");
                args.Add($"\"{fwdCmd}\"");
            }

            args.Add(cfg.RcvPort);

            return new[] { storescp }.Concat(args).ToArray();
        }

        // Helper for LINQ without using directive
        private static string[] Concat(string[] first, System.Collections.Generic.List<string> second)
        {
            var result = new string[1 + second.Count];
            result[0] = first[0];
            second.CopyTo(result, 1);
            return result;
        }

        // ── Start ────────────────────────────────────────────────────────────

        /// <summary>
        /// Starts the DCMTK storescp listener process and associated output monitor threads.
        /// Generates forward.bat which storescp invokes upon end-of-study timeout.
        /// </summary>
        public (bool ok, string? error) Start(Config cfg)
        {
            lock (_lock)
            {
                if (_running) return (false, "Already running.");

                var (ok, err) = Validate(cfg);
                if (!ok) return (false, err);

                // Build args string for ProcessStartInfo
                var storescp = Path.Combine(cfg.DcmtkBin, "storescp.exe");
                var storescu = Path.Combine(cfg.DcmtkBin, "storescu.exe");

                Directory.CreateDirectory(cfg.RcvDir);

                // Write a batch file for reliable exec-on-eostudy (avoids quoting issues on Windows)
                var batPath = WriteBatchFile(cfg);

                var args = new StringBuilder();
                args.Append($"-v -aet {cfg.RcvAet} -od \"{cfg.RcvDir}\" --sort-conc-studies study --eostudy-timeout {cfg.EosTimeout}");
                if (cfg.AcceptAll) args.Append(" +xa");
                if (cfg.MaxPdu > 0) args.Append($" --max-pdu {cfg.MaxPdu}");
                // Always run batch on eostudy — in manual mode it writes .ready; in auto it forwards
                args.Append($" --exec-on-eostudy \"{batPath} #p\"");
                args.Append($" {cfg.RcvPort}");

                Log("info", $"Starting storescp — AET: {cfg.RcvAet}  Port: {cfg.RcvPort}  MaxPDU: {cfg.MaxPdu}");
                Log("info", $"Storage dir: {cfg.RcvDir}");
                Log("info", cfg.ManualMode
                    ? "Mode: MANUAL — studies will queue for review before forwarding"
                    : $"Mode: AUTO-FORWARD → {cfg.FwdHost}:{cfg.FwdPort} ({cfg.FwdAet})");

                var psi = new ProcessStartInfo
                {
                    FileName               = storescp,
                    Arguments              = args.ToString(),
                    UseShellExecute        = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    CreateNoWindow         = true,
                };

                try
                {
                    _process = Process.Start(psi)
                        ?? throw new Exception("Process.Start returned null.");
                }
                catch (Exception ex)
                {
                    return (false, $"Failed to launch storescp:\n{ex.Message}");
                }

                _running   = true;
                _startTime = DateTime.Now;
                StatusChanged?.Invoke(true);

                // Read stdout
                var t1 = new Thread(() => ReadStream(_process.StandardOutput)) { IsBackground = true };
                var t2 = new Thread(() => ReadStream(_process.StandardError))  { IsBackground = true };
                t1.Start(); t2.Start();

                // Watch for unexpected exit
                var t3 = new Thread(WatchProcess) { IsBackground = true };
                t3.Start();

                return (true, null);
            }
        }

        // ── Batch file writer ────────────────────────────────────────────────

        /// <summary>
        /// Writes forward.bat used by storescp --exec-on-eostudy.
        /// Handles optional dcmodify tag modification, retry loop, and post-forward cleanup.
        /// </summary>
        private static string WriteBatchFile(Config cfg)
        {
            var appDir   = Path.GetDirectoryName(System.AppContext.BaseDirectory) ?? ".";
            var batPath  = Path.Combine(appDir, "forward.bat");
            var dcmodify = Path.Combine(cfg.DcmtkBin, "dcmodify.exe");
            var storescu = Path.Combine(cfg.DcmtkBin, "storescu.exe");

            var sb = new StringBuilder();
            sb.AppendLine("@echo off");
            sb.AppendLine("set STUDYDIR=%~1");
            sb.AppendLine("if \"%STUDYDIR%\"==\"\" (");
            sb.AppendLine("  echo ERROR: No study directory provided");
            sb.AppendLine("  exit /b 1");
            sb.AppendLine(")");

            if (cfg.ManualMode)
            {
                // Manual mode: just drop a .ready marker so the app picks it up
                sb.AppendLine("echo Manual mode: marking study as ready for review");
                sb.AppendLine("echo ready > \"%STUDYDIR%\\study.ready\"");
                sb.AppendLine("exit /b 0");
            }
            else
            {
                sb.AppendLine("echo Forwarding study: %STUDYDIR%");

                // dcmodify — apply configured tag overrides if any
                var overrides = cfg.TagOverrides
                    .Where(t => !string.IsNullOrWhiteSpace(t.Tag) && t.Value != null)
                    .ToList();

                if (overrides.Count > 0)
                {
                    var argsBuilder = new StringBuilder("-nb");
                    foreach (var ov in overrides)
                    {
                        var cleanTag = ov.Tag.Trim();
                        var cleanVal = ov.Value.Replace("\"", "\\\"");
                        argsBuilder.Append($" -i \"{cleanTag}={cleanVal}\"");
                    }

                    sb.AppendLine($"echo Applying {overrides.Count} DICOM tag override(s)...");
                    sb.AppendLine("for /r \"%STUDYDIR%\" %%f in (*) do (");
                    sb.AppendLine($"  \"{dcmodify}\" {argsBuilder} \"%%f\"");
                    sb.AppendLine(")");
                }

                // Also drop .ready marker in auto mode so Studies tab stays current
                sb.AppendLine("echo ready > \"%STUDYDIR%\\study.ready\"");

                // storescu forward with retry logic
                // Remove .ready marker files first so storescu doesn't try to send them
                sb.AppendLine("del /q \"%STUDYDIR%\\*.ready\" 2>nul");
                sb.AppendLine("for /r \"%STUDYDIR%\" %%f in (*.ready) do del /q \"%%f\" 2>nul");
                // --max-pdu limits outgoing PDU size, --no-halt keeps going if one file fails
                sb.AppendLine("set ATTEMPTS=0");
                sb.AppendLine(":retry");
                sb.AppendLine("set /a ATTEMPTS+=1");
                sb.AppendLine($"\"{storescu}\" -v --no-halt --max-pdu {cfg.MaxPdu} -aet {cfg.FwdMyAet} -aec {cfg.FwdAet} {cfg.FwdHost} {cfg.FwdPort} +sd +r \"%STUDYDIR%\"");
                sb.AppendLine("set RESULT=%ERRORLEVEL%");
                sb.AppendLine("if %RESULT%==0 goto success");
                sb.AppendLine($"if %ATTEMPTS% GEQ {cfg.RetryCount} goto failed");
                sb.AppendLine("echo Retry %ATTEMPTS% — waiting 5 seconds...");
                sb.AppendLine("timeout /t 5 /nobreak >nul");
                sb.AppendLine("goto retry");
                sb.AppendLine(":failed");
                sb.AppendLine($"echo Forward failed after {cfg.RetryCount} attempts.");
                sb.AppendLine("exit /b 1");
                sb.AppendLine(":success");
                sb.AppendLine("echo Forward succeeded.");

                if (cfg.DeleteAfterFwd)
                {
                    sb.AppendLine("echo Deleting local study files...");
                    sb.AppendLine("rmdir /s /q \"%STUDYDIR%\"");
                }

                sb.AppendLine("exit /b 0");
            }

            File.WriteAllText(batPath, sb.ToString());
            Logger.Write("info", $"Batch file written: {(cfg.ManualMode ? "MANUAL mode" : "AUTO mode")}");
            return batPath;
        }

        // ── Stop ─────────────────────────────────────────────────────────────
        public void Stop()
        {
            lock (_lock)
            {
                if (!_running) return;
                Log("warn", "Stopping relay...");
                try
                {
                    _process?.Kill(entireProcessTree: true);
                    _process?.WaitForExit(3000);
                }
                catch { }
                finally
                {
                    _process?.Dispose();
                    _process    = null;
                    _running    = false;
                    _startTime  = null;
                    StatusChanged?.Invoke(false);
                    Log("info", "Relay stopped.");
                }
            }
        }

        // ── Output reading ───────────────────────────────────────────────────
        private void ReadStream(StreamReader reader)
        {
            try
            {
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    // Parse DCMTK log prefixes: "I: ", "W: ", "E: ", "D: "
                    if (line.Length > 2 && line[1] == ':' && line[2] == ' ')
                    {
                        var level = line[0] switch
                        {
                            'E' => "error",
                            'W' => "warn",
                            'D' => "debug",
                            _   => "info",
                        };
                        Log(level, line[3..].Trim());
                    }
                    else
                    {
                        Log("info", line.Trim());
                    }
                }
            }
            catch { }
        }

        private void WatchProcess()
        {
            _process?.WaitForExit();
            if (_running) // unexpected exit
            {
                lock (_lock)
                {
                    _running   = false;
                    _startTime = null;
                }
                StatusChanged?.Invoke(false);
                Log("error", "storescp exited unexpectedly. Check the log for details.");
            }
        }

        // ── Logging ──────────────────────────────────────────────────────────
        private void Log(string level, string msg)
        {
            Logger.Write(level, msg);
            LogMessage?.Invoke(level, msg);
        }

        public void Dispose()
        {
            Stop();
            _process?.Dispose();
        }
    }
}
