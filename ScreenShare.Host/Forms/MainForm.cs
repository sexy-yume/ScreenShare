// ScreenShare.Host/Forms/MainForm.cs
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using ScreenShare.Common.Models;
using ScreenShare.Common.Settings;
using ScreenShare.Host.Decoder;
using ScreenShare.Host.Network;
using ScreenShare.Common.Utils;
using System.Linq;

namespace ScreenShare.Host.Forms
{
    public partial class MainForm : Form
    {
        private HostSettings _settings;
        private NetworkServer _networkServer;
        private System.Windows.Forms.Timer _networkUpdateTimer;
        private ConcurrentDictionary<int, RemoteControlForm> _remoteControlForms = new ConcurrentDictionary<int, RemoteControlForm>();
        private ConcurrentDictionary<int, FFmpegDecoder> _decoders = new ConcurrentDictionary<int, FFmpegDecoder>();
        private ConcurrentDictionary<int, Bitmap> _lastFrames = new ConcurrentDictionary<int, Bitmap>();
        private List<ClientTile> _clientTiles;
        private int _tileWidth = 256;
        private int _tileHeight = 144;

        public MainForm()
        {
            InitializeComponent();

            try
            {
                // ���� �ε�
                _settings = HostSettings.Load();

                // �ΰ� �ʱ�ȭ
                FileLogger.Instance.WriteInfo("ȣ��Ʈ ���� �� �ʱ�ȭ ����");

                // ��Ʈ��ũ ���� �ʱ�ȭ
                _networkServer = new NetworkServer(_settings);
                _networkServer.ClientConnected += OnClientConnected;
                _networkServer.ClientDisconnected += OnClientDisconnected;
                _networkServer.ScreenDataReceived += OnScreenDataReceived;

                // Ÿ�̸� �ʱ�ȭ
                _networkUpdateTimer = new System.Windows.Forms.Timer();
                _networkUpdateTimer.Interval = 15;
                _networkUpdateTimer.Tick += (s, e) => _networkServer.Update();

                // ���ڴ� �� ������ ����� �ʱ�ȭ
                _decoders = new ConcurrentDictionary<int, FFmpegDecoder>();
                _lastFrames = new ConcurrentDictionary<int, Bitmap>();
                _clientTiles = new List<ClientTile>();

                // Ÿ�� �׸��� �ʱ�ȭ
                InitializeTileGrid();

                FileLogger.Instance.WriteInfo("ȣ��Ʈ ���� �� �ʱ�ȭ �Ϸ�");
            }
            catch (Exception ex)
            {
                FileLogger.Instance.WriteError("ȣ��Ʈ ���� �� �ʱ�ȭ ����", ex);
                MessageBox.Show($"�ʱ�ȭ ����: {ex.Message}", "����", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void InitializeTileGrid()
        {
            // ���� Ÿ�� ����
            tilePanel.Controls.Clear();
            _clientTiles.Clear();

            // Ÿ�� ����
            for (int i = 1; i <= _settings.MaxClients; i++)
            {
                var tile = new ClientTile(i);
                tile.Width = _tileWidth;
                tile.Height = _tileHeight;

                // �̺�Ʈ ó�� ����: DoubleClick �̺�Ʈ ���
                tile.DoubleClick += OnTileDoubleClick;

                int row = (i - 1) / _settings.TileColumns;
                int col = (i - 1) % _settings.TileColumns;

                tile.Location = new Point(col * (_tileWidth + 5), row * (_tileHeight + 5));

                tilePanel.Controls.Add(tile);
                _clientTiles.Add(tile);

                FileLogger.Instance.WriteInfo($"Ÿ�� {i} ����");
            }
        }

        private void Cleanup()
        {
            try
            {
                // ���� ���� �� ����
                foreach (var form in _remoteControlForms.Values)
                {
                    form.Close();
                    form.Dispose();
                }
                _remoteControlForms.Clear();

                // ���ڴ� ����
                foreach (var decoder in _decoders.Values)
                {
                    decoder.Dispose();
                }
                _decoders.Clear();

                // ������ ������ ����
                foreach (var frame in _lastFrames.Values)
                {
                    frame.Dispose();
                }
                _lastFrames.Clear();

                // ��Ÿ ���ҽ� ����
                _networkServer?.Stop();
                _networkUpdateTimer?.Stop();
            }
            catch (Exception ex)
            {
                FileLogger.Instance.WriteError("���ҽ� ���� �� ���� �߻�", ex);
            }
        }

        private void OnStartButtonClick(object sender, EventArgs e)
        {
            if (!_networkServer.IsRunning)
            {
                try
                {
                    _networkServer.Start();
                    _networkUpdateTimer.Start();

                    btnStart.Enabled = false;
                    btnStop.Enabled = true;
                    logListBox.Items.Add($"���� ���� - ��Ʈ: {_settings.HostPort}");
                    FileLogger.Instance.WriteInfo($"���� ���� - ��Ʈ: {_settings.HostPort}");
                }
                catch (Exception ex)
                {
                    FileLogger.Instance.WriteError("���� ���� ����", ex);
                    MessageBox.Show($"���� ���� ����: {ex.Message}", "����", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void OnStopButtonClick(object sender, EventArgs e)
        {
            if (_networkServer.IsRunning)
            {
                _networkServer.Stop();
                _networkUpdateTimer.Stop();

                btnStart.Enabled = true;
                btnStop.Enabled = false;
                logListBox.Items.Add("���� ����");
                FileLogger.Instance.WriteInfo("���� ����");

                // ��� Ÿ�� �ʱ�ȭ
                foreach (var tile in _clientTiles)
                {
                    tile.UpdateImage(null);
                    tile.Connected = false;
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

            // �α� �߰�
            string message = $"Ŭ���̾�Ʈ {e.ClientNumber} ����� - {e.ClientInfo.ClientIp}:{e.ClientInfo.ClientPort}";
            logListBox.Items.Add(message);
            FileLogger.Instance.WriteInfo(message);

            // ���ڴ� ����
            var decoder = new FFmpegDecoder();
            decoder.FrameDecoded += (s, bitmap) => OnFrameDecoded(e.ClientNumber, bitmap);
            _decoders[e.ClientNumber] = decoder;

            // �ش� Ÿ�� ������Ʈ
            if (e.ClientNumber > 0 && e.ClientNumber <= _clientTiles.Count)
            {
                var tile = _clientTiles[e.ClientNumber - 1];
                tile.Connected = true;
                tile.UpdateLabel($"Ŭ���̾�Ʈ {e.ClientNumber} - �����");
            }
        }

        private void OnClientDisconnected(object sender, ClientEventArgs e)
        {
            if (InvokeRequired)
            {
                Invoke(new Action<object, ClientEventArgs>(OnClientDisconnected), sender, e);
                return;
            }

            // �α� �߰�
            string message = $"Ŭ���̾�Ʈ {e.ClientNumber} ���� ����";
            logListBox.Items.Add(message);
            FileLogger.Instance.WriteInfo(message);

            // ���ڴ� ����
            if (_decoders.TryRemove(e.ClientNumber, out var decoder))
            {
                decoder.Dispose();
            }

            // ������ ������ ����
            if (_lastFrames.TryRemove(e.ClientNumber, out var frame))
            {
                frame.Dispose();
            }

            // �ش� Ÿ�� ������Ʈ
            if (e.ClientNumber > 0 && e.ClientNumber <= _clientTiles.Count)
            {
                var tile = _clientTiles[e.ClientNumber - 1];
                tile.Connected = false;
                tile.UpdateImage(null);
                tile.UpdateLabel($"Ŭ���̾�Ʈ {e.ClientNumber} - ���� ����");
            }

            // �����ִ� ���� ���� â �ݱ�
            foreach (Form form in Application.OpenForms)
            {
                if (form is RemoteControlForm rcForm && rcForm.ClientNumber == e.ClientNumber)
                {
                    rcForm.Close();
                    break;
                }
            }
        }

        private void OnScreenDataReceived(object sender, ScreenDataEventArgs e)
        {
            try
            {
                int clientNumber = e.ClientNumber;

                // �ش� Ŭ���̾�Ʈ�� ���ڴ� ��������
                var decoder = _decoders.GetOrAdd(clientNumber, num => {
                    var newDecoder = new FFmpegDecoder();
                    FileLogger.Instance.WriteInfo($"Ŭ���̾�Ʈ {clientNumber}�� �� ���ڴ� ����");
                    return newDecoder;
                });

                // ������ ���ڵ�
                Bitmap decodedFrame = decoder.DecodeFrame(e.ScreenData, e.Width, e.Height);

                if (decodedFrame != null)
                {
                    // ���� ���� ���� ������ ������ ����
                    if (_remoteControlForms.TryGetValue(clientNumber, out var form))
                    {
                        form.UpdateImage(decodedFrame);
                    }
                    else
                    {
                        // Ÿ�Ͽ� ��ҵ� �̹��� ǥ��
                        var clientTile = _clientTiles.FirstOrDefault(tile => tile.ClientNumber == clientNumber);
                        if (clientTile != null)
                        {
                            // Ÿ�� ũ�⿡ �°� �̹��� ���
                            using (Bitmap resizedImage = new Bitmap(decodedFrame, clientTile.Width, clientTile.Height))
                            {
                                clientTile.UpdateImage(resizedImage);
                            }
                        }

                        // ������ ������ ���� (���� ���� ���� �� ���)
                        Bitmap oldFrame;
                        if (_lastFrames.TryRemove(clientNumber, out oldFrame))
                        {
                            oldFrame.Dispose();
                        }
                        _lastFrames[clientNumber] = decodedFrame;
                    }
                }
            }
            catch (Exception ex)
            {
                FileLogger.Instance.WriteError("ȭ�� ������ ó�� ����", ex);
            }
        }

        private void OnFrameDecoded(int clientNumber, Bitmap bitmap)
        {
            if (InvokeRequired)
            {
                Invoke(new Action<int, Bitmap>(OnFrameDecoded), clientNumber, bitmap);
                return;
            }

            // ������ ������ ������Ʈ
            if (_lastFrames.TryGetValue(clientNumber, out var oldFrame))
            {
                oldFrame.Dispose();
            }

            _lastFrames[clientNumber] = bitmap;

            // �ش� Ÿ�� ������Ʈ
            if (clientNumber > 0 && clientNumber <= _clientTiles.Count)
            {
                var tile = _clientTiles[clientNumber - 1];
                tile.UpdateImage(bitmap);
            }

            // �����ִ� ���� ���� â ������Ʈ
            foreach (Form form in Application.OpenForms)
            {
                if (form is RemoteControlForm rcForm && rcForm.ClientNumber == clientNumber)
                {
                    if (!rcForm.IsDisposed)
                    {
                        rcForm.UpdateImage(bitmap);
                    }
                    break;
                }
            }
        }

        // ���� OnTileClick �޼��带 OnTileDoubleClick���� ��ü�ϰ� ����
        private void OnTileDoubleClick(object sender, EventArgs e)
        {
            if (sender is ClientTile tile)
            {
                int clientNumber = tile.ClientNumber;
                FileLogger.Instance.WriteInfo($"Ÿ�� {clientNumber} ����Ŭ�� - ���� ���� ����");

                // �̹� ���� ���� ���� â�� �ִ��� Ȯ��
                if (_remoteControlForms.ContainsKey(clientNumber))
                {
                    // �̹� ���� ������ ��Ŀ�� ��������
                    _remoteControlForms[clientNumber].Focus();
                    return;
                }

                // �ֱ� �������� �ִ��� Ȯ��
                Bitmap initialFrame = null;
                if (_lastFrames.TryGetValue(clientNumber, out var frame))
                {
                    initialFrame = frame;
                }

                // ���� ���� ��û ����
                if (_networkServer.SendRemoteControlRequest(clientNumber))
                {
                    // ���� ���� �� ����
                    var remoteForm = new RemoteControlForm(_networkServer, clientNumber, initialFrame);
                    _remoteControlForms.TryAdd(clientNumber, remoteForm);

                    // �� ���� �̺�Ʈ ó��
                    remoteForm.FormClosed += (s, args) => {
                        // ���� ���� ���� �޽��� ����
                        _networkServer.SendRemoteControlEnd(clientNumber);
                        RemoteControlForm removedForm;
                        _remoteControlForms.TryRemove(clientNumber, out removedForm);
                    };

                    // ���� ���� â ǥ��
                    remoteForm.Show();
                    remoteForm.Focus();
                    FileLogger.Instance.WriteInfo($"Ŭ���̾�Ʈ {clientNumber} ���� ���� â ����");
                }
                else
                {
                    FileLogger.Instance.WriteError($"Ŭ���̾�Ʈ {clientNumber}�� ���� ���� ��û ����");
                    MessageBox.Show($"Ŭ���̾�Ʈ {clientNumber}�� ���� ���� ��û�� ���� �� �����ϴ�.", "����", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            Cleanup();
        }
    }

    // Ŭ���̾�Ʈ Ÿ�� ��Ʈ��
    public class ClientTile : Panel
    {
        private PictureBox _pictureBox;
        private Label _label;

        public int ClientNumber { get; }
        public bool Connected { get; set; }

        public ClientTile(int clientNumber)
        {
            ClientNumber = clientNumber;
            Connected = false;
            BorderStyle = BorderStyle.FixedSingle;

            _pictureBox = new PictureBox
            {
                Dock = DockStyle.Fill,
                SizeMode = PictureBoxSizeMode.Zoom
            };

            _label = new Label
            {
                Dock = DockStyle.Bottom,
                TextAlign = ContentAlignment.MiddleCenter,
                Height = 20,
                Text = $"Ŭ���̾�Ʈ {clientNumber} - �����"
            };

            _pictureBox.DoubleClick += (s, e) => this.OnDoubleClick(e);
            _label.DoubleClick += (s, e) => this.OnDoubleClick(e);

            Controls.Add(_pictureBox);
            Controls.Add(_label);
        }



        public void UpdateImage(Bitmap image)
        {
            if (this.IsDisposed || _pictureBox.IsDisposed)
                return;

            if (InvokeRequired)
            {
                BeginInvoke(new Action<Bitmap>(UpdateImage), image);
                return;
            }

            try
            {
                if (_pictureBox.Image != null)
                {
                    var oldImage = _pictureBox.Image;
                    _pictureBox.Image = null;
                    oldImage.Dispose();
                }

                if (image != null)
                {
                    _pictureBox.Image = new Bitmap(image);
                }
            }
            catch (Exception ex)
            {
                FileLogger.Instance.WriteError("���� ���� ȭ�� ������Ʈ ����", ex);
            }
        }


        public void UpdateLabel(string text)
        {
            _label.Text = text;
        }
    }
}