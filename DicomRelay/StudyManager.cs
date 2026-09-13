using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DicomRelay
{
    /// <summary>
    /// Current forwarding lifecycle status of a DICOM study.
    /// </summary>
    public enum StudyStatus
    {
        /// <summary>Study received and ready for review/forwarding.</summary>
        Ready,
        /// <summary>Forwarding to PACS currently in progress.</summary>
        Forwarding,
        /// <summary>Successfully forwarded to PACS.</summary>
        Forwarded,
        /// <summary>Forwarding failed after all configured retries.</summary>
        Error
    }

    /// <summary>
    /// Encapsulates metadata extracted from a DICOM study folder via dcmdump.
    /// </summary>
    public class StudyInfo
    {
        /// <summary>Full local file system path to the study directory.</summary>
        public string FolderPath      { get; set; } = "";

        /// <summary>Patient Name tag (0010,0010).</summary>
        public string PatientName     { get; set; } = "(unknown)";

        /// <summary>Patient ID tag (0010,0020).</summary>
        public string PatientId       { get; set; } = "";

        /// <summary>Modality tag (0008,0060) (e.g. US, CT, MR, DX).</summary>
        public string Modality        { get; set; } = "US";

        /// <summary>Accession Number tag (0008,0050).</summary>
        public string AccessionNumber { get; set; } = "";

        /// <summary>Unique Study Instance UID tag (0020,000D).</summary>
        public string StudyInstanceUid{ get; set; } = "";

        /// <summary>Study Date tag (0008,0020) formatted as MM/DD/YYYY.</summary>
        public string StudyDate       { get; set; } = "";

        /// <summary>Total count of DICOM instance files in this study folder.</summary>
        public int    ImageCount      { get; set; }

        /// <summary>Current forwarding lifecycle status.</summary>
        public StudyStatus Status     { get; set; } = StudyStatus.Ready;

        /// <summary>Timestamp when this study was received.</summary>
        public DateTime ReceivedAt    { get; set; } = DateTime.Now;

        /// <summary>Directory name of the study folder.</summary>
        public string FolderName      => Path.GetFileName(FolderPath);
    }

    /// <summary>
    /// Watches the incoming directory for completed studies (.ready marker files
    /// written by forward.bat in manual mode, or by auto-receive in auto mode).
    /// Provides study list management, dedup/merge, and manual forward support.
    /// </summary>
    internal class StudyManager : IDisposable
    {
        private FileSystemWatcher? _watcher;
        private readonly List<StudyInfo> _studies = new();
        private readonly object _lock = new();
        private string _dcmtkBin = "";
        private string _watchDir = "";

        public event Action? StudiesChanged;
        public event Action<string, string>? LogMessage;

        // ── Init ─────────────────────────────────────────────────────────────
        public void Start(string incomingDir, string dcmtkBin)
        {
            _dcmtkBin = dcmtkBin;
            _watchDir = incomingDir;

            Directory.CreateDirectory(incomingDir);

            // Scan existing folders on startup
            ScanExistingFolders(incomingDir);

            _watcher = new FileSystemWatcher(incomingDir)
            {
                Filter            = "*.ready",
                IncludeSubdirectories = true,
                NotifyFilter      = NotifyFilters.FileName | NotifyFilters.CreationTime,
                EnableRaisingEvents = true,
            };
            _watcher.Created += (_, e) => OnReadyFile(e.FullPath);
        }

        public void Stop()
        {
            if (_watcher != null)
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Dispose();
                _watcher = null;
            }
        }

        private void ScanExistingFolders(string dir)
        {
            try
            {
                foreach (var readyFile in Directory.GetFiles(dir, "*.ready", SearchOption.AllDirectories))
                    OnReadyFile(readyFile);
            }
            catch { }
        }

        // ── .ready file handler ───────────────────────────────────────────────
        private void OnReadyFile(string readyFilePath)
        {
            var studyFolder = Path.GetDirectoryName(readyFilePath) ?? "";
            if (!Directory.Exists(studyFolder)) return;

            // Small delay to ensure all files are flushed
            Thread.Sleep(500);

            var info = ReadStudyInfo(studyFolder);
            if (info == null) return;

            lock (_lock)
            {
                // Check for existing entry with same folder
                var existing = _studies.FirstOrDefault(s => s.FolderPath == studyFolder);
                if (existing != null)
                {
                    existing.ImageCount = CountDicomFiles(studyFolder);
                    existing.Status = StudyStatus.Ready;
                }
                else
                {
                    _studies.Insert(0, info);
                }
            }

            Log("info", $"Study ready for review: {info.PatientName} ({info.Modality}) — {info.ImageCount} images");
            StudiesChanged?.Invoke();
        }

        // ── Study info reader (via dcmdump) ───────────────────────────────────
        public StudyInfo? ReadStudyInfo(string folderPath)
        {
            try
            {
                var firstFile = GetDicomFiles(folderPath).FirstOrDefault();
                if (firstFile == null) return null;

                var dump = RunDcmDump(firstFile);
                if (dump == null) return null;

                return new StudyInfo
                {
                    FolderPath       = folderPath,
                    PatientName      = ParseTag(dump, "0010,0010") ?? "(unknown)",
                    PatientId        = ParseTag(dump, "0010,0020") ?? "",
                    Modality         = ParseTag(dump, "0008,0060") ?? "US",
                    AccessionNumber  = ParseTag(dump, "0008,0050") ?? "",
                    StudyInstanceUid = ParseTag(dump, "0020,000D") ?? "",
                    StudyDate        = ParseStudyDate(ParseTag(dump, "0008,0020")),
                    ImageCount       = CountDicomFiles(folderPath),
                    Status           = StudyStatus.Ready,
                    ReceivedAt       = DateTime.Now,
                };
            }
            catch (Exception ex)
            {
                Log("warn", $"Could not read study info from {folderPath}: {ex.Message}");
                return null;
            }
        }

        private string? RunDcmDump(string filePath)
        {
            try
            {
                var dcmdump = Path.Combine(_dcmtkBin, "dcmdump.exe");
                if (!File.Exists(dcmdump)) return null;

                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName               = dcmdump,
                    Arguments              = $"\"{filePath}\"",
                    UseShellExecute        = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    CreateNoWindow         = true,
                };
                using var p = System.Diagnostics.Process.Start(psi);
                var output = p?.StandardOutput.ReadToEnd() ?? "";
                p?.WaitForExit(5000);
                return output;
            }
            catch { return null; }
        }

        private static string? ParseTag(string dump, string tag)
        {
            // Match: (XXXX,XXXX) XX [value]
            var pattern = $@"\({tag}\)\s+\w+\s+\[([^\]]*)\]";
            var m = Regex.Match(dump, pattern, RegexOptions.IgnoreCase);
            if (!m.Success) return null;
            var val = m.Groups[1].Value.Trim();
            return val.Length == 0 ? null : val;
        }

        private static string ParseStudyDate(string? raw)
        {
            if (raw == null || raw.Length != 8) return raw ?? "";
            return $"{raw[4..6]}/{raw[6..8]}/{raw[0..4]}";
        }

        private static IEnumerable<string> GetDicomFiles(string folder) =>
            Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                     .Where(f => !f.EndsWith(".ready") && !f.EndsWith(".bak") && !f.EndsWith(".txt"));

        private static int CountDicomFiles(string folder) =>
            GetDicomFiles(folder).Count();

        // ── Public accessors ──────────────────────────────────────────────────
        public List<StudyInfo> GetStudies()
        {
            lock (_lock) return _studies.ToList();
        }

        public void RefreshStudy(StudyInfo study)
        {
            study.ImageCount = CountDicomFiles(study.FolderPath);
            var fresh = ReadStudyInfo(study.FolderPath);
            if (fresh != null)
            {
                study.PatientName      = fresh.PatientName;
                study.Modality         = fresh.Modality;
                study.StudyInstanceUid = fresh.StudyInstanceUid;
            }
            StudiesChanged?.Invoke();
        }

        public void RemoveStudy(StudyInfo study)
        {
            lock (_lock) _studies.Remove(study);
            StudiesChanged?.Invoke();
        }

        public void ClearForwarded()
        {
            lock (_lock) _studies.RemoveAll(s => s.Status == StudyStatus.Forwarded);
            StudiesChanged?.Invoke();
        }

        // ── Merge duplicates by StudyInstanceUID ──────────────────────────────
        /// <summary>
        /// Merges all folders with the same StudyInstanceUID into the oldest folder.
        /// Returns the surviving folder path.
        /// </summary>
        public string MergeByUid(List<StudyInfo> group)
        {
            if (group.Count == 1) return group[0].FolderPath;

            // Keep oldest folder as the target
            var target = group.OrderBy(s => s.ReceivedAt).First();
            var others = group.Where(s => s != target).ToList();

            foreach (var other in others)
            {
                foreach (var srcFile in GetDicomFiles(other.FolderPath))
                {
                    var dest = Path.Combine(target.FolderPath, Path.GetFileName(srcFile));
                    if (!File.Exists(dest))
                        File.Copy(srcFile, dest, overwrite: false);
                }
                try { Directory.Delete(other.FolderPath, recursive: true); } catch { }

                lock (_lock) _studies.Remove(other);
            }

            target.ImageCount = CountDicomFiles(target.FolderPath);
            Log("info", $"Merged {group.Count} folders → {target.FolderName} ({target.ImageCount} images)");
            return target.FolderPath;
        }

        // ── Manual forward ────────────────────────────────────────────────────
        public async Task ForwardStudiesAsync(List<StudyInfo> toForward, Config cfg)
        {
            // Group by StudyInstanceUID and merge duplicates
            var groups = toForward
                .GroupBy(s => string.IsNullOrEmpty(s.StudyInstanceUid) ? s.FolderPath : s.StudyInstanceUid)
                .ToList();

            foreach (var group in groups)
            {
                var studies = group.ToList();
                string folder;

                if (studies.Count > 1)
                {
                    folder = MergeByUid(studies);
                    var merged = studies.First(s => s.FolderPath == folder);
                    merged.Status = StudyStatus.Forwarding;
                }
                else
                {
                    studies[0].Status = StudyStatus.Forwarding;
                    folder = studies[0].FolderPath;
                }

                StudiesChanged?.Invoke();

                var (ok, msg) = await RunStorescuAsync(folder, cfg);
                var study = GetStudies().FirstOrDefault(s => s.FolderPath == folder);
                if (study != null)
                {
                    study.Status = ok ? StudyStatus.Forwarded : StudyStatus.Error;
                    if (ok && cfg.DeleteAfterFwd)
                    {
                        try { Directory.Delete(folder, recursive: true); } catch { }
                    }
                }

                Log(ok ? "ok" : "error",
                    ok ? $"Manual forward complete: {folder}"
                       : $"Manual forward failed: {msg}");

                StudiesChanged?.Invoke();
            }
        }

        private async Task<(bool ok, string msg)> RunStorescuAsync(string folder, Config cfg)
        {
            int maxAttempts = Math.Max(1, cfg.RetryCount);
            string lastMsg = "";

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                if (attempt > 1)
                {
                    Log("warn", $"Retry {attempt - 1} of {maxAttempts - 1} — waiting 5 seconds...");
                    await Task.Delay(5000);
                }

                var (ok, msg) = await TryStorescu(folder, cfg);
                lastMsg = msg;

                if (ok)
                {
                    if (attempt > 1)
                        Log("ok", $"Forward succeeded on attempt {attempt}.");
                    return (true, msg);
                }

                Log("warn", $"Attempt {attempt} failed: {msg}");
            }

            return (false, $"Failed after {maxAttempts} attempts. Last error: {lastMsg}");
        }

        private async Task<(bool ok, string msg)> TryStorescu(string folder, Config cfg)
        {
            try
            {
                var storescu = Path.Combine(cfg.DcmtkBin, "storescu.exe");
                var dcmodify = Path.Combine(cfg.DcmtkBin, "dcmodify.exe");

                // Remove .ready marker files — storescu tries to send them as DICOM and fails
                foreach (var f in Directory.GetFiles(folder, "*.ready", SearchOption.AllDirectories))
                {
                    try { File.Delete(f); } catch { }
                }

                // Apply configured DICOM tag overrides if any
                var overrides = cfg.TagOverrides
                    .Where(t => !string.IsNullOrWhiteSpace(t.Tag) && t.Value != null)
                    .ToList();

                if (overrides.Count > 0 && File.Exists(dcmodify))
                {
                    var argsBuilder = new StringBuilder("-nb");
                    foreach (var ov in overrides)
                    {
                        var cleanTag = ov.Tag.Trim();
                        var cleanVal = ov.Value.Replace("\"", "\\\"");
                        argsBuilder.Append($" -i \"{cleanTag}={cleanVal}\"");
                    }
                    var modifyArgs = argsBuilder.ToString();

                    foreach (var f in GetDicomFiles(folder))
                    {
                        var psi2 = new System.Diagnostics.ProcessStartInfo
                        {
                            FileName               = dcmodify,
                            Arguments              = $"{modifyArgs} \"{f}\"",
                            UseShellExecute        = false,
                            CreateNoWindow         = true,
                            RedirectStandardOutput = true,
                            RedirectStandardError  = true,
                        };
                        using var p2 = System.Diagnostics.Process.Start(psi2);
                        await Task.Run(() => p2?.WaitForExit(10000));
                    }
                }

                // --no-halt: don't abort if one file fails, keep sending the rest
                var args = $"-v --no-halt --max-pdu {cfg.MaxPdu} -aet {cfg.FwdMyAet} -aec {cfg.FwdAet} {cfg.FwdHost} {cfg.FwdPort} +sd +r \"{folder}\"";
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName               = storescu,
                    Arguments              = args,
                    UseShellExecute        = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    CreateNoWindow         = true,
                };

                using var proc = System.Diagnostics.Process.Start(psi);
                if (proc == null) return (false, "Failed to start storescu");

                var output = await proc.StandardOutput.ReadToEndAsync();
                var error  = await proc.StandardError.ReadToEndAsync();
                await Task.Run(() => proc.WaitForExit(120000));

                var allOutput = (output + error).Trim();
                foreach (var line in allOutput.Split('\n'))
                    if (!string.IsNullOrWhiteSpace(line))
                        Log(line.StartsWith("E:") ? "error" : "info", line.Trim());

                return (proc.ExitCode == 0, allOutput);
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        // ── Get DICOM files for viewer ────────────────────────────────────────
        public List<string> GetFilesForViewer(StudyInfo study) =>
            GetDicomFiles(study.FolderPath).OrderBy(f => f).ToList();

        private void Log(string level, string msg) => LogMessage?.Invoke(level, msg);

        public void Dispose() => Stop();
    }
}
