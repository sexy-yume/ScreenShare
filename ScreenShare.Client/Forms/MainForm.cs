using System;
using System.Drawing;
using System.Windows.Forms;
using System.Diagnostics;
using ScreenShare.Client.Capture;
using ScreenShare.Client.Encoder;
using ScreenShare.Client.Network;
using ScreenShare.Common.Models;
using ScreenShare.Common.Settings;
using ScreenShare.Common.Utils;

namespace ScreenShare.Client.Forms
{
    public partial class MainForm : Form
    {
        private ClientSettings _settings;
        private OptimizedScreenCapture _screenCapture;
        private FFmpegEncoder _encoder;
        private NetworkClient _networkClient;
        private System.Windows.Forms.Timer _networkUpdateTimer;
        private System.Windows.Forms.Timer _statusUpdateTimer;
        private readonly object _syncLock = new object();

        // Performance tracking
        private PerformanceMetrics _currentMetrics = new PerformanceMetrics();
        private bool _isPerformanceInfoVisible = false;

        // UI components for status display
        private StatusStrip _statusStrip;
        private ToolStripStatusLabel _statusLabel;
        private ToolStripStatusLabel _fpsLabel;
        private ToolStripStatusLabel _bitrateLabel;
        private ToolStripStatusLabel _pingLabel;

        public MainForm()
        {
            InitializeComponent();

            try
            {
                // Initialize EnhancedLogger instead of FileLogger
                EnhancedLogger.Instance.SetLogLevels(
                    EnhancedLogger.LogLevel.Info,  // Console level
                    EnhancedLogger.LogLevel.Debug  // File level
                );

                // Load settings
                _settings = ClientSettings.Load();

                lblStatus.Text = "Status: Initializing...";
                EnhancedLogger.Instance.Info("Main form initialization starting");

                // Initialize components with enhanced status UI
                InitializeStatusUI();

                // Get screen size
                var screenSize = Screen.PrimaryScreen.Bounds;

                // Initialize network client
                _networkClient = new NetworkClient(_settings);
                _networkClient.RemoteControlStatusChanged += OnRemoteControlStatusChanged;
                _networkClient.RemoteControlReceived += OnRemoteControlReceived;
                _networkClient.ConnectionStatusChanged += OnConnectionStatusChanged;
                _networkClient.PerformanceUpdated += OnPerformanceUpdated;

                // Initialize encoder
                _encoder = new FFmpegEncoder(screenSize.Width, screenSize.Height, _settings.LowResQuality);
                _encoder.FrameEncoded += OnFrameEncoded;

                // Initialize screen capture
                _screenCapture = new OptimizedScreenCapture();
                _screenCapture.Fps = _settings.LowResFps;
                _screenCapture.Quality = _settings.LowResQuality;
                _screenCapture.FrameCaptured += OnFrameCaptured;

                // Network update timer
                _networkUpdateTimer = new System.Windows.Forms.Timer();
                _networkUpdateTimer.Interval = 15;
                _networkUpdateTimer.Tick += (s, e) => _networkClient.Update();
                _networkUpdateTimer.Start();

                // Status update timer
                _statusUpdateTimer = new System.Windows.Forms.Timer();
                _statusUpdateTimer.Interval = 1000; // Update status every second
                _statusUpdateTimer.Tick += OnStatusUpdateTick;
                _statusUpdateTimer.Start();

                // Start services
                lblStatus.Text = "Status: Connecting to server...";
                _networkClient.Start();
                _screenCapture.Start();

                FormClosing += (s, e) => Cleanup();

                // Register keyboard shortcuts
                KeyPreview = true;
                KeyDown += OnKeyDown;

                EnhancedLogger.Instance.Info("Main form initialization complete");
            }
            catch (Exception ex)
            {
                EnhancedLogger.Instance.Error("Main form initialization error", ex);
                MessageBox.Show($"Initialization error: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Close();
            }
        }

        private void InitializeStatusUI()
        {
            // Set up status strip
            _statusStrip = new StatusStrip();
            _statusStrip.SizingGrip = false;

            // Create status labels
            _statusLabel = new ToolStripStatusLabel("Initializing...");
            _statusLabel.AutoSize = true;
            _statusLabel.Spring = true;
            _statusLabel.TextAlign = ContentAlignment.MiddleLeft;

            _fpsLabel = new ToolStripStatusLabel("FPS: --");
            _fpsLabel.AutoSize = true;
            _fpsLabel.BorderSides = ToolStripStatusLabelBorderSides.Left;
            _fpsLabel.BorderStyle = Border3DStyle.Etched;
            _fpsLabel.Padding = new Padding(5, 0, 5, 0);

            _bitrateLabel = new ToolStripStatusLabel("-- Mbps");
            _bitrateLabel.AutoSize = true;
            _bitrateLabel.BorderSides = ToolStripStatusLabelBorderSides.Left;
            _bitrateLabel.BorderStyle = Border3DStyle.Etched;
            _bitrateLabel.Padding = new Padding(5, 0, 5, 0);

            _pingLabel = new ToolStripStatusLabel("-- ms");
            _pingLabel.AutoSize = true;
            _pingLabel.BorderSides = ToolStripStatusLabelBorderSides.Left;
            _pingLabel.BorderStyle = Border3DStyle.Etched;
            _pingLabel.Padding = new Padding(5, 0, 5, 0);

            // Add labels to status strip
            _statusStrip.Items.AddRange(new ToolStripItem[] {
                _statusLabel,
                _fpsLabel,
                _bitrateLabel,
                _pingLabel
            });

            // Add status strip to form
            Controls.Add(_statusStrip);

            // Set initial visibility of performance info
            SetPerformanceInfoVisibility(_isPerformanceInfoVisible);
        }

        private void SetPerformanceInfoVisibility(bool visible)
        {
            _isPerformanceInfoVisible = visible;
            _fpsLabel.Visible = visible;
            _bitrateLabel.Visible = visible;
            _pingLabel.Visible = visible;
        }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            // Ctrl+P to toggle performance info
            if (e.Control && e.KeyCode == Keys.P)
            {
                SetPerformanceInfoVisibility(!_isPerformanceInfoVisible);
                e.Handled = true;
            }

            // Ctrl+D to toggle debug console
            if (e.Control && e.KeyCode == Keys.D)
            {
                ConsoleHelper.ShowConsoleWindow();
                e.Handled = true;
            }
        }

        private void OnStatusUpdateTick(object sender, EventArgs e)
        {
            // Update performance info in status bar
            if (_isPerformanceInfoVisible)
            {
                _fpsLabel.Text = $"FPS: {_currentMetrics.CurrentFps:F1}";
                _bitrateLabel.Text = $"{_currentMetrics.CurrentBitrateMbps:F1} Mbps";
                _pingLabel.Text = $"{_currentMetrics.Ping} ms";
            }
        }

        private void OnPerformanceUpdated(object sender, PerformanceMetrics metrics)
        {
            _currentMetrics = metrics;
        }

        private void Cleanup()
        {
            lock (_syncLock)
            {
                lblStatus.Text = "Status: Shutting down...";

                _networkUpdateTimer?.Stop();
                _statusUpdateTimer?.Stop();
                _screenCapture?.Stop();
                _networkClient?.Stop();

                _screenCapture?.Dispose();
                _encoder?.Dispose();
                _networkClient?.Dispose();

                EnhancedLogger.Instance.Info("Main form closed");
                EnhancedLogger.Instance.Dispose();
            }
        }

        private void OnFrameCaptured(object sender, Bitmap bitmap)
        {
            try
            {
                // Bitmap is already cloned so encode directly
                _encoder.EncodeFrame(bitmap);
            }
            catch (Exception ex)
            {
                EnhancedLogger.Instance.Error("Encoding error", ex);
            }
            finally
            {
                // Dispose bitmap as it was cloned in OptimizedScreenCapture
                bitmap.Dispose();
            }
        }

        private void OnFrameEncoded(object sender, byte[] encodedData)
        {
            try
            {
                _networkClient.SendScreenData(
                    encodedData,
                    Screen.PrimaryScreen.Bounds.Width,
                    Screen.PrimaryScreen.Bounds.Height);
            }
            catch (Exception ex)
            {
                EnhancedLogger.Instance.Error("Network transmission error", ex);
            }
        }

        private void OnConnectionStatusChanged(object sender, bool isConnected)
        {
            if (InvokeRequired)
            {
                Invoke(new Action<object, bool>(OnConnectionStatusChanged), sender, isConnected);
                return;
            }

            if (isConnected)
            {
                _statusLabel.Text = "Connected: Transmitting screen";
                EnhancedLogger.Instance.Info("Connected: Transmitting screen");
            }
            else
            {
                _statusLabel.Text = "Disconnected: Attempting reconnection...";
                EnhancedLogger.Instance.Info("Disconnected: Attempting reconnection");
            }
        }

        private void OnRemoteControlStatusChanged(object sender, bool isActive)
        {
            if (InvokeRequired)
            {
                Invoke(new Action<object, bool>(OnRemoteControlStatusChanged), sender, isActive);
                return;
            }

            if (isActive)
            {
                // Remote control mode activated
                _statusLabel.Text = "Remote control mode active";
                EnhancedLogger.Instance.Info($"Remote control mode activated (FPS: {_settings.HighResFps}, Quality: {_settings.HighResQuality})");

                // Set process priority higher for remote control
                if (Process.GetCurrentProcess().PriorityClass != ProcessPriorityClass.AboveNormal)
                    Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.AboveNormal;
            }
            else
            {
                // Normal mode
                _statusLabel.Text = "Connected: Transmitting screen";
                EnhancedLogger.Instance.Info($"Normal mode (FPS: {_settings.LowResFps}, Quality: {_settings.LowResQuality})");

                // Reset process priority
                if (Process.GetCurrentProcess().PriorityClass != ProcessPriorityClass.Normal)
                    Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.Normal;
            }
        }

        private void OnRemoteControlReceived(object sender, ScreenPacket packet)
        {
            if (InvokeRequired)
            {
                Invoke(new Action<object, ScreenPacket>(OnRemoteControlReceived), sender, packet);
                return;
            }

            // Handle remote control commands
            switch (packet.Type)
            {
                case PacketType.MouseMove:
                    if (packet.MouseX.HasValue && packet.MouseY.HasValue)
                    {
                        Cursor.Position = new Point(
                            packet.MouseX.Value,
                            packet.MouseY.Value);
                    }
                    break;

                case PacketType.MouseClick:
                    if (packet.MouseX.HasValue && packet.MouseY.HasValue && packet.MouseButton.HasValue)
                    {
                        Cursor.Position = new Point(
                            packet.MouseX.Value,
                            packet.MouseY.Value);

                        // Mouse click simulation
                        if (packet.MouseButton.Value == 0) // Left click
                        {
                            MouseOperations.MouseEvent(MouseOperations.MouseEventFlags.LeftDown);
                            MouseOperations.MouseEvent(MouseOperations.MouseEventFlags.LeftUp);
                        }
                        else if (packet.MouseButton.Value == 1) // Right click
                        {
                            MouseOperations.MouseEvent(MouseOperations.MouseEventFlags.RightDown);
                            MouseOperations.MouseEvent(MouseOperations.MouseEventFlags.RightUp);
                        }
                    }
                    break;

                case PacketType.KeyPress:
                    if (packet.KeyCode.HasValue)
                    {
                        // Keyboard event simulation
                        KeyboardOperations.KeyDown((Keys)packet.KeyCode.Value);
                        KeyboardOperations.KeyUp((Keys)packet.KeyCode.Value);
                    }
                    break;
            }
        }
    }

    // Mouse event simulation (unchanged)
    public static class MouseOperations
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern void mouse_event(int dwFlags, int dx, int dy, int cButtons, int dwExtraInfo);

        public enum MouseEventFlags
        {
            LeftDown = 0x02,
            LeftUp = 0x04,
            RightDown = 0x08,
            RightUp = 0x10
        }

        public static void MouseEvent(MouseEventFlags value)
        {
            mouse_event((int)value, 0, 0, 0, 0);
        }
    }

    // Keyboard event simulation (unchanged)
    public static class KeyboardOperations
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, int dwFlags, int dwExtraInfo);

        private const int KEYEVENTF_KEYDOWN = 0x0000;
        private const int KEYEVENTF_KEYUP = 0x0002;

        public static void KeyDown(Keys key)
        {
            keybd_event((byte)key, 0, KEYEVENTF_KEYDOWN, 0);
        }

        public static void KeyUp(Keys key)
        {
            keybd_event((byte)key, 0, KEYEVENTF_KEYUP, 0);
        }
    }
}