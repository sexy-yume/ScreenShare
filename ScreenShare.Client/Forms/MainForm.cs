// ScreenShare.Client/Forms/MainForm.cs
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
        private readonly object _syncLock = new object();

        public MainForm()
        {
            InitializeComponent();

            try
            {
                // 설정 로드
                _settings = ClientSettings.Load();

                lblStatus.Text = "상태: 초기화 중...";
                FileLogger.Instance.WriteInfo("메인 폼 초기화 시작");

                // 화면 크기 가져오기
                var screenSize = Screen.PrimaryScreen.Bounds;

                // 네트워크 클라이언트 초기화
                _networkClient = new NetworkClient(_settings);
                _networkClient.RemoteControlStatusChanged += OnRemoteControlStatusChanged;
                _networkClient.RemoteControlReceived += OnRemoteControlReceived;
                _networkClient.ConnectionStatusChanged += OnConnectionStatusChanged;

                // 인코더 초기화
                _encoder = new FFmpegEncoder(screenSize.Width, screenSize.Height, _settings.LowResQuality);
                _encoder.FrameEncoded += OnFrameEncoded;

                // 화면 캡처 초기화 - OptimizedScreenCapture 사용
                _screenCapture = new OptimizedScreenCapture();
                _screenCapture.Fps = _settings.LowResFps;
                _screenCapture.Quality = _settings.LowResQuality;
                _screenCapture.FrameCaptured += OnFrameCaptured;

                // 타이머 초기화 - 네트워크 업데이트용
                _networkUpdateTimer = new System.Windows.Forms.Timer();
                _networkUpdateTimer.Interval = 15;
                _networkUpdateTimer.Tick += (s, e) => _networkClient.Update();
                _networkUpdateTimer.Start();

                // 시작
                lblStatus.Text = "상태: 서버에 연결 중...";
                _networkClient.Start();
                _screenCapture.Start();

                FormClosing += (s, e) => Cleanup();

                FileLogger.Instance.WriteInfo("메인 폼 초기화 완료");
            }
            catch (Exception ex)
            {
                FileLogger.Instance.WriteError("메인 폼 초기화 오류", ex);
                MessageBox.Show($"초기화 오류: {ex.Message}", "오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Close();
            }
        }

        private void Cleanup()
        {
            lock (_syncLock)
            {
                lblStatus.Text = "상태: 종료 중...";

                _networkUpdateTimer?.Stop();
                _screenCapture?.Stop();
                _networkClient?.Stop();

                _screenCapture?.Dispose();
                _encoder?.Dispose();
                _networkClient?.Dispose();

                FileLogger.Instance.WriteInfo("메인 폼 종료");
            }
        }

        private void OnFrameCaptured(object sender, Bitmap bitmap)
        {
            try
            {
                // 비트맵이 이미 클론되었으므로 여기서는 직접 인코딩
                _encoder.EncodeFrame(bitmap);
            }
            catch (Exception ex)
            {
                FileLogger.Instance.WriteError("인코딩 오류", ex);
            }
            finally
            {
                // bitmap은 OptimizedScreenCapture에서 클론으로 생성되었으므로
                // 이 메서드에서 처리 후 반드시 해제해야 함
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
                FileLogger.Instance.WriteError("네트워크 전송 오류", ex);
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
                lblStatus.Text = "상태: 화면 전송 중";
                FileLogger.Instance.WriteInfo("연결됨: 화면 전송 중");
            }
            else
            {
                lblStatus.Text = "상태: 재연결 중...";
                FileLogger.Instance.WriteInfo("연결 해제: 재연결 시도 중");
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
                // 원격 제어 모드 활성화
                _screenCapture.Fps = _settings.HighResFps;
                _screenCapture.Quality = _settings.HighResQuality;
                lblStatus.Text = "상태: 원격 제어 모드";
                FileLogger.Instance.WriteInfo($"원격 제어 모드 활성화 (FPS: {_settings.HighResFps}, 품질: {_settings.HighResQuality})");

                // 모든 설정에 우선순위 부여
                if (Process.GetCurrentProcess().PriorityClass != ProcessPriorityClass.AboveNormal)
                    Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.AboveNormal;
            }
            else
            {
                // 일반 모드
                _screenCapture.Fps = _settings.LowResFps;
                _screenCapture.Quality = _settings.LowResQuality;
                lblStatus.Text = "상태: 화면 전송 중";
                FileLogger.Instance.WriteInfo($"일반 모드 전환 (FPS: {_settings.LowResFps}, 품질: {_settings.LowResQuality})");

                // 프로세스 우선순위 정상화
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

            // 원격 제어 명령 처리
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

                        // 마우스 클릭 이벤트 시뮬레이션
                        if (packet.MouseButton.Value == 0) // 왼쪽 클릭
                        {
                            MouseOperations.MouseEvent(MouseOperations.MouseEventFlags.LeftDown);
                            MouseOperations.MouseEvent(MouseOperations.MouseEventFlags.LeftUp);
                        }
                        else if (packet.MouseButton.Value == 1) // 오른쪽 클릭
                        {
                            MouseOperations.MouseEvent(MouseOperations.MouseEventFlags.RightDown);
                            MouseOperations.MouseEvent(MouseOperations.MouseEventFlags.RightUp);
                        }
                    }
                    break;

                case PacketType.KeyPress:
                    if (packet.KeyCode.HasValue)
                    {
                        // 키보드 이벤트 시뮬레이션
                        KeyboardOperations.KeyDown((Keys)packet.KeyCode.Value);
                        KeyboardOperations.KeyUp((Keys)packet.KeyCode.Value);
                    }
                    break;
            }
        }
    }

    // 마우스 이벤트 시뮬레이션을 위한 유틸리티 클래스
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

    // 키보드 이벤트 시뮬레이션을 위한 유틸리티 클래스
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