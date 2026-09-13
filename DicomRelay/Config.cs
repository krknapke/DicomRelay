using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DicomRelay
{
    /// <summary>
    /// Configuration settings for the DICOM Relay application.
    /// Manages receiver (storescp), forwarder (storescu), tag overrides, and reliability options.
    /// Serialized to and loaded from dicom_relay_config.json located in the application directory.
    /// </summary>
    internal class Config
    {
        // ── Receiver Settings (storescp) ──────────────────────────────────────

        /// <summary>
        /// Absolute directory path containing DCMTK executable binaries (storescp.exe, storescu.exe, etc.).
        /// </summary>
        public string DcmtkBin        { get; set; } = @"C:\dcmtk\bin";

        /// <summary>
        /// Application Entity (AE) Title for the local receiver service (storescp).
        /// Modality devices (e.g., ultrasound) must be configured to send to this AE Title.
        /// </summary>
        public string RcvAet          { get; set; } = "VETRELAY";

        /// <summary>
        /// Port on which storescp listens for incoming DICOM associations (default: 4242).
        /// Standard DICOM port is 104; ports &lt; 1024 require elevated administrator privileges.
        /// </summary>
        public string RcvPort         { get; set; } = "4242";

        /// <summary>
        /// Directory path where received DICOM instances are stored locally.
        /// </summary>
        public string RcvDir          { get; set; } = Path.Combine(AppDir, "incoming");

        /// <summary>
        /// Association timeout in seconds before an idle incoming connection is dropped.
        /// </summary>
        public string RcvTimeout      { get; set; } = "30";

        /// <summary>
        /// End-of-study timeout in seconds. Period of silence after which a study is marked complete.
        /// </summary>
        public string EosTimeout      { get; set; } = "30";

        // ── Forwarder Settings (storescu) ─────────────────────────────────────

        /// <summary>
        /// Calling AE Title used by storescu when initiating associations with the remote destination.
        /// </summary>
        public string FwdMyAet        { get; set; } = "VETRELAY";

        /// <summary>
        /// Called AE Title expected by the remote destination (e.g., WebPACS or local PACS).
        /// </summary>
        public string FwdAet          { get; set; } = "WEBPACS";

        /// <summary>
        /// IP address or hostname of the remote DICOM server / WebPACS.
        /// </summary>
        public string FwdHost         { get; set; } = "192.168.1.100";

        /// <summary>
        /// Network port of the remote DICOM server / WebPACS (typically 104 or 11112).
        /// </summary>
        public string FwdPort         { get; set; } = "104";

        // ── Tag Overrides ─────────────────────────────────────────────────────

        /// <summary>
        /// Legacy single-field institution name override for DICOM tag (0008,0080).
        /// Maintained for backwards compatibility with earlier configuration files.
        /// </summary>
        public string InstitutionName { get; set; } = "";

        /// <summary>
        /// List of custom DICOM tag modifications applied via dcmodify before forwarding.
        /// </summary>
        public List<DicomTagOverride> TagOverrides { get; set; } = new();

        // ── Reliability & Performance ─────────────────────────────────────────

        /// <summary>
        /// Scheduled restart interval in hours to maintain storescp stability on 24/7 systems. 0 disables auto-restart.
        /// </summary>
        public int    AutoRestartHours { get; set; } = 6;

        /// <summary>
        /// Whether the application registers with Windows Startup (HKCU Run key) for automatic startup on user login.
        /// </summary>
        public bool   StartWithWindows { get; set; } = false;

        /// <summary>
        /// Maximum Protocol Data Unit (PDU) size in bytes (e.g. 16384, 8192, 4096).
        /// Smaller PDUs provide increased stability on slow or lossy connections.
        /// </summary>
        public int    MaxPdu           { get; set; } = 8192;

        /// <summary>
        /// Number of retry attempts when a storescu forward operation fails.
        /// </summary>
        public int    RetryCount       { get; set; } = 3;

        // ── Operation Mode & Options ──────────────────────────────────────────

        /// <summary>
        /// When true, enables DCMTK option (+xa) to accept all supported transfer syntaxes (helpful for older modalities).
        /// </summary>
        public bool   AcceptAll        { get; set; } = true;

        /// <summary>
        /// When true, automatically forwards studies once the end-of-study timeout expires.
        /// </summary>
        public bool   AutoForward      { get; set; } = true;

        /// <summary>
        /// When true, received studies are queued in the Studies tab for manual operator review before forwarding.
        /// </summary>
        public bool   ManualMode       { get; set; } = false;

        /// <summary>
        /// When true, deletes local DICOM files from the storage directory after successful forward.
        /// </summary>
        public bool   DeleteAfterFwd   { get; set; } = false;

        // ── Persistence ──────────────────────────────────────────────────────
        private static readonly string AppDir =
            Path.GetDirectoryName(System.AppContext.BaseDirectory) ?? ".";

        private static readonly string ConfigPath =
            Path.Combine(AppDir, "dicom_relay_config.json");

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

        /// <summary>
        /// Loads configuration from dicom_relay_config.json if it exists; otherwise returns default configuration.
        /// </summary>
        public static Config Load()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    var json = File.ReadAllText(ConfigPath);
                    var cfg = JsonSerializer.Deserialize<Config>(json, JsonOpts) ?? new Config();

                    // Auto-migrate legacy InstitutionName if TagOverrides does not already contain (0008,0080)
                    if (!string.IsNullOrWhiteSpace(cfg.InstitutionName) &&
                        !cfg.TagOverrides.Exists(t => t.Tag.Trim().Equals("(0008,0080)", StringComparison.OrdinalIgnoreCase)))
                    {
                        cfg.TagOverrides.Insert(0, new DicomTagOverride
                        {
                            Tag   = "(0008,0080)",
                            Name  = "Institution Name",
                            Value = cfg.InstitutionName
                        });
                    }

                    return cfg;
                }
            }
            catch { /* fall through to defaults */ }
            return new Config();
        }

        /// <summary>
        /// Saves the current configuration to dicom_relay_config.json.
        /// </summary>
        public void Save()
        {
            try
            {
                File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, JsonOpts));
            }
            catch (Exception ex)
            {
                Logger.Write("error", $"Could not save config: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Defines a DICOM tag modification rule applied prior to forwarding via dcmodify.
    /// </summary>
    public class DicomTagOverride
    {
        /// <summary>
        /// Tag address in hex format, e.g. "(0008,0080)".
        /// </summary>
        public string Tag   { get; set; } = "";

        /// <summary>
        /// Descriptive name for the tag (e.g. "Institution Name").
        /// </summary>
        public string Name  { get; set; } = "";

        /// <summary>
        /// Replacement value inserted into the DICOM dataset.
        /// </summary>
        public string Value { get; set; } = "";
    }
}
