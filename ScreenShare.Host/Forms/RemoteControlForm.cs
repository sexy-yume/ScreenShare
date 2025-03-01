using System;
using System.Drawing;
using System.Windows.Forms;
using ScreenShare.Host.Network;
using ScreenShare.Common.Utils;
using ScreenShare.Host.Rendering;

namespace ScreenShare.Host.Forms
{
    public class RemoteControlForm : Form
    {
        private NetworkServer _networkServer;
        private Panel _renderPanel;
        private DirectXRenderer _renderer;
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
            InitializeDirectXRenderer(initialImage);

            FormClosing += (s, e) => Cleanup();
            FileLogger.Instance.WriteInfo($"클라이언트 {clientNumber} 원격 제어 창 생성 (DirectX 렌더러 사용)");
        }

        private void InitializeComponent()
        {
            // 폼 설정
            Text = $"원격 제어 - 클라이언트 {_clientNumber}";
            Size = new Size(1024, 768);
            StartPosition = FormStartPosition.CenterScreen;

            // 상태 표시 레이블
            _statusLabel = new Label
            {
                Dock = DockStyle.Bottom,
                Height = 25,
                TextAlign = ContentAlignment.MiddleCenter,
                Text = "원격 제어 모드 - 마우스와 키보드 입력이 클라이언트에 전달됩니다."
            };

            // DirectX 렌더링을 위한 패널
            _renderPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Black
            };

            // 이벤트 핸들러 등록
            _renderPanel.MouseMove += OnPanelMouseMove;
            _renderPanel.MouseClick += OnPanelMouseClick;
            _renderPanel.MouseDown += OnPanelMouseDown;
            _renderPanel.MouseUp += OnPanelMouseUp;

            KeyPreview = true;
            KeyDown += OnFormKeyDown;
            KeyUp += OnFormKeyUp;

            // 컨트롤 추가
            Controls.Add(_renderPanel);
            Controls.Add(_statusLabel);
        }

        private void InitializeDirectXRenderer(Bitmap initialImage)
        {
            try
            {
                if (initialImage != null)
                {
                    lock (_frameLock)
                    {
                        _currentFrame = new Bitmap(initialImage);
                    }
                }
                else
                {
                    // 초기 이미지가 없을 경우 대기 메시지 표시
                    lock (_frameLock)
                    {
                        _currentFrame = new Bitmap(800, 600);
                        using (Graphics g = Graphics.FromImage(_currentFrame))
                        {
                            g.Clear(Color.Black);
                            g.DrawString("화면 데이터 수신 대기중...",
                                new Font("Arial", 24), Brushes.White,
                                new RectangleF(0, 0, 800, 600),
                                new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center });
                        }
                    }
                }

                // 렌더러 초기화
                if (_currentFrame != null)
                {
                    _renderer = new DirectXRenderer(_renderPanel, _currentFrame.Width, _currentFrame.Height);

                    // SetBackgroundColor의 경우 System.Drawing.Color 타입을 명시적으로 전달
                    Color backgroundColor = Color.Black;
                    _renderer.SetBackgroundColor(backgroundColor);

                    _renderer.SetStretchMode(true);
                    _renderer.SetVSync(false); // VSync 비활성화로 저지연 구현
                }
            }
            catch (Exception ex)
            {
                FileLogger.Instance.WriteError("DirectX 렌더러 초기화 실패", ex);
                MessageBox.Show($"DirectX 렌더러 초기화 중 오류가 발생했습니다: {ex.Message}", "오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        public void UpdateImage(Bitmap image)
        {
            if (_isDisposed)
                return;

            try
            {
                Console.WriteLine($"이미지 업데이트 시작: image={image != null}");
                if (image != null)
                {
                    Console.WriteLine($"이미지 정보: {image.Width}x{image.Height}, Format={image.PixelFormat}");
                }

                Bitmap frameToRender = null;

                // 이전 프레임 정리 및 새 프레임 설정
                lock (_frameLock)
                {
                    if (_currentFrame != null && _currentFrame != image)
                    {
                        _currentFrame.Dispose();
                    }

                    if (image != null)
                    {
                        _currentFrame = image;
                        frameToRender = _currentFrame;
                    }
                    else
                    {
                        // 테스트 이미지 생성
                        frameToRender = CreateTestImage();
                    }
                }

                // UI 스레드에서 렌더링 실행
                if (frameToRender != null)
                {
                    if (InvokeRequired)
                    {
                        BeginInvoke(new Action(() => RenderCurrentFrame(frameToRender)));
                    }
                    else
                    {
                        RenderCurrentFrame(frameToRender);
                    }
                }
            }
            catch (Exception ex)
            {
                FileLogger.Instance.WriteError("화면 업데이트 오류", ex);
                Console.WriteLine($"화면 업데이트 상세 오류: {ex.StackTrace}");
            }
        }
        private Bitmap CreateTestImage()
        {
            Console.WriteLine("테스트 이미지 생성 중...");

            int width = 800;
            int height = 600;

            if (_currentFrame != null)
            {
                width = _currentFrame.Width;
                height = _currentFrame.Height;
            }

            Bitmap testImage = new Bitmap(width, height);

            using (Graphics g = Graphics.FromImage(testImage))
            {
                // 배경색 채우기
                g.Clear(Color.DarkBlue);

                // 테스트 패턴 그리기
                for (int x = 0; x < width; x += 100)
                {
                    for (int y = 0; y < height; y += 100)
                    {
                        bool isEvenX = (x / 100) % 2 == 0;
                        bool isEvenY = (y / 100) % 2 == 0;

                        Color color = (isEvenX ^ isEvenY) ? Color.LightGray : Color.DarkGray;
                        g.FillRectangle(new SolidBrush(color), x, y, 100, 100);
                    }
                }

                // 텍스트 추가
                g.DrawString("테스트 화면 - " + DateTime.Now.ToString("HH:mm:ss.fff"),
                    new Font("Arial", 24, FontStyle.Bold),
                    Brushes.Yellow, 50, 50);

                g.DrawString("화면 데이터가 없거나 렌더링 문제가 있습니다.",
                    new Font("Arial", 18),
                    Brushes.White, 50, 100);
            }

            Console.WriteLine("테스트 이미지 생성 완료");
            return testImage;
        }
        private void RenderCurrentFrame(Bitmap frame)
        {
            try
            {
                if (!_isDisposed && _renderer != null && frame != null)
                {
                    Console.WriteLine($"프레임 렌더링 시작: {frame.Width}x{frame.Height}, Format={frame.PixelFormat}");

                    // 확실히 하기 위해 새 비트맵으로 복사
                    using (var copy = new Bitmap(frame))
                    {
                        // 테스트 - 비트맵에 텍스트 그리기 (화면에 나타나는지 확인용)
                        using (Graphics g = Graphics.FromImage(copy))
                        {
                            g.DrawString(DateTime.Now.ToString("HH:mm:ss.fff"),
                                new Font("Arial", 24, FontStyle.Bold),
                                Brushes.Red, 10, 10);

                            // 테스트 패턴 - 화면 모서리에 색상 사각형 그리기
                            g.FillRectangle(Brushes.Red, 0, 0, 50, 50);
                            g.FillRectangle(Brushes.Green, copy.Width - 50, 0, 50, 50);
                            g.FillRectangle(Brushes.Blue, 0, copy.Height - 50, 50, 50);
                            g.FillRectangle(Brushes.Yellow, copy.Width - 50, copy.Height - 50, 50, 50);
                        }

                        _renderer.RenderFrame(copy);
                        Console.WriteLine("프레임 렌더링 완료");
                    }
                }
                else
                {
                    Console.WriteLine($"프레임 렌더링 스킵: isDisposed={_isDisposed}, renderer={_renderer != null}, frame={frame != null}");
                    if (_renderer != null && frame == null)
                    {
                        // null 비트맵을 전달해 빨간색 테스트 화면 표시
                        _renderer.RenderFrame(null);
                    }
                }
            }
            catch (Exception ex)
            {
                FileLogger.Instance.WriteError($"DirectX 렌더링 오류: {ex.Message}", ex);
                Console.WriteLine($"DirectX 렌더링 상세 오류: {ex.StackTrace}");
            }
        }

        private void OnPanelMouseMove(object sender, MouseEventArgs e)
        {
            if (!_isControlling || _currentFrame == null)
                return;

            try
            {
                // 좌표 변환
                float scaleX = (float)_currentFrame.Width / _renderPanel.ClientSize.Width;
                float scaleY = (float)_currentFrame.Height / _renderPanel.ClientSize.Height;

                int x = (int)(e.X * scaleX);
                int y = (int)(e.Y * scaleY);

                _networkServer.SendMouseMove(_clientNumber, x, y);
            }
            catch (Exception ex)
            {
                FileLogger.Instance.WriteError("마우스 이동 전송 오류", ex);
            }
        }

        private void OnPanelMouseClick(object sender, MouseEventArgs e)
        {
            if (!_isControlling || _currentFrame == null)
                return;

            try
            {
                // 좌표 변환
                float scaleX = (float)_currentFrame.Width / _renderPanel.ClientSize.Width;
                float scaleY = (float)_currentFrame.Height / _renderPanel.ClientSize.Height;

                int x = (int)(e.X * scaleX);
                int y = (int)(e.Y * scaleY);

                int button = e.Button == MouseButtons.Left ? 0 : e.Button == MouseButtons.Right ? 1 : -1;
                if (button >= 0)
                {
                    _networkServer.SendMouseClick(_clientNumber, x, y, button);
                    FileLogger.Instance.WriteInfo($"마우스 클릭 전송: 좌표 ({x},{y}), 버튼 {button}");
                }
            }
            catch (Exception ex)
            {
                FileLogger.Instance.WriteError("마우스 클릭 전송 오류", ex);
            }
        }

        private void OnPanelMouseDown(object sender, MouseEventArgs e)
        {
            _renderPanel.Focus();
        }

        private void OnPanelMouseUp(object sender, MouseEventArgs e)
        {
            // 필요한 경우 마우스 업 이벤트 처리
        }

        private void OnFormKeyDown(object sender, KeyEventArgs e)
        {
            if (!_isControlling)
                return;

            try
            {
                _networkServer.SendKeyPress(_clientNumber, (int)e.KeyCode);
                e.Handled = true;

                // ESC 키로 원격 제어 종료 기능 추가
                if (e.KeyCode == Keys.Escape)
                {
                    DialogResult result = MessageBox.Show(
                        "원격 제어를 종료하시겠습니까?",
                        "원격 제어 종료",
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
                FileLogger.Instance.WriteError("키 입력 전송 오류", ex);
            }
        }

        private void OnFormKeyUp(object sender, KeyEventArgs e)
        {
            // 필요한 경우 키 업 이벤트 처리
        }

        private void Cleanup()
        {
            if (_isDisposed)
                return;

            _isDisposed = true;
            _isControlling = false;

            try
            {
                _renderer?.Dispose();
                _renderer = null;

                lock (_frameLock)
                {
                    if (_currentFrame != null)
                    {
                        _currentFrame.Dispose();
                        _currentFrame = null;
                    }
                }

                FileLogger.Instance.WriteInfo($"클라이언트 {_clientNumber} 원격 제어 종료");
            }
            catch (Exception ex)
            {
                FileLogger.Instance.WriteError("원격 제어 창 종료 중 오류", ex);
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