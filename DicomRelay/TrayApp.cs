using System;
using System.Drawing;
using System.Windows.Forms;

namespace DicomRelay
{
    /// <summary>
    /// Manages the system tray icon and context menu.
    /// </summary>
    internal class TrayApp : IDisposable
    {
        private readonly NotifyIcon _trayIcon;
        private readonly RelayManager _relay;
        private readonly StudyManager _studyManager;
        private SettingsForm? _settingsForm;
        private readonly System.Windows.Forms.Timer _restartTimer;
        private DateTime _lastRestart = DateTime.Now;

        public TrayApp()
        {
            _relay        = new RelayManager();
            _studyManager = new StudyManager();
            _relay.StatusChanged += OnRelayStatusChanged;

            _trayIcon = new NotifyIcon
            {
                Icon = IconHelper.MakeStopped(),
                Text = "DICOM Relay — Stopped",
                Visible = true,
                ContextMenuStrip = BuildMenu()
            };

            _trayIcon.DoubleClick += (_, _) => OpenSettings();
            _trayIcon.MouseClick += (_, e) =>
            {
                if (e.Button == MouseButtons.Left) OpenSettings();
            };

            // Periodic check (every minute) for scheduled restart
            _restartTimer = new System.Windows.Forms.Timer { Interval = 60_000 };
            _restartTimer.Tick += (_, _) => CheckScheduledRestart();
            _restartTimer.Start();

            // Open settings window on first launch
            OpenSettings();

            // Auto-start the relay so the server doesn't need a human to click Start
            StartRelay();
        }

        private ContextMenuStrip BuildMenu()
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add("Open Settings / Log", null, (_, _) => OpenSettings());
            menu.Items.Add(new ToolStripSeparator());

            var startItem = new ToolStripMenuItem("Start Relay");
            startItem.Click += (_, _) => StartRelay();
            menu.Items.Add(startItem);

            var stopItem = new ToolStripMenuItem("Stop Relay");
            stopItem.Click += (_, _) => StopRelay();
            menu.Items.Add(stopItem);

            var restartItem = new ToolStripMenuItem("Restart Relay Now");
            restartItem.Click += (_, _) => RestartRelay();
            menu.Items.Add(restartItem);

            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Quit", null, (_, _) => QuitApp());

            return menu;
        }

        private void OpenSettings()
        {
            if (_settingsForm != null && !_settingsForm.IsDisposed)
            {
                if (!_settingsForm.Visible)
                    _settingsForm.Show();
                _settingsForm.WindowState = FormWindowState.Normal;
                _settingsForm.BringToFront();
                _settingsForm.Activate();
                return;
            }

            _settingsForm = new SettingsForm(_relay, _studyManager);
            _settingsForm.Show();
        }

        private void StartRelay()
        {
            var cfg = Config.Load();
            _studyManager.Start(cfg.RcvDir, cfg.DcmtkBin);
            var (ok, err) = _relay.Start(cfg);
            if (!ok)
                Logger.Write("warn", $"Auto-start did not start relay: {err}");
            _lastRestart = DateTime.Now;
        }

        private void StopRelay() => _relay.Stop();

        private void RestartRelay()
        {
            Logger.Write("info", "Restarting relay (scheduled or manual)...");
            _relay.Stop();
            System.Threading.Thread.Sleep(1000);
            StartRelay();
        }

        private void CheckScheduledRestart()
        {
            var cfg = Config.Load();
            if (cfg.AutoRestartHours <= 0) return;
            if (!_relay.IsRunning) return;

            var elapsedHours = (DateTime.Now - _lastRestart).TotalHours;
            if (elapsedHours >= cfg.AutoRestartHours)
            {
                Logger.Write("info", $"Auto-restart interval reached ({cfg.AutoRestartHours}h). Restarting relay.");
                RestartRelay();
            }
        }

        private void QuitApp()
        {
            _relay.Stop();
            _trayIcon.Visible = false;
            Application.Exit();
        }

        private void OnRelayStatusChanged(bool running)
        {
            _trayIcon.Icon = running ? IconHelper.MakeRunning() : IconHelper.MakeStopped();
            _trayIcon.Text = running
                ? $"DICOM Relay — Running ({_relay.Uptime})"
                : "DICOM Relay — Stopped";
        }

        public void Dispose()
        {
            _restartTimer.Stop();
            _restartTimer.Dispose();
            _relay.Stop();
            _studyManager.Dispose();
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _relay.Dispose();
        }
    }
}

