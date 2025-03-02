using System;
using System.Collections.Concurrent;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using ScreenShare.Common.Models;
using ScreenShare.Common.Settings;
using ScreenShare.Common.Utils;
using ScreenShare.Host.Decoder;
using ScreenShare.Host.Forms;
using ScreenShare.Host.Network;
using ScreenShare.Host.Processing;

namespace ScreenShare.Host.Forms
{
    public partial class MainForm : Form
    {
        // Settings and main components
        private HostSettings _settings;
        private NetworkServer _networkServer;
        private FFmpegDecoder _decoder;
        private FrameProcessingManager _processingManager;
        private System.Windows.Forms.Timer _networkUpdateTimer;
        private readonly object _syncLock = new object();

        // Client tracking
        private readonly ConcurrentDictionary<int, ClientTile> _clientTiles = new ConcurrentDictionary<int, ClientTile>();
        private readonly ConcurrentDictionary<int, RemoteControlForm> _remoteControlForms = new ConcurrentDictionary<int, RemoteControlForm>();

        // Performance monitoring
        private readonly ConcurrentDictionary<int, ClientPerformanceMetrics> _clientMetrics = new ConcurrentDictionary<int, ClientPerformanceMetrics>();
        private System.Windows.Forms.Timer _performanceUpdateTimer;
        private System.Windows.Forms.Timer _uiRefreshTimer;
        private bool _showDebugInfo = false;

        // UI components for performance display
        private StatusStrip _statusStrip;
        private ToolStripStatusLabel _statusLabel;
        private ToolStripStatusLabel _clientCountLabel;
        private ToolStripStatusLabel _processingFpsLabel;
        private ToolStripStatusLabel _memoryUsageLabel;

        public MainForm()
        {
            InitializeComponent();

            try
            {
                // Initialize the enhanced logger
                EnhancedLogger.Instance.SetLogLevels(
                    EnhancedLogger.LogLevel.Debug,   // Console level - set to Debug for troubleshooting
                    EnhancedLogger.LogLevel.Debug    // File level
                );

                EnhancedLogger.Instance.Info("Host MainForm initializing");

                // Initialize UI components
                InitializeStatusUI();

                // Load settings
                _settings = HostSettings.Load();

                // Update UI title with server information
                Text = $"ScreenShare Manager - {_settings.HostIp}:{_settings.HostPort}";

                // Initialize client tiles panel
                tilePanel.AutoScroll = true;
                tilePanel.Padding = new Padding(5);

                FormClosing += (s, e) => Cleanup();

                // Register keyboard shortcuts
                KeyPreview = true;
                KeyDown += OnKeyDown;

                // Start with buttons in correct state
                btnStart.Enabled = true;
                btnStop.Enabled = false;

                EnhancedLogger.Instance.Info("Host MainForm initialization complete");
            }
            catch (Exception ex)
            {
                EnhancedLogger.Instance.Error("Host MainForm initialization error", ex);
                MessageBox.Show($"Error initializing: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void InitializeStatusUI()
        {
            // Set up status strip
            _statusStrip = new StatusStrip();
            _statusStrip.SizingGrip = false;

            // Create status labels
            _statusLabel = new ToolStripStatusLabel("Ready");
            _statusLabel.AutoSize = true;
            _statusLabel.Spring = true;
            _statusLabel.TextAlign = ContentAlignment.MiddleLeft;

            _clientCountLabel = new ToolStripStatusLabel("Clients: 0");
            _clientCountLabel.AutoSize = true;
            _clientCountLabel.BorderSides = ToolStripStatusLabelBorderSides.Left;
            _clientCountLabel.BorderStyle = Border3DStyle.Etched;
            _clientCountLabel.Padding = new Padding(5, 0, 5, 0);

            _processingFpsLabel = new ToolStripStatusLabel("FPS: --");
            _processingFpsLabel.AutoSize = true;
            _processingFpsLabel.BorderSides = ToolStripStatusLabelBorderSides.Left;
            _processingFpsLabel.BorderStyle = Border3DStyle.Etched;
            _processingFpsLabel.Padding = new Padding(5, 0, 5, 0);
            _processingFpsLabel.Visible = _showDebugInfo;

            _memoryUsageLabel = new ToolStripStatusLabel("Mem: -- MB");
            _memoryUsageLabel.AutoSize = true;
            _memoryUsageLabel.BorderSides = ToolStripStatusLabelBorderSides.Left;
            _memoryUsageLabel.BorderStyle = Border3DStyle.Etched;
            _memoryUsageLabel.Padding = new Padding(5, 0, 5, 0);
            _memoryUsageLabel.Visible = _showDebugInfo;

            // Add labels to status strip
            _statusStrip.Items.AddRange(new ToolStripItem[] {
                _statusLabel,
                _clientCountLabel,
                _processingFpsLabel,
                _memoryUsageLabel
            });

            // Add status strip to form
            Controls.Add(_statusStrip);
        }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            // Toggle debug info with Ctrl+D
            if (e.Control && e.KeyCode == Keys.D)
            {
                _showDebugInfo = !_showDebugInfo;
                _processingFpsLabel.Visible = _showDebugInfo;
                _memoryUsageLabel.Visible = _showDebugInfo;

                if (_showDebugInfo)
                {
                    ConsoleHelper.ShowConsoleWindow();
                }
                else
                {
                    ConsoleHelper.HideConsoleWindow();
                }

                e.Handled = true;
            }

            // Force GC with Ctrl+G (for debugging memory issues)
            if (e.Control && e.KeyCode == Keys.G && _showDebugInfo)
            {
                GC.Collect();
                EnhancedLogger.Instance.Info("Manual garbage collection triggered");
                e.Handled = true;
            }
        }

        private void OnStartButtonClick(object sender, EventArgs e)
        {
            StartServer();
        }

        private void OnStopButtonClick(object sender, EventArgs e)
        {
            StopServer();
        }

        private void StartServer()
        {
            try
            {
                lock (_syncLock)
                {
                    // Initialize components
                    _networkServer = new NetworkServer(_settings);
                    _decoder = new FFmpegDecoder(useHardwareAcceleration: true, threadCount: 2);
                    _decoder.SetVerboseLogging(_showDebugInfo);

                    // Configure and start frame processing manager
                    _processingManager = new FrameProcessingManager(_networkServer, _decoder);
                    _processingManager.Configure(
                        dropOutdatedFrames: true,
                        maxQueueSize: 4,
                        frameExpirationMs: 1000,
                        useParallelProcessing: true
                    );

                    // Subscribe to events
                    _networkServer.ClientConnected += OnClientConnected;
                    _networkServer.ClientDisconnected += OnClientDisconnected;
                    _networkServer.PerformanceUpdated += OnClientPerformanceUpdated;
                    _processingManager.FrameProcessed += OnFrameProcessed;
                    _processingManager.MetricsUpdated += OnProcessingMetricsUpdated;

                    // Initialize network timer
                    _networkUpdateTimer = new System.Windows.Forms.Timer();
                    _networkUpdateTimer.Interval = 15;
                    _networkUpdateTimer.Tick += (s, e) => _networkServer.Update();
                    _networkUpdateTimer.Start();

                    // Initialize performance update timer
                    _performanceUpdateTimer = new System.Windows.Forms.Timer();
                    _performanceUpdateTimer.Interval = 1000; // Update once per second
                    _performanceUpdateTimer.Tick += OnPerformanceUpdateTick;
                    _performanceUpdateTimer.Start();

                    // Initialize UI refresh timer
                    _uiRefreshTimer = new System.Windows.Forms.Timer();
                    _uiRefreshTimer.Interval = 250; // Refresh 4 times per second
                    _uiRefreshTimer.Tick += (s, e) => ForceRedrawClientTiles();
                    _uiRefreshTimer.Start();

                    // Start network server
                    _networkServer.Start();

                    // Update UI
                    btnStart.Enabled = false;
                    btnStop.Enabled = true;
                    _statusLabel.Text = $"Server running on {_settings.HostIp}:{_settings.HostPort}";

                    AddLogMessage($"Server started on {_settings.HostIp}:{_settings.HostPort}");
                    EnhancedLogger.Instance.Info($"Server started on {_settings.HostIp}:{_settings.HostPort}");
                }
            }
            catch (Exception ex)
            {
                EnhancedLogger.Instance.Error("Failed to start server", ex);
                MessageBox.Show($"Failed to start server: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void StopServer()
        {
            try
            {
                lock (_syncLock)
                {
                    // Stop timers
                    _networkUpdateTimer?.Stop();
                    _performanceUpdateTimer?.Stop();
                    _uiRefreshTimer?.Stop();

                    // Close remote control forms
                    foreach (var form in _remoteControlForms.Values)
                    {
                        try
                        {
                            form.Close();
                            form.Dispose();
                        }
                        catch { /* Ignore errors during cleanup */ }
                    }
                    _remoteControlForms.Clear();

                    // Clear client tiles
                    foreach (var tile in _clientTiles.Values)
                    {
                        try
                        {
                            tilePanel.Controls.Remove(tile);
                            tile.Dispose();
                        }
                        catch { /* Ignore errors during cleanup */ }
                    }
                    _clientTiles.Clear();

                    // Dispose components
                    _processingManager?.Dispose();
                    _decoder?.Dispose();
                    _networkServer?.Dispose();

                    // Update UI
                    btnStart.Enabled = true;
                    btnStop.Enabled = false;
                    _statusLabel.Text = "Server stopped";
                    _clientCountLabel.Text = "Clients: 0";

                    AddLogMessage("Server stopped");
                    EnhancedLogger.Instance.Info("Server stopped");

                    // Force garbage collection to clean up resources
                    GC.Collect();
                }
            }
            catch (Exception ex)
            {
                EnhancedLogger.Instance.Error("Error stopping server", ex);
                MessageBox.Show($"Error stopping server: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void ForceRedrawClientTiles()
        {
            foreach (var tile in _clientTiles.Values)
            {
                try
                {
                    if (tile != null && !tile.IsDisposed)
                    {
                        tile.ForceRefresh();
                    }
                }
                catch (Exception ex)
                {
                    EnhancedLogger.Instance.Error($"Error refreshing tile: {ex.Message}", ex);
                }
            }
        }

        private void OnClientConnected(object sender, ClientEventArgs e)
        {
            if (InvokeRequired)
            {
                Invoke(new Action<object, ClientEventArgs>(OnClientConnected), sender, e);
                return;
            }

            try
            {
                // Create a new client tile
                var clientTile = new ClientTile(e.ClientNumber, e.ClientInfo);
                clientTile.RemoteControlRequested += OnRemoteControlRequested;

                // Add to panel
                tilePanel.Controls.Add(clientTile);
                _clientTiles[e.ClientNumber] = clientTile;

                // Update UI
                ArrangeTiles();
                _clientCountLabel.Text = $"Clients: {_clientTiles.Count}";

                AddLogMessage($"Client {e.ClientNumber} connected from {e.ClientInfo.ClientIp}");
                EnhancedLogger.Instance.Info($"Client {e.ClientNumber} connected from {e.ClientInfo.ClientIp}");
            }
            catch (Exception ex)
            {
                EnhancedLogger.Instance.Error($"Error handling client connection: {e.ClientNumber}", ex);
            }
        }

        private void OnClientDisconnected(object sender, ClientEventArgs e)
        {
            if (InvokeRequired)
            {
                Invoke(new Action<object, ClientEventArgs>(OnClientDisconnected), sender, e);
                return;
            }

            try
            {
                // Remove client tile
                if (_clientTiles.TryRemove(e.ClientNumber, out var tile))
                {
                    tilePanel.Controls.Remove(tile);
                    tile.Dispose();
                }

                // Close remote control form if open
                if (_remoteControlForms.TryRemove(e.ClientNumber, out var form))
                {
                    form.Close();
                    form.Dispose();
                }

                // Remove client metrics
                _clientMetrics.TryRemove(e.ClientNumber, out _);

                // Update UI
                ArrangeTiles();
                _clientCountLabel.Text = $"Clients: {_clientTiles.Count}";

                AddLogMessage($"Client {e.ClientNumber} disconnected");
                EnhancedLogger.Instance.Info($"Client {e.ClientNumber} disconnected");
            }
            catch (Exception ex)
            {
                EnhancedLogger.Instance.Error($"Error handling client disconnection: {e.ClientNumber}", ex);
            }
        }

        private void OnFrameProcessed(object sender, FrameProcessedEventArgs e)
        {
            if (e == null || e.Bitmap == null)
                return;

            try
            {
                EnhancedLogger.Instance.Debug($"Frame processed event: client={e.ClientNumber}, size={e.Bitmap.Width}x{e.Bitmap.Height}");

                // Fix 1: Make exact copies of the bitmap for each destination
                // This is critical - we need separate instances

                // Handle tile update
                if (_clientTiles.TryGetValue(e.ClientNumber, out var tile))
                {
                    try
                    {
                        // Create a CLEAN COPY for the tile
                        Bitmap tileCopy = new Bitmap(e.Bitmap.Width, e.Bitmap.Height);
                        using (Graphics g = Graphics.FromImage(tileCopy))
                        {
                            g.Clear(Color.Black); // Start with a clean slate
                            g.DrawImage(e.Bitmap, 0, 0, e.Bitmap.Width, e.Bitmap.Height);
                        }

                        EnhancedLogger.Instance.Debug($"Created clean copy for tile: {e.ClientNumber}");

                        // Send copy to the tile
                        tile.UpdateImage(tileCopy);
                    }
                    catch (Exception ex)
                    {
                        EnhancedLogger.Instance.Error($"Error creating bitmap for tile: {ex.Message}", ex);
                    }
                }

                // Handle remote control form update
                if (_remoteControlForms.TryGetValue(e.ClientNumber, out var form))
                {
                    try
                    {
                        // Create a different copy for the remote control form
                        Bitmap remoteControlCopy = new Bitmap(e.Bitmap.Width, e.Bitmap.Height);
                        using (Graphics g = Graphics.FromImage(remoteControlCopy))
                        {
                            g.Clear(Color.Black); // Start with a clean slate
                            g.DrawImage(e.Bitmap, 0, 0, e.Bitmap.Width, e.Bitmap.Height);
                        }

                        EnhancedLogger.Instance.Debug($"Created clean copy for remote control: {e.ClientNumber}");

                        // Send copy to the remote control form
                        form.UpdateImage(remoteControlCopy);
                    }
                    catch (Exception ex)
                    {
                        EnhancedLogger.Instance.Error($"Error creating bitmap for remote control: {ex.Message}", ex);
                    }
                }

                // Always dispose the original bitmap since we've made copies
                e.Bitmap.Dispose();
            }
            catch (Exception ex)
            {
                EnhancedLogger.Instance.Error($"Error in OnFrameProcessed: {ex.Message}", ex);

                // Clean up on error
                try { e.Bitmap?.Dispose(); } catch { }
            }
        }

        private void OnClientPerformanceUpdated(object sender, PerformanceEventArgs e)
        {
            try
            {
                // Store metrics for display
                _clientMetrics[e.ClientNumber] = e.Metrics;

                // Update client tile if on UI thread
                if (!InvokeRequired && _clientTiles.TryGetValue(e.ClientNumber, out var tile))
                {
                    tile.UpdatePerformance(e.Metrics);
                }
            }
            catch (Exception ex)
            {
                EnhancedLogger.Instance.Error($"Error updating performance for client {e.ClientNumber}", ex);
            }
        }

        private void OnProcessingMetricsUpdated(object sender, ProcessingMetricsEventArgs e)
        {
            try
            {
                if (!InvokeRequired)
                {
                    _processingFpsLabel.Text = $"FPS: {e.Metrics.ProcessingFps:F1}";
                }
            }
            catch { /* Ignore errors in metrics updates */ }
        }

        private void OnPerformanceUpdateTick(object sender, EventArgs e)
        {
            // Update memory usage display
            if (_showDebugInfo)
            {
                long memoryMB = GC.GetTotalMemory(false) / (1024 * 1024);
                _memoryUsageLabel.Text = $"Mem: {memoryMB} MB";
            }
        }

        private void OnRemoteControlRequested(object sender, int clientNumber)
        {
            try
            {
                // Check if we already have a remote control form open
                if (_remoteControlForms.ContainsKey(clientNumber))
                {
                    // Bring existing form to front
                    _remoteControlForms[clientNumber].BringToFront();
                    return;
                }

                // Get the client info
                if (!_clientTiles.TryGetValue(clientNumber, out var tile))
                {
                    return;
                }

                // Get latest image
                var latestImage = tile.GetLatestImage();
                if (latestImage == null)
                {
                    latestImage = new Bitmap(800, 600); // Default size
                    using (var g = Graphics.FromImage(latestImage))
                    {
                        g.Clear(Color.Black);
                        g.DrawString("Waiting for screen data...",
                            new Font("Arial", 16), Brushes.White, 10, 10);
                    }
                }

                // Create and show remote control form
                var form = new RemoteControlForm(_networkServer, clientNumber, latestImage);
                form.FormClosed += (s, e) => {
                    // Send remote control end command when form is closed
                    _networkServer.SendRemoteControlEnd(clientNumber);
                    _remoteControlForms.TryRemove(clientNumber, out _);
                };

                _remoteControlForms[clientNumber] = form;
                form.Show();

                // Send remote control request to client
                _networkServer.SendRemoteControlRequest(clientNumber);

                AddLogMessage($"Remote control started for client {clientNumber}");
                EnhancedLogger.Instance.Info($"Remote control started for client {clientNumber}");
            }
            catch (Exception ex)
            {
                EnhancedLogger.Instance.Error($"Error starting remote control for client {clientNumber}", ex);
                MessageBox.Show($"Error starting remote control: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void ArrangeTiles()
        {
            int tileWidth = 320;
            int tileHeight = 240;
            int margin = 10;
            int maxColumns = Math.Max(1, _settings.TileColumns);

            int column = 0;
            int row = 0;

            foreach (var tile in _clientTiles.Values)
            {
                tile.Location = new Point(
                    column * (tileWidth + margin) + margin,
                    row * (tileHeight + margin) + margin);
                tile.Size = new Size(tileWidth, tileHeight);

                column++;
                if (column >= maxColumns)
                {
                    column = 0;
                    row++;
                }
            }
        }

        private void AddLogMessage(string message)
        {
            if (InvokeRequired)
            {
                Invoke(new Action<string>(AddLogMessage), message);
                return;
            }

            // Add timestamp
            string logEntry = $"[{DateTime.Now:HH:mm:ss}] {message}";

            // Add to log list box
            logListBox.Items.Add(logEntry);

            // Auto-scroll to bottom
            logListBox.SelectedIndex = logListBox.Items.Count - 1;
            logListBox.ClearSelected();

            // Limit entries
            while (logListBox.Items.Count > 1000)
            {
                logListBox.Items.RemoveAt(0);
            }
        }

        private void Cleanup()
        {
            StopServer();
            EnhancedLogger.Instance.Info("Main form closed");
            EnhancedLogger.Instance.Dispose();
        }
    }

    /// <summary>
    /// Client tile for displaying client screen and status
    /// </summary>
    public class ClientTile : Panel
    {
        private readonly int _clientNumber;
        private readonly ClientInfo _clientInfo;
        private PictureBox _pictureBox;
        private Label _infoLabel;
        private Label _performanceLabel;
        private Button _remoteControlButton;
        private Bitmap _currentImage;
        private readonly object _imageLock = new object();
        private volatile bool _updatingImage = false;

        public event EventHandler<int> RemoteControlRequested;

        public ClientTile(int clientNumber, ClientInfo clientInfo)
        {
            _clientNumber = clientNumber;
            _clientInfo = clientInfo;

            InitializeComponents();
        }

        private void InitializeComponents()
        {
            // Configure panel
            BorderStyle = BorderStyle.FixedSingle;
            Padding = new Padding(2);

            // Create picture box
            _pictureBox = new PictureBox
            {
                Dock = DockStyle.Fill,
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = Color.Black
            };

            // Create info label
            _infoLabel = new Label
            {
                Dock = DockStyle.Bottom,
                TextAlign = ContentAlignment.MiddleLeft,
                BackColor = Color.FromArgb(64, 64, 64),
                ForeColor = Color.White,
                Height = 20,
                Text = $"Client {_clientNumber} - {_clientInfo.ClientIp}"
            };

            // Create performance label
            _performanceLabel = new Label
            {
                Dock = DockStyle.Bottom,
                TextAlign = ContentAlignment.MiddleRight,
                BackColor = Color.FromArgb(64, 64, 64),
                ForeColor = Color.LightGreen,
                Height = 20,
                Text = "Waiting for data...",
                Visible = false
            };

            // Create remote control button
            _remoteControlButton = new Button
            {
                Dock = DockStyle.None,
                Text = "Control",
                Size = new Size(60, 22),
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                Location = new Point(Width - 65, 5)
            };
            _remoteControlButton.Click += (s, e) => RemoteControlRequested?.Invoke(this, _clientNumber);

            // Add controls
            Controls.Add(_pictureBox);
            Controls.Add(_infoLabel);
            Controls.Add(_performanceLabel);
            Controls.Add(_remoteControlButton);

            // Event handlers
            Resize += (s, e) => {
                _remoteControlButton.Location = new Point(Width - 65, 5);
            };

            // Double click to start remote control
            _pictureBox.DoubleClick += (s, e) => RemoteControlRequested?.Invoke(this, _clientNumber);
        }

        public void UpdateImage(Bitmap image)
        {
            if (image == null)
                return;

            if (_updatingImage)
            {
                // Already in the middle of an update, dispose this bitmap and exit
                image.Dispose();
                return;
            }

            try
            {
                _updatingImage = true;

                // Handle cross-thread operation
                if (InvokeRequired)
                {
                    try
                    {
                        // Use Invoke (not BeginInvoke) to ensure synchronous execution
                        Invoke(new Action<Bitmap>(innerImage =>
                        {
                            try
                            {
                                // On UI thread now, update directly
                                EnhancedLogger.Instance.Debug($"UI thread image update: Client {_clientNumber}, size={innerImage.Width}x{innerImage.Height}");
                                UpdateImageDirect(innerImage);
                            }
                            catch (Exception ex)
                            {
                                EnhancedLogger.Instance.Error($"Error in UI thread image update: {ex.Message}", ex);
                                innerImage.Dispose();
                            }
                        }), image);

                        return;
                    }
                    catch (ObjectDisposedException)
                    {
                        // Control might be disposed if form is closing
                        image.Dispose();
                        return;
                    }
                    catch (Exception ex)
                    {
                        EnhancedLogger.Instance.Error($"Error invoking image update: {ex.Message}", ex);
                        image.Dispose();
                        return;
                    }
                }
                else
                {
                    // Already on UI thread
                    UpdateImageDirect(image);
                }
            }
            catch (Exception ex)
            {
                EnhancedLogger.Instance.Error($"Error in UpdateImage: {ex.Message}", ex);
                try { image?.Dispose(); } catch { }
            }
            finally
            {
                _updatingImage = false;
            }
        }

        // Direct update method (always called on UI thread)
        private void UpdateImageDirect(Bitmap image)
        {
            lock (_imageLock)
            {
                try
                {
                    // Store a reference to the old image for later disposal
                    var oldImage = _currentImage;

                    // Store the new image first
                    _currentImage = image;

                    // Update PictureBox on UI thread
                    if (_pictureBox != null && !IsDisposed && !_pictureBox.IsDisposed)
                    {
                        // Set new image first
                        _pictureBox.Image = _currentImage;
                        EnhancedLogger.Instance.Debug($"PictureBox image set for client {_clientNumber}, size={image.Width}x{image.Height}");

                        // Update client info dimensions if needed
                        if (_clientInfo != null &&
                            (_clientInfo.ScreenWidth != image.Width || _clientInfo.ScreenHeight != image.Height))
                        {
                            _clientInfo.ScreenWidth = image.Width;
                            _clientInfo.ScreenHeight = image.Height;
                            if (_infoLabel != null && !_infoLabel.IsDisposed)
                            {
                                _infoLabel.Text = $"Client {_clientNumber} - {_clientInfo.ClientIp} - {_clientInfo.ScreenWidth}x{_clientInfo.ScreenHeight}";
                            }
                        }

                        // Force redraw after setting image
                        _pictureBox.Refresh();
                        this.Refresh();

                        // Now dispose the old image AFTER setting the new one
                        if (oldImage != null && oldImage != image)
                        {
                            try
                            {
                                oldImage.Dispose();
                            }
                            catch (Exception ex)
                            {
                                EnhancedLogger.Instance.Error($"Error disposing old image: {ex.Message}", ex);
                            }
                        }
                    }
                    else
                    {
                        EnhancedLogger.Instance.Warning($"PictureBox unavailable for client {_clientNumber}");
                        // Dispose the image if we can't use it
                        image.Dispose();
                    }
                }
                catch (Exception ex)
                {
                    EnhancedLogger.Instance.Error($"Error in UpdateImageDirect: {ex.Message}", ex);
                    try { image?.Dispose(); } catch { }
                }
            }
        }

        public void UpdatePerformance(ClientPerformanceMetrics metrics)
        {
            if (metrics == null)
                return;

            try
            {
                if (InvokeRequired)
                {
                    try
                    {
                        BeginInvoke(new Action<ClientPerformanceMetrics>(UpdatePerformance), metrics);
                    }
                    catch
                    {
                        // Ignore invoke errors
                    }
                    return;
                }

                _performanceLabel.Visible = true;
                _performanceLabel.Text = $"{metrics.AverageFps:F1} FPS, {metrics.AverageBitrateMbps:F1} Mbps";

                // Update color based on performance
                if (metrics.AverageFps < 10)
                {
                    _performanceLabel.ForeColor = Color.Red;
                }
                else if (metrics.AverageFps < 20)
                {
                    _performanceLabel.ForeColor = Color.Yellow;
                }
                else
                {
                    _performanceLabel.ForeColor = Color.LightGreen;
                }
            }
            catch
            {
                // Ignore errors in performance updates
            }
        }

        public Bitmap GetLatestImage()
        {
            lock (_imageLock)
            {
                if (_currentImage == null)
                    return null;

                // Create a copy of the current image
                try
                {
                    return new Bitmap(_currentImage);
                }
                catch (Exception ex)
                {
                    EnhancedLogger.Instance.Error($"Error copying image: {ex.Message}", ex);
                    return null;
                }
            }
        }

        public void ForceRefresh()
        {
            if (IsDisposed || _pictureBox == null || _pictureBox.IsDisposed)
                return;

            try
            {
                // Force UI update
                _pictureBox.Invalidate();
                _pictureBox.Update();
            }
            catch { /* Ignore errors during refresh */ }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);

            // Add extra verification during painting to ensure image is visible
            try
            {
                if (_pictureBox != null && _pictureBox.Image == null && _currentImage != null)
                {
                    EnhancedLogger.Instance.Debug($"Detected missing image in PictureBox during paint for client {_clientNumber} - restoring");
                    _pictureBox.Image = _currentImage;
                    _pictureBox.Refresh();
                }
            }
            catch (Exception ex)
            {
                EnhancedLogger.Instance.Error($"Error during paint: {ex.Message}", ex);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                lock (_imageLock)
                {
                    try
                    {
                        if (_pictureBox != null)
                        {
                            _pictureBox.Image = null;
                        }

                        if (_currentImage != null)
                        {
                            _currentImage.Dispose();
                            _currentImage = null;
                        }
                    }
                    catch (Exception ex)
                    {
                        EnhancedLogger.Instance.Error($"Error disposing client tile: {ex.Message}", ex);
                    }
                }
            }

            base.Dispose(disposing);
        }
    }
}