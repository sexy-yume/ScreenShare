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
            try
            {
                // 원격 제어 폼 정리
                foreach (var form in _remoteControlForms.Values)
                {
                    form.Close();
                    form.Dispose();
                }
                _remoteControlForms.Clear();

                // 디코더 정리
                foreach (var decoder in _decoders.Values)
                {
                    decoder.Dispose();
                }
                _decoders.Clear();

                // 마지막 프레임 정리
                foreach (var frame in _lastFrames.Values)
                {
                    frame.Dispose();
                }
                _lastFrames.Clear();

                // 기타 리소스 정리
                _networkServer?.Stop();
                _networkUpdateTimer?.Stop();
            }
            catch (Exception ex)
            {
                FileLogger.Instance.WriteError("리소스 정리 중 오류 발생", ex);
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
            try
            {
                int clientNumber = e.ClientNumber;

                // 해당 클라이언트용 디코더 가져오기
                var decoder = _decoders.GetOrAdd(clientNumber, num => {
                    var newDecoder = new FFmpegDecoder();
                    FileLogger.Instance.WriteInfo($"클라이언트 {clientNumber}용 새 디코더 생성");
                    return newDecoder;
                });

                // 데이터 디코딩
                Bitmap decodedFrame = decoder.DecodeFrame(e.ScreenData, e.Width, e.Height);

                if (decodedFrame != null)
                {
                    // 원격 제어 폼이 있으면 프레임 전달
                    if (_remoteControlForms.TryGetValue(clientNumber, out var form))
                    {
                        form.UpdateImage(decodedFrame);
                    }
                    else
                    {
                        // 타일에 축소된 이미지 표시
                        var clientTile = _clientTiles.FirstOrDefault(tile => tile.ClientNumber == clientNumber);
                        if (clientTile != null)
                        {
                            // 타일 크기에 맞게 이미지 축소
                            using (Bitmap resizedImage = new Bitmap(decodedFrame, clientTile.Width, clientTile.Height))
                            {
                                clientTile.UpdateImage(resizedImage);
                            }
                        }

                        // 마지막 프레임 저장 (원격 제어 시작 시 사용)
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
                FileLogger.Instance.WriteError("화면 데이터 처리 오류", ex);
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
            if (sender is ClientTile tile)
            {
                int clientNumber = tile.ClientNumber;
                FileLogger.Instance.WriteInfo($"타일 {clientNumber} 더블클릭 - 원격 제어 시작");

                // 이미 열린 원격 제어 창이 있는지 확인
                if (_remoteControlForms.ContainsKey(clientNumber))
                {
                    // 이미 열려 있으면 포커스 가져오기
                    _remoteControlForms[clientNumber].Focus();
                    return;
                }

                // 최근 프레임이 있는지 확인
                Bitmap initialFrame = null;
                if (_lastFrames.TryGetValue(clientNumber, out var frame))
                {
                    initialFrame = frame;
                }

                // 원격 제어 요청 전송
                if (_networkServer.SendRemoteControlRequest(clientNumber))
                {
                    // 원격 제어 폼 생성
                    var remoteForm = new RemoteControlForm(_networkServer, clientNumber, initialFrame);
                    _remoteControlForms.TryAdd(clientNumber, remoteForm);

                    // 폼 종료 이벤트 처리
                    remoteForm.FormClosed += (s, args) => {
                        // 원격 제어 종료 메시지 전송
                        _networkServer.SendRemoteControlEnd(clientNumber);
                        RemoteControlForm removedForm;
                        _remoteControlForms.TryRemove(clientNumber, out removedForm);
                    };

                    // 원격 제어 창 표시
                    remoteForm.Show();
                    remoteForm.Focus();
                    FileLogger.Instance.WriteInfo($"클라이언트 {clientNumber} 원격 제어 창 열림");
                }
                else
                {
                    FileLogger.Instance.WriteError($"클라이언트 {clientNumber}에 원격 제어 요청 실패");
                    MessageBox.Show($"클라이언트 {clientNumber}에 원격 제어 요청을 보낼 수 없습니다.", "오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
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