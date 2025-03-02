using System;
using System.Drawing;
using System.Windows.Forms;
using ScreenShare.Host.Network;
using ScreenShare.Common.Utils;
using ScreenShare.Host.Rendering;
using SharpDX.DXGI;
using SharpDX;

namespace ScreenShare.Host.Forms
{
    public class RemoteControlForm : Form
    {
        private NetworkServer _networkServer;
        private Panel _renderPanel;
        private SimplifiedDirectXRenderer _renderer; // Using the simplified renderer
        private Label _statusLabel;
        private int _clientNumber;
        private bool _isControlling;
        private bool _isDisposed = false;
        private Bitmap _currentFrame;
        private readonly object _frameLock = new object();

        public int ClientNumber => _clientNumber;

        public RemoteControlForm(NetworkServer networkServer, int clientNumber, Bitmap initialImage)
        {
            _networkServer = networkServer;
            _clientNumber = clientNumber;
            _isControlling = true;

            InitializeComponent();
            InitializeRenderer(initialImage);

            FormClosing += (s, e) => Cleanup();
            FileLogger.Instance.WriteInfo($"Client {clientNumber} remote control window created (Simplified DirectX renderer)");
        }

        private void InitializeComponent()
        {
            // Form settings
            Text = $"Remote Control - Client {_clientNumber}";
            Size = new Size(1024, 768);
            StartPosition = FormStartPosition.CenterScreen;

            // Status label
            _statusLabel = new Label
            {
                Dock = DockStyle.Bottom,
                Height = 25,
                TextAlign = ContentAlignment.MiddleCenter,
                Text = "Remote Control Mode - Mouse and keyboard inputs are sent to the client"
            };

            // Rendering panel
            _renderPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = System.Drawing.Color.Black
            };

            // Event handlers
            _renderPanel.MouseMove += OnPanelMouseMove;
            _renderPanel.MouseClick += OnPanelMouseClick;
            _renderPanel.MouseDown += OnPanelMouseDown;
            _renderPanel.MouseUp += OnPanelMouseUp;

            KeyPreview = true;
            KeyDown += OnFormKeyDown;
            KeyUp += OnFormKeyUp;

            // Add controls
            Controls.Add(_renderPanel);
            Controls.Add(_statusLabel);
        }

        private void InitializeRenderer(Bitmap initialImage)
        {
            try
            {
                // Create default image if none provided
                if (initialImage == null)
                {
                    initialImage = CreateWaitingImage();
                }

                // Store a copy of the initial image
                lock (_frameLock)
                {
                    _currentFrame = new Bitmap(initialImage);
                }

                // Initialize the simplified DirectX renderer
                _renderer = new SimplifiedDirectXRenderer(_renderPanel, initialImage.Width, initialImage.Height);

                // Render the initial frame
                _renderer.RenderFrame(_currentFrame);

                FileLogger.Instance.WriteInfo($"Renderer initialized with {initialImage.Width}x{initialImage.Height} image");
            }
            catch (Exception ex)
            {
                FileLogger.Instance.WriteError("Failed to initialize renderer", ex);
                MessageBox.Show($"Error initializing renderer: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private Bitmap CreateWaitingImage()
        {
            // Create a simple "waiting" image
            Bitmap waitingImage = new Bitmap(800, 600);
            using (Graphics g = Graphics.FromImage(waitingImage))
            {
                g.Clear(System.Drawing.Color.DarkBlue);

                // Add text
                using (Font font = new Font("Arial", 24, FontStyle.Bold))
                {
                    string message = "Waiting for screen data...";
                    SizeF textSize = g.MeasureString(message, font);
                    PointF textPosition = new PointF(
                        (waitingImage.Width - textSize.Width) / 2,
                        (waitingImage.Height - textSize.Height) / 2);

                    g.DrawString(message, font, Brushes.White, textPosition);
                }

                // Draw colored rectangles in the corners for testing
                g.FillRectangle(Brushes.Red, 0, 0, 50, 50);
                g.FillRectangle(Brushes.Green, waitingImage.Width - 50, 0, 50, 50);
                g.FillRectangle(Brushes.Blue, 0, waitingImage.Height - 50, 50, 50);
                g.FillRectangle(Brushes.Yellow, waitingImage.Width - 50, waitingImage.Height - 50, 50, 50);
            }

            return waitingImage;
        }

        public void UpdateImage(Bitmap image)
        {
            if (_isDisposed || image == null)
                return;

            try
            {
                // Handle cross-thread operation
                if (InvokeRequired)
                {
                    try
                    {
                        BeginInvoke(new Action<Bitmap>(UpdateImage), image);
                        return; // Image will be handled by UI thread
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

                // Now on UI thread - replace the current frame with thread safety
                lock (_frameLock)
                {
                    try
                    {
                        // First, nullify the renderer's current image
                        if (_renderer != null)
                        {
                            _renderer.ClearFrame();
                        }

                        // Dispose the old image
                        if (_currentFrame != null && _currentFrame != image)
                        {
                            var oldFrame = _currentFrame;
                            _currentFrame = null;
                            oldFrame.Dispose();
                        }

                        // Set the new image
                        _currentFrame = image;

                        // Render the new image
                        if (_renderer != null && _currentFrame != null && !_isDisposed)
                        {
                            _renderer.RenderFrame(_currentFrame);
                        }
                    }
                    catch (Exception ex)
                    {
                        EnhancedLogger.Instance.Error($"Error updating remote control image: {ex.Message}", ex);

                        // Cleanup on error
                        try
                        {
                            if (_currentFrame != null && _currentFrame != image)
                            {
                                _currentFrame.Dispose();
                            }
                            _currentFrame = null;

                            if (image != null)
                            {
                                image.Dispose();
                            }
                        }
                        catch { /* Ignore cleanup errors */ }
                    }
                }
            }
            catch (Exception ex)
            {
                EnhancedLogger.Instance.Error("Error updating image", ex);
                try { image?.Dispose(); } catch { }
            }
        }
        
        private void OnPanelMouseMove(object sender, MouseEventArgs e)
        {
            if (!_isControlling || _currentFrame == null)
                return;

            try
            {
                // Convert coordinates
                float scaleX = (float)_currentFrame.Width / _renderPanel.ClientSize.Width;
                float scaleY = (float)_currentFrame.Height / _renderPanel.ClientSize.Height;

                int x = (int)(e.X * scaleX);
                int y = (int)(e.Y * scaleY);

                _networkServer.SendMouseMove(_clientNumber, x, y);
            }
            catch (Exception ex)
            {
                FileLogger.Instance.WriteError("Error sending mouse move", ex);
            }
        }

        private void OnPanelMouseClick(object sender, MouseEventArgs e)
        {
            if (!_isControlling || _currentFrame == null)
                return;

            try
            {
                // Convert coordinates
                float scaleX = (float)_currentFrame.Width / _renderPanel.ClientSize.Width;
                float scaleY = (float)_currentFrame.Height / _renderPanel.ClientSize.Height;

                int x = (int)(e.X * scaleX);
                int y = (int)(e.Y * scaleY);

                int button = e.Button == MouseButtons.Left ? 0 : e.Button == MouseButtons.Right ? 1 : -1;
                if (button >= 0)
                {
                    _networkServer.SendMouseClick(_clientNumber, x, y, button);
                }
            }
            catch (Exception ex)
            {
                FileLogger.Instance.WriteError("Error sending mouse click", ex);
            }
        }

        private void OnPanelMouseDown(object sender, MouseEventArgs e)
        {
            _renderPanel.Focus();
        }

        private void OnPanelMouseUp(object sender, MouseEventArgs e)
        {
            // Handle if needed
        }

        private void OnFormKeyDown(object sender, KeyEventArgs e)
        {
            if (!_isControlling)
                return;

            try
            {
                _networkServer.SendKeyPress(_clientNumber, (int)e.KeyCode);
                e.Handled = true;

                // ESC key to exit remote control
                if (e.KeyCode == Keys.Escape)
                {
                    DialogResult result = MessageBox.Show(
                        "End remote control?",
                        "End Remote Control",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Question);

                    if (result == DialogResult.Yes)
                    {
                        Close();
                    }
                }
            }
            catch (Exception ex)
            {
                FileLogger.Instance.WriteError("Error sending key press", ex);
            }
        }

        private void OnFormKeyUp(object sender, KeyEventArgs e)
        {
            // Handle if needed
        }

        private void Cleanup()
        {
            if (_isDisposed)
                return;

            _isDisposed = true;
            _isControlling = false;

            try
            {
                if (_renderer != null)
                {
                    _renderer.Dispose();
                    _renderer = null;
                }

                lock (_frameLock)
                {
                    if (_currentFrame != null)
                    {
                        _currentFrame.Dispose();
                        _currentFrame = null;
                    }
                }

                FileLogger.Instance.WriteInfo($"Client {_clientNumber} remote control ended");
            }
            catch (Exception ex)
            {
                FileLogger.Instance.WriteError("Error during cleanup", ex);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Cleanup();
            }
            base.Dispose(disposing);
        }
    }
}