// ScreenShare.Client/Program.cs
using System;
using System.Windows.Forms;
using System.Threading;
using ScreenShare.Client.Forms;
using ScreenShare.Common.Settings;
using ScreenShare.Common.Utils;

namespace ScreenShare.Client
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // 로깅 초기화
            var logger = FileLogger.Instance;
            logger.WriteInfo("애플리케이션 시작");

            // 전역 예외 처리
            Application.ThreadException += new ThreadExceptionEventHandler(Application_ThreadException);
            AppDomain.CurrentDomain.UnhandledException += new UnhandledExceptionEventHandler(CurrentDomain_UnhandledException);

            try
            {
                var settings = ClientSettings.Load();
                logger.WriteInfo($"설정 로드 완료 - 클라이언트 번호: {settings.ClientNumber}, 호스트: {settings.HostIp}:{settings.HostPort}");

                bool showLoginForm = true;

                // 설정 확인 - 모든 필수 항목이 설정되었는지 검증
                if (!string.IsNullOrEmpty(settings.HostIp) && settings.HostPort > 0 && settings.ClientNumber > 0)
                {
                    // 기존 설정으로 시작하려면 showLoginForm = false로 설정
                    showLoginForm = false; // 로그인 창을 표시하지 않음

                    // 테스트 중일 때는 아래 줄의 주석을 해제하여 항상 로그인 창 표시
                    // showLoginForm = true;
                }

                if (showLoginForm)
                {
                    logger.WriteInfo("로그인 창 표시");
                    using (var loginForm = new LoginForm())
                    {
                        if (loginForm.ShowDialog() == DialogResult.Cancel)
                        {
                            logger.WriteInfo("사용자가 로그인 취소함");
                            return; // 취소 버튼을 누른 경우 프로그램 종료
                        }
                        logger.WriteInfo($"로그인 완료 - 클라이언트 번호: {settings.ClientNumber}, 호스트: {settings.HostIp}:{settings.HostPort}");
                    }
                }

                Application.Run(new MainForm());
            }
            catch (Exception ex)
            {
                logger.WriteError("프로그램 실행 중 오류 발생", ex);
                MessageBox.Show($"오류가 발생했습니다: {ex.Message}", "오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                logger.WriteInfo("애플리케이션 종료");
                logger.Close();
            }
        }

        private static void Application_ThreadException(object sender, ThreadExceptionEventArgs e)
        {
            FileLogger.Instance.WriteError("UI 스레드 예외 발생", e.Exception);
            MessageBox.Show($"처리되지 않은 오류가 발생했습니다: {e.Exception.Message}", "오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        private static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            FileLogger.Instance.WriteError("처리되지 않은 예외 발생", e.ExceptionObject as Exception);
            MessageBox.Show($"심각한 오류가 발생했습니다: {(e.ExceptionObject as Exception)?.Message}", "오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}