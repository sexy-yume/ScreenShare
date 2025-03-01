// ScreenShare.Host/Forms/RemoteControlForm.cs
using System;
using System.Drawing;
using System.Windows.Forms;
using ScreenShare.Host.Network;
using ScreenShare.Common.Utils;

namespace ScreenShare.Host.Forms
{
    public class RemoteControlForm : Form
    {
        private NetworkServer _networkServer;
        private PictureBox _pictureBox;
        private int _clientNumber;
        private bool _isControlling;
        private Label _statusLabel;

        public int ClientNumber => _clientNumber;

        public RemoteControlForm(NetworkServer networkServer, int clientNumber, Bitmap initialImage)
        {
            _networkServer = networkServer;
            _clientNumber = clientNumber;
            _isControlling = true;

            // 폼 설정
            Text = $"원격 제어 - 클라이언트 {clientNumber}";
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

            // 화면 표시 PictureBox
            _pictureBox = new PictureBox
            {
                Dock = DockStyle.Fill,
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = Color.Black
            };

            if (initialImage != null)
            {
                _pictureBox.Image = initialImage;
            }
            else
            {
                // 초기 이미지가 없을 경우 대기 메시지 표시
                Bitmap waitingImage = new Bitmap(800, 600);
                using (Graphics g = Graphics.FromImage(waitingImage))
                {
                    g.Clear(Color.Black);
                    g.DrawString("화면 데이터 수신 대기중...",
                        new Font("Arial", 24), Brushes.White,
                        new RectangleF(0, 0, 800, 600),
                        new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center });
                }
                _pictureBox.Image = waitingImage;
            }

            // 이벤트 핸들러 등록
            _pictureBox.MouseMove += OnPictureBoxMouseMove;
            _pictureBox.MouseClick += OnPictureBoxMouseClick;
            _pictureBox.MouseDown += OnPictureBoxMouseDown;
            _pictureBox.MouseUp += OnPictureBoxMouseUp;

            KeyPreview = true;
            KeyDown += OnFormKeyDown;
            KeyUp += OnFormKeyUp;

            // 컨트롤 추가
            Controls.Add(_pictureBox);
            Controls.Add(_statusLabel);

            // 폼 종료 이벤트
            FormClosed += OnFormClosed;

            FileLogger.Instance.WriteInfo($"클라이언트 {clientNumber} 원격 제어 창 생성");
        }

        public void UpdateImage(Bitmap image)
        {
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

        private void OnPictureBoxMouseMove(object sender, MouseEventArgs e)
        {
            if (!_isControlling || _pictureBox.Image == null)
                return;

            try
            {
                // 좌표 변환
                float scaleX = (float)_pictureBox.Image.Width / _pictureBox.ClientSize.Width;
                float scaleY = (float)_pictureBox.Image.Height / _pictureBox.ClientSize.Height;

                int x = (int)(e.X * scaleX);
                int y = (int)(e.Y * scaleY);

                _networkServer.SendMouseMove(_clientNumber, x, y);
            }
            catch (Exception ex)
            {
                FileLogger.Instance.WriteError("마우스 이동 전송 오류", ex);
            }
        }

        private void OnPictureBoxMouseClick(object sender, MouseEventArgs e)
        {
            if (!_isControlling || _pictureBox.Image == null)
                return;

            try
            {
                // 좌표 변환
                float scaleX = (float)_pictureBox.Image.Width / _pictureBox.ClientSize.Width;
                float scaleY = (float)_pictureBox.Image.Height / _pictureBox.ClientSize.Height;

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

        private void OnPictureBoxMouseDown(object sender, MouseEventArgs e)
        {
            _pictureBox.Focus();
        }

        private void OnPictureBoxMouseUp(object sender, MouseEventArgs e)
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

        private void OnFormClosed(object sender, FormClosedEventArgs e)
        {
            _isControlling = false;

            try
            {
                if (_pictureBox.Image != null)
                {
                    _pictureBox.Image.Dispose();
                    _pictureBox.Image = null;
                }
            }
            catch (Exception ex)
            {
                FileLogger.Instance.WriteError("원격 제어 창 종료 중 오류", ex);
            }
        }
    }
}