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
        private CheckBox _startWithWindows = null!;
        private TextBox  _autoRestartHours = null!;
        private TextBox  _maxPdu           = null!;
        private TextBox  _retryCount       = null!;

        // Tag overrides controls
        private ComboBox _tagPresetCombo   = null!;
        private TextBox  _tagHexBox        = null!;
        private TextBox  _tagNameBox       = null!;
        private TextBox  _tagValBox        = null!;
        private Button   _addTagBtn        = null!;
        private Button   _removeTagBtn     = null!;
        private ListView _tagListView       = null!;

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

        // Common DICOM tag presets for quick selection
        private static readonly (string tag, string name)[] PresetTags = new[]
        {
            ("(0008,0080)", "Institution Name"),
            ("(0008,0081)", "Institution Address"),
            ("(0008,1040)", "Institutional Dept Name"),
            ("(0008,1010)", "Station Name"),
            ("(0008,1030)", "Study Description"),
            ("(0008,103E)", "Series Description"),
            ("(0008,0070)", "Manufacturer"),
            ("(0008,1090)", "Manufacturer Model Name"),
            ("(0008,0090)", "Referring Physician Name"),
            ("(0008,1050)", "Performing Physician Name"),
            ("(0008,1060)", "Reading Physician Name"),
            ("(0008,1070)", "Operators' Name"),
            ("(0018,1000)", "Device Serial Number"),
            ("(0018,1020)", "Software Versions"),
            ("(CUSTOM)",    "Custom Tag...")
        };

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
            Size            = new Size(760, 720);
            MinimumSize     = new Size(720, 560);
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

            int y = 12;

            // ── Group 0: DCMTK Configuration ──
            var g0 = MakeGroup("DCMTK Configuration", scroll, ref y);
            _dcmtkBin = AddFieldWithBrowse(g0, "DCMTK Bin Directory", _cfg.DcmtkBin, 14, 20, 680, browseDir: true);
            FinalizeGroup(g0, 70);
            y = g0.Bottom + 12;

            // ── Group 1: Receiver (SCP — storescp) ──
            var g1 = MakeGroup("Receiver (SCP — storescp)   ←   Ultrasound sends here", scroll, ref y);
            _rcvAet     = AddColField(g1, "AE Title",                _cfg.RcvAet,     14,  20, 320);
            _rcvPort    = AddColField(g1, "Listen Port",             _cfg.RcvPort,    350, 20, 320);
            _rcvTimeout = AddColField(g1, "Association Timeout (s)", _cfg.RcvTimeout, 14,  66, 320);
            _eosTimeout = AddColField(g1, "End-of-Study Timeout (s)", _cfg.EosTimeout, 350, 66, 320);
            _rcvDir     = AddFieldWithBrowse(g1, "Storage Directory", _cfg.RcvDir, 14, 112, 680, browseDir: true);
            FinalizeGroup(g1, 164);
            y = g1.Bottom + 12;

            // ── Group 2: Forwarder (SCU — storescu) ──
            var g2 = MakeGroup("Forwarder (SCU — storescu)   →   WebPACS", scroll, ref y);
            _fwdMyAet = AddColField(g2, "My AE Title (Calling)", _cfg.FwdMyAet, 14,  20, 320);
            _fwdAet   = AddColField(g2, "WebPACS AE Title",      _cfg.FwdAet,   350, 20, 320);
            _fwdHost  = AddColField(g2, "WebPACS Host / IP",     _cfg.FwdHost,  14,  66, 320);
            _fwdPort  = AddColField(g2, "WebPACS Port",          _cfg.FwdPort,  350, 66, 320);
            FinalizeGroup(g2, 118);
            y = g2.Bottom + 12;

            // ── Group 3: Options & Reliability ──
            var g3 = MakeGroup("Options & Reliability", scroll, ref y);
            _autoFwd          = AddCheckAt(g3, "Auto-forward on study complete",              _cfg.AutoForward,      14,  22);
            _manualMode       = AddCheckAt(g3, "Manual mode (queue studies for review)",      _cfg.ManualMode,       350, 22);
            _acceptAll        = AddCheckAt(g3, "Accept all transfer syntaxes (+xa)",          _cfg.AcceptAll,        14,  48);
            _deleteAfter      = AddCheckAt(g3, "Delete local files after successful forward", _cfg.DeleteAfterFwd, 350, 48);
            _startWithWindows = AddCheckAt(g3, "Start automatically when Windows logs in",    _cfg.StartWithWindows, 14,  74);

            _autoRestartHours = AddColField(g3, "Auto-restart relay every N hours (0 = never)", _cfg.AutoRestartHours.ToString(), 14,  102, 320);
            _retryCount       = AddColField(g3, "Forward retry attempts on failure (e.g. 3)",   _cfg.RetryCount.ToString(),       350, 102, 320);
            _maxPdu           = AddColField(g3, "Max PDU size in bytes (e.g. 16384, 8192)",     _cfg.MaxPdu.ToString(),           14,  148, 320);
            FinalizeGroup(g3, 200);
            y = g3.Bottom + 12;

            // ── Group 4: DICOM Tag Overrides ──
            var g4 = MakeGroup("DICOM Tag Overrides (Modified before forwarding)", scroll, ref y);

            var tagNote = MakeLabel("Add DICOM tags to modify or insert before sending to PACS (e.g., Institution Name, Station Name, Study Description):", 8, color: Muted);
            tagNote.Location = new Point(14, 20);
            g4.Controls.Add(tagNote);

            // Row 1 of Tag input: Preset picker, Tag Hex, Tag Name
            var lblPreset = MakeLabel("Preset Tag", 8, color: Muted);
            lblPreset.Location = new Point(14, 42);
            g4.Controls.Add(lblPreset);

            _tagPresetCombo = new ComboBox
            {
                Location      = new Point(14, 59),
                Width         = 195,
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor     = Surface,
                ForeColor     = TextCol,
                FlatStyle     = FlatStyle.Flat,
                Font          = new Font("Segoe UI", 9f),
            };
            foreach (var preset in PresetTags)
                _tagPresetCombo.Items.Add($"{preset.tag} {preset.name}");
            _tagPresetCombo.SelectedIndex = 0;
            _tagPresetCombo.SelectedIndexChanged += (_, _) => OnTagPresetChanged();
            g4.Controls.Add(_tagPresetCombo);

            var lblHex = MakeLabel("Tag Hex (GGGG,EEEE)", 8, color: Muted);
            lblHex.Location = new Point(219, 42);
            g4.Controls.Add(lblHex);

            _tagHexBox = new TextBox
            {
                Text        = PresetTags[0].tag,
                Location    = new Point(219, 59),
                Width       = 120,
                BackColor   = Surface,
                ForeColor   = TextCol,
                BorderStyle = BorderStyle.FixedSingle,
                Font        = new Font("Consolas", 9f),
                ReadOnly    = true,
            };
            g4.Controls.Add(_tagHexBox);

            var lblName = MakeLabel("Description", 8, color: Muted);
            lblName.Location = new Point(349, 42);
            g4.Controls.Add(lblName);

            _tagNameBox = new TextBox
            {
                Text        = PresetTags[0].name,
                Location    = new Point(349, 59),
                Width       = 330,
                BackColor   = Surface,
                ForeColor   = TextCol,
                BorderStyle = BorderStyle.FixedSingle,
                Font        = new Font("Segoe UI", 9f),
                ReadOnly    = true,
            };
            g4.Controls.Add(_tagNameBox);

            // Row 2: Replacement Value and + Add Tag button
            var lblVal = MakeLabel("Replacement Value", 8, color: Muted);
            lblVal.Location = new Point(14, 90);
            g4.Controls.Add(lblVal);

            _tagValBox = new TextBox
            {
                Location    = new Point(14, 107),
                Width       = 530,
                BackColor   = Surface,
                ForeColor   = TextCol,
                BorderStyle = BorderStyle.FixedSingle,
                Font        = new Font("Consolas", 9f),
            };
            _tagValBox.KeyDown += (_, e) =>
            {
                if (e.KeyCode == Keys.Enter)
                {
                    e.SuppressKeyPress = true;
                    AddTagOverride();
                }
            };
            g4.Controls.Add(_tagValBox);

            _addTagBtn = MakeButton("+ Add / Update Tag", Accent, 140);
            _addTagBtn.Location = new Point(554, 106);
            _addTagBtn.Height   = 24;
            _addTagBtn.Font     = new Font("Segoe UI", 8.5f, FontStyle.Bold);
            _addTagBtn.Click   += (_, _) => AddTagOverride();
            g4.Controls.Add(_addTagBtn);

            // Row 3: Tag list
            _tagListView = new ListView
            {
                Location      = new Point(14, 138),
                Width         = 680,
                Height        = 115,
                View          = View.Details,
                FullRowSelect = true,
                GridLines     = false,
                BackColor     = Surface,
                ForeColor     = TextCol,
                BorderStyle   = BorderStyle.FixedSingle,
                Font          = new Font("Consolas", 8.5f),
            };
            _tagListView.Columns.Add("Tag", 110);
            _tagListView.Columns.Add("Description", 200);
            _tagListView.Columns.Add("Replacement Value", 350);
            g4.Controls.Add(_tagListView);

            // Row 4: Action buttons for tags
            _removeTagBtn = MakeButton("Remove Selected", Surface, 130);
            _removeTagBtn.ForeColor = Red;
            _removeTagBtn.Location  = new Point(14, 260);
            _removeTagBtn.Height    = 25;
            _removeTagBtn.Font      = new Font("Segoe UI", 8.5f);
            _removeTagBtn.Click    += (_, _) => RemoveSelectedTag();
            g4.Controls.Add(_removeTagBtn);

            var clearTagsBtn = MakeButton("Clear All Overrides", Surface, 140);
            clearTagsBtn.ForeColor = Muted;
            clearTagsBtn.Location  = new Point(152, 260);
            clearTagsBtn.Height    = 25;
            clearTagsBtn.Font      = new Font("Segoe UI", 8.5f);
            clearTagsBtn.Click    += (_, _) => ClearAllTags();
            g4.Controls.Add(clearTagsBtn);

            FinalizeGroup(g4, 296);
            y = g4.Bottom + 16;

            PopulateTagListView();

            // Save button
            var saveBtn = MakeButton("Save Settings", Accent, 140);
            saveBtn.Height = 32;
            saveBtn.Left   = 554;
            saveBtn.Top    = y;
            saveBtn.Click += (_, _) => SaveSettings();
            scroll.Controls.Add(saveBtn);

            // Dynamic layout adjustment on scroll resize
            scroll.Resize += (_, _) =>
            {
                int w = scroll.ClientSize.Width;
                if (w < 600) return;
                int groupW = w - 28;
                int innerW = groupW - 28;
                int colW = (innerW - 16) / 2;
                int col2X = 14 + colW + 16;

                // Adjust Groups
                g0.Width = groupW;
                g1.Width = groupW;
                g2.Width = groupW;
                g3.Width = groupW;
                g4.Width = groupW;

                // DCMTK
                ResizeBrowseField(g0, _dcmtkBin, 14, innerW);

                // Receiver
                ResizeColField(_rcvAet, 14, colW);
                ResizeColField(_rcvPort, col2X, colW);
                ResizeColField(_rcvTimeout, 14, colW);
                ResizeColField(_eosTimeout, col2X, colW);
                ResizeBrowseField(g1, _rcvDir, 14, innerW);

                // Forwarder
                ResizeColField(_fwdMyAet, 14, colW);
                ResizeColField(_fwdAet, col2X, colW);
                ResizeColField(_fwdHost, 14, colW);
                ResizeColField(_fwdPort, col2X, colW);

                // Options & Reliability
                ResizeColField(_autoRestartHours, 14, colW);
                ResizeColField(_retryCount, col2X, colW);
                ResizeColField(_maxPdu, 14, colW);

                // Tag overrides
                _tagNameBox.Width = Math.Max(120, innerW - 349);
                _tagValBox.Width  = Math.Max(200, innerW - 156);
                _addTagBtn.Left   = 14 + _tagValBox.Width + 10;
                _tagListView.Width = innerW;
                if (_tagListView.Columns.Count >= 3)
                    _tagListView.Columns[2].Width = Math.Max(150, innerW - 320);

                // Save button
                saveBtn.Left = groupW - saveBtn.Width + 14;
            };
        }

        private GroupBox MakeGroup(string title, Panel parent, ref int y)
        {
            var g = new GroupBox
            {
                Text      = title,
                Left      = 14,
                Top       = y,
                Width     = parent.ClientSize.Width > 0 ? parent.ClientSize.Width - 28 : 680,
                BackColor = BG,
                ForeColor = Muted,
                Font      = new Font("Segoe UI", 8.5f, FontStyle.Bold),
                AutoSize  = false,
            };
            parent.Controls.Add(g);
            return g;
        }

        private static void FinalizeGroup(GroupBox g, int height)
        {
            g.Height = height;
        }

        private static TextBox AddColField(GroupBox group, string label, string value, int x, int y, int width)
        {
            var lbl = MakeLabel(label, 8, color: Muted);
            lbl.Location = new Point(x, y);
            group.Controls.Add(lbl);

            var tb = new TextBox
            {
                Text        = value,
                Location    = new Point(x, y + 17),
                Width       = width,
                BackColor   = Surface,
                ForeColor   = TextCol,
                BorderStyle = BorderStyle.FixedSingle,
                Font        = new Font("Consolas", 9f),
            };
            group.Controls.Add(tb);
            return tb;
        }

        private static TextBox AddFieldWithBrowse(GroupBox group, string label, string value, int x, int y, int width, bool browseDir = false)
        {
            var lbl = MakeLabel(label, 8, color: Muted);
            lbl.Location = new Point(x, y);
            group.Controls.Add(lbl);

            var tb = new TextBox
            {
                Text        = value,
                Location    = new Point(x, y + 17),
                Width       = width - 40,
                BackColor   = Surface,
                ForeColor   = TextCol,
                BorderStyle = BorderStyle.FixedSingle,
                Font        = new Font("Consolas", 9f),
            };
            group.Controls.Add(tb);

            var browse = new Button
            {
                Text      = "…",
                Location  = new Point(x + width - 36, y + 16),
                Width     = 34,
                Height    = 23,
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
            return tb;
        }

        private static CheckBox AddCheckAt(GroupBox group, string label, bool value, int x, int y)
        {
            var cb = new CheckBox
            {
                Text      = label,
                Checked   = value,
                Location  = new Point(x, y),
                AutoSize  = true,
                BackColor = BG,
                ForeColor = TextCol,
                Font      = new Font("Segoe UI", 9f),
            };
            group.Controls.Add(cb);
            return cb;
        }

        private static void ResizeColField(TextBox tb, int x, int width)
        {
            if (tb == null) return;
            tb.Left  = x;
            tb.Width = width;
        }

        private static void ResizeBrowseField(GroupBox g, TextBox tb, int x, int fullWidth)
        {
            if (tb == null) return;
            tb.Left  = x;
            tb.Width = fullWidth - 40;
            foreach (Control c in g.Controls)
            {
                if (c is Button b && b.Text == "…")
                    b.Left = x + fullWidth - 36;
            }
        }

        private void OnTagPresetChanged()
        {
            int idx = _tagPresetCombo.SelectedIndex;
            if (idx < 0 || idx >= PresetTags.Length) return;

            var (tag, name) = PresetTags[idx];
            if (tag == "(CUSTOM)")
            {
                _tagHexBox.Text = "(0000,0000)";
                _tagNameBox.Text = "Custom Tag";
                _tagHexBox.ReadOnly = false;
                _tagNameBox.ReadOnly = false;
                _tagHexBox.Focus();
                _tagHexBox.SelectAll();
            }
            else
            {
                _tagHexBox.Text = tag;
                _tagNameBox.Text = name;
                _tagHexBox.ReadOnly = true;
                _tagNameBox.ReadOnly = true;
                _tagValBox.Focus();
            }
        }

        private void AddTagOverride()
        {
            var rawTag = _tagHexBox.Text.Trim().ToUpperInvariant();
            if (!rawTag.StartsWith("(") && !rawTag.EndsWith(")"))
            {
                var parts = rawTag.Split(new[] { ',', ' ', ':' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2 && parts[0].Length == 4 && parts[1].Length == 4)
                    rawTag = $"({parts[0]},{parts[1]})";
            }

            if (!System.Text.RegularExpressions.Regex.IsMatch(rawTag, @"^\([0-9A-F]{4},[0-9A-F]{4}\)$"))
            {
                MessageBox.Show("Tag must be in standard hex format: (GGGG,EEEE) e.g. (0008,0080)", "Invalid Tag", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                _tagHexBox.Focus();
                return;
            }

            var name = string.IsNullOrWhiteSpace(_tagNameBox.Text) ? "Custom Tag" : _tagNameBox.Text.Trim();
            var val = _tagValBox.Text;

            var existing = _cfg.TagOverrides.FirstOrDefault(t => t.Tag.Equals(rawTag, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                existing.Name = name;
                existing.Value = val;
            }
            else
            {
                _cfg.TagOverrides.Add(new DicomTagOverride { Tag = rawTag, Name = name, Value = val });
            }

            PopulateTagListView();
            _tagValBox.Clear();
            RefreshCommands();
        }

        private void RemoveSelectedTag()
        {
            if (_tagListView.SelectedItems.Count == 0) return;
            var selected = _tagListView.SelectedItems[0];
            if (selected.Tag is DicomTagOverride tagOverride)
            {
                _cfg.TagOverrides.Remove(tagOverride);
                PopulateTagListView();
                RefreshCommands();
            }
        }

        private void ClearAllTags()
        {
            if (_cfg.TagOverrides.Count == 0) return;
            if (MessageBox.Show("Remove all configured tag overrides?", "Clear Tags", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                _cfg.TagOverrides.Clear();
                PopulateTagListView();
                RefreshCommands();
            }
        }

        private void PopulateTagListView()
        {
            _tagListView.Items.Clear();
            foreach (var tag in _cfg.TagOverrides)
            {
                var item = new ListViewItem(tag.Tag);
                item.SubItems.Add(tag.Name);
                item.SubItems.Add(tag.Value);
                item.Tag = tag;
                _tagListView.Items.Add(item);
            }
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
            var dcmodify = Path.Combine(_cfg.DcmtkBin, "dcmodify.exe");

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
            if (_cfg.TagOverrides.Count > 0)
            {
                sb.AppendLine(":: dcmodify — Tag overrides applied before forward");
                var tagArgs = string.Join(" ", _cfg.TagOverrides.Select(t => $"-nb -i \"{t.Tag}={t.Value}\""));
                sb.AppendLine($"\"{dcmodify}\" {tagArgs} \"<dicom_file>\"");
                sb.AppendLine();
            }
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
            _cfg.StartWithWindows = _startWithWindows.Checked;
            if (int.TryParse(_autoRestartHours.Text, out var hrs))
                _cfg.AutoRestartHours = hrs;
            if (int.TryParse(_maxPdu.Text, out var pdu))
                _cfg.MaxPdu = pdu;
            if (int.TryParse(_retryCount.Text, out var retry))
                _cfg.RetryCount = retry;

            // Sync legacy InstitutionName property
            _cfg.InstitutionName = _cfg.TagOverrides.FirstOrDefault(t => t.Tag.Equals("(0008,0080)", StringComparison.OrdinalIgnoreCase))?.Value ?? "";

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
