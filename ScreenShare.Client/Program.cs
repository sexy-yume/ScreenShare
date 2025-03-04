using System;
using System.Windows.Forms;
using ScreenShare.Client.Forms;
using ScreenShare.Common.Utils;

namespace ScreenShare.Client
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            // 디버그 콘솔 활성화
            ConsoleHelper.ShowConsoleWindow();
            Console.WriteLine("ScreenShare 클라이언트 애플리케이션 시작");

            // 기본 Windows Forms 초기화
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // 메인 폼을 활성화하기 전에 예외 처리기 등록
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            Application.ThreadException += Application_ThreadException;

            try
            {
                // 로그인 폼을 먼저 표시
                var loginForm = new LoginForm();
                if (loginForm.ShowDialog() == DialogResult.OK)
                {
                    Console.WriteLine("로그인 성공, 메인 클라이언트 폼 시작");
                    Application.Run(new MainForm());
                }
                else
                {
                    Console.WriteLine("로그인 취소됨");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"예기치 않은 오류: {ex.Message}\n{ex.StackTrace}");
                MessageBox.Show($"애플리케이션 실행 중 오류가 발생했습니다: {ex.Message}",
                    "오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }

            Console.WriteLine("애플리케이션 종료");
        }

        private static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            var ex = e.ExceptionObject as Exception;
            Console.WriteLine($"처리되지 않은 예외: {ex?.Message}\n{ex?.StackTrace}");
            FileLogger.Instance.WriteError("처리되지 않은 예외", ex);
        }

        private static void Application_ThreadException(object sender, System.Threading.ThreadExceptionEventArgs e)
        {
            Console.WriteLine($"UI 스레드 예외: {e.Exception.Message}\n{e.Exception.StackTrace}");
            FileLogger.Instance.WriteError("UI 스레드 예외", e.Exception);
        }
    }
}