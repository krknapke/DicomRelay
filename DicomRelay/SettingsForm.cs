using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace DicomRelay
{
    /// <summary>
    /// Main settings and management window for DICOM Relay.
    /// Provides four tabs:
    /// 1. Settings: Configuration for DCMTK paths, receiver (SCP), forwarder (SCU), tag overrides, and reliability.
    /// 2. Studies: Real-time list of received studies with manual forward, deduplication, and viewer access.
    /// 3. Activity Log: Color-coded real-time log viewer with search/clear/export capabilities.
    /// 4. Commands: Live preview of generated DCMTK CLI commands for storescp and storescu.
    /// </summary>
    internal class SettingsForm : Form
    {
        private readonly RelayManager _relay;
        private readonly StudyManager _studyManager;
        private Config _cfg;

        // Status bar
        private Label  _statusLbl   = null!;
        private Label  _uptimeLbl   = null!;
        private Button _startBtn    = null!;
        private Button _stopBtn     = null!;
        private System.Windows.Forms.Timer _uptimeTimer = null!;

        // Settings fields
        private TextBox _dcmtkBin   = null!;
        private TextBox _rcvAet     = null!;
        private TextBox _rcvPort    = null!;
        private TextBox _rcvDir     = null!;
        private TextBox _rcvTimeout = null!;
        private TextBox _eosTimeout = null!;
        private TextBox _fwdMyAet   = null!;
        private TextBox _fwdAet     = null!;
        private TextBox _fwdHost    = null!;
        private TextBox _fwdPort    = null!;
        private CheckBox _acceptAll        = null!;
        private CheckBox _autoFwd          = null!;
        private CheckBox _manualMode       = null!;
        private CheckBox _deleteAfter      = null!;
        private TextBox  _institutionName  = null!;
        private CheckBox _startWithWindows = null!;
        private TextBox  _autoRestartHours = null!;
        private TextBox  _maxPdu           = null!;
        private TextBox  _retryCount       = null!;

        // Log
        private RichTextBox _logBox = null!;

        // Command preview
        private RichTextBox _cmdBox = null!;

        // Studies tab
        private ListView _studyListView = null!;
        private Button   _fwdSelectedBtn = null!;
        private Button   _viewBtn        = null!;
        private Button   _refreshBtn     = null!;
        private Button   _clearFwdBtn    = null!;
        private Label    _studyStatusLbl = null!;

        // Colors
        private static readonly Color BG      = Color.FromArgb(13,  17,  23);
        private static readonly Color Surface = Color.FromArgb(22,  27,  34);
        private static readonly Color Border  = Color.FromArgb(48,  54,  61);
        private static readonly Color TextCol = Color.FromArgb(230, 237, 243);
        private static readonly Color Muted   = Color.FromArgb(139, 148, 158);
        private static readonly Color Accent  = Color.FromArgb(31,  111, 235);
        private static readonly Color Green   = Color.FromArgb(63,  185, 80);
        private static readonly Color Red     = Color.FromArgb(248, 81,  73);
        private static readonly Color Yellow  = Color.FromArgb(210, 153, 34);

        public SettingsForm(RelayManager relay, StudyManager studyManager)
        {
            _relay        = relay;
            _studyManager = studyManager;
            _cfg          = Config.Load();

            _relay.StatusChanged += OnStatusChanged;
            _relay.LogMessage    += OnLogMessage;
            _studyManager.StudiesChanged += OnStudiesChanged;
            _studyManager.LogMessage     += OnLogMessage;

            InitializeForm();
            BuildUI();
            RefreshStatus();
        }

        private void InitializeForm()
        {
            Text            = "DICOM Relay";
            Size            = new Size(720, 640);
            MinimumSize     = new Size(600, 500);
            BackColor       = BG;
            ForeColor       = TextCol;
            FormBorderStyle = FormBorderStyle.Sizable;
            StartPosition   = FormStartPosition.CenterScreen;
            Font            = new Font("Segoe UI", 9f);

            // Hide to tray instead of closing
            FormClosing += (_, e) =>
            {
                e.Cancel = true;
                Hide();
            };
        }

        // ── UI Construction ──────────────────────────────────────────────────
        private void BuildUI()
        {
            // ── Status bar ──
            var statusBar = new Panel
            {
                Dock      = DockStyle.Top,
                Height    = 44,
                BackColor = Surface,
            };
            Controls.Add(statusBar);

            var titleLbl = MakeLabel("DICOM Relay", 11, bold: true, color: TextCol);
            titleLbl.Location = new Point(12, 12);
            statusBar.Controls.Add(titleLbl);

            _statusLbl = MakeLabel("● Stopped", 9, color: Muted);
            _statusLbl.Location = new Point(116, 14);
            statusBar.Controls.Add(_statusLbl);

            _uptimeLbl = MakeLabel("", 9, color: Muted);
            _uptimeLbl.Location = new Point(200, 14);
            _uptimeLbl.Width = 120;
            statusBar.Controls.Add(_uptimeLbl);

            _stopBtn = MakeButton("■  Stop", Red, 90);
            _stopBtn.Anchor  = AnchorStyles.Top | AnchorStyles.Right;
            _stopBtn.Location = new Point(statusBar.Width - 106, 8);
            _stopBtn.Click   += (_, _) => DoStop();
            statusBar.Controls.Add(_stopBtn);

            _startBtn = MakeButton("▶  Start", Accent, 90);
            _startBtn.Anchor  = AnchorStyles.Top | AnchorStyles.Right;
            _startBtn.Location = new Point(statusBar.Width - 204, 8);
            _startBtn.Click   += (_, _) => DoStart();
            statusBar.Controls.Add(_startBtn);

            statusBar.Resize += (_, _) =>
            {
                _stopBtn.Left  = statusBar.Width - 106;
                _startBtn.Left = statusBar.Width - 204;
            };

            // ── Tab control ──
            var tabs = new TabControl
            {
                Dock      = DockStyle.Fill,
                BackColor = BG,
            };
            Controls.Add(tabs);
            tabs.BringToFront();

            var settingsPage = new TabPage("  Settings  ") { BackColor = BG, ForeColor = TextCol };
            var studiesPage  = new TabPage("  Studies  ")  { BackColor = BG, ForeColor = TextCol };
            var logPage      = new TabPage("  Activity Log  ") { BackColor = BG, ForeColor = TextCol };
            var cmdPage      = new TabPage("  Commands  ") { BackColor = BG, ForeColor = TextCol };

            tabs.TabPages.Add(settingsPage);
            tabs.TabPages.Add(studiesPage);
            tabs.TabPages.Add(logPage);
            tabs.TabPages.Add(cmdPage);

            BuildSettingsTab(settingsPage);
            BuildStudiesTab(studiesPage);
            BuildLogTab(logPage);
            BuildCmdTab(cmdPage);

            // ── Uptime refresh timer ──
            _uptimeTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            _uptimeTimer.Tick += (_, _) =>
            {
                if (_relay.IsRunning)
                    _uptimeLbl.Text = $"uptime {_relay.Uptime}";
            };
            _uptimeTimer.Start();
        }

        // ── Settings tab ─────────────────────────────────────────────────────
        private void BuildSettingsTab(TabPage page)
        {
            var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = BG };
            page.Controls.Add(scroll);

            int y = 10;

            // DCMTK section
            var g0 = MakeGroup("DCMTK", scroll, ref y);
            _dcmtkBin = AddFieldWithBrowse(g0, "Bin Folder", _cfg.DcmtkBin, ref y, browseDir: true);
            FinalizeGroup(g0, y);
            y += g0.Height + 8;

            // Receiver section
            y = g0.Bottom + 18;
            var g1 = MakeGroup("Receiver (SCP — storescp)   ←   Ultrasound sends here", scroll, ref y);
            _rcvAet     = AddField(g1, "AE Title",                _cfg.RcvAet,     ref y);
            _rcvPort    = AddField(g1, "Listen Port",             _cfg.RcvPort,    ref y);
            _rcvDir     = AddFieldWithBrowse(g1, "Storage Directory", _cfg.RcvDir, ref y, browseDir: true);
            _rcvTimeout = AddField(g1, "Association Timeout (s)", _cfg.RcvTimeout, ref y);
            _eosTimeout = AddField(g1, "End-of-Study Timeout (s)", _cfg.EosTimeout, ref y);
            FinalizeGroup(g1, y);

            // Forwarder section
            y = g1.Bottom + 18;
            var g2 = MakeGroup("Forwarder (SCU — storescu)   →   WebPACS", scroll, ref y);
            _fwdMyAet = AddField(g2, "My AE Title (Calling)", _cfg.FwdMyAet, ref y);
            _fwdAet   = AddField(g2, "WebPACS AE Title",      _cfg.FwdAet,   ref y);
            _fwdHost  = AddField(g2, "WebPACS Host / IP",     _cfg.FwdHost,  ref y);
            _fwdPort  = AddField(g2, "WebPACS Port",          _cfg.FwdPort,  ref y);
            FinalizeGroup(g2, y);

            // Options section
            y = g2.Bottom + 18;
            var g3 = MakeGroup("Options", scroll, ref y);
            _autoFwd        = AddCheck(g3, "Auto-forward on study complete",           _cfg.AutoForward,    ref y);
            _manualMode     = AddCheck(g3, "Manual mode — queue studies for review before forwarding", _cfg.ManualMode, ref y);
            _acceptAll      = AddCheck(g3, "Accept all DICOM transfer syntaxes (+xa)", _cfg.AcceptAll,      ref y);
            _deleteAfter    = AddCheck(g3, "Delete local files after successful forward", _cfg.DeleteAfterFwd, ref y);
            _institutionName = AddField(g3, "Override Institution Name (blank = don't change)", _cfg.InstitutionName, ref y);
            FinalizeGroup(g3, y);

            // Reliability section
            y = g3.Bottom + 18;
            var g4 = MakeGroup("Reliability", scroll, ref y);
            _startWithWindows = AddCheck(g4, "Start automatically when Windows logs in", _cfg.StartWithWindows, ref y);
            _autoRestartHours = AddField(g4, "Auto-restart relay every N hours (0 = never)", _cfg.AutoRestartHours.ToString(), ref y);
            _maxPdu           = AddField(g4, "Max PDU size (bytes — lower = more stable on slow links, e.g. 8192, 4096)", _cfg.MaxPdu.ToString(), ref y);
            _retryCount       = AddField(g4, "Forward retry attempts on failure (e.g. 3)", _cfg.RetryCount.ToString(), ref y);
            FinalizeGroup(g4, y);

            y = g4.Bottom + 18;
            // Save button
            var saveBtn = MakeButton("Save Settings", Accent, 130);
            saveBtn.Left = scroll.Width - 150;
            saveBtn.Top  = y;
            saveBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            saveBtn.Click += (_, _) => SaveSettings();
            scroll.Controls.Add(saveBtn);

            scroll.Resize += (_, _) =>
            {
                foreach (Control c in scroll.Controls)
                    if (c is GroupBox gb) gb.Width = scroll.ClientSize.Width - 20;
            };
        }

        private GroupBox MakeGroup(string title, Panel parent, ref int y)
        {
            var g = new GroupBox
            {
                Text      = title,
                Left      = 10,
                Top       = y,
                Width     = parent.ClientSize.Width > 0 ? parent.ClientSize.Width - 20 : 660,
                BackColor = BG,
                ForeColor = Muted,
                Font      = new Font("Segoe UI", 8f),
                AutoSize  = false,
            };
            parent.Controls.Add(g);
            return g;
        }

        private void FinalizeGroup(GroupBox g, int innerY)
        {
            g.Height = innerY + 12;
        }

        private TextBox AddField(GroupBox group, string label, string value, ref int y)
        {
            y = y == 0 ? 22 : y;
            var lbl = MakeLabel(label, 8, color: Muted);
            lbl.Location = new Point(10, y);
            group.Controls.Add(lbl);
            y += 18;

            var tb = new TextBox
            {
                Text      = value,
                Location  = new Point(10, y),
                Width     = 280,
                BackColor = Surface,
                ForeColor = TextCol,
                BorderStyle = BorderStyle.FixedSingle,
                Font      = new Font("Consolas", 9f),
            };
            group.Controls.Add(tb);
            y += 30;
            return tb;
        }

        private TextBox AddFieldWithBrowse(GroupBox group, string label, string value, ref int y, bool browseDir = false)
        {
            y = y == 0 ? 22 : y;
            var lbl = MakeLabel(label, 8, color: Muted);
            lbl.Location = new Point(10, y);
            group.Controls.Add(lbl);
            y += 18;

            var tb = new TextBox
            {
                Text      = value,
                Location  = new Point(10, y),
                Width     = 340,
                BackColor = Surface,
                ForeColor = TextCol,
                BorderStyle = BorderStyle.FixedSingle,
                Font      = new Font("Consolas", 9f),
            };
            group.Controls.Add(tb);

            var browse = new Button
            {
                Text      = "…",
                Location  = new Point(356, y - 1),
                Width     = 30,
                Height    = 22,
                BackColor = Surface,
                ForeColor = Muted,
                FlatStyle = FlatStyle.Flat,
                Font      = new Font("Segoe UI", 9f),
                Cursor    = Cursors.Hand,
            };
            browse.FlatAppearance.BorderColor = Border;
            browse.Click += (_, _) =>
            {
                if (browseDir)
                {
                    using var dlg = new FolderBrowserDialog { SelectedPath = tb.Text };
                    if (dlg.ShowDialog() == DialogResult.OK) tb.Text = dlg.SelectedPath;
                }
                else
                {
                    using var dlg = new OpenFileDialog { Filter = "EXE files|*.exe|All files|*.*" };
                    if (dlg.ShowDialog() == DialogResult.OK) tb.Text = dlg.FileName;
                }
            };
            group.Controls.Add(browse);
            y += 30;
            return tb;
        }

        private CheckBox AddCheck(GroupBox group, string label, bool value, ref int y)
        {
            y = y == 0 ? 22 : y;
            var cb = new CheckBox
            {
                Text      = label,
                Checked   = value,
                Location  = new Point(10, y),
                AutoSize  = true,
                BackColor = BG,
                ForeColor = TextCol,
                Font      = new Font("Segoe UI", 9f),
            };
            group.Controls.Add(cb);
            y += 26;
            return cb;
        }


        // ── Studies tab ───────────────────────────────────────────────────────
        private void BuildStudiesTab(TabPage page)
        {
            // Toolbar
            var toolbar = new Panel { Dock = DockStyle.Top, Height = 40, BackColor = Surface };
            page.Controls.Add(toolbar);

            _fwdSelectedBtn = MakeButton("▶ Forward Selected", Accent, 150);
            _fwdSelectedBtn.Location = new Point(8, 7);
            _fwdSelectedBtn.Click   += async (_, _) => await ForwardSelected();
            toolbar.Controls.Add(_fwdSelectedBtn);

            _viewBtn = MakeButton("👁 View Images", Color.FromArgb(40, 60, 40), 120);
            _viewBtn.ForeColor = Green;
            _viewBtn.Location  = new Point(166, 7);
            _viewBtn.Click    += (_, _) => ViewSelected();
            toolbar.Controls.Add(_viewBtn);

            _refreshBtn = MakeButton("↺ Refresh", Surface, 90);
            _refreshBtn.ForeColor = Muted;
            _refreshBtn.Location  = new Point(294, 7);
            _refreshBtn.Click    += (_, _) => RefreshStudyList();
            toolbar.Controls.Add(_refreshBtn);

            _clearFwdBtn = MakeButton("Clear Forwarded", Surface, 130);
            _clearFwdBtn.ForeColor = Muted;
            _clearFwdBtn.Anchor    = AnchorStyles.Top | AnchorStyles.Right;
            _clearFwdBtn.Location  = new Point(toolbar.Width - 145, 7);
            _clearFwdBtn.Click    += (_, _) => { _studyManager.ClearForwarded(); RefreshStudyList(); };
            toolbar.Controls.Add(_clearFwdBtn);

            toolbar.Resize += (_, _) => _clearFwdBtn.Left = toolbar.Width - 145;

            // Status label
            _studyStatusLbl = MakeLabel("", 8, color: Muted);
            _studyStatusLbl.Dock = DockStyle.Bottom;
            _studyStatusLbl.Height = 22;
            _studyStatusLbl.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;
            _studyStatusLbl.Padding = new Padding(8, 0, 0, 0);
            page.Controls.Add(_studyStatusLbl);

            // ListView
            _studyListView = new ListView
            {
                Dock          = DockStyle.Fill,
                View          = View.Details,
                FullRowSelect = true,
                MultiSelect   = true,
                BackColor     = BG,
                ForeColor     = TextCol,
                BorderStyle   = BorderStyle.None,
                Font          = new Font("Consolas", 9f),
                GridLines     = true,
            };
            _studyListView.Columns.Add("Patient",    160);
            _studyListView.Columns.Add("Modality",    70);
            _studyListView.Columns.Add("Images",      60);
            _studyListView.Columns.Add("Date",        90);
            _studyListView.Columns.Add("Accession",  120);
            _studyListView.Columns.Add("Received",   100);
            _studyListView.Columns.Add("Status",      90);
            _studyListView.DoubleClick += (_, _) => ViewSelected();
            _studyListView.SelectedIndexChanged += (_, _) => UpdateStudyButtons();
            page.Controls.Add(_studyListView);
            _studyListView.BringToFront();

            RefreshStudyList();
            UpdateStudyButtons();
        }

        private void RefreshStudyList()
        {
            if (_studyListView == null) return;
            _studyListView.Items.Clear();

            var studies = _studyManager.GetStudies();
            foreach (var s in studies)
            {
                var item = new ListViewItem(s.PatientName) { Tag = s };
                item.SubItems.Add(s.Modality);
                item.SubItems.Add(s.ImageCount.ToString());
                item.SubItems.Add(s.StudyDate);
                item.SubItems.Add(s.AccessionNumber);
                item.SubItems.Add(s.ReceivedAt.ToString("HH:mm:ss"));
                item.SubItems.Add(s.Status.ToString());

                item.ForeColor = s.Status switch
                {
                    StudyStatus.Forwarded  => Green,
                    StudyStatus.Forwarding => Yellow,
                    StudyStatus.Error      => Red,
                    _                      => TextCol,
                };
                _studyListView.Items.Add(item);
            }

            _studyStatusLbl.Text = $"  {studies.Count} studies — {studies.Count(s => s.Status == StudyStatus.Ready)} ready, {studies.Count(s => s.Status == StudyStatus.Forwarded)} forwarded";
            UpdateStudyButtons();
        }

        private void UpdateStudyButtons()
        {
            var hasSelection = _studyListView?.SelectedItems.Count > 0;
            if (_fwdSelectedBtn != null) _fwdSelectedBtn.Enabled = hasSelection;
            if (_viewBtn != null)        _viewBtn.Enabled        = _studyListView?.SelectedItems.Count == 1;
        }

        private async System.Threading.Tasks.Task ForwardSelected()
        {
            if (_studyListView.SelectedItems.Count == 0) return;

            CollectFieldsToCfg();
            var toForward = _studyListView.SelectedItems
                .Cast<ListViewItem>()
                .Select(i => (StudyInfo)i.Tag!)
                .Where(s => s.Status != StudyStatus.Forwarding)
                .ToList();

            if (toForward.Count == 0) return;

            _fwdSelectedBtn.Enabled = false;
            _studyStatusLbl.Text    = $"  Forwarding {toForward.Count} study/studies...";

            await _studyManager.ForwardStudiesAsync(toForward, _cfg);

            RefreshStudyList();
            _fwdSelectedBtn.Enabled = true;
        }

        private void ViewSelected()
        {
            if (_studyListView.SelectedItems.Count != 1) return;
            CollectFieldsToCfg();

            var study = (StudyInfo)_studyListView.SelectedItems[0].Tag!;
            var files = _studyManager.GetFilesForViewer(study);

            if (files.Count == 0)
            {
                MessageBox.Show("No DICOM files found in study folder.", "No Images",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var viewer = new DicomViewerForm(files, _cfg.DcmtkBin, study.PatientName);
            viewer.Show(this);
        }

        private void OnStudiesChanged()
        {
            if (InvokeRequired) { Invoke(OnStudiesChanged); return; }
            RefreshStudyList();
        }

        // ── Log tab ───────────────────────────────────────────────────────────
        private void BuildLogTab(TabPage page)
        {
            var toolbar = new Panel { Dock = DockStyle.Top, Height = 36, BackColor = Surface };
            page.Controls.Add(toolbar);

            var lbl = MakeLabel("Activity Log", 9, color: Muted);
            lbl.Location = new Point(10, 10);
            toolbar.Controls.Add(lbl);

            var clearBtn = MakeButton("Clear", Surface, 60);
            clearBtn.ForeColor = Muted;
            clearBtn.Anchor    = AnchorStyles.Top | AnchorStyles.Right;
            clearBtn.Location  = new Point(toolbar.Width - 140, 6);
            clearBtn.Click    += (_, _) => _logBox.Clear();
            toolbar.Controls.Add(clearBtn);

            var openBtn = MakeButton("Open Log File", Surface, 110);
            openBtn.ForeColor = Muted;
            openBtn.Anchor    = AnchorStyles.Top | AnchorStyles.Right;
            openBtn.Location  = new Point(toolbar.Width - 220, 6);
            openBtn.Click    += (_, _) =>
            {
                if (File.Exists(Logger.LogFilePath))
                    Process.Start(new ProcessStartInfo(Logger.LogFilePath) { UseShellExecute = true });
            };
            toolbar.Controls.Add(openBtn);

            toolbar.Resize += (_, _) =>
            {
                clearBtn.Left = toolbar.Width - 140;
                openBtn.Left  = toolbar.Width - 220;
            };

            _logBox = new RichTextBox
            {
                Dock      = DockStyle.Fill,
                BackColor = BG,
                ForeColor = TextCol,
                Font      = new Font("Consolas", 9f),
                ReadOnly  = true,
                BorderStyle = BorderStyle.None,
                ScrollBars = RichTextBoxScrollBars.Vertical,
            };
            page.Controls.Add(_logBox);
            _logBox.BringToFront();

            // Initial messages
            AppendLog("info", "DICOM Relay ready. Configure settings and press Start.");
        }

        // ── Commands tab ──────────────────────────────────────────────────────
        private void BuildCmdTab(TabPage page)
        {
            var lbl = MakeLabel("Generated commands based on your current settings:", 9, color: Muted);
            lbl.Location = new Point(12, 12);
            lbl.AutoSize = true;
            page.Controls.Add(lbl);

            _cmdBox = new RichTextBox
            {
                Left      = 10,
                Top       = 36,
                Width     = page.ClientSize.Width - 20,
                Height    = page.ClientSize.Height - 80,
                Anchor    = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                BackColor = Surface,
                ForeColor = Color.FromArgb(121, 192, 255),
                Font      = new Font("Consolas", 9f),
                ReadOnly  = true,
                BorderStyle = BorderStyle.FixedSingle,
                ScrollBars = RichTextBoxScrollBars.Both,
                WordWrap  = false,
            };
            page.Controls.Add(_cmdBox);

            var copyBtn = MakeButton("Copy to Clipboard", Accent, 140);
            copyBtn.Anchor   = AnchorStyles.Bottom | AnchorStyles.Right;
            copyBtn.Location = new Point(page.ClientSize.Width - 160, page.ClientSize.Height - 38);
            copyBtn.Click   += (_, _) =>
            {
                Clipboard.SetText(_cmdBox.Text);
                copyBtn.Text = "✓ Copied!";
                var t = new System.Windows.Forms.Timer { Interval = 1500 };
                t.Tick += (_, _) => { copyBtn.Text = "Copy to Clipboard"; t.Stop(); t.Dispose(); };
                t.Start();
            };
            page.Controls.Add(copyBtn);

            page.Resize += (_, _) =>
            {
                _cmdBox.Width  = page.ClientSize.Width - 20;
                _cmdBox.Height = page.ClientSize.Height - 80;
                copyBtn.Left   = page.ClientSize.Width - 160;
                copyBtn.Top    = page.ClientSize.Height - 38;
            };

            RefreshCommands();
        }

        private void RefreshCommands()
        {
            if (_cmdBox == null) return;
            CollectFieldsToCfg();

            var storescp = Path.Combine(_cfg.DcmtkBin, "storescp.exe");
            var storescu = Path.Combine(_cfg.DcmtkBin, "storescu.exe");

            var fwdCmd = $"\"{storescu}\" -aet {_cfg.FwdMyAet} -aec {_cfg.FwdAet} {_cfg.FwdHost} {_cfg.FwdPort} +sd \"#p\"";
            if (_cfg.DeleteAfterFwd) fwdCmd = $"cmd /c ({fwdCmd}) && rmdir /s /q \"#p\"";

            var args = new StringBuilder();
            args.Append($"-v -aet {_cfg.RcvAet} -od \"{_cfg.RcvDir}\"");
            args.Append($" --sort-conc-studies study --eostudy-timeout {_cfg.EosTimeout}");
            if (_cfg.AcceptAll)    args.Append(" +xa");
            if (_cfg.AutoForward) args.Append($" --exec-on-eostudy \"{fwdCmd}\"");
            args.Append($" {_cfg.RcvPort}");

            var sb = new StringBuilder();
            sb.AppendLine(":: storescp — Receiver");
            sb.AppendLine($"\"{storescp}\" {args}");
            sb.AppendLine();
            sb.AppendLine(":: storescu — Manual forward (replace <study_folder> with actual path)");
            sb.Append($"\"{storescu}\" -aet {_cfg.FwdMyAet} -aec {_cfg.FwdAet} {_cfg.FwdHost} {_cfg.FwdPort} +sd \"<study_folder>\"");

            _cmdBox.Text = sb.ToString();
        }

        // ── Actions ───────────────────────────────────────────────────────────
        private void DoStart()
        {
            CollectFieldsToCfg();
            _cfg.Save();
            _studyManager.Start(_cfg.RcvDir, _cfg.DcmtkBin);
            var (ok, err) = _relay.Start(_cfg);
            if (!ok)
                MessageBox.Show(err, "Start Failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            RefreshCommands();
        }

        private void DoStop() => _relay.Stop();

        private void SaveSettings()
        {
            CollectFieldsToCfg();
            _cfg.Save();
            RefreshCommands();
            AppendLog("info", "Settings saved.");
        }

        private void CollectFieldsToCfg()
        {
            if (_dcmtkBin == null) return;
            _cfg.DcmtkBin       = _dcmtkBin.Text;
            _cfg.RcvAet         = _rcvAet.Text;
            _cfg.RcvPort        = _rcvPort.Text;
            _cfg.RcvDir         = _rcvDir.Text;
            _cfg.RcvTimeout     = _rcvTimeout.Text;
            _cfg.EosTimeout     = _eosTimeout.Text;
            _cfg.FwdMyAet       = _fwdMyAet.Text;
            _cfg.FwdAet         = _fwdAet.Text;
            _cfg.FwdHost        = _fwdHost.Text;
            _cfg.FwdPort        = _fwdPort.Text;
            _cfg.AcceptAll      = _acceptAll.Checked;
            _cfg.AutoForward    = _autoFwd.Checked;
            _cfg.ManualMode     = _manualMode.Checked;
            _cfg.DeleteAfterFwd = _deleteAfter.Checked;
            _cfg.InstitutionName = _institutionName.Text;
            _cfg.StartWithWindows = _startWithWindows.Checked;
            if (int.TryParse(_autoRestartHours.Text, out var hrs))
                _cfg.AutoRestartHours = hrs;
            if (int.TryParse(_maxPdu.Text, out var pdu))
                _cfg.MaxPdu = pdu;
            if (int.TryParse(_retryCount.Text, out var retry))
                _cfg.RetryCount = retry;

            // Apply Windows startup registration immediately
            try { StartupHelper.SetEnabled(_cfg.StartWithWindows); }
            catch (Exception ex) { Logger.Write("warn", $"Could not update startup registration: {ex.Message}"); }
        }

        // ── Status updates ────────────────────────────────────────────────────
        private void RefreshStatus()
        {
            if (_relay.IsRunning)
            {
                _statusLbl.Text      = "● Running";
                _statusLbl.ForeColor = Green;
                _startBtn.Enabled    = false;
                _stopBtn.Enabled     = true;
            }
            else
            {
                _statusLbl.Text      = "● Stopped";
                _statusLbl.ForeColor = Muted;
                _uptimeLbl.Text      = "";
                _startBtn.Enabled    = true;
                _stopBtn.Enabled     = false;
            }
        }

        private void OnStatusChanged(bool running)
        {
            if (InvokeRequired) { Invoke(() => OnStatusChanged(running)); return; }
            RefreshStatus();
        }

        private void OnLogMessage(string level, string msg)
        {
            if (InvokeRequired) { Invoke(() => OnLogMessage(level, msg)); return; }
            AppendLog(level, msg);
        }

        private void AppendLog(string level, string msg)
        {
            var ts = DateTime.Now.ToString("HH:mm:ss");
            _logBox.AppendText($"{ts} ");

            var tag = $"[{level.ToUpper(),-7}] ";
            var color = level switch
            {
                "error"   => Red,
                "warn"    => Yellow,
                "warning" => Yellow,
                "ok"      => Green,
                "debug"   => Muted,
                _         => Color.FromArgb(121, 192, 255),
            };

            int start = _logBox.TextLength;
            _logBox.AppendText(tag);
            _logBox.Select(start, tag.Length);
            _logBox.SelectionColor = color;

            _logBox.SelectionStart  = _logBox.TextLength;
            _logBox.SelectionLength = 0;
            _logBox.SelectionColor  = TextCol;
            _logBox.AppendText($"{msg}\n");
            _logBox.ScrollToCaret();
        }

        // ── Helpers ───────────────────────────────────────────────────────────
        private static Label MakeLabel(string text, float size, bool bold = false, Color? color = null)
        {
            return new Label
            {
                Text      = text,
                AutoSize  = true,
                BackColor = Color.Transparent,
                ForeColor = color ?? Color.FromArgb(230, 237, 243),
                Font      = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular),
            };
        }

        private static Button MakeButton(string text, Color back, int width)
        {
            var b = new Button
            {
                Text      = text,
                Width     = width,
                Height    = 28,
                BackColor = back,
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font      = new Font("Segoe UI", 9f, FontStyle.Bold),
                Cursor    = Cursors.Hand,
            };
            b.FlatAppearance.BorderSize = 0;
            return b;
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _relay.StatusChanged         -= OnStatusChanged;
            _relay.LogMessage            -= OnLogMessage;
            _studyManager.StudiesChanged -= OnStudiesChanged;
            _studyManager.LogMessage     -= OnLogMessage;
            _uptimeTimer.Stop();
            _uptimeTimer.Dispose();
            base.OnFormClosed(e);
        }
    }
}
