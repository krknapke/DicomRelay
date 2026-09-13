using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DicomRelay
{
    /// <summary>
    /// Simple DICOM image viewer. Uses dcm2pnm.exe from DCMTK to convert
    /// each frame to a temporary PNG, then displays it in a PictureBox.
    /// </summary>
    internal class DicomViewerForm : Form
    {
        private readonly List<string> _files;
        private readonly string _dcmtkBin;
        private readonly string _patientName;
        private int _currentIndex;

        // UI
        private PictureBox  _pictureBox = null!;
        private Label       _infoLabel  = null!;
        private Label       _loadLabel  = null!;
        private Button      _prevBtn    = null!;
        private Button      _nextBtn    = null!;
        private TrackBar    _slider     = null!;
        private string?     _tempDir;

        // Colors
        private static readonly Color BG      = Color.FromArgb(13, 17, 23);
        private static readonly Color Surface = Color.FromArgb(22, 27, 34);
        private static readonly Color TextCol = Color.FromArgb(230, 237, 243);
        private static readonly Color Muted   = Color.FromArgb(139, 148, 158);
        private static readonly Color Accent  = Color.FromArgb(31, 111, 235);

        public DicomViewerForm(List<string> files, string dcmtkBin, string patientName)
        {
            _files       = files;
            _dcmtkBin    = dcmtkBin;
            _patientName = patientName;
            _tempDir     = Path.Combine(Path.GetTempPath(), $"dicomrelay_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_tempDir);

            InitForm();
            BuildUI();

            if (_files.Count > 0)
                _ = LoadImageAsync(0);
        }

        private void InitForm()
        {
            Text            = $"DICOM Viewer — {_patientName}";
            Size            = new Size(900, 720);
            MinimumSize     = new Size(600, 500);
            BackColor       = BG;
            ForeColor       = TextCol;
            FormBorderStyle = FormBorderStyle.Sizable;
            StartPosition   = FormStartPosition.CenterScreen;
            KeyPreview      = true;
            KeyDown += OnKeyDown;
        }

        private void BuildUI()
        {
            // ── Top info bar ──
            var topBar = new Panel { Dock = DockStyle.Top, Height = 36, BackColor = Surface };
            Controls.Add(topBar);

            _infoLabel = new Label
            {
                AutoSize  = true,
                ForeColor = TextCol,
                BackColor = Color.Transparent,
                Font      = new Font("Consolas", 9f),
                Location  = new Point(10, 10),
            };
            topBar.Controls.Add(_infoLabel);

            // ── Image area ──
            _pictureBox = new PictureBox
            {
                Dock        = DockStyle.Fill,
                BackColor   = Color.Black,
                SizeMode    = PictureBoxSizeMode.Zoom,
            };
            Controls.Add(_pictureBox);

            _loadLabel = new Label
            {
                Text      = "Loading...",
                AutoSize  = false,
                Dock      = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                BackColor = Color.Black,
                ForeColor = Muted,
                Font      = new Font("Segoe UI", 14f),
                Visible   = false,
            };
            Controls.Add(_loadLabel);
            _loadLabel.BringToFront();

            // ── Bottom nav bar ──
            var navBar = new Panel { Dock = DockStyle.Bottom, Height = 48, BackColor = Surface };
            Controls.Add(navBar);

            _prevBtn = MakeBtn("◀ Prev", 90);
            _prevBtn.Location = new Point(8, 10);
            _prevBtn.Click   += (_, _) => Navigate(-1);
            navBar.Controls.Add(_prevBtn);

            _nextBtn = MakeBtn("Next ▶", 90);
            _nextBtn.Location = new Point(104, 10);
            _nextBtn.Click   += (_, _) => Navigate(1);
            navBar.Controls.Add(_nextBtn);

            _slider = new TrackBar
            {
                Minimum   = 0,
                Maximum   = Math.Max(_files.Count - 1, 0),
                Value     = 0,
                TickStyle = TickStyle.None,
                Height    = 30,
                Location  = new Point(210, 9),
                Width     = 400,
                BackColor = Surface,
            };
            _slider.Scroll += (_, _) => _ = LoadImageAsync(_slider.Value);
            navBar.Controls.Add(_slider);

            // Fit slider on resize
            navBar.Resize += (_, _) =>
            {
                _slider.Width = navBar.Width - 220 - 10;
            };

            UpdateNav();
        }

        private async Task LoadImageAsync(int index)
        {
            if (index < 0 || index >= _files.Count) return;
            _currentIndex = index;

            _loadLabel.Visible   = true;
            _loadLabel.Text      = $"Converting image {index + 1} of {_files.Count}...";
            _pictureBox.Image    = null;

            UpdateNav();

            var pngPath = Path.Combine(_tempDir!, $"frame_{index:D5}.png");

            if (!File.Exists(pngPath))
            {
                var success = await Task.Run(() => ConvertToPng(_files[index], pngPath));
                if (!success)
                {
                    _loadLabel.Text = "Could not convert image.\n(dcm2pnm.exe required in DCMTK bin folder)";
                    return;
                }
            }

            try
            {
                // Load from file (not stream) so we can delete later
                using var tmp = new Bitmap(pngPath);
                _pictureBox.Image = new Bitmap(tmp);
                _loadLabel.Visible = false;
            }
            catch (Exception ex)
            {
                _loadLabel.Text = $"Display error: {ex.Message}";
            }
        }

        private bool ConvertToPng(string dcmFile, string outPng)
        {
            try
            {
                var dcm2pnm = Path.Combine(_dcmtkBin, "dcm2pnm.exe");
                if (!File.Exists(dcm2pnm)) dcm2pnm = Path.Combine(_dcmtkBin, "dcm2pnm");
                if (!File.Exists(dcm2pnm)) return false;

                var psi = new ProcessStartInfo
                {
                    FileName               = dcm2pnm,
                    Arguments              = $"+op \"{dcmFile}\" \"{outPng}\"",
                    UseShellExecute        = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    CreateNoWindow         = true,
                };
                using var p = Process.Start(psi);
                p?.WaitForExit(15000);
                return File.Exists(outPng);
            }
            catch { return false; }
        }

        private void Navigate(int delta)
        {
            var next = Math.Clamp(_currentIndex + delta, 0, _files.Count - 1);
            if (next != _currentIndex)
            {
                _slider.Value = next;
                _ = LoadImageAsync(next);
            }
        }

        private void UpdateNav()
        {
            _infoLabel.Text = _files.Count == 0
                ? "No images"
                : $"{_patientName}   Image {_currentIndex + 1} / {_files.Count}";

            _prevBtn.Enabled = _currentIndex > 0;
            _nextBtn.Enabled = _currentIndex < _files.Count - 1;
            if (_slider.Maximum != _files.Count - 1)
                _slider.Maximum = Math.Max(_files.Count - 1, 0);
        }

        private void OnKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Left  || e.KeyCode == Keys.Up)   Navigate(-1);
            if (e.KeyCode == Keys.Right || e.KeyCode == Keys.Down)  Navigate(1);
        }

        private static Button MakeBtn(string text, int width)
        {
            var b = new Button
            {
                Text      = text,
                Width     = width,
                Height    = 28,
                BackColor = Color.FromArgb(31, 111, 235),
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
            _pictureBox.Image?.Dispose();
            // Clean up temp files
            try { if (_tempDir != null) Directory.Delete(_tempDir, recursive: true); } catch { }
            base.OnFormClosed(e);
        }
    }
}
