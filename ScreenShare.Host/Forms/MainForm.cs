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

namespace ScreenShare.Host.Forms
{
    public partial class MainForm : Form
    {
        private HostSettings _settings;
        private NetworkServer _networkServer;
        private System.Windows.Forms.Timer _networkUpdateTimer;
        private ConcurrentDictionary<int, FFmpegDecoder> _decoders;
        private ConcurrentDictionary<int, Bitmap> _lastFrames;
        private List<ClientTile> _clientTiles;
        private int _tileWidth = 256;
        private int _tileHeight = 144;

        public MainForm()
        {
            InitializeComponent();

            try
            {
                // 설정 로드
                _settings = HostSettings.Load();

                // 로거 초기화
                FileLogger.Instance.WriteInfo("호스트 메인 폼 초기화 시작");

                // 네트워크 서버 초기화
                _networkServer = new NetworkServer(_settings);
                _networkServer.ClientConnected += OnClientConnected;
                _networkServer.ClientDisconnected += OnClientDisconnected;
                _networkServer.ScreenDataReceived += OnScreenDataReceived;

                // 타이머 초기화
                _networkUpdateTimer = new System.Windows.Forms.Timer();
                _networkUpdateTimer.Interval = 15;
                _networkUpdateTimer.Tick += (s, e) => _networkServer.Update();

                // 디코더 및 프레임 저장소 초기화
                _decoders = new ConcurrentDictionary<int, FFmpegDecoder>();
                _lastFrames = new ConcurrentDictionary<int, Bitmap>();
                _clientTiles = new List<ClientTile>();

                // 타일 그리드 초기화
                InitializeTileGrid();

                FileLogger.Instance.WriteInfo("호스트 메인 폼 초기화 완료");
            }
            catch (Exception ex)
            {
                FileLogger.Instance.WriteError("호스트 메인 폼 초기화 오류", ex);
                MessageBox.Show($"초기화 오류: {ex.Message}", "오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void InitializeTileGrid()
        {
            // 기존 타일 제거
            tilePanel.Controls.Clear();
            _clientTiles.Clear();

            // 타일 생성
            for (int i = 1; i <= _settings.MaxClients; i++)
            {
                var tile = new ClientTile(i);
                tile.Width = _tileWidth;
                tile.Height = _tileHeight;

                // 이벤트 처리 수정: DoubleClick 이벤트 사용
                tile.DoubleClick += OnTileDoubleClick;

                int row = (i - 1) / _settings.TileColumns;
                int col = (i - 1) % _settings.TileColumns;

                tile.Location = new Point(col * (_tileWidth + 5), row * (_tileHeight + 5));

                tilePanel.Controls.Add(tile);
                _clientTiles.Add(tile);

                FileLogger.Instance.WriteInfo($"타일 {i} 생성");
            }
        }

        private void Cleanup()
        {
            _networkUpdateTimer?.Stop();
            _networkServer?.Stop();

            foreach (var decoder in _decoders.Values)
            {
                decoder.Dispose();
            }

            foreach (var frame in _lastFrames.Values)
            {
                frame.Dispose();
            }

            _networkServer?.Dispose();

            FileLogger.Instance.WriteInfo("호스트 메인 폼 종료");
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
                    logListBox.Items.Add($"서버 시작 - 포트: {_settings.HostPort}");
                    FileLogger.Instance.WriteInfo($"서버 시작 - 포트: {_settings.HostPort}");
                }
                catch (Exception ex)
                {
                    FileLogger.Instance.WriteError("서버 시작 오류", ex);
                    MessageBox.Show($"서버 시작 오류: {ex.Message}", "오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
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
                logListBox.Items.Add("서버 중지");
                FileLogger.Instance.WriteInfo("서버 중지");

                // 모든 타일 초기화
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

            // 로그 추가
            string message = $"클라이언트 {e.ClientNumber} 연결됨 - {e.ClientInfo.ClientIp}:{e.ClientInfo.ClientPort}";
            logListBox.Items.Add(message);
            FileLogger.Instance.WriteInfo(message);

            // 디코더 생성
            var decoder = new FFmpegDecoder();
            decoder.FrameDecoded += (s, bitmap) => OnFrameDecoded(e.ClientNumber, bitmap);
            _decoders[e.ClientNumber] = decoder;

            // 해당 타일 업데이트
            if (e.ClientNumber > 0 && e.ClientNumber <= _clientTiles.Count)
            {
                var tile = _clientTiles[e.ClientNumber - 1];
                tile.Connected = true;
                tile.UpdateLabel($"클라이언트 {e.ClientNumber} - 연결됨");
            }
        }

        private void OnClientDisconnected(object sender, ClientEventArgs e)
        {
            if (InvokeRequired)
            {
                Invoke(new Action<object, ClientEventArgs>(OnClientDisconnected), sender, e);
                return;
            }

            // 로그 추가
            string message = $"클라이언트 {e.ClientNumber} 연결 해제";
            logListBox.Items.Add(message);
            FileLogger.Instance.WriteInfo(message);

            // 디코더 제거
            if (_decoders.TryRemove(e.ClientNumber, out var decoder))
            {
                decoder.Dispose();
            }

            // 마지막 프레임 제거
            if (_lastFrames.TryRemove(e.ClientNumber, out var frame))
            {
                frame.Dispose();
            }

            // 해당 타일 업데이트
            if (e.ClientNumber > 0 && e.ClientNumber <= _clientTiles.Count)
            {
                var tile = _clientTiles[e.ClientNumber - 1];
                tile.Connected = false;
                tile.UpdateImage(null);
                tile.UpdateLabel($"클라이언트 {e.ClientNumber} - 연결 끊김");
            }

            // 열려있는 원격 제어 창 닫기
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
            if (_decoders.TryGetValue(e.ClientNumber, out var decoder))
            {
                try
                {
                    decoder.DecodeFrame(e.ScreenData, e.Width, e.Height);
                }
                catch (Exception ex)
                {
                    FileLogger.Instance.WriteError($"클라이언트 {e.ClientNumber} 디코딩 오류", ex);
                }
            }
        }

        private void OnFrameDecoded(int clientNumber, Bitmap bitmap)
        {
            if (InvokeRequired)
            {
                Invoke(new Action<int, Bitmap>(OnFrameDecoded), clientNumber, bitmap);
                return;
            }

            // 마지막 프레임 업데이트
            if (_lastFrames.TryGetValue(clientNumber, out var oldFrame))
            {
                oldFrame.Dispose();
            }

            _lastFrames[clientNumber] = bitmap;

            // 해당 타일 업데이트
            if (clientNumber > 0 && clientNumber <= _clientTiles.Count)
            {
                var tile = _clientTiles[clientNumber - 1];
                tile.UpdateImage(bitmap);
            }

            // 열려있는 원격 제어 창 업데이트
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

        // 이전 OnTileClick 메서드를 OnTileDoubleClick으로 대체하고 개선
        private void OnTileDoubleClick(object sender, EventArgs e)
        {
            if (sender is ClientTile tile && tile.Connected)
            {
                FileLogger.Instance.WriteInfo($"타일 {tile.ClientNumber} 더블클릭 - 원격 제어 시작");

                // 이미 열려있는 창 확인
                foreach (Form form in Application.OpenForms)
                {
                    if (form is RemoteControlForm rcForm && rcForm.ClientNumber == tile.ClientNumber)
                    {
                        form.Activate();
                        FileLogger.Instance.WriteInfo($"클라이언트 {tile.ClientNumber}의 원격 제어 창이 이미 존재함");
                        return;
                    }
                }

                // 원격 제어 요청
                if (_networkServer.SendRemoteControlRequest(tile.ClientNumber))
                {
                    try
                    {
                        // 원격 제어 창 열기

                        Bitmap frameCopy = null;
                        if (_lastFrames.TryGetValue(tile.ClientNumber, out var lastFrame))
                        {
                            try
                            {
                                frameCopy = (Bitmap)lastFrame.Clone();
                            }
                            catch (Exception ex)
                            {
                                FileLogger.Instance.WriteError("원격 제어 창 생성 시 lastFrame 복제 오류", ex);
                                frameCopy = null;
                            }
                        }


                        var remoteForm = new RemoteControlForm(
                            _networkServer, tile.ClientNumber, frameCopy);

                        remoteForm.FormClosed += (s, args) =>
                        {
                            // 원격 제어 종료 요청
                            _networkServer.SendRemoteControlEnd(tile.ClientNumber);
                            FileLogger.Instance.WriteInfo($"클라이언트 {tile.ClientNumber} 원격 제어 종료");
                        };

                        remoteForm.Show();
                        FileLogger.Instance.WriteInfo($"클라이언트 {tile.ClientNumber} 원격 제어 창 열림");
                    }
                    catch (Exception ex)
                    {
                        FileLogger.Instance.WriteError($"원격 제어 창 생성 오류", ex);
                        MessageBox.Show($"원격 제어 창을 열 수 없습니다: {ex.Message}", "오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
                else
                {
                    FileLogger.Instance.WriteWarning($"클라이언트 {tile.ClientNumber}에 원격 제어 요청 실패");
                    MessageBox.Show($"클라이언트 {tile.ClientNumber}에 원격 제어 요청을 보낼 수 없습니다.", "경고", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
            else if (sender is ClientTile disconnectedTile)
            {
                // 연결되지 않은 타일을 클릭한 경우
                FileLogger.Instance.WriteWarning($"타일 {disconnectedTile.ClientNumber} 클릭 - 연결되지 않음");
                MessageBox.Show($"클라이언트 {disconnectedTile.ClientNumber}가 연결되어 있지 않습니다.", "알림", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            Cleanup();
        }
    }

    // 클라이언트 타일 컨트롤
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
                Text = $"클라이언트 {clientNumber} - 대기중"
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
                FileLogger.Instance.WriteError("원격 제어 화면 업데이트 오류", ex);
            }
        }


        public void UpdateLabel(string text)
        {
            _label.Text = text;
        }
    }
}